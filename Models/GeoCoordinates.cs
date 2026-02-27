namespace Malie.Models;

public sealed record GeoCoordinates(
    double Latitude,
    double Longitude,
    string DisplayName,
    string? TimeZoneId = null);
