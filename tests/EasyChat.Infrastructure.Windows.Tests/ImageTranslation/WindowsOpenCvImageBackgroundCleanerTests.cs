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
