namespace EasyChat.Contracts.Platform;

public readonly record struct ScreenId(string Value);

/// <summary>
/// Position in physical pixels within the platform's unified desktop space. The origin may be
/// negative on desktops whose displays extend left of or above the primary display.
/// </summary>
public readonly record struct PhysicalScreenPoint(int X, int Y);

/// <summary>
/// Region in physical pixels within the platform's unified desktop space. Values must not be
/// combined with toolkit logical coordinates until they have been converted for the target display.
/// </summary>
public readonly record struct PhysicalScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Describes one display in physical desktop coordinates. <see cref="DpiX"/> and
/// <see cref="DpiY"/> are effective DPI values relative to 96 logical DPI. Platform adapters map
/// Retina, fractional scaling, and mixed-DPI displays to these values; Presentation owns conversion
/// to its toolkit's display-local logical coordinate space.
/// </summary>
public sealed record ScreenDescriptor(
    ScreenId Id,
    PhysicalScreenRegion Bounds,
    double DpiX,
    double DpiY,
    bool IsPrimary)
{
    public const double LogicalDpi = 96d;

    public double ScaleX => DpiX / LogicalDpi;
    public double ScaleY => DpiY / LogicalDpi;
}
