namespace Malie.Models;

public sealed record WeatherAlertItem(
    string EventName,
    string Severity,
    string Headline,
    string Description);
