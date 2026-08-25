using Avalonia;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Translation;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TranslationWindowPlacementTests
{
    [TestMethod]
    [DataRow(0, 0, 1920, 1040, 1.5, 1300, 300, 450, 350, 1245, 330)]
    [DataRow(-2560, -1400, 2560, 1360, 2.0, -100, -100, 450, 350, -900, -740)]
    public void Near_ClampsToTheNearestVisiblePosition(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        double scaling,
        int anchorX,
        int anchorY,
        double logicalWidth,
        double logicalHeight,
        int expectedX,
        int expectedY)
    {
        var result = TranslationWindowPlacement.Near(
            new PixelRect(areaX, areaY, areaWidth, areaHeight),
            scaling,
            new PhysicalScreenPoint(anchorX, anchorY),
            logicalWidth,
            logicalHeight,
            logicalOffset: 20);

        Assert.AreEqual(new PixelPoint(expectedX, expectedY), result);
    }

    [TestMethod]
    public void Near_ClampsAnOversizedWindowToTheWorkingAreaOrigin()
    {
        var result = TranslationWindowPlacement.Near(
            new PixelRect(-1920, -1080, 800, 600),
            scaling: 2,
            new PhysicalScreenPoint(-1500, -700),
            logicalWidth: 900,
            logicalHeight: 700,
            logicalOffset: 20);

        Assert.AreEqual(new PixelPoint(-1920, -1080), result);
    }

    [TestMethod]
    public void ClampToArea_RepositionsAWindowThatGrewBeyondTheWorkingArea()
    {
        var result = TranslationWindowPlacement.ClampToArea(
            new PixelRect(0, 0, 1920, 1040),
            scaling: 1.5,
            new PixelPoint(1300, 800),
            logicalWidth: 450,
            logicalHeight: 350);

        Assert.AreEqual(new PixelPoint(1245, 515), result);
    }

    [TestMethod]
    public void ClampToArea_RepositionsWithinANegativeOriginWorkingArea()
    {
        var result = TranslationWindowPlacement.ClampToArea(
            new PixelRect(-2560, -1400, 2560, 1360),
            scaling: 2,
            new PixelPoint(-600, -200),
            logicalWidth: 450,
            logicalHeight: 350);

        Assert.AreEqual(new PixelPoint(-900, -740), result);
    }
}
