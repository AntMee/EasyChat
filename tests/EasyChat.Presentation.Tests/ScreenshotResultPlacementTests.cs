using Avalonia;
using EasyChat.Presentation.Features.Capture;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class ScreenshotResultPlacementTests
{
    [TestMethod]
    [DataRow(1920, 1040, 1.5, 1100, 760, 16, 1100, 661.3333333333)]
    [DataRow(1920, 1040, 2.0, 1100, 760, 16, 928, 488)]
    [DataRow(3840, 2120, 2.0, 1100, 760, 16, 1100, 760)]
    public void FitLogicalSize_KeepsAHighDpiWindowInsideThePhysicalWorkingArea(
        int areaWidth,
        int areaHeight,
        double scaling,
        double desiredWidth,
        double desiredHeight,
        double marginDip,
        double expectedWidth,
        double expectedHeight)
    {
        var result = ScreenshotResultPlacement.FitLogicalSize(
            new PixelRect(-1920, -1080, areaWidth, areaHeight),
            scaling,
            desiredWidth,
            desiredHeight,
            marginDip);

        Assert.AreEqual(expectedWidth, result.Width, 0.001);
        Assert.AreEqual(expectedHeight, result.Height, 0.001);
    }

    [TestMethod]
    [DataRow(-1920, 0, 1920, 1080, 1.0, 800, 600, -1360, 240)]
    [DataRow(-2560, -1440, 2560, 1440, 1.25, 800, 600, -1780, -1095)]
    [DataRow(1920, -1440, 2560, 1440, 1.5, 1100, 760, 2375, -1290)]
    [DataRow(0, 0, 3840, 2160, 2.0, 1100, 760, 820, 320)]
    public void Center_UsesPhysicalWindowSizeAndPreservesScreenOrigin(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        double scaling,
        double logicalWidth,
        double logicalHeight,
        int expectedX,
        int expectedY)
    {
        var result = ScreenshotResultPlacement.Center(
            new PixelRect(areaX, areaY, areaWidth, areaHeight),
            scaling,
            logicalWidth,
            logicalHeight);

        Assert.AreEqual(new PixelPoint(expectedX, expectedY), result);
    }

    [TestMethod]
    [DataRow(-2560, -1440, 2560, 1440, 1.5, 600, -5, -1730, -1448)]
    [DataRow(1920, 0, 1920, 1080, 1.25, 480, -5, 2580, -6)]
    public void CenterHorizontallyAtTop_ScalesWidthAndOffsetsFromTargetScreen(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        double scaling,
        double logicalWidth,
        double topOffsetDip,
        int expectedX,
        int expectedY)
    {
        var result = ScreenshotResultPlacement.CenterHorizontallyAtTop(
            new PixelRect(areaX, areaY, areaWidth, areaHeight),
            scaling,
            logicalWidth,
            topOffsetDip);

        Assert.AreEqual(new PixelPoint(expectedX, expectedY), result);
    }
}
