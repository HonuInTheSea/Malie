namespace Malie.Models;

public sealed record WeatherSnapshot(
    string LocationName,
    string ShortForecast,
    string DetailedForecast,
    double? TemperatureF,
    double? TemperatureC,
    string Wind,
    int? RelativeHumidityPercent,
    string? IconUrl,
    string? TimeZoneId,
    IReadOnlyList<WeatherAlertItem> Alerts,
    DateTimeOffset CapturedAt)
{
    public double? GetTemperature(TemperatureScale scale)
    {
        return scale switch
        {
            TemperatureScale.Celsius => TemperatureC ?? (TemperatureF.HasValue ? ConvertFahrenheitToCelsius(TemperatureF.Value) : null),
            _ => TemperatureF ?? (TemperatureC.HasValue ? ConvertCelsiusToFahrenheit(TemperatureC.Value) : null)
        };
    }

    public static double ConvertCelsiusToFahrenheit(double celsius)
    {
        return (celsius * 9d / 5d) + 32d;
    }

    public static double ConvertFahrenheitToCelsius(double fahrenheit)
    {
        return (fahrenheit - 32d) * 5d / 9d;
    }
}
