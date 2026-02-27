using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Malie.Models;

namespace Malie.Services;

public sealed class WeatherApiService
{
    private const string WeatherApiBaseUrl = "https://api.weatherapi.com/v1";
    private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromSeconds(75);

    private readonly HttpClient _httpClient;
    private readonly Action<string>? _debugLog;
    private readonly object _snapshotCacheSync = new();
    private readonly Dictionary<string, SnapshotCacheEntry> _snapshotCache = new(StringComparer.Ordinal);

    public WeatherApiService(HttpClient httpClient, Action<string>? debugLog = null)
    {
        _httpClient = httpClient;
        _debugLog = debugLog;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<WeatherSnapshot> GetSnapshotAsync(
        GeoCoordinates coordinates,
        string weatherApiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(weatherApiKey))
        {
            throw new InvalidOperationException("Weather API key is required.");
        }

        var cacheKey = BuildSnapshotCacheKey(coordinates);
        if (TryGetCachedSnapshot(cacheKey, out var cached))
        {
            EmitDebug(
                $"WeatherAPI cache hit for {coordinates.DisplayName} " +
                $"({coordinates.Latitude:0.####}, {coordinates.Longitude:0.####}).");
            return cached;
        }

        var query = $"{coordinates.Latitude.ToString("0.####", CultureInfo.InvariantCulture)},{coordinates.Longitude.ToString("0.####", CultureInfo.InvariantCulture)}";
        var requestUrl =
            $"{WeatherApiBaseUrl}/forecast.json?key={Uri.EscapeDataString(weatherApiKey)}&q={Uri.EscapeDataString(query)}&days=1&aqi=no&alerts=yes";
        EmitDebug(
            $"WeatherAPI request: /v1/forecast.json for {coordinates.DisplayName} " +
            $"({coordinates.Latitude:0.####}, {coordinates.Longitude:0.####}) with alerts enabled.");

        using var forecastDocument = await GetJsonAsync(requestUrl, cancellationToken);
        if (TryReadWeatherApiError(forecastDocument.RootElement, out var weatherApiError))
        {
            EmitDebug($"WeatherAPI returned error payload: {weatherApiError}");
            throw new InvalidOperationException($"Weather API request failed: {weatherApiError}");
        }

        if (!forecastDocument.RootElement.TryGetProperty("location", out var locationElement) ||
            !forecastDocument.RootElement.TryGetProperty("current", out var currentElement))
        {
            throw new InvalidOperationException("Weather API response did not include expected location/current payload.");
        }

        var forecastDayElement = TryReadForecastDay(forecastDocument.RootElement);
        var forecastDayCondition = TryReadNestedString(forecastDayElement, "day", "condition", "text");
        var shortForecast = FirstNonEmpty(
            TryReadNestedString(currentElement, "condition", "text"),
            forecastDayCondition,
            "Unavailable");

        var detailedForecast = BuildDetailedForecast(shortForecast, forecastDayElement);
        var snapshot = new WeatherSnapshot(
            LocationName: ReadLocationName(locationElement, coordinates),
            ShortForecast: shortForecast,
            DetailedForecast: detailedForecast,
            TemperatureF: TryReadDouble(currentElement, "temp_f"),
            TemperatureC: TryReadDouble(currentElement, "temp_c"),
            Wind: BuildWindText(
                TryReadDouble(currentElement, "wind_mph"),
                TryReadString(currentElement, "wind_dir")),
            RelativeHumidityPercent: TryReadRoundedInt(currentElement, "humidity"),
            IconUrl: NormalizeIconUrl(TryReadNestedString(currentElement, "condition", "icon")),
            TimeZoneId: FirstNonEmpty(TryReadString(locationElement, "tz_id"), coordinates.TimeZoneId),
            Alerts: ReadAlerts(forecastDocument.RootElement),
            CapturedAt: DateTimeOffset.UtcNow);

        CacheSnapshot(cacheKey, snapshot);
        EmitDebug(
            $"WeatherAPI snapshot: {snapshot.LocationName} | {snapshot.ShortForecast} | " +
            $"TempF={snapshot.TemperatureF?.ToString("0.#", CultureInfo.InvariantCulture) ?? "N/A"} | " +
            $"Alerts={snapshot.Alerts.Count}.");
        return snapshot;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            EmitDebug(
                $"WeatherAPI HTTP error {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseText, 200)}");
            throw new HttpRequestException(
                $"Weather API HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseText, 200)}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static string ReadLocationName(JsonElement locationElement, GeoCoordinates coordinates)
    {
        var city = TryReadString(locationElement, "name");
        var region = TryReadString(locationElement, "region");
        var country = TryReadString(locationElement, "country");

        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(region))
        {
            return $"{city}, {region}";
        }

        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(country))
        {
            return $"{city}, {country}";
        }

        return FirstNonEmpty(city, coordinates.DisplayName, "Unknown location");
    }

