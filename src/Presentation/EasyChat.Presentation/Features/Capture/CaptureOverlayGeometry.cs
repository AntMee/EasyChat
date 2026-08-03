using Avalonia;
using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.Features.Capture;

internal static class CaptureOverlayGeometry
{
    public static bool MatchesTopology(
        IEnumerable<ScreenDescriptor> expected,
        IEnumerable<(PixelRect Bounds, double Scaling)> actual)
    {
        var remaining = actual.ToList();
        foreach (var screen in expected)
        {
            var bounds = new PixelRect(
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height);
            var match = remaining.FindIndex(candidate =>
                candidate.Bounds == bounds &&
                SameScale(candidate.Scaling, screen.ScaleX) &&
                SameScale(candidate.Scaling, screen.ScaleY));
            if (match < 0)
                return false;
            remaining.RemoveAt(match);
        }
        return remaining.Count == 0;
    }

    public static Size GetLogicalSize(ScreenDescriptor screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (screen.Bounds.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(screen), "Screen bounds must not be empty.");
        return new Size(
            screen.Bounds.Width / PositiveScale(screen.ScaleX),
            screen.Bounds.Height / PositiveScale(screen.ScaleY));
    }

    public static PixelRect GetDesktopSlice(
        PhysicalScreenRegion screen,
        PhysicalScreenRegion desktop)
    {
        if (screen.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(screen));
        if (desktop.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(desktop));
        var screenRight = checked(screen.X + screen.Width);
        var screenBottom = checked(screen.Y + screen.Height);
        var desktopRight = checked(desktop.X + desktop.Width);
        var desktopBottom = checked(desktop.Y + desktop.Height);
        if (screen.X < desktop.X || screen.Y < desktop.Y ||
            screenRight > desktopRight || screenBottom > desktopBottom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(screen),
                "The screen must be contained by the captured desktop bounds.");
        }

        return new PixelRect(
            checked(screen.X - desktop.X),
            checked(screen.Y - desktop.Y),
            screen.Width,
            screen.Height);
    }

    private static double PositiveScale(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1d;

    private static bool SameScale(double first, double second) =>
        Math.Abs(PositiveScale(first) - PositiveScale(second)) <= 0.01;
}
