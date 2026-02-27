namespace Malie.Models;

public sealed record GlbOrientationSettings(
    double RotationXDegrees,
    double RotationYDegrees,
    double RotationZDegrees,
    double Scale,
    double OffsetX,
    double OffsetY,
    double OffsetZ)
{
    public static GlbOrientationSettings Default { get; } = new(
        RotationXDegrees: 0,
        RotationYDegrees: 0,
        RotationZDegrees: 0,
        Scale: 1,
        OffsetX: 0,
        OffsetY: 0,
        OffsetZ: 0);

    public GlbOrientationSettings Normalize()
    {
        return new GlbOrientationSettings(
            RotationXDegrees: NormalizeAngle(RotationXDegrees),
            RotationYDegrees: NormalizeAngle(RotationYDegrees),
            RotationZDegrees: NormalizeAngle(RotationZDegrees),
            Scale: Clamp(Scale, 0.05, 8),
            OffsetX: Clamp(OffsetX, -30, 30),
            OffsetY: Clamp(OffsetY, -30, 30),
            OffsetZ: Clamp(OffsetZ, -30, 30));
    }

    private static double NormalizeAngle(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        var normalized = value % 360;
        if (normalized > 180)
        {
            normalized -= 360;
        }
        else if (normalized < -180)
        {
            normalized += 360;
        }

        return normalized;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return Math.Max(min, Math.Min(max, value));
    }
}
