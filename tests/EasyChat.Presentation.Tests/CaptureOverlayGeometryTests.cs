using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Capture;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class CaptureOverlayGeometryTests
{
    [TestMethod]
    [DataRow(96d, 1920d, 1080d)]
    [DataRow(120d, 1536d, 864d)]
    [DataRow(144d, 1280d, 720d)]
    [DataRow(192d, 960d, 540d)]
    public void LogicalSize_ConvertsPhysicalPixelsWithTheTargetScreenDpi(
        double dpi,
        double expectedWidth,
        double expectedHeight)
    {
        var screen = new ScreenDescriptor(
            new ScreenId("display"),
            new PhysicalScreenRegion(-1920, -1080, 1920, 1080),
            dpi,
            dpi,
            false);

        var result = CaptureOverlayGeometry.GetLogicalSize(screen);

        Assert.AreEqual(expectedWidth, result.Width, 0.001);
        Assert.AreEqual(expectedHeight, result.Height, 0.001);
    }

    [TestMethod]
    public void DesktopSlice_SubtractsNegativeVirtualDesktopOrigin()
    {
        var desktop = new PhysicalScreenRegion(-1920, -1200, 4480, 2640);

        var left = CaptureOverlayGeometry.GetDesktopSlice(
            new PhysicalScreenRegion(-1920, 0, 1920, 1080),
            desktop);
        var above = CaptureOverlayGeometry.GetDesktopSlice(
            new PhysicalScreenRegion(0, -1200, 1920, 1200),
            desktop);

        Assert.AreEqual(new Avalonia.PixelRect(0, 1200, 1920, 1080), left);
        Assert.AreEqual(new Avalonia.PixelRect(1920, 0, 1920, 1200), above);
    }

    [TestMethod]
    public void DesktopSlice_RejectsAScreenOutsideTheCapturedDesktop()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CaptureOverlayGeometry.GetDesktopSlice(
                new PhysicalScreenRegion(-2000, 0, 1920, 1080),
                new PhysicalScreenRegion(-1920, 0, 4480, 1440)));
    }

    [TestMethod]
    public void MatchesTopology_AcceptsNegativeMixedDpiScreensInAnyOrder()
    {
        ScreenDescriptor[] expected =
        [
            new(new ScreenId("primary"), new PhysicalScreenRegion(0, 0, 2560, 1440), 120, 120, true),
            new(new ScreenId("left"), new PhysicalScreenRegion(-1920, 0, 1920, 1080), 96, 96, false),
            new(new ScreenId("above"), new PhysicalScreenRegion(0, -2160, 3840, 2160), 192, 192, false)
        ];
        (Avalonia.PixelRect Bounds, double Scaling)[] actual =
        [
            (new Avalonia.PixelRect(0, -2160, 3840, 2160), 2),
            (new Avalonia.PixelRect(-1920, 0, 1920, 1080), 1),
            (new Avalonia.PixelRect(0, 0, 2560, 1440), 1.25)
        ];

        Assert.IsTrue(CaptureOverlayGeometry.MatchesTopology(expected, actual));
    }

    [TestMethod]
    public void MatchesTopology_RejectsChangesMissedBeforeTheScreensEventSubscription()
    {
        ScreenDescriptor[] expected =
        [
            new(new ScreenId("primary"), new PhysicalScreenRegion(0, 0, 1920, 1080), 96, 96, true)
        ];

        Assert.IsFalse(CaptureOverlayGeometry.MatchesTopology(
            expected,
            [(new Avalonia.PixelRect(0, 0, 1920, 1080), 1.5)]));
        Assert.IsFalse(CaptureOverlayGeometry.MatchesTopology(
            expected,
            [
                (new Avalonia.PixelRect(0, 0, 1920, 1080), 1),
                (new Avalonia.PixelRect(-1280, 0, 1280, 1024), 1)
            ]));
    }
}
