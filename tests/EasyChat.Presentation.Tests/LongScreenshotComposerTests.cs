using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Capture;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class LongScreenshotComposerTests
{
    [TestMethod]
    public void Compose_RemovesFixedViewportOverlap()
    {
        const int width = 2;
        const int viewportHeight = 80;
        const int overlap = 40;
        var first = CreateFrame(width, viewportHeight, 0);
        var second = CreateFrame(width, viewportHeight, viewportHeight - overlap);

        var composed = LongScreenshotComposer.Compose([first, second]);

        Assert.AreEqual(viewportHeight * 2 - overlap, composed.Height);
        AssertRows(composed, 0, composed.Height);
    }

    [TestMethod]
    public void Compose_StopsAtRepeatedViewport()
    {
        var first = CreateFrame(width: 2, height: 80, firstRow: 0);

        var composed = LongScreenshotComposer.Compose([first, first]);

        Assert.AreEqual(first.Height, composed.Height);
        CollectionAssert.AreEqual(first.Pixels.ToArray(), composed.Pixels.ToArray());
    }

    [TestMethod]
    public void Compose_RejectsDifferentWidths()
    {
        var first = CreateFrame(width: 2, height: 80, firstRow: 0);
        var second = CreateFrame(width: 3, height: 80, firstRow: 40);

        Assert.ThrowsExactly<ArgumentException>(
            () => LongScreenshotComposer.Compose([first, second]));
    }

    [TestMethod]
    public void Compose_UniformOverlapPrefersExpectedScrollDistance()
    {
        var first = CreateUniformFrame(width: 2, height: 80, markerRow: 0);
        var second = CreateUniformFrame(width: 2, height: 80, markerRow: 79);

        var composed = LongScreenshotComposer.Compose([first, second]);

        Assert.AreEqual(100, composed.Height);
    }

    [TestMethod]
    public void Compose_AllowsSmallOverlapAfterLargeManualScroll()
    {
        const int viewportHeight = 80;
        const int overlap = 5;
        var first = CreateFrame(width: 2, height: viewportHeight, firstRow: 0);
        var second = CreateFrame(width: 2, height: viewportHeight, firstRow: viewportHeight - overlap);

        var composed = LongScreenshotComposer.Compose([first, second]);

        Assert.AreEqual(viewportHeight * 2 - overlap, composed.Height);
        AssertRows(composed, 0, composed.Height);
    }

    [TestMethod]
    public void Compose_UsesFullFrameWhenThereIsNoOverlap()
    {
        const int viewportHeight = 80;
        var first = CreateFrame(width: 2, height: viewportHeight, firstRow: 0);
        var second = CreateFrame(width: 2, height: viewportHeight, firstRow: 1_000);

        var composed = LongScreenshotComposer.Compose([first, second]);

        Assert.AreEqual(viewportHeight * 2, composed.Height);
        AssertRows(composed, 0, viewportHeight);
        var secondRowOffset = viewportHeight * composed.Stride;
        Assert.AreEqual((byte)(1_000 % 256), composed.Pixels.Span[secondRowOffset]);
    }

    [TestMethod]
    public void Compose_Horizontally_RemovesFixedViewportOverlap()
    {
        const int height = 2;
        const int viewportWidth = 80;
        const int overlap = 40;
        var first = CreateHorizontalFrame(viewportWidth, height, 0);
        var second = CreateHorizontalFrame(viewportWidth, height, viewportWidth - overlap);

        var composed = LongScreenshotComposer.Compose(
            [first, second],
            LongScreenshotDirection.Horizontal);

        Assert.AreEqual(viewportWidth * 2 - overlap, composed.Width);
        AssertHorizontalColumns(composed, 0, composed.Width);
    }

    [TestMethod]
    public void Compose_Horizontally_RejectsDifferentHeights()
    {
        var first = CreateHorizontalFrame(width: 80, height: 2, firstColumn: 0);
        var second = CreateHorizontalFrame(width: 80, height: 3, firstColumn: 40);

        Assert.ThrowsExactly<ArgumentException>(() => LongScreenshotComposer.Compose(
            [first, second],
            LongScreenshotDirection.Horizontal));
    }

    [TestMethod]
    public void Compose_Horizontally_AllowsSmallOverlapAfterLargeManualScroll()
    {
        const int viewportWidth = 80;
        const int overlap = 5;
        var first = CreateHorizontalFrame(viewportWidth, height: 2, firstColumn: 0);
        var second = CreateHorizontalFrame(viewportWidth, height: 2, firstColumn: viewportWidth - overlap);

        var composed = LongScreenshotComposer.Compose(
            [first, second],
            LongScreenshotDirection.Horizontal);

        Assert.AreEqual(viewportWidth * 2 - overlap, composed.Width);
        AssertHorizontalColumns(composed, 0, composed.Width);
    }

    private static ImageFrame CreateFrame(int width, int height, int firstRow)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            var value = (byte)(firstRow + row);
            for (var x = 0; x < width; x++)
            {
                var offset = row * stride + x * 4;
                pixels[offset] = value;
                pixels[offset + 1] = (byte)(value + 17);
                pixels[offset + 2] = (byte)(value + 31);
                pixels[offset + 3] = 255;
            }
        }

        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static ImageFrame CreateUniformFrame(int width, int height, int markerRow)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = row * stride + x * 4;
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        for (var x = 0; x < width; x++)
            pixels[markerRow * stride + x * 4] = 0;
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static ImageFrame CreateHorizontalFrame(int width, int height, int firstColumn)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var value = (byte)(firstColumn + column);
                var offset = row * stride + column * 4;
                pixels[offset] = value;
                pixels[offset + 1] = (byte)(value + 17);
                pixels[offset + 2] = (byte)(value + 31);
                pixels[offset + 3] = 255;
            }
        }

        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static void AssertRows(ImageFrame frame, int firstRow, int rowCount)
    {
        for (var row = 0; row < rowCount; row++)
        {
            var value = (byte)(firstRow + row);
            var offset = row * frame.Stride;
            Assert.AreEqual(value, frame.Pixels.Span[offset]);
            Assert.AreEqual((byte)(value + 17), frame.Pixels.Span[offset + 1]);
            Assert.AreEqual((byte)(value + 31), frame.Pixels.Span[offset + 2]);
            Assert.AreEqual(255, frame.Pixels.Span[offset + 3]);
        }
    }

    private static void AssertHorizontalColumns(ImageFrame frame, int firstColumn, int columnCount)
    {
        for (var column = 0; column < columnCount; column++)
        {
            var value = (byte)(firstColumn + column);
            for (var row = 0; row < frame.Height; row++)
            {
                var offset = row * frame.Stride + column * 4;
                Assert.AreEqual(value, frame.Pixels.Span[offset]);
                Assert.AreEqual((byte)(value + 17), frame.Pixels.Span[offset + 1]);
                Assert.AreEqual((byte)(value + 31), frame.Pixels.Span[offset + 2]);
                Assert.AreEqual(255, frame.Pixels.Span[offset + 3]);
            }
        }
    }
}
