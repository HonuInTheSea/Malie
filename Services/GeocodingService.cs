using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Malie.Models;

namespace Malie.Services;

public sealed class GeocodingService
{
    private static readonly Regex ZipPattern = new(@"^\d{5}(?:-\d{4})?$", RegexOptions.Compiled);
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromMinutes(20);

    private readonly HttpClient _httpClient;
    private readonly object _resolveCacheSync = new();
    private readonly Dictionary<string, ResolveCacheEntry> _resolveCache = new(StringComparer.OrdinalIgnoreCase);

    public GeocodingService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Malie/1.0");
    }

    public async Task<GeoCoordinates> ResolveAsync(string locationInput, CancellationToken cancellationToken)
    {
        var normalized = locationInput.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Please provide an address, city, or ZIP code.");
        }

        var cacheKey = NormalizeQueryKey(normalized);
        if (TryGetCachedResolve(cacheKey, out var cached))
        {
            return cached;
        }

        GeoCoordinates resolved;
        if (ZipPattern.IsMatch(normalized))
        {
            resolved = await ResolveZipAsync(normalized[..5], cancellationToken);
            var zipTimeZone = await TryResolveTimeZoneAsync(resolved.Latitude, resolved.Longitude, cancellationToken);
            return CacheResolved(cacheKey, resolved with { TimeZoneId = zipTimeZone });
        }

        if (LooksLikeCityQuery(normalized))
        {
            var cityResult = await TryResolveCityAsync(normalized, cancellationToken);
            if (cityResult is not null)
            {
                var cityTimeZone = await TryResolveTimeZoneAsync(cityResult.Latitude, cityResult.Longitude, cancellationToken);
                return CacheResolved(cacheKey, cityResult with { TimeZoneId = cityTimeZone });
            }
        }

        try
        {
            resolved = await ResolveAddressAsync(normalized, cancellationToken);
            var addressTimeZone = await TryResolveTimeZoneAsync(resolved.Latitude, resolved.Longitude, cancellationToken);
            return CacheResolved(cacheKey, resolved with { TimeZoneId = addressTimeZone });
        }
        catch (Exception)
        {
            var cityFallback = await TryResolveCityAsync(normalized, cancellationToken);
            if (cityFallback is not null)
            {
                var fallbackTimeZone = await TryResolveTimeZoneAsync(cityFallback.Latitude, cityFallback.Longitude, cancellationToken);
                return CacheResolved(cacheKey, cityFallback with { TimeZoneId = fallbackTimeZone });
            }

            throw new InvalidOperationException("Location could not be geocoded. Try 'City, ST', full address, or ZIP code.");
        }
    }

    private async Task<GeoCoordinates> ResolveZipAsync(string zipCode, CancellationToken cancellationToken)
    {
        var url = $"https://api.zippopotam.us/us/{zipCode}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("places", out var places) || places.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("ZIP code not found.");
        }

        var place = places[0];
        var latitude = ParseNumericProperty(place, "latitude");
        var longitude = ParseNumericProperty(place, "longitude");
        var placeName = TryReadStringProperty(place, "place name") ?? zipCode;
        var stateAbbreviation = TryReadStringProperty(place, "state abbreviation") ?? "US";

        return new GeoCoordinates(latitude, longitude, $"{placeName}, {stateAbbreviation}");
    }

    private async Task<GeoCoordinates> ResolveAddressAsync(string address, CancellationToken cancellationToken)
    {
        var encodedAddress = Uri.EscapeDataString(address);
        var url = $"https://geocoding.geo.census.gov/geocoder/locations/onelineaddress?address={encodedAddress}&benchmark=Public_AR_Current&format=json";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("addressMatches", out var matches) ||
            matches.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Address could not be geocoded.");
        }

        var bestMatch = matches[0];
        if (!bestMatch.TryGetProperty("coordinates", out var coordinates))
        {
            throw new InvalidOperationException("Address coordinates were missing in geocoding response.");
        }

        var longitude = ParseNumericProperty(coordinates, "x");
        var latitude = ParseNumericProperty(coordinates, "y");
        var displayName = TryReadStringProperty(bestMatch, "matchedAddress") ?? address;

        return new GeoCoordinates(latitude, longitude, displayName);
    }

    private async Task<GeoCoordinates?> TryResolveCityAsync(string cityInput, CancellationToken cancellationToken)
    {
        var encodedCity = Uri.EscapeDataString(cityInput);
        var url =
            $"https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&limit=8&q={encodedCity}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement? bestCandidate = null;
        var bestScore = int.MinValue;
        foreach (var candidate in document.RootElement.EnumerateArray())
        {
            var score = 0;

            var type = TryReadStringProperty(candidate, "type")?.ToLowerInvariant() ?? string.Empty;
            if (type is "city" or "town" or "village" or "borough" or "municipality")
            {
                score += 40;
            }
            else if (type is "county" or "administrative")
            {
                score += 12;
            }

            var category = TryReadStringProperty(candidate, "category")?.ToLowerInvariant() ?? string.Empty;
            if (category == "boundary")
            {
                score += 8;
            }

            var classValue = TryReadStringProperty(candidate, "class")?.ToLowerInvariant() ?? string.Empty;
            if (classValue == "place")
            {
                score += 12;
            }

            if (candidate.TryGetProperty("address", out var addressElement) && addressElement.ValueKind == JsonValueKind.Object)
            {
                if (HasAnyAddressProperty(addressElement, "city", "town", "village", "municipality"))
                {
                    score += 25;
                }

                var countryCode = TryReadStringProperty(addressElement, "country_code");
                if (string.Equals(countryCode, "us", StringComparison.OrdinalIgnoreCase))
                {
                    score += 20;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            return null;
        }

        var element = bestCandidate.Value;
        var latitude = ParseNumericProperty(element, "lat");
        var longitude = ParseNumericProperty(element, "lon");
        var displayName = BuildCityDisplayName(element, cityInput);
        return new GeoCoordinates(latitude, longitude, displayName);
    }

    private async Task<string?> TryResolveTimeZoneAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            var url =
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString("0.######", CultureInfo.InvariantCulture)}&longitude={longitude.ToString("0.######", CultureInfo.InvariantCulture)}&current=temperature_2m&timezone=auto&forecast_days=1";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var timeZone = TryReadStringProperty(document.RootElement, "timezone");
            return string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static double ParseNumericProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"Missing numeric property '{propertyName}'.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsedNumeric))
        {
            return parsedNumeric;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedString))
        {
            return parsedString;
        }

        throw new InvalidOperationException($"Property '{propertyName}' is not numeric.");
    }

    private static bool LooksLikeCityQuery(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (input.Any(char.IsDigit))
        {
            return false;
        }

        var lower = input.ToLowerInvariant();
        if (lower.Contains("street") ||
            lower.Contains(" st ") ||
            lower.Contains(" avenue") ||
            lower.Contains(" ave ") ||
            lower.Contains(" road") ||
            lower.Contains(" rd ") ||
            lower.Contains(" lane") ||
            lower.Contains(" ln ") ||
            lower.Contains(" boulevard") ||
            lower.Contains(" blvd ") ||
            lower.Contains(" drive") ||
            lower.Contains(" dr ") ||
            lower.Contains(" suite") ||
            lower.Contains(" apt "))
        {
            return false;
        }

        return true;
    }

    private static bool HasAnyAddressProperty(JsonElement addressElement, params string[] names)
    {
        foreach (var name in names)
        {
            if (addressElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildCityDisplayName(JsonElement candidate, string fallbackInput)
    {
        if (candidate.TryGetProperty("address", out var addressElement) && addressElement.ValueKind == JsonValueKind.Object)
        {
            var city = ReadFirstNonEmpty(addressElement, "city", "town", "village", "municipality", "county");
            var state = ReadFirstNonEmpty(addressElement, "state", "region");
            if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
            {
                return $"{city}, {state}";
            }

            if (!string.IsNullOrWhiteSpace(city))
            {
                return city!;
            }
        }

        var displayName = TryReadStringProperty(candidate, "display_name");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Split(',')[0].Trim();
        }

        return fallbackInput;
    }

    private static string? ReadFirstNonEmpty(JsonElement addressElement, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryReadStringProperty(addressElement, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private bool TryGetCachedResolve(string cacheKey, out GeoCoordinates coordinates)
    {
        coordinates = null!;
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        lock (_resolveCacheSync)
        {
            if (!_resolveCache.TryGetValue(cacheKey, out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.CachedAtUtc > ResolveCacheTtl)
            {
                _resolveCache.Remove(cacheKey);
                return false;
            }

            coordinates = entry.Value;
            return true;
        }
    }

    private GeoCoordinates CacheResolved(string cacheKey, GeoCoordinates resolved)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return resolved;
        }

        lock (_resolveCacheSync)
        {
            _resolveCache[cacheKey] = new ResolveCacheEntry(resolved, DateTimeOffset.UtcNow);
            if (_resolveCache.Count > 256)
            {
                PruneResolveCacheUnsafe();
            }
        }

        return resolved;
    }

    private void PruneResolveCacheUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _resolveCache
            .Where(pair => now - pair.Value.CachedAtUtc > ResolveCacheTtl)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expiredKeys)
        {
            _resolveCache.Remove(key);
        }

        if (_resolveCache.Count <= 256)
        {
            return;
        }

        var oldestKeys = _resolveCache
            .OrderBy(pair => pair.Value.CachedAtUtc)
            .Take(_resolveCache.Count - 256)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in oldestKeys)
        {
            _resolveCache.Remove(key);
        }
    }

    private static string NormalizeQueryKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            input
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private sealed record ResolveCacheEntry(GeoCoordinates Value, DateTimeOffset CachedAtUtc);
}
