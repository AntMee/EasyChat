using Avalonia;

namespace EasyChat.Presentation.Features.Capture;

internal enum CaptureSelectionMode
{
    Idle,
    Selecting,
    Moving,
    Resizing,
    Done
}

internal enum CaptureResizeHandle
{
    None,
    TopLeft,
    TopCenter,
    TopRight,
    RightCenter,
    BottomRight,
    BottomCenter,
    BottomLeft,
    LeftCenter
}

internal sealed class PhysicalSelectionState
{
    private PixelPoint _interactionStart;
    private PixelRect _initialRegion;

    public PhysicalSelectionState(PixelRect virtualDesktopBounds)
    {
        PhysicalPixelGeometry.EnsureNotEmpty(virtualDesktopBounds, nameof(virtualDesktopBounds));
        VirtualDesktopBounds = virtualDesktopBounds;
    }

    public PixelRect VirtualDesktopBounds { get; }
    public PixelRect? Region { get; private set; }
    public CaptureSelectionMode Mode { get; private set; }
    public CaptureResizeHandle ActiveResizeHandle { get; private set; }
    public PixelPoint? CompletionPoint { get; private set; }
    public bool HasSelection => Region is { Width: > 0, Height: > 0 };

    public void BeginSelection(PixelPoint anchor)
    {
        _interactionStart = PhysicalPixelGeometry.ClampEndpoint(anchor, VirtualDesktopBounds);
        _initialRegion = default;
        Region = null;
        CompletionPoint = null;
        ActiveResizeHandle = CaptureResizeHandle.None;
        Mode = CaptureSelectionMode.Selecting;
    }

    public bool BeginMove(PixelPoint pointer)
    {
        if (Region is not { } region || !PhysicalPixelGeometry.Contains(region, pointer))
            return false;

        _interactionStart = pointer;
        _initialRegion = region;
        CompletionPoint = null;
        ActiveResizeHandle = CaptureResizeHandle.None;
        Mode = CaptureSelectionMode.Moving;
        return true;
    }

    public bool BeginResize(CaptureResizeHandle handle, PixelPoint pointer)
    {
        if (handle is <= CaptureResizeHandle.None or > CaptureResizeHandle.LeftCenter ||
            Region is not { } region)
            return false;

        _interactionStart = pointer;
        _initialRegion = region;
        CompletionPoint = null;
        ActiveResizeHandle = handle;
        Mode = CaptureSelectionMode.Resizing;
        return true;
    }

    public bool Update(PixelPoint pointer)
    {
        switch (Mode)
        {
            case CaptureSelectionMode.Selecting:
                Region = PhysicalPixelGeometry.Normalize(
                    _interactionStart,
                    PhysicalPixelGeometry.ClampEndpoint(pointer, VirtualDesktopBounds));
                break;
            case CaptureSelectionMode.Moving:
                Region = PhysicalPixelGeometry.MoveWithin(
                    _initialRegion,
                    _interactionStart,
                    pointer,
                    VirtualDesktopBounds);
                break;
            case CaptureSelectionMode.Resizing:
                Region = PhysicalPixelGeometry.ResizeWithin(
                    _initialRegion,
                    _interactionStart,
                    pointer,
                    ActiveResizeHandle,
                    VirtualDesktopBounds);
                break;
        }

        return HasSelection;
    }

    public bool Complete(PixelPoint completionPoint)
    {
        if (!HasSelection)
        {
            Reset();
            return false;
        }

        CompletionPoint = completionPoint;
        ActiveResizeHandle = CaptureResizeHandle.None;
        Mode = CaptureSelectionMode.Done;
        return true;
    }

    public void Reset()
    {
        _interactionStart = default;
        _initialRegion = default;
        Region = null;
        CompletionPoint = null;
        ActiveResizeHandle = CaptureResizeHandle.None;
        Mode = CaptureSelectionMode.Idle;
    }

    public PixelRect? ToUnionBitmapRect(PixelRect unionBounds) =>
        Region is { } region
            ? PhysicalPixelGeometry.ToUnionBitmapRect(region, unionBounds)
            : null;
}

internal static class PhysicalPixelGeometry
{
    public static PixelRect? Normalize(PixelPoint first, PixelPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X, second.X);
        var bottom = Math.Max(first.Y, second.Y);

