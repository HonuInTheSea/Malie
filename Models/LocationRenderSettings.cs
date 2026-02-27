namespace Malie.Models;

public sealed record LocationRenderSettings(
    string LocationQuery,
    string MeshyApiKey,
    string WeatherApiKey,
    string LatLngApiKey,
    TemperatureScale TemperatureScale,
    string WallpaperMonitorDeviceName,
    string WallpaperBackgroundColor,
    string WallpaperBackgroundImageFileName,
    string WallpaperBackgroundDisplayMode,
    bool UseAnimatedAiBackground,
    WallpaperTextStyleSettings WallpaperTextStyle,
    bool ShowWallpaperStatsOverlay,
    bool ShowDebugLogPane,
    LogFilterSettings LogFilters,
    GlbOrientationSettings GlbOrientation);
