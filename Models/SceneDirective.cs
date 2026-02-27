namespace Malie.Models;

public sealed record SceneDirective(
    int Seed,
    string TimeOfDay,
    string PaletteA,
    string PaletteB,
    string PaletteC,
    IReadOnlyList<string> Props,
    double PrecipitationIntensity,
    double CloudCoverage,
    double WindFactor,
    string AnimationMood,
    string AccentEffect);
