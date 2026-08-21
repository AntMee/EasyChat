using System.Runtime.Versioning;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.ImageTranslation;
using OpenCvSharp;

namespace EasyChat.Infrastructure.Windows.Tests.ImageTranslation;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenCvImageBackgroundCleanerTests
{
    [TestMethod]
    public void SelectFastInpaintStrategy_UsesTeleaForFlatBackground()
    {
        using var source = new Mat(new Size(96, 96), MatType.CV_8UC3, Scalar.All(128));
        using var erasedMask = CreateMask(source.Size());

        var strategy = WindowsOpenCvImageBackgroundCleaner.SelectFastInpaintStrategy(
            source,
            erasedMask,
            source.Rows * source.Cols);

        Assert.AreEqual(FastInpaintStrategy.Telea, strategy);
    }

    [TestMethod]
    public void SelectFastInpaintStrategy_UsesFsrBestForSmallTexturedBackground()
    {
        using var source = CreateCheckerboard(64, 64);
        using var erasedMask = CreateMask(source.Size());

        var strategy = WindowsOpenCvImageBackgroundCleaner.SelectFastInpaintStrategy(
            source,
            erasedMask,
            source.Rows * source.Cols);

        Assert.AreEqual(FastInpaintStrategy.FsrBest, strategy);
    }

    [TestMethod]
    public void SelectFastInpaintStrategy_UsesFsrFastForLargeTexturedBackground()
    {
        using var source = CreateCheckerboard(512, 512);
        using var erasedMask = CreateMask(source.Size());

        var strategy = WindowsOpenCvImageBackgroundCleaner.SelectFastInpaintStrategy(
            source,
            erasedMask,
            source.Rows * source.Cols);

        Assert.AreEqual(FastInpaintStrategy.FsrFast, strategy);
    }

