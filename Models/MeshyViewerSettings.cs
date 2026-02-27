namespace Malie.Models;

public sealed record MeshyViewerSettings(
    string ActiveModelRelativePath,
    double RotationMinutes)
{
    public static MeshyViewerSettings Default => new(string.Empty, 0);

    public MeshyViewerSettings Normalize()
    {
        var modelPath = ActiveModelRelativePath?.Trim() ?? string.Empty;
        var minutes = RotationMinutes;
        if (double.IsNaN(minutes) || double.IsInfinity(minutes))
        {
            minutes = 0;
        }

        minutes = Math.Max(0, Math.Min(1440, minutes));
        return this with
        {
            ActiveModelRelativePath = modelPath,
            RotationMinutes = minutes
        };
    }
}