        return right > left && bottom > top
            ? new PixelRect(left, top, right - left, bottom - top)
            : null;
    }

    public static bool Contains(PixelRect region, PixelPoint point) =>
        region.Width > 0 &&
        region.Height > 0 &&
        point.X >= region.X &&
        point.X < region.Right &&
        point.Y >= region.Y &&
        point.Y < region.Bottom;

    public static PixelRect? Intersect(PixelRect first, PixelRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        return right > left && bottom > top
            ? new PixelRect(left, top, right - left, bottom - top)
            : null;
    }

    public static PixelRect? Union(IEnumerable<PixelRect> regions)
    {
        var hasRegion = false;
        var left = 0;
        var top = 0;
        var right = 0;
        var bottom = 0;

        foreach (var region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0)
                continue;

            if (!hasRegion)
            {
                left = region.X;
                top = region.Y;
                right = region.Right;
                bottom = region.Bottom;
                hasRegion = true;
                continue;
            }

            left = Math.Min(left, region.X);
            top = Math.Min(top, region.Y);
            right = Math.Max(right, region.Right);
            bottom = Math.Max(bottom, region.Bottom);
        }

        return hasRegion
            ? new PixelRect(left, top, right - left, bottom - top)
            : null;
    }

    public static PixelRect MoveWithin(
        PixelRect initialRegion,
        PixelPoint interactionStart,
        PixelPoint pointer,
        PixelRect bounds)
    {
        EnsureNotEmpty(initialRegion, nameof(initialRegion));
        EnsureNotEmpty(bounds, nameof(bounds));

        var width = Math.Min(initialRegion.Width, bounds.Width);
        var height = Math.Min(initialRegion.Height, bounds.Height);
        var x = Clamp(
            (long)initialRegion.X + pointer.X - interactionStart.X,
            bounds.X,
            (long)bounds.Right - width);
        var y = Clamp(
            (long)initialRegion.Y + pointer.Y - interactionStart.Y,
            bounds.Y,
            (long)bounds.Bottom - height);

        return new PixelRect(x, y, width, height);
    }

    public static PixelRect ResizeWithin(
        PixelRect initialRegion,
        PixelPoint interactionStart,
        PixelPoint pointer,
        CaptureResizeHandle handle,
        PixelRect bounds)
    {
        EnsureNotEmpty(initialRegion, nameof(initialRegion));
        EnsureNotEmpty(bounds, nameof(bounds));

        var restricted = Intersect(initialRegion, bounds)
            ?? throw new ArgumentOutOfRangeException(
                nameof(initialRegion),
                "The selection must overlap the virtual desktop bounds.");
        if (handle == CaptureResizeHandle.None)
            return restricted;

        var left = (long)restricted.X;
        var top = (long)restricted.Y;
        var right = restricted.Right;
        var bottom = restricted.Bottom;
        var deltaX = (long)pointer.X - interactionStart.X;
        var deltaY = (long)pointer.Y - interactionStart.Y;

        if (handle is CaptureResizeHandle.TopLeft or CaptureResizeHandle.BottomLeft or CaptureResizeHandle.LeftCenter)
            left = Clamp((long)restricted.X + deltaX, bounds.X, (long)restricted.Right - 1);
        if (handle is CaptureResizeHandle.TopRight or CaptureResizeHandle.RightCenter or CaptureResizeHandle.BottomRight)
            right = Clamp((long)restricted.Right + deltaX, (long)restricted.X + 1, bounds.Right);
        if (handle is CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopCenter or CaptureResizeHandle.TopRight)
            top = Clamp((long)restricted.Y + deltaY, bounds.Y, (long)restricted.Bottom - 1);
        if (handle is CaptureResizeHandle.BottomRight or CaptureResizeHandle.BottomCenter or CaptureResizeHandle.BottomLeft)
            bottom = Clamp((long)restricted.Bottom + deltaY, (long)restricted.Y + 1, bounds.Bottom);

        return new PixelRect(
            checked((int)left),
            checked((int)top),
            checked((int)(right - left)),
            checked((int)(bottom - top)));
    }

    public static PixelRect? ToUnionBitmapRect(PixelRect globalRegion, PixelRect unionBounds)
    {
        var clipped = Intersect(globalRegion, unionBounds);
        return clipped is { } region
            ? new PixelRect(
                checked(region.X - unionBounds.X),
                checked(region.Y - unionBounds.Y),
                region.Width,
                region.Height)
            : null;
    }

    internal static PixelPoint ClampEndpoint(PixelPoint point, PixelRect bounds) => new(
        Clamp(point.X, bounds.X, bounds.Right),
        Clamp(point.Y, bounds.Y, bounds.Bottom));

    internal static void EnsureNotEmpty(PixelRect region, string parameterName)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "A physical pixel region must not be empty.");
    }

    private static int Clamp(long value, long minimum, long maximum) =>
        checked((int)Math.Clamp(value, minimum, maximum));
}
