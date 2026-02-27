namespace Malie.Models;

public sealed record LogFilterSettings(
    bool ShowSystem,
    bool ShowWeather,
    bool ShowLatLng,
    bool ShowMeshy,
    bool ShowHighDetail,
    bool ShowRendererDebug,
    bool ShowErrors)
{
    public static LogFilterSettings CreateDefault() => new(
        ShowSystem: true,
        ShowWeather: true,
        ShowLatLng: true,
        ShowMeshy: true,
        ShowHighDetail: true,
        ShowRendererDebug: false,
        ShowErrors: true);

    public bool IsEnabled(DebugLogCategory category)
    {
        return category switch
        {
            DebugLogCategory.System => ShowSystem,
            DebugLogCategory.Weather => ShowWeather,
            DebugLogCategory.LatLng => ShowLatLng,
            DebugLogCategory.Meshy => ShowMeshy,
            DebugLogCategory.HighDetail => ShowHighDetail,
            DebugLogCategory.RendererDebug => ShowRendererDebug,
            DebugLogCategory.Error => ShowErrors,
            _ => true
        };
    }
}