    private static JsonElement? TryReadForecastDay(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("forecast", out var forecastElement) ||
            !forecastElement.TryGetProperty("forecastday", out var forecastDaysElement) ||
            forecastDaysElement.ValueKind != JsonValueKind.Array ||
            forecastDaysElement.GetArrayLength() == 0)
        {
            return null;
        }

        return forecastDaysElement[0];
    }

    private static IReadOnlyList<WeatherAlertItem> ReadAlerts(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("alerts", out var alertsElement) ||
            !alertsElement.TryGetProperty("alert", out var alertListElement) ||
            alertListElement.ValueKind != JsonValueKind.Array ||
            alertListElement.GetArrayLength() == 0)
        {
            return Array.Empty<WeatherAlertItem>();
        }

        var alerts = new List<WeatherAlertItem>();
        var maxAlerts = Math.Min(alertListElement.GetArrayLength(), 5);
        for (var index = 0; index < maxAlerts; index++)
        {
            var alert = alertListElement[index];
            alerts.Add(new WeatherAlertItem(
                EventName: FirstNonEmpty(TryReadString(alert, "event"), TryReadString(alert, "headline"), "Weather Alert"),
                Severity: FirstNonEmpty(TryReadString(alert, "severity"), TryReadString(alert, "urgency"), "Unknown"),
                Headline: FirstNonEmpty(TryReadString(alert, "headline"), TryReadString(alert, "event"), "No headline"),
                Description: FirstNonEmpty(
                    TryReadString(alert, "desc"),
                    TryReadString(alert, "instruction"),
                    TryReadString(alert, "note"),
                    "No details provided")));
        }

        return alerts;
    }

    private static string BuildDetailedForecast(string shortForecast, JsonElement? forecastDayElement)
    {
        if (forecastDayElement is null || forecastDayElement.Value.ValueKind != JsonValueKind.Object)
        {
            return shortForecast;
        }

        var dayElement = forecastDayElement.Value.TryGetProperty("day", out var dayValue)
            ? dayValue
            : default;

        var conditionText = TryReadNestedString(dayElement, "condition", "text");
        var rainChance = TryReadRoundedInt(dayElement, "daily_chance_of_rain");
        var snowChance = TryReadRoundedInt(dayElement, "daily_chance_of_snow");
        var maxWindMph = TryReadDouble(dayElement, "maxwind_mph");

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(conditionText) &&
            !string.Equals(conditionText, shortForecast, StringComparison.OrdinalIgnoreCase))
        {
            details.Add(conditionText);
        }

        if (rainChance.HasValue)
        {
            details.Add($"Chance of rain: {rainChance.Value}%");
        }

        if (snowChance.HasValue)
        {
            details.Add($"Chance of snow: {snowChance.Value}%");
        }

        if (maxWindMph.HasValue && maxWindMph.Value > 0)
        {
            details.Add($"Max wind: {Math.Round(maxWindMph.Value)} mph");
        }

        if (details.Count == 0)
        {
            return shortForecast;
        }

        return $"{shortForecast}. {string.Join(" | ", details)}";
    }

    private static string BuildWindText(double? windMph, string? windDirection)
    {
        if (!windMph.HasValue || windMph.Value <= 0.1)
        {
            return "Calm";
        }

        var direction = string.IsNullOrWhiteSpace(windDirection) ? "N" : windDirection.Trim();
        return $"{direction} {Math.Round(windMph.Value)} mph";
    }

    private static bool TryReadWeatherApiError(JsonElement rootElement, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!rootElement.TryGetProperty("error", out var errorElement) || errorElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var code = TryReadString(errorElement, "code");
        var message = TryReadString(errorElement, "message");
        errorMessage = string.IsNullOrWhiteSpace(code)
            ? FirstNonEmpty(message, "Unknown error")
            : $"{code}: {FirstNonEmpty(message, "Unknown error")}";
        return true;
    }

    private static string? NormalizeIconUrl(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return null;
        }

        var trimmed = iconUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{trimmed}";
        }

        return trimmed;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => propertyValue.GetString(),
            JsonValueKind.Number => propertyValue.ToString(),
            _ => propertyValue.ToString()
        };
    }

    private static string? TryReadNestedString(JsonElement? element, params string[] path)
    {
        if (!element.HasValue)
        {
            return null;
        }

        return TryReadNestedString(element.Value, path);
    }

    private static string? TryReadNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null => null,
            _ => current.ToString()
        };
    }

    private static double? TryReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind switch
        {
            JsonValueKind.Number when propertyValue.TryGetDouble(out var numericValue) => numericValue,
            JsonValueKind.String when double.TryParse(propertyValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringValue) => stringValue,
            _ => null
        };
    }

    private static int? TryReadRoundedInt(JsonElement element, string propertyName)
    {
        var value = TryReadDouble(element, propertyName);
        return value.HasValue ? (int?)Math.Round(value.Value) : null;
    }

    private bool TryGetCachedSnapshot(string cacheKey, out WeatherSnapshot snapshot)
    {
        snapshot = null!;
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        lock (_snapshotCacheSync)
        {
            if (!_snapshotCache.TryGetValue(cacheKey, out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.CachedAtUtc > SnapshotCacheTtl)
            {
                _snapshotCache.Remove(cacheKey);
                return false;
            }

            snapshot = entry.Snapshot;
            return true;
        }
    }

    private void CacheSnapshot(string cacheKey, WeatherSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        lock (_snapshotCacheSync)
        {
            _snapshotCache[cacheKey] = new SnapshotCacheEntry(snapshot, DateTimeOffset.UtcNow);
            if (_snapshotCache.Count > 128)
            {
                PruneSnapshotCacheUnsafe();
            }
        }
    }

    private void PruneSnapshotCacheUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _snapshotCache
            .Where(pair => now - pair.Value.CachedAtUtc > SnapshotCacheTtl)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            _snapshotCache.Remove(key);
        }

        if (_snapshotCache.Count <= 128)
        {
            return;
        }

        var oldest = _snapshotCache
            .OrderBy(pair => pair.Value.CachedAtUtc)
            .Take(_snapshotCache.Count - 128)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in oldest)
        {
            _snapshotCache.Remove(key);
        }
    }

    private static string BuildSnapshotCacheKey(GeoCoordinates coordinates)
    {
        var lat = Math.Round(coordinates.Latitude, 4);
        var lon = Math.Round(coordinates.Longitude, 4);
        return $"{lat.ToString("0.####", CultureInfo.InvariantCulture)}:{lon.ToString("0.####", CultureInfo.InvariantCulture)}";
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return $"{trimmed[..maxLength]}...";
    }

    private void EmitDebug(string message)
    {
        try
        {
            _debugLog?.Invoke(message);
        }
        catch
        {
            // Logging must not break weather flow.
        }
    }

    private sealed record SnapshotCacheEntry(WeatherSnapshot Snapshot, DateTimeOffset CachedAtUtc);
}
