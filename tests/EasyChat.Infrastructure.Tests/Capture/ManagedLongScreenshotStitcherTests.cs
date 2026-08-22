using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Capture;

namespace EasyChat.Infrastructure.Tests.Capture;

[TestClass]
public sealed class ManagedLongScreenshotStitcherTests
{
    [TestMethod]
    public void Match_VerticalFrames_ReturnsViewportOverlap()
    {
        var stitcher = new ManagedLongScreenshotStitcher();

        var placement = stitcher.Match(CreateVerticalFrame(8, 80, 0), CreateVerticalFrame(8, 80, 40), LongScreenshotAxis.Vertical);

        Assert.AreEqual(40, placement.Overlap);
        Assert.IsGreaterThan(0.5d, placement.Confidence);
        Assert.IsGreaterThanOrEqualTo(0, placement.SeamStart);
        Assert.IsGreaterThan(0, placement.SeamLength);
    }

    [TestMethod]
    public void Compose_UsesCumulativePlacementsForMultipleFrames()
    {
        var stitcher = new ManagedLongScreenshotStitcher();
        var frames = new[]
        {
            CreateVerticalFrame(8, 80, 0),
            CreateVerticalFrame(8, 80, 40),
            CreateVerticalFrame(8, 80, 80)
        };

        var composed = stitcher.Compose(
            frames,
            [new LongScreenshotPlacement(40, 1, Offset: 40), new LongScreenshotPlacement(40, 1, Offset: 80)],
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(160, composed.Height);
        Assert.AreEqual((byte)159, composed.Pixels.Span[(composed.Height - 1) * composed.Stride]);
    }

    [TestMethod]
    public void Match_HorizontalFrames_ReturnsViewportOverlap()
    {
        var stitcher = new ManagedLongScreenshotStitcher();

        var placement = stitcher.Match(CreateHorizontalFrame(80, 8, 0), CreateHorizontalFrame(80, 8, 40), LongScreenshotAxis.Horizontal);

        Assert.AreEqual(40, placement.Overlap);
        Assert.IsGreaterThan(0.5d, placement.Confidence);
    }

    [TestMethod]
    public void Match_LargerTail_ReturnsViewportOverlap()
    {
        var stitcher = new ManagedLongScreenshotStitcher();

        var placement = stitcher.Match(
            CreateVerticalFrame(8, 240, 0),
            CreateVerticalFrame(8, 80, 200),
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(40, placement.Overlap);
    }

    [TestMethod]
    public void Compose_VerticalSeam_PreservesEveryContentRowOnce()
    {
        var stitcher = new ManagedLongScreenshotStitcher();
        var frames = new[]
        {
            CreateVerticalFrame(8, 80, 0),
            CreateVerticalFrame(8, 80, 40),
            CreateVerticalFrame(8, 80, 80)
        };

        var composed = stitcher.Compose(
            frames,
            [
                new LongScreenshotPlacement(40, 1, Offset: 40, SeamStart: 14, SeamLength: 12),
                new LongScreenshotPlacement(40, 1, Offset: 80, SeamStart: 14, SeamLength: 12)
            ],
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(160, composed.Height);
        for (var row = 0; row < composed.Height; row++)
            Assert.AreEqual((byte)row, composed.Pixels.Span[row * composed.Stride]);
    }

    [TestMethod]
    public void Compose_HorizontalSeam_PreservesEveryContentColumnOnce()
    {
        var stitcher = new ManagedLongScreenshotStitcher();
        var frames = new[]
        {
            CreateHorizontalFrame(80, 8, 0),
            CreateHorizontalFrame(80, 8, 40),
            CreateHorizontalFrame(80, 8, 80)
        };

        var composed = stitcher.Compose(
            frames,
            [
                new LongScreenshotPlacement(40, 1, Offset: 40, SeamStart: 14, SeamLength: 12),
                new LongScreenshotPlacement(40, 1, Offset: 80, SeamStart: 14, SeamLength: 12)
            ],
            LongScreenshotAxis.Horizontal);

        Assert.AreEqual(160, composed.Width);
        for (var column = 0; column < composed.Width; column++)
            Assert.AreEqual((byte)column, composed.Pixels.Span[column * 4]);
    }

    private static ImageFrame CreateVerticalFrame(int width, int height, int firstRow)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                pixels[row * stride + column * 4] = (byte)(firstRow + row);
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static ImageFrame CreateHorizontalFrame(int width, int height, int firstColumn)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                pixels[row * stride + column * 4] = (byte)(firstColumn + column);
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }
}
