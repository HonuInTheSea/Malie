namespace Malie.Models;

public sealed record WallpaperTextStyleSettings(
    string TimeFontFamily,
    string LocationFontFamily,
    string DateFontFamily,
    string TemperatureFontFamily,
    string SummaryFontFamily,
    string PoiFontFamily,
    string AlertsFontFamily,
    double TimeFontSize,
    double LocationFontSize,
    double DateFontSize,
    double TemperatureFontSize,
    double SummaryFontSize,
    double PoiFontSize,
    double AlertsFontSize)
{
    private const string DefaultFont = "Segoe UI";
    private const double MinFontSize = 8;
    private const double MaxFontSize = 144;

    public static WallpaperTextStyleSettings Default { get; } = new(
        TimeFontFamily: DefaultFont,
        LocationFontFamily: DefaultFont,
        DateFontFamily: DefaultFont,
        TemperatureFontFamily: DefaultFont,
        SummaryFontFamily: DefaultFont,
        PoiFontFamily: DefaultFont,
        AlertsFontFamily: DefaultFont,
        TimeFontSize: 58,
        LocationFontSize: 54,
        DateFontSize: 14,
        TemperatureFontSize: 34,
        SummaryFontSize: 14,
        PoiFontSize: 14,
        AlertsFontSize: 14);

    public WallpaperTextStyleSettings Normalize()
    {
        return new WallpaperTextStyleSettings(
            NormalizeFontFamily(TimeFontFamily),
            NormalizeFontFamily(LocationFontFamily),
            NormalizeFontFamily(DateFontFamily),
            NormalizeFontFamily(TemperatureFontFamily),
            NormalizeFontFamily(SummaryFontFamily),
            NormalizeFontFamily(PoiFontFamily),
            NormalizeFontFamily(AlertsFontFamily),
            NormalizeFontSize(TimeFontSize, Default.TimeFontSize),
            NormalizeFontSize(LocationFontSize, Default.LocationFontSize),
            NormalizeFontSize(DateFontSize, Default.DateFontSize),
            NormalizeFontSize(TemperatureFontSize, Default.TemperatureFontSize),
            NormalizeFontSize(SummaryFontSize, Default.SummaryFontSize),
            NormalizeFontSize(PoiFontSize, Default.PoiFontSize),
            NormalizeFontSize(AlertsFontSize, Default.AlertsFontSize));
    }

    public static string NormalizeFontFamily(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return DefaultFont;
        }

        // Keep this strict to avoid CSS/script injection in renderer.
        var filtered = new string(trimmed.Where(c =>
            char.IsLetterOrDigit(c) ||
            c == ' ' ||
            c == '-' ||
            c == '_' ||
            c == '.' ||
            c == ',' ||
            c == '\'' ||
            c == '"' ||
            c == '(' ||
            c == ')').ToArray()).Trim();

        return string.IsNullOrWhiteSpace(filtered) ? DefaultFont : filtered;
    }

    private static double NormalizeFontSize(double value, double fallback)
    {
        var source = double.IsFinite(value) ? value : fallback;
        var clamped = Math.Clamp(source, MinFontSize, MaxFontSize);
        return Math.Round(clamped, 1);
    }
}
