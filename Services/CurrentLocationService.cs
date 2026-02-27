using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Malie.Services;

public sealed class CurrentLocationService
{
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    public CurrentLocationService(HttpClient httpClient, Action<string>? log = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Malie/1.0");
        }

        _log = log;
    }

    public async Task<string> ResolveLocationQueryAsync(CancellationToken cancellationToken)
    {
        var fromIpApiCo = await TryResolveViaIpApiCoAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromIpApiCo))
        {
            return fromIpApiCo;
        }

        var fromIpApiCom = await TryResolveViaIpApiComAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromIpApiCom))
        {
            return fromIpApiCom;
        }

        throw new InvalidOperationException(
            "Could not detect current location from network providers. Enter city, address, or ZIP code manually.");
    }

    private async Task<string?> TryResolveViaIpApiCoAsync(CancellationToken cancellationToken)
    {
        const string provider = "ipapi.co";
        const string url = "https://ipapi.co/json/";
        try
        {
            _log?.Invoke($"Current location request -> {provider}");
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"Current location provider '{provider}' failed: HTTP {(int)response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var city = ReadString(root, "city");
            var region = FirstNonEmpty(ReadString(root, "region_code"), ReadString(root, "region"));
            var country = FirstNonEmpty(ReadString(root, "country_code"), ReadString(root, "country_name"));
            var latitude = ReadDouble(root, "latitude");
            var longitude = ReadDouble(root, "longitude");

            var query = BuildLocationQuery(city, region, country);
            if (string.IsNullOrWhiteSpace(query))
            {
                _log?.Invoke($"Current location provider '{provider}' returned no usable city/region.");
                return null;
            }

            _log?.Invoke(
                $"Current location provider '{provider}' resolved '{query}' at " +
                $"{FormatCoordinate(latitude)}, {FormatCoordinate(longitude)}.");
            return query;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Current location provider '{provider}' exception: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> TryResolveViaIpApiComAsync(CancellationToken cancellationToken)
    {
        const string provider = "ip-api.com";
        const string url = "http://ip-api.com/json/?fields=status,message,city,regionName,countryCode,lat,lon";
        try
        {
            _log?.Invoke($"Current location request -> {provider}");
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"Current location provider '{provider}' failed: HTTP {(int)response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var status = ReadString(root, "status");
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = ReadString(root, "message");
                _log?.Invoke($"Current location provider '{provider}' returned '{status ?? "unknown"}': {error ?? "n/a"}.");
                return null;
            }

            var city = ReadString(root, "city");
            var region = ReadString(root, "regionName");
            var country = ReadString(root, "countryCode");
            var latitude = ReadDouble(root, "lat");
            var longitude = ReadDouble(root, "lon");
            var query = BuildLocationQuery(city, region, country);
            if (string.IsNullOrWhiteSpace(query))
            {
                _log?.Invoke($"Current location provider '{provider}' returned no usable city/region.");
                return null;
            }

            _log?.Invoke(
                $"Current location provider '{provider}' resolved '{query}' at " +
                $"{FormatCoordinate(latitude)}, {FormatCoordinate(longitude)}.");
            return query;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"Current location provider '{provider}' exception: {ex.Message}");
            return null;
        }
    }

    private static string BuildLocationQuery(string? city, string? region, string? country)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            return $"{city.Trim()}, {region.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            return $"{city.Trim()}, {country.Trim()}";
        }

        return city.Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return parsedValue;
        }

        return null;
    }

    private static string FormatCoordinate(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.####", CultureInfo.InvariantCulture)
            : "n/a";
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}
