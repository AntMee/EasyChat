using Avalonia;
using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.Features.Translation;

internal static class TranslationWindowPlacement
{
    public static PixelPoint Near(
        PixelRect area,
        double scaling,
        PhysicalScreenPoint anchor,
        double logicalWidth,
        double logicalHeight,
        double logicalOffset)
    {
        var effectiveScaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var width = ToPhysicalSize(logicalWidth, effectiveScaling, area.Width);
        var height = ToPhysicalSize(logicalHeight, effectiveScaling, area.Height);
        var offset = Math.Max(1, ToPhysicalSize(logicalOffset, effectiveScaling, int.MaxValue));
        var left = checked(anchor.X + offset);
        var top = checked(anchor.Y + offset);

        return new PixelPoint(
            Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    public static PixelPoint ClampToArea(
        PixelRect area,
        double scaling,
        PixelPoint position,
        double logicalWidth,
        double logicalHeight)
    {
        var effectiveScaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var width = ToPhysicalSize(logicalWidth, effectiveScaling, area.Width);
        var height = ToPhysicalSize(logicalHeight, effectiveScaling, area.Height);

        return new PixelPoint(
            Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private static int ToPhysicalSize(double logicalSize, double scaling, int maximum)
    {
        var size = double.IsFinite(logicalSize) && logicalSize > 0
            ? Math.Max(1, (int)Math.Ceiling(logicalSize * scaling))
            : 1;
        return Math.Min(size, Math.Max(1, maximum));
    }
}
