using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Malie.Models;

namespace Malie.Services;

public sealed class PointOfInterestService
{
    private const string LatLngApiBaseUrl = "https://api.latlng.work";
    private const int DefaultMaxResults = 12;
    private const int LatLngNearbyRadiusMeters = 5000;
    private const int LatLngNearbyLimit = 20;
    private const int LatLngSearchLimit = 14;
    private const int LatLngSearchParallelism = 4;
    private static readonly TimeSpan PoiCacheTtl = TimeSpan.FromMinutes(20);

    private readonly HttpClient _httpClient;
    private readonly Action<string>? _debugLog;
    private readonly object _poiCacheSync = new();
    private readonly Dictionary<string, PoiCacheEntry> _poiCache = new(StringComparer.OrdinalIgnoreCase);

    public PointOfInterestService(HttpClient httpClient, Action<string>? debugLog = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Malie/1.0");
        _debugLog = debugLog;
    }

    public async Task<IReadOnlyList<string>> GetNearbyLandmarksAsync(
        GeoCoordinates coordinates,
        string locationInput,
        string latLngApiKey,
        IReadOnlyCollection<string>? excludedPoiNames,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var cityHint = GetCityHint(locationInput, coordinates.DisplayName);
        var effectiveMaxResults = Math.Clamp(maxResults, 4, 48);
        var excludedKeys = (excludedPoiNames ?? Array.Empty<string>())
            .Select(NormalizeCandidateKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cacheKey = BuildPoiCacheKey(
            coordinates,
            locationInput,
            !string.IsNullOrWhiteSpace(latLngApiKey),
            excludedKeys,
            effectiveMaxResults);
        if (TryGetCachedPoiResults(cacheKey, out var cachedPoiResults))
        {
            _debugLog?.Invoke($"POI resolver cache hit: {cachedPoiResults.Count} landmark(s).");
            return cachedPoiResults;
        }

        var scored = new Dictionary<string, PoiCandidate>(StringComparer.OrdinalIgnoreCase);

        void AddCandidates(IEnumerable<string> names, int sourceWeight, string source)
        {
            foreach (var rawName in names)
            {
                var candidateName = NormalizeCandidateName(rawName);
                if (string.IsNullOrWhiteSpace(candidateName) || ShouldExcludeName(candidateName))
                {
                    continue;
                }

                var key = NormalizeCandidateKey(candidateName);
                if (string.IsNullOrWhiteSpace(key) || excludedKeys.Contains(key))
                {
                    continue;
                }

                var weight = sourceWeight + (ContainsLandmarkCue(candidateName) ? 2 : 0);
                if (scored.TryGetValue(key, out var existing))
                {
                    existing.Score += weight;
                    existing.Sources.Add(source);
                    if (candidateName.Length > existing.Name.Length)
                    {
                        existing.Name = candidateName;
                    }

                    continue;
                }

                scored[key] = new PoiCandidate(candidateName, weight, source);
            }
        }

        if (string.IsNullOrWhiteSpace(latLngApiKey))
        {
            _debugLog?.Invoke("POI resolver: LatLng API key is empty, skipping LatLng endpoints.");
        }
        else
        {
            var seeds = await BuildLatLngSeedCoordinatesAsync(coordinates, locationInput, latLngApiKey, cancellationToken);
            if (seeds.Count > 0 && string.IsNullOrWhiteSpace(cityHint))
            {
                var reverseTasks = seeds
                    .Select(seed => TryResolveCityHintFromReverseGeocodeAsync(seed, latLngApiKey, cancellationToken))
                    .ToArray();
                var reverseHints = await Task.WhenAll(reverseTasks);
                cityHint = reverseHints.FirstOrDefault(hint => !string.IsNullOrWhiteSpace(hint)) ?? cityHint;
            }

            var seedCandidateTasks = seeds
                .Select(seed => ResolveLatLngSeedCandidatesAsync(seed, latLngApiKey, cancellationToken))
                .ToArray();
            var seedCandidates = await Task.WhenAll(seedCandidateTasks);
            foreach (var seedCandidate in seedCandidates)
            {
                AddCandidates(seedCandidate.NearbyNames, sourceWeight: 14, source: "LatLng places nearby");
            }
        }

        var overpassTask = TryGetLandmarksFromOverpassAsync(
            coordinates,
            cityHint is null ? 12000 : 22000,
            cancellationToken);

        var wikipediaGeoTask = TryGetLandmarksFromWikipediaAsync(
            coordinates,
            cityHint is null ? 10000 : 22000,
            cancellationToken);
        var citySearchTask = string.IsNullOrWhiteSpace(cityHint)
            ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>())
            : TryGetLandmarksByCityQueryAsync(cityHint, cancellationToken);
        await Task.WhenAll(overpassTask, wikipediaGeoTask, citySearchTask);

        AddCandidates(overpassTask.Result, sourceWeight: 8, source: "Overpass");
        AddCandidates(wikipediaGeoTask.Result, sourceWeight: 6, source: "Wikipedia geo");

        if (!string.IsNullOrWhiteSpace(cityHint) && citySearchTask.Result.Count > 0)
        {
            AddCandidates(citySearchTask.Result, sourceWeight: 4, source: "Wikipedia city");
        }

        var distinct = scored
            .OrderByDescending(pair => pair.Value.Score)
            .ThenByDescending(pair => pair.Value.Sources.Count)
            .ThenBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value.Name)
            .Take(effectiveMaxResults)
            .ToArray();