    [TestMethod]
    public void RemoveText_FastModeOnlyChangesPixelsInsideTheOriginalPolygon()
    {
        const int width = 128;
        const int height = 128;
        var pixels = CreateCheckerboardPixels(width, height);
        var source = new ImageFrame(width, height, width * 4, 96, 96, pixels);
        var region = new OcrTextRegion(
            "text",
            [
                new ImagePoint(48, 48),
                new ImagePoint(80, 48),
                new ImagePoint(80, 80),
                new ImagePoint(48, 80)
            ],
            0);

        var result = WindowsOpenCvImageBackgroundCleaner.RemoveText(
            source,
            [region],
            ImageTextEraseMode.Fast,
            string.Empty);

        Assert.AreEqual(source.Width, result.Width);
        Assert.AreEqual(source.Height, result.Height);
        Assert.AreEqual(source.Stride, result.Stride);
        using var textMask = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(
            textMask,
            [[new Point(48, 48), new Point(80, 48), new Point(80, 80), new Point(48, 80)]],
            Scalar.All(255));
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (textMask.At<byte>(y, x) != 0)
                continue;

            var offset = (y * width + x) * 4;
            CollectionAssert.AreEqual(
                source.Pixels.Span.Slice(offset, 4).ToArray(),
                result.Pixels.Span.Slice(offset, 4).ToArray(),
                $"Pixel ({x}, {y}) outside the OCR polygon changed.");
        }
    }

    [TestMethod]
    public void RemoveText_FastModeClearsHighContrastTextOnFlatBlackBackground()
    {
        const int width = 128;
        const int height = 96;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
            pixels[index + 3] = 255;

        // Simulate several antialiased white glyph strokes inside one OCR region.
        for (var y = 36; y < 60; y++)
        for (var x = 34; x < 94; x++)
        {
            if (x % 11 is 0 or 1 or 2 || y % 9 is 0 or 1)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
            }
        }
        // Gray edge pixels model antialiasing; all pixels outside the selected
        // polygon, including neighboring glyphs, must remain untouched.
        for (var y = 40; y < 56; y++)
        {
            foreach (var x in new[] { 31, 97 })
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = 160;
                pixels[offset + 1] = 160;
                pixels[offset + 2] = 160;
            }

            var neighboringTextOffset = (y * width + 99) * 4;
            pixels[neighboringTextOffset] = 255;
            pixels[neighboringTextOffset + 1] = 255;
            pixels[neighboringTextOffset + 2] = 255;
        }

        var source = new ImageFrame(width, height, width * 4, 96, 96, pixels);
        var region = new OcrTextRegion(
            "text",
            [
                new ImagePoint(32, 32),
                new ImagePoint(96, 32),
                new ImagePoint(96, 64),
                new ImagePoint(32, 64)
            ],
            0);

        var result = WindowsOpenCvImageBackgroundCleaner.RemoveText(
            source,
            [region],
            ImageTextEraseMode.Fast,
            string.Empty);

        using var textMask = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(
            textMask,
            [[new Point(32, 32), new Point(96, 32), new Point(96, 64), new Point(32, 64)]],
            Scalar.All(255));
        for (var y = 32; y < 64; y++)
        for (var x = 32; x < 96; x++)
        {
            if (textMask.At<byte>(y, x) == 0)
                continue;

            var offset = (y * width + x) * 4;
            Assert.IsLessThanOrEqualTo(8, result.Pixels.Span[offset]);
            Assert.IsLessThanOrEqualTo(8, result.Pixels.Span[offset + 1]);
            Assert.IsLessThanOrEqualTo(8, result.Pixels.Span[offset + 2]);
        }

        for (var y = 40; y < 56; y++)
        {
            foreach (var x in new[] { 31, 97 })
            {
                var offset = (y * width + x) * 4;
                Assert.AreEqual((byte)160, result.Pixels.Span[offset]);
                Assert.AreEqual((byte)160, result.Pixels.Span[offset + 1]);
                Assert.AreEqual((byte)160, result.Pixels.Span[offset + 2]);
            }

            var neighboringTextOffset = (y * width + 99) * 4;
            Assert.AreEqual((byte)255, result.Pixels.Span[neighboringTextOffset]);
        }
    }

    [TestMethod]
    public void RemoveText_PreservesPaddedSourceStride()
    {
        const int width = 32;
        const int height = 32;
        const int stride = width * 4 + 8;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                pixels[y * stride + x * 4 + 3] = 255;
            for (var offset = width * 4; offset < stride; offset++)
                pixels[y * stride + offset] = 0xEE;
        }

        var source = new ImageFrame(width, height, stride, 96, 96, pixels);
        var region = new OcrTextRegion(
            "text",
            [
                new ImagePoint(8, 8),
                new ImagePoint(24, 8),
                new ImagePoint(24, 24),
                new ImagePoint(8, 24)
            ],
            0);

        var result = WindowsOpenCvImageBackgroundCleaner.RemoveText(
            source,
            [region],
            ImageTextEraseMode.Fast,
            string.Empty);

        Assert.AreEqual(stride, result.Stride);
        for (var y = 0; y < height; y++)
        for (var offset = width * 4; offset < stride; offset++)
            Assert.AreEqual((byte)0xEE, result.Pixels.Span[y * stride + offset]);
    }

    private static Mat CreateMask(Size size)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(size.Width / 3, size.Height / 3, 24, 24), Scalar.All(255), -1);
        return mask;
    }

    private static Mat CreateCheckerboard(int width, int height)
    {
        var source = new Mat(height, width, MatType.CV_8UC3);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = ((x / 4) + (y / 4)) % 2 == 0 ? (byte)32 : (byte)224;
            source.Set(y, x, new Vec3b(value, value, value));
        }

        return source;
    }

    private static byte[] CreateCheckerboardPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = ((x / 4) + (y / 4)) % 2 == 0 ? (byte)32 : (byte)224;
            var offset = (y * width + x) * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = (byte)((x + y) % 256);
        }

        return pixels;
    }
}
