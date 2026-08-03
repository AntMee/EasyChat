using Avalonia;
using EasyChat.Presentation.Features.Capture;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class PhysicalSelectionStateTests
{
    private static readonly PixelRect VirtualDesktop = new(-1920, -1200, 4480, 2640);

    [TestMethod]
    [DataRow(96d)]
    [DataRow(120d)]
    [DataRow(144d)]
    [DataRow(192d)]
    public void Selecting_DoesNotRescaleAlreadyPhysicalPoints(double dpi)
    {
        var state = new PhysicalSelectionState(VirtualDesktop);

        state.BeginSelection(new PixelPoint(-800, -400));
        var updated = state.Update(new PixelPoint(1200, 900));

        Assert.IsTrue(updated, $"Selection unexpectedly failed at {dpi} DPI.");
        Assert.AreEqual(new PixelRect(-800, -400, 2000, 1300), state.Region);
        Assert.AreEqual(CaptureSelectionMode.Selecting, state.Mode);
    }

    [TestMethod]
    public void Selecting_ClampsOutsideEndpointsToTheVirtualDesktop()
    {
        var state = new PhysicalSelectionState(VirtualDesktop);

        state.BeginSelection(new PixelPoint(-5000, -5000));
        state.Update(new PixelPoint(5000, 5000));

        Assert.AreEqual(VirtualDesktop, state.Region);
    }

    [TestMethod]
    public void Geometry_NormalizesNegativeCoordinatesAndRejectsEmptyRegions()
    {
        var normalized = PhysicalPixelGeometry.Normalize(
            new PixelPoint(250, 400),
            new PixelPoint(-1750, -800));

        Assert.AreEqual(new PixelRect(-1750, -800, 2000, 1200), normalized);
        Assert.IsNull(PhysicalPixelGeometry.Normalize(
            new PixelPoint(-100, 50),
            new PixelPoint(-100, 80)));
    }

    [TestMethod]
    public void Geometry_ContainsUsesRightAndBottomExclusivePixelEdges()
    {
        var region = new PixelRect(-100, -50, 200, 100);

        Assert.IsTrue(PhysicalPixelGeometry.Contains(region, new PixelPoint(-100, -50)));
        Assert.IsTrue(PhysicalPixelGeometry.Contains(region, new PixelPoint(99, 49)));
        Assert.IsFalse(PhysicalPixelGeometry.Contains(region, new PixelPoint(100, 49)));
        Assert.IsFalse(PhysicalPixelGeometry.Contains(region, new PixelPoint(99, 50)));
    }

    [TestMethod]
    public void Geometry_IntersectAndUnionPreserveUnifiedDesktopCoordinates()
    {
        PixelRect[] screens =
        [
            new(-1920, 0, 1920, 1080),
            new(0, 0, 2560, 1440),
            new(0, -1200, 1920, 1200)
        ];
        var selection = new PixelRect(-800, -400, 2000, 1300);

        Assert.AreEqual(VirtualDesktop, PhysicalPixelGeometry.Union(screens));
        Assert.AreEqual(
            new PixelRect(-800, 0, 800, 900),
            PhysicalPixelGeometry.Intersect(selection, screens[0]));
        Assert.AreEqual(
            new PixelRect(0, 0, 1200, 900),
            PhysicalPixelGeometry.Intersect(selection, screens[1]));
        Assert.AreEqual(
            new PixelRect(0, -400, 1200, 400),
            PhysicalPixelGeometry.Intersect(selection, screens[2]));
        Assert.IsNull(PhysicalPixelGeometry.Intersect(
            new PixelRect(-10, -10, 10, 10),
            new PixelRect(0, 0, 10, 10)));
    }

    [TestMethod]
    public void Moving_ClampsToEveryVirtualDesktopEdgeWithoutChangingSize()
    {
        var state = Select(new PixelPoint(-300, 100), new PixelPoint(200, 500));
        Assert.IsTrue(state.Complete(new PixelPoint(200, 500)));
        Assert.IsTrue(state.BeginMove(new PixelPoint(-100, 200)));

        state.Update(new PixelPoint(-5000, -5000));
        Assert.AreEqual(new PixelRect(-1920, -1200, 500, 400), state.Region);

        Assert.IsTrue(state.Complete(new PixelPoint(-5000, -5000)));
        Assert.IsTrue(state.BeginMove(new PixelPoint(-1700, -1000)));
        state.Update(new PixelPoint(5000, 5000));
        Assert.AreEqual(new PixelRect(2060, 1040, 500, 400), state.Region);
    }

    [TestMethod]
    public void Moving_EachUpdateUsesTheInteractionStartRegion()
    {
        var state = Select(new PixelPoint(0, 0), new PixelPoint(100, 100));
        Assert.IsTrue(state.Complete(new PixelPoint(100, 100)));
        Assert.IsTrue(state.BeginMove(new PixelPoint(50, 50)));

        state.Update(new PixelPoint(60, 60));
        state.Update(new PixelPoint(70, 80));

        Assert.AreEqual(new PixelRect(20, 30, 100, 100), state.Region);
    }

    [TestMethod]
    [DataRow((int)CaptureResizeHandle.TopLeft, 80, 70, 120, 130)]
    [DataRow((int)CaptureResizeHandle.TopCenter, 100, 70, 100, 130)]
    [DataRow((int)CaptureResizeHandle.TopRight, 100, 70, 120, 130)]
    [DataRow((int)CaptureResizeHandle.RightCenter, 100, 100, 120, 100)]
    [DataRow((int)CaptureResizeHandle.BottomRight, 100, 100, 120, 130)]
    [DataRow((int)CaptureResizeHandle.BottomCenter, 100, 100, 100, 130)]
    [DataRow((int)CaptureResizeHandle.BottomLeft, 80, 100, 120, 130)]
    [DataRow((int)CaptureResizeHandle.LeftCenter, 80, 100, 120, 100)]
    public void Resizing_UsesInitialRegionForAllEightHandles(
        int handleValue,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var handle = (CaptureResizeHandle)handleValue;
        var state = Select(new PixelPoint(100, 100), new PixelPoint(200, 200));
        Assert.IsTrue(state.Complete(new PixelPoint(200, 200)));
        var pointerStart = HandlePoint(handle, 100, 150, 200);
        var intermediate = HandlePoint(handle, 90, 150, 210, 85, 150, 215);
        var pointerEnd = HandlePoint(handle, 80, 150, 220, 70, 150, 230);
        Assert.IsTrue(state.BeginResize(handle, pointerStart));

        state.Update(intermediate);
        state.Update(pointerEnd);

        Assert.AreEqual(
            new PixelRect(expectedX, expectedY, expectedWidth, expectedHeight),
            state.Region);
    }

    [TestMethod]
    [DataRow((int)CaptureResizeHandle.TopLeft)]
    [DataRow((int)CaptureResizeHandle.TopCenter)]
    [DataRow((int)CaptureResizeHandle.TopRight)]
    [DataRow((int)CaptureResizeHandle.RightCenter)]
    [DataRow((int)CaptureResizeHandle.BottomRight)]
    [DataRow((int)CaptureResizeHandle.BottomCenter)]
    [DataRow((int)CaptureResizeHandle.BottomLeft)]
    [DataRow((int)CaptureResizeHandle.LeftCenter)]
    public void Resizing_StaysInsideDesktopAndNeverBecomesEmpty(int handleValue)
    {
        var handle = (CaptureResizeHandle)handleValue;
        var initial = new PixelRect(-100, -100, 300, 300);
        var pointer = CollapsePoint(handle);
        var resized = PhysicalPixelGeometry.ResizeWithin(
            initial,
            new PixelPoint(50, 50),
            pointer,
            handle,
            new PixelRect(-100, -100, 300, 300));

        var changesWidth = handle is
            CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopRight or
            CaptureResizeHandle.RightCenter or CaptureResizeHandle.BottomRight or
            CaptureResizeHandle.BottomLeft or CaptureResizeHandle.LeftCenter;
        var changesHeight = handle is
            CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopCenter or
            CaptureResizeHandle.TopRight or CaptureResizeHandle.BottomRight or
            CaptureResizeHandle.BottomCenter or CaptureResizeHandle.BottomLeft;
        Assert.AreEqual(changesWidth ? 1 : 300, resized.Width);
        Assert.AreEqual(changesHeight ? 1 : 300, resized.Height);
        Assert.IsGreaterThanOrEqualTo(-100, resized.X);
        Assert.IsGreaterThanOrEqualTo(-100, resized.Y);
        Assert.IsLessThanOrEqualTo(200, resized.Right);
        Assert.IsLessThanOrEqualTo(200, resized.Bottom);
    }

    [TestMethod]
    public void Resizing_OutwardStopsAtNegativeAndPositiveDesktopEdges()
    {
        var bounds = new PixelRect(-100, -100, 300, 300);
        var initial = new PixelRect(-50, -50, 100, 100);

        var topLeft = PhysicalPixelGeometry.ResizeWithin(
            initial,
            new PixelPoint(-50, -50),
            new PixelPoint(-1000, -1000),
            CaptureResizeHandle.TopLeft,
            bounds);
        var bottomRight = PhysicalPixelGeometry.ResizeWithin(
            initial,
            new PixelPoint(50, 50),
            new PixelPoint(1000, 1000),
            CaptureResizeHandle.BottomRight,
            bounds);

        Assert.AreEqual(new PixelRect(-100, -100, 150, 150), topLeft);
        Assert.AreEqual(new PixelRect(-50, -50, 250, 250), bottomRight);
    }

    [TestMethod]
    public void BeginResize_RejectsUndefinedHandleWithoutChangingCompletedState()
    {
        var state = Select(new PixelPoint(0, 0), new PixelPoint(100, 100));
        Assert.IsTrue(state.Complete(new PixelPoint(100, 100)));

        var started = state.BeginResize((CaptureResizeHandle)999, new PixelPoint(100, 100));

        Assert.IsFalse(started);
        Assert.AreEqual(CaptureSelectionMode.Done, state.Mode);
        Assert.AreEqual(new PixelRect(0, 0, 100, 100), state.Region);
    }

    [TestMethod]
    public void CropMapping_SubtractsNegativeUnionOriginAndClipsOutsideSelection()
    {
        var state = Select(new PixelPoint(-1000, -500), new PixelPoint(500, 500));

        Assert.AreEqual(
            new PixelRect(920, 700, 1500, 1000),
            state.ToUnionBitmapRect(VirtualDesktop));
        Assert.AreEqual(
            new PixelRect(0, 0, 420, 400),
            PhysicalPixelGeometry.ToUnionBitmapRect(
                new PixelRect(-2500, -1600, 1000, 800),
                VirtualDesktop));
        Assert.AreEqual(
            new PixelRect(4300, 2500, 180, 140),
            PhysicalPixelGeometry.ToUnionBitmapRect(
                new PixelRect(2380, 1300, 500, 500),
                VirtualDesktop));
        Assert.AreEqual(
            new PixelRect(50, 50, 100, 100),
            PhysicalPixelGeometry.ToUnionBitmapRect(
                new PixelRect(150, 250, 100, 100),
                new PixelRect(100, 200, 500, 500)));
        Assert.IsNull(PhysicalPixelGeometry.ToUnionBitmapRect(
            new PixelRect(3000, 2000, 100, 100),
            VirtualDesktop));
    }

    [TestMethod]
    public void CompleteAndReset_DoNotExposeAnEmptyRegion()
    {
        var state = new PhysicalSelectionState(VirtualDesktop);

        state.BeginSelection(new PixelPoint(10, 10));
        Assert.IsFalse(state.Update(new PixelPoint(10, 200)));
        Assert.IsFalse(state.Complete(new PixelPoint(10, 200)));
        Assert.AreEqual(CaptureSelectionMode.Idle, state.Mode);
        Assert.IsNull(state.Region);

        state.BeginSelection(new PixelPoint(10, 10));
        Assert.IsTrue(state.Update(new PixelPoint(20, 20)));
        Assert.IsTrue(state.Complete(new PixelPoint(20, 20)));
        Assert.AreEqual(new PixelPoint(20, 20), state.CompletionPoint);

        state.Reset();
        Assert.AreEqual(CaptureSelectionMode.Idle, state.Mode);
        Assert.IsNull(state.Region);
        Assert.IsNull(state.ToUnionBitmapRect(VirtualDesktop));
        Assert.IsNull(state.CompletionPoint);
    }

    private static PhysicalSelectionState Select(PixelPoint first, PixelPoint second)
    {
        var state = new PhysicalSelectionState(VirtualDesktop);
        state.BeginSelection(first);
        Assert.IsTrue(state.Update(second));
        return state;
    }

    private static PixelPoint HandlePoint(
        CaptureResizeHandle handle,
        int left,
        int center,
        int right,
        int? top = null,
        int? middle = null,
        int? bottom = null)
    {
        var resolvedTop = top ?? left;
        var resolvedMiddle = middle ?? center;
        var resolvedBottom = bottom ?? right;
        return handle switch
        {
            CaptureResizeHandle.TopLeft => new PixelPoint(left, resolvedTop),
            CaptureResizeHandle.TopCenter => new PixelPoint(center, resolvedTop),
            CaptureResizeHandle.TopRight => new PixelPoint(right, resolvedTop),
            CaptureResizeHandle.RightCenter => new PixelPoint(right, resolvedMiddle),
            CaptureResizeHandle.BottomRight => new PixelPoint(right, resolvedBottom),
            CaptureResizeHandle.BottomCenter => new PixelPoint(center, resolvedBottom),
            CaptureResizeHandle.BottomLeft => new PixelPoint(left, resolvedBottom),
            CaptureResizeHandle.LeftCenter => new PixelPoint(left, resolvedMiddle),
            _ => throw new ArgumentOutOfRangeException(nameof(handle), handle, null)
        };
    }

    private static PixelPoint CollapsePoint(CaptureResizeHandle handle)
    {
        var x = handle switch
        {
            CaptureResizeHandle.TopLeft or CaptureResizeHandle.BottomLeft or
                CaptureResizeHandle.LeftCenter => 10000,
            CaptureResizeHandle.TopRight or CaptureResizeHandle.RightCenter or
                CaptureResizeHandle.BottomRight => -10000,
            _ => 50
        };
        var y = handle switch
        {
            CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopCenter or
                CaptureResizeHandle.TopRight => 10000,
            CaptureResizeHandle.BottomRight or CaptureResizeHandle.BottomCenter or
                CaptureResizeHandle.BottomLeft => -10000,
            _ => 50
        };
        return new PixelPoint(x, y);
    }
}
