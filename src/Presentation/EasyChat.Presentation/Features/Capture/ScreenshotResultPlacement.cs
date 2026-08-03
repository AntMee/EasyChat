using Avalonia;

namespace EasyChat.Presentation.Features.Capture;

internal static class ScreenshotResultPlacement
{
    public static Size FitLogicalSize(
        PixelRect area,
        double scaling,
        double desiredWidth,
        double desiredHeight,
        double marginDip)
    {
        var effectiveScaling = PositiveScale(scaling);
        var margin = double.IsFinite(marginDip) && marginDip > 0 ? marginDip : 0;
        var maximumWidth = Math.Max(1, area.Width / effectiveScaling - margin * 2);
        var maximumHeight = Math.Max(1, area.Height / effectiveScaling - margin * 2);
        return new Size(
            Math.Min(PositiveSize(desiredWidth, maximumWidth), maximumWidth),
            Math.Min(PositiveSize(desiredHeight, maximumHeight), maximumHeight));
    }

    public static PixelPoint Center(
        PixelRect area,
        double scaling,
        double logicalWidth,
        double logicalHeight)
    {
        var physicalWidth = ToPhysicalSize(logicalWidth, scaling);
        var physicalHeight = ToPhysicalSize(logicalHeight, scaling);
        return new PixelPoint(
            area.X + (area.Width - physicalWidth) / 2,
            area.Y + (area.Height - physicalHeight) / 2);
    }

    public static PixelPoint CenterHorizontallyAtTop(
        PixelRect area,
        double scaling,
        double logicalWidth,
        double topOffsetDip)
    {
        var physicalWidth = ToPhysicalSize(logicalWidth, scaling);
        return new PixelPoint(
            area.X + (area.Width - physicalWidth) / 2,
            area.Y + ToPhysicalOffset(topOffsetDip, scaling));
    }

    private static int ToPhysicalSize(double logicalSize, double scaling)
    {
        if (!double.IsFinite(logicalSize) || logicalSize <= 0)
            return 0;
        var effectiveScaling = PositiveScale(scaling);
        return Math.Max(1, (int)Math.Ceiling(logicalSize * effectiveScaling));
    }

    private static int ToPhysicalOffset(double logicalOffset, double scaling)
    {
        if (!double.IsFinite(logicalOffset))
            return 0;
        var effectiveScaling = PositiveScale(scaling);
        return (int)Math.Round(
            logicalOffset * effectiveScaling,
            MidpointRounding.AwayFromZero);
    }

    private static double PositiveScale(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1;

    private static double PositiveSize(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}
