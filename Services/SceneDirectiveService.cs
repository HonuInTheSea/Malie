using Malie.Models;

namespace Malie.Services;

public sealed class SceneDirectiveService
{
    public const string CanonicalIsometricPromptTemplate =
        "Create a clear 45 degree top-down isometric miniature 3D cartoon representation of the location showcasing recognizable landmarks and architecture with soft refined textures using realistic PBR-style materials, gentle lifelike lighting and shadows, and weather-aware atmosphere in a clean composition.";

    public Task<SceneGenerationResult> GenerateFastSceneAsync(
        WeatherSnapshot weather,
        IReadOnlyList<string> pointsOfInterest,
        IReadOnlyList<PointOfInterestImage> pointOfInterestImages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directive = BuildFallback(weather, pointsOfInterest, pointOfInterestImages, highDetail: false);
        return Task.FromResult(new SceneGenerationResult(
            directive,
            "Fast procedural scene generated from weather and POI metadata."));
    }

    public Task<SceneGenerationResult> GenerateHighDetailSceneAsync(
        WeatherSnapshot weather,
        IReadOnlyList<string> pointsOfInterest,
        IReadOnlyList<PointOfInterestImage> pointOfInterestImages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directive = BuildFallback(weather, pointsOfInterest, pointOfInterestImages, highDetail: true);
        return Task.FromResult(new SceneGenerationResult(
            directive,
            "High-detail procedural scene generated from weather and POI metadata."));
    }

    public static SceneDirective BuildFallback(
        WeatherSnapshot weather,
        IReadOnlyList<string>? pointsOfInterest = null,
        IReadOnlyList<PointOfInterestImage>? pointOfInterestImages = null,
        bool highDetail = false)
    {
        var weatherText = $"{weather.ShortForecast} {weather.DetailedForecast}".ToLowerInvariant();
        var isSnow = weatherText.Contains("snow") || weatherText.Contains("blizzard") || weatherText.Contains("flurr");
        var isRain = weatherText.Contains("rain") || weatherText.Contains("drizzle") || weatherText.Contains("thunder");
        var isFog = weatherText.Contains("fog") || weatherText.Contains("haze") || weatherText.Contains("smoke");

        var accent = isSnow ? "snow" : isRain ? "rain" : isFog ? "fog" : "clear";
        var precipitation = isSnow || isRain ? 0.78 : 0.0;
        var cloudCoverage = weatherText.Contains("clear") ? 0.12 : weatherText.Contains("partly") ? 0.42 : 0.74;
        var windFactor = weather.Wind.Contains("mph", StringComparison.OrdinalIgnoreCase) ? 0.58 : 0.28;

        var timeOfDay = ResolveTimeOfDay(weather.TimeZoneId);
        var seed = Math.Abs(HashCode.Combine(weather.LocationName, weather.ShortForecast, DateTimeOffset.UtcNow.Date));

        var props = new List<string>
        {
            "mid-rise buildings",
            "layered rooftops",
            "tree clusters",
            "road markings",
            "water edge",
            highDetail ? "micro facade details" : "soft cartoon facades",
            highDetail ? "ambient occlusion shadows" : "stylized soft shadows"
        };

        if (pointsOfInterest is not null)
        {
            foreach (var pointOfInterest in pointsOfInterest.Where(point => !string.IsNullOrWhiteSpace(point)).Take(6))
            {
                props.Add($"landmark: {pointOfInterest.Trim()}");
            }
        }

        if (pointOfInterestImages is not null)
        {
            foreach (var image in pointOfInterestImages.Where(image => !string.IsNullOrWhiteSpace(image.Name)).Take(3))
            {
                props.Add($"image-angle-ref: {image.Name.Trim()}");
            }
        }

        return new SceneDirective(
            seed == 0 ? 1337 : seed,
            timeOfDay,
            highDetail ? "#3E7BD4" : "#4C8EE8",
            highDetail ? "#62B698" : "#7BC6A4",
            highDetail ? "#E9B96A" : "#F2C879",
            props,
            precipitation,
            cloudCoverage,
            windFactor,
            highDetail
                ? "cinematic drifting camera with layered parallax motion"
                : "ambient looping motion with gentle camera drift",
            accent);
    }

    private static string ResolveTimeOfDay(string? timeZoneId)
    {
        DateTimeOffset current;
        try
        {
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                current = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
            }
            else
            {
                current = DateTimeOffset.Now;
            }
        }
        catch
        {
            current = DateTimeOffset.Now;
        }

        return current.Hour switch
        {
            >= 5 and < 8 => "dawn",
            >= 8 and < 17 => "day",
            >= 17 and < 20 => "dusk",
            _ => "night"
        };
    }
}
