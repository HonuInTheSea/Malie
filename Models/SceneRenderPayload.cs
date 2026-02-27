namespace Malie.Models;

public sealed record SceneRenderPayload(
    string LocationQuery,
    GeoCoordinates Coordinates,
    WeatherSnapshot Weather,
    string TemperatureUnit,
    string WallpaperBackgroundColor,
    string WallpaperBackgroundImageUrl,
    string WallpaperBackgroundDisplayMode,
    bool UseAnimatedAiBackground,
    bool ShowWallpaperStatsOverlay,
    WallpaperTextStyleSettings WallpaperTextStyle,
    IReadOnlyList<string> PointsOfInterest,
    IReadOnlyList<PointOfInterestImage> PointOfInterestImages,
    IReadOnlyList<PointOfInterestMesh> PointOfInterestMeshes,
    GlbOrientationSettings GlbOrientation,
    MeshyViewerSettings MeshyViewer,
    SceneDirective Scene,
    DateTimeOffset GeneratedAt);