        if (distinct.Length > 0)
        {
            var latLngState = string.IsNullOrWhiteSpace(latLngApiKey) ? "off" : "on";
            _debugLog?.Invoke(
                $"POI resolver selected {distinct.Length} landmarks (LatLng key {latLngState}, excluded cached={excludedKeys.Count}). " +
                $"Sources used: {DescribeSources(scored)}");
            CachePoiResults(cacheKey, distinct);
            return distinct;
        }

        if (!string.IsNullOrWhiteSpace(cityHint))
        {
            var fallback = BuildCityFallbackLandmarks(cityHint)
                .Where(name => !excludedKeys.Contains(NormalizeCandidateKey(name)))
                .Take(effectiveMaxResults)
                .ToArray();
            if (fallback.Length > 0)
            {
                _debugLog?.Invoke($"POI resolver fell back to city defaults for '{cityHint}'.");
                CachePoiResults(cacheKey, fallback);
                return fallback;
            }
        }

        _debugLog?.Invoke("POI resolver returned no landmarks.");
        CachePoiResults(cacheKey, Array.Empty<string>());
        return Array.Empty<string>();
    }

    private async Task<IReadOnlyList<GeoCoordinates>> BuildLatLngSeedCoordinatesAsync(
        GeoCoordinates coordinates,
        string locationInput,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var seeds = new List<GeoCoordinates>();

        if (!string.IsNullOrWhiteSpace(locationInput))
        {
            var forward = await TryForwardGeocodeWithLatLngAsync(locationInput, apiKey, cancellationToken);
            if (forward is not null)
            {
                seeds.Add(forward);
            }
        }

        seeds.Add(coordinates);

        var distinct = new List<GeoCoordinates>();
        foreach (var seed in seeds)
        {
            if (distinct.Any(existing => AreCoordinatesNear(existing, seed)))
            {
                continue;
            }

            distinct.Add(seed);
        }

        return distinct;
    }

    private async Task<LatLngSeedCandidates> ResolveLatLngSeedCandidatesAsync(
        GeoCoordinates seed,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var nearbyNames = await TryGetLandmarksFromLatLngNearbyAsync(seed, apiKey, cancellationToken);
        return new LatLngSeedCandidates(nearbyNames);
    }

    private async Task<GeoCoordinates?> TryForwardGeocodeWithLatLngAsync(
        string locationInput,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{LatLngApiBaseUrl}/api?q={Uri.EscapeDataString(locationInput.Trim())}&limit=1";
            using var request = CreateLatLngRequest(url, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var feature in features.EnumerateArray())
            {
                if (!TryReadLatLonFromFeature(feature, out var latitude, out var longitude))
                {
                    continue;
                }

                var displayName = TryGetString(feature, "properties", "name") ??
                                  TryGetString(feature, "properties", "label") ??
                                  locationInput.Trim();
                _debugLog?.Invoke($"LatLng forward geocoding resolved '{locationInput}' => {latitude:0.####}, {longitude:0.####}.");
                return new GeoCoordinates(latitude, longitude, displayName);
            }

            return null;
        }
        catch (Exception ex)
        {
            _debugLog?.Invoke($"LatLng forward geocoding failed: {ex.Message}");
            return null;
        }
    }
    private async Task<string?> TryResolveCityHintFromReverseGeocodeAsync(
        GeoCoordinates coordinates,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var lat = coordinates.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
            var lon = coordinates.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
            var url = $"{LatLngApiBaseUrl}/reverse?lat={lat}&lon={lon}&limit=1";
            using var request = CreateLatLngRequest(url, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var feature in features.EnumerateArray())
            {
                var candidate = TryGetString(feature, "properties", "city") ??
                                TryGetString(feature, "properties", "town") ??
                                TryGetString(feature, "properties", "village") ??
                                TryGetString(feature, "properties", "name");
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsDigit))
                {
                    continue;
                }

                return candidate.Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            _debugLog?.Invoke($"LatLng reverse geocoding failed: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> TryGetLandmarksFromLatLngNearbyAsync(
        GeoCoordinates coordinates,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var lat = coordinates.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
            var lon = coordinates.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
            var url =
                $"{LatLngApiBaseUrl}/places/nearby?lat={lat}&lon={lon}&radius={LatLngNearbyRadiusMeters}&limit={LatLngNearbyLimit}";
            using var request = CreateLatLngRequest(url, apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var names = ParseLatLngNearbyPlaceNames(document.RootElement);
            _debugLog?.Invoke($"LatLng places nearby returned {names.Count} candidate(s).");
            return names;
        }
        catch (Exception ex)
        {
            _debugLog?.Invoke($"LatLng places nearby failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseLatLngNearbyPlaceNames(JsonElement root)
    {
        if (!root.TryGetProperty("places", out var places) || places.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var place in places.EnumerateArray())
        {
            if (place.ValueKind != JsonValueKind.Object ||
                !place.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var category = TryGetString(place, "category") ??
                           TryGetString(place, "type") ??
                           TryGetString(place, "class");
            if (!LooksLikePointOfInterest(name, category))
            {
                continue;
            }

            names.Add(name);
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> TryGetLandmarksFromLatLngSearchAsync(
        GeoCoordinates coordinates,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var queries = new[]
        {
            "landmark",
            "monument",
            "museum",
            "historic site",
            "viewpoint",
            "bridge",
            "tower",
            "park"
        };

        using var semaphore = new SemaphoreSlim(LatLngSearchParallelism, LatLngSearchParallelism);
        var searchTasks = queries
            .Select(async query =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var lat = coordinates.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
                    var lon = coordinates.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
                    var url =
                    $"{LatLngApiBaseUrl}/places/search?q={Uri.EscapeDataString(query)}&lat={lat}&lon={lon}&limit={LatLngSearchLimit}";
                    using var request = CreateLatLngRequest(url, apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    return ParseLatLngPlaceNames(document.RootElement, strictPoiFilter: false);
                }
                catch (Exception ex)
                {
                    _debugLog?.Invoke($"LatLng places search failed for query '{query}': {ex.Message}");
                    return Array.Empty<string>();
                }
                finally
                {
                    semaphore.Release();
                }
            })
            .ToArray();
        var searchBatches = await Task.WhenAll(searchTasks);
        var distinct = searchBatches
            .SelectMany(batch => batch)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(28)
            .ToArray();
        _debugLog?.Invoke($"LatLng places search aggregated {distinct.Length} candidate(s).");
        return distinct;
    }

    private static IReadOnlyList<string> ParseLatLngPlaceNames(JsonElement root, bool strictPoiFilter)
    {
        JsonElement places;
        if (root.TryGetProperty("places", out var placesElement) && placesElement.ValueKind == JsonValueKind.Array)
        {
            places = placesElement;
        }
        else if (root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            places = resultsElement;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            places = root;
        }
        else
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (var place in places.EnumerateArray())
        {
            if (place.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = TryGetString(place, "name") ?? TryGetString(place, "display_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var category = TryGetString(place, "category") ??
                           TryGetString(place, "type") ??
                           TryGetString(place, "class");
            if (strictPoiFilter && !LooksLikePointOfInterest(name, category))
            {
                continue;
            }

            names.Add(name.Trim());
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();
    }

    private static bool LooksLikePointOfInterest(string name, string? category)
    {
        var lowerName = name.ToLowerInvariant();
        var lowerCategory = category?.ToLowerInvariant() ?? string.Empty;

        if (lowerName.Contains("hotel") ||
            lowerName.Contains("restaurant") ||
            lowerName.Contains("cafe") ||
            lowerName.Contains("bar") ||
            lowerName.Contains("mall") ||
            lowerName.Contains("station") ||
            lowerName.Contains("airport"))
        {
            return false;
        }

        if (lowerCategory.Contains("hotel") ||
            lowerCategory.Contains("restaurant") ||
            lowerCategory.Contains("food") ||
            lowerCategory.Contains("cafe") ||
            lowerCategory.Contains("bar") ||
            lowerCategory.Contains("retail") ||
            lowerCategory.Contains("shopping") ||
            lowerCategory.Contains("supermarket") ||
            lowerCategory.Contains("fuel") ||
            lowerCategory.Contains("parking") ||
            lowerCategory.Contains("bank") ||
            lowerCategory.Contains("pharmacy") ||
            lowerCategory.Contains("hospital"))
        {
            return false;
        }

        return ContainsLandmarkCue(name) ||
               lowerCategory.Contains("landmark") ||
               lowerCategory.Contains("attraction") ||
               lowerCategory.Contains("museum") ||
               lowerCategory.Contains("historic") ||
               lowerCategory.Contains("monument") ||
               lowerCategory.Contains("tourism") ||
               lowerCategory.Contains("park") ||
               lowerCategory.Contains("beach") ||
               lowerCategory.Contains("viewpoint");
    }

    private static HttpRequestMessage CreateLatLngRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<IReadOnlyList<string>> TryGetLandmarksFromOverpassAsync(
        GeoCoordinates coordinates,
        int radiusMeters,
        CancellationToken cancellationToken)
    {
        try
        {
            var latitude = coordinates.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
            var longitude = coordinates.Longitude.ToString("0.####", CultureInfo.InvariantCulture);

            var overpassQuery =
                $"""
                 [out:json][timeout:25];
                 (
                   node(around:{radiusMeters},{latitude},{longitude})["tourism"~"attraction|museum|gallery|viewpoint|theme_park|zoo"];
                   way(around:{radiusMeters},{latitude},{longitude})["tourism"~"attraction|museum|gallery|viewpoint|theme_park|zoo"];
                   relation(around:{radiusMeters},{latitude},{longitude})["tourism"~"attraction|museum|gallery|viewpoint|theme_park|zoo"];
                   node(around:{radiusMeters},{latitude},{longitude})["historic"];
                   way(around:{radiusMeters},{latitude},{longitude})["historic"];
                   relation(around:{radiusMeters},{latitude},{longitude})["historic"];
                   node(around:{radiusMeters},{latitude},{longitude})["man_made"~"tower|obelisk|lighthouse"];
                   way(around:{radiusMeters},{latitude},{longitude})["man_made"~"tower|obelisk|lighthouse"];
                   relation(around:{radiusMeters},{latitude},{longitude})["man_made"~"tower|obelisk|lighthouse"];
                 );
                 out center 120;
                 """;

            var encodedQuery = Uri.EscapeDataString(overpassQuery);
            var url = $"https://overpass-api.de/api/interpreter?data={encodedQuery}";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!tags.TryGetProperty("name", out var nameProperty))
                {
                    continue;
                }

                var name = nameProperty.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || ShouldExcludeName(name) || HasZooOrSpeciesTags(tags))
                {
                    continue;
                }

                names.Add(name);
                if (names.Count >= 22)
                {
                    break;
                }
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
    private async Task<IReadOnlyList<string>> TryGetLandmarksFromWikipediaAsync(
        GeoCoordinates coordinates,
        int radiusMeters,
        CancellationToken cancellationToken)
    {
        try
        {
            var latitude = coordinates.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
            var longitude = coordinates.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
            var url =
                $"https://en.wikipedia.org/w/api.php?action=query&list=geosearch&gscoord={latitude}|{longitude}&gsradius={Math.Clamp(radiusMeters, 1000, 40000)}&gslimit=36&format=json";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("geosearch", out var geosearchElement) ||
                geosearchElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var item in geosearchElement.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleProperty))
                {
                    continue;
                }

                var name = titleProperty.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || ShouldExcludeName(name))
                {
                    continue;
                }

                names.Add(name);
                if (names.Count >= 14)
                {
                    break;
                }
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> TryGetLandmarksByCityQueryAsync(string cityHint, CancellationToken cancellationToken)
    {
        try
        {
            var searchQuery =
                $"intitle:\"{cityHint}\" landmark OR monument OR tower OR palace OR museum OR harbor OR bridge";
            var url =
                $"https://en.wikipedia.org/w/api.php?action=query&list=search&srnamespace=0&srlimit=30&srsearch={Uri.EscapeDataString(searchQuery)}&format=json";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("search", out var searchElement) ||
                searchElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var item in searchElement.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleProperty))
                {
                    continue;
                }

                var name = titleProperty.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || ShouldExcludeName(name))
                {
                    continue;
                }

                if (!ContainsLandmarkCue(name) && !name.Contains(cityHint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(name);
                if (names.Count >= 12)
                {
                    break;
                }
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryReadLatLonFromFeature(JsonElement feature, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = coordinates.EnumerateArray().Take(2).ToArray();
        if (values.Length < 2)
        {
            return false;
        }

        if (!TryGetDouble(values[0], out longitude) || !TryGetDouble(values[1], out latitude))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var token in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool AreCoordinatesNear(GeoCoordinates first, GeoCoordinates second)
    {
        var latDiff = Math.Abs(first.Latitude - second.Latitude);
        var lonDiff = Math.Abs(first.Longitude - second.Longitude);
        return latDiff <= 0.01 && lonDiff <= 0.01;
    }

    private static string NormalizeCandidateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? NormalizeCandidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return string.Join(
            " ",
            name
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? GetCityHint(string locationInput, string displayName)
    {
        static string? ToCityFragment(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var first = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first) || first.Any(char.IsDigit))
            {
                return null;
            }

            return first;
        }

        return ToCityFragment(locationInput) ?? ToCityFragment(displayName);
    }

    private static bool ContainsLandmarkCue(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("tower") ||
               lower.Contains("palace") ||
               lower.Contains("museum") ||
               lower.Contains("harbor") ||
               lower.Contains("bridge") ||
               lower.Contains("park") ||
               lower.Contains("cathedral") ||
               lower.Contains("statue") ||
               lower.Contains("lookout") ||
               lower.Contains("memorial") ||
               lower.Contains("plaza") ||
               lower.Contains("center") ||
               lower.Contains("beach") ||
               lower.Contains("garden") ||
               lower.Contains("temple");
    }

    private static IReadOnlyList<string> BuildCityFallbackLandmarks(string cityHint)
    {
        var city = cityHint.Trim();
        return new[]
        {
            $"{city} City Hall",
            $"{city} Harbor",
            $"{city} Museum District",
            $"{city} Central Park",
            $"{city} Landmark Tower",
            $"{city} Historic District"
        };
    }

    private static bool ShouldExcludeName(string name)
    {
        if (name.StartsWith("List of ", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("(disambiguation)", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lower = name.ToLowerInvariant();
        if (lower is "tiger" or "flamingo" or "komodo dragon" or "reptile house" or "orangutan")
        {
            return true;
        }

        if (lower.Contains("birds") || lower.Contains("reptile") || lower.Contains("aviary"))
        {
            return true;
        }

        var wordCount = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount == 1 && name.Length < 15;
    }

    private static bool HasZooOrSpeciesTags(JsonElement tags)
    {
        if (tags.TryGetProperty("tourism", out var tourismValue) &&
            string.Equals(tourismValue.GetString(), "zoo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tags.TryGetProperty("attraction", out var attractionValue) &&
            string.Equals(attractionValue.GetString(), "animal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return tags.TryGetProperty("zoo", out _) ||
               tags.TryGetProperty("species", out _) ||
               tags.TryGetProperty("animal", out _) ||
               tags.TryGetProperty("taxon", out _);
    }

    private bool TryGetCachedPoiResults(string cacheKey, out IReadOnlyList<string> results)
    {
        results = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        lock (_poiCacheSync)
        {
            if (!_poiCache.TryGetValue(cacheKey, out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.CachedAtUtc > PoiCacheTtl)
            {
                _poiCache.Remove(cacheKey);
                return false;
            }

            results = entry.Results;
            return true;
        }
    }

    private void CachePoiResults(string cacheKey, IReadOnlyList<string> results)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        var normalized = results
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_poiCacheSync)
        {
            _poiCache[cacheKey] = new PoiCacheEntry(normalized, DateTimeOffset.UtcNow);
            if (_poiCache.Count > 160)
            {
                PrunePoiCacheUnsafe();
            }
        }
    }

    private void PrunePoiCacheUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _poiCache
            .Where(pair => now - pair.Value.CachedAtUtc > PoiCacheTtl)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _poiCache.Remove(key);
        }

        if (_poiCache.Count <= 160)
        {
            return;
        }

        var oldest = _poiCache
            .OrderBy(pair => pair.Value.CachedAtUtc)
            .Take(_poiCache.Count - 160)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in oldest)
        {
            _poiCache.Remove(key);
        }
    }

    private static string BuildPoiCacheKey(
        GeoCoordinates coordinates,
        string locationInput,
        bool hasLatLngKey,
        IReadOnlyCollection<string> excludedKeys,
        int maxResults)
    {
        var normalizedLocation = NormalizeCandidateKey(locationInput);
        var lat = Math.Round(coordinates.Latitude, 4).ToString("0.####", CultureInfo.InvariantCulture);
        var lon = Math.Round(coordinates.Longitude, 4).ToString("0.####", CultureInfo.InvariantCulture);
        var excludedToken = excludedKeys.Count == 0
            ? "-"
            : string.Join(",", excludedKeys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return $"loc={normalizedLocation}|coord={lat},{lon}|latlng={(hasLatLngKey ? "1" : "0")}|exclude={excludedToken}|max={maxResults}";
    }

    private static string DescribeSources(IReadOnlyDictionary<string, PoiCandidate> candidates)
    {
        var sourceCounts = candidates
            .SelectMany(pair => pair.Value.Sources)
            .GroupBy(source => source, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();

        return sourceCounts.Length == 0 ? "none" : string.Join(", ", sourceCounts);
    }

    private sealed class PoiCandidate
    {
        public PoiCandidate(string name, int score, string source)
        {
            Name = name;
            Score = score;
            Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        }

        public string Name { get; set; }

        public int Score { get; set; }

        public HashSet<string> Sources { get; }
    }

    private sealed record LatLngSeedCandidates(
        IReadOnlyList<string> NearbyNames);

    private sealed record PoiCacheEntry(
        IReadOnlyList<string> Results,
        DateTimeOffset CachedAtUtc);
}
