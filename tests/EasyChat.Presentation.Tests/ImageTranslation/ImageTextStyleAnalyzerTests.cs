using Avalonia.Media;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.ImageTranslation;

namespace EasyChat.Presentation.Tests.ImageTranslation;

[TestClass]
public sealed class ImageTextStyleAnalyzerTests
{
    private static readonly Color LightBackground = Color.FromRgb(245, 245, 245);
    private static readonly Color DarkForeground = Color.FromRgb(24, 24, 24);

    [TestMethod]
    public void Analyze_DistinguishesThinAndBoldStrokesAtTheSameLineHeight()
    {
        var thin = CreateHorizontalStrokeSample(LightBackground, DarkForeground, strokeWidth: 2);
        var bold = CreateHorizontalStrokeSample(LightBackground, DarkForeground, strokeWidth: 3);
        var analyzer = new ImageTextStyleAnalyzer();

        var thinStyle = analyzer.Analyze(thin.Frame, thin.Region);
        var boldStyle = analyzer.Analyze(bold.Frame, bold.Region);

        Assert.AreEqual(FontWeight.Normal, thinStyle.FontWeight);
        Assert.AreEqual(FontWeight.Bold, boldStyle.FontWeight);
    }

    [TestMethod]
    public void Analyze_DetectsLightBoldStrokesOnADarkBackground()
    {
        var sample = CreateHorizontalStrokeSample(
            Color.FromRgb(20, 20, 20),
            Color.FromRgb(235, 235, 235),
            strokeWidth: 3);

        var style = new ImageTextStyleAnalyzer().Analyze(sample.Frame, sample.Region);

        Assert.AreEqual(FontWeight.Bold, style.FontWeight);
        Assert.AreEqual(Color.FromRgb(235, 235, 235), style.Foreground);
    }

    [TestMethod]
    public void Analyze_DetectsColouredBoldStrokes()
    {
        var foreground = Color.FromRgb(35, 95, 220);
        var sample = CreateHorizontalStrokeSample(LightBackground, foreground, strokeWidth: 3);

        var style = new ImageTextStyleAnalyzer().Analyze(sample.Frame, sample.Region);

        Assert.AreEqual(FontWeight.Bold, style.FontWeight);
        Assert.AreEqual(foreground, style.Foreground);
    }

    [TestMethod]
    public void Analyze_DetectsBoldStrokesInsideARotatedPolygon()
    {
        const int width = 120;
        const int height = 100;
        const double centerX = 60;
        const double centerY = 50;
        const double angle = 30;
        var pixels = CreatePixels(width, height, LightBackground);
        var radians = angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var strokeCenters = new[] { -24d, -16d, -8d, 0d, 8d, 16d, 24d };
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x + 0.5 - centerX;
                var dy = y + 0.5 - centerY;
                var localX = dx * cosine + dy * sine;
                var localY = -dx * sine + dy * cosine;
                if (Math.Abs(localY) <= 8
                    && strokeCenters.Any(center => Math.Abs(localX - center) <= 1.75))
                {
                    SetPixel(pixels, width, x, y, DarkForeground);
                }
            }
        }

        var region = RotatedRegion("rotated", centerX, centerY, 64, 20, angle);
        var frame = new ImageFrame(width, height, width * 4, 96, 96, pixels);

        var style = new ImageTextStyleAnalyzer().Analyze(frame, region);

        Assert.AreEqual(FontWeight.Bold, style.FontWeight);
    }

    [TestMethod]
    public void Analyze_DefaultsToNormalForSmallOrLowContrastSamples()
    {
        var small = CreateHorizontalStrokeSample(
            LightBackground,
            DarkForeground,
            strokeWidth: 3,
            lineHeight: 10);
        var lowContrast = CreateHorizontalStrokeSample(
            Color.FromRgb(240, 240, 240),
            Color.FromRgb(239, 239, 239),
            strokeWidth: 3);
        var analyzer = new ImageTextStyleAnalyzer();

        Assert.AreEqual(FontWeight.Normal, analyzer.Analyze(small.Frame, small.Region).FontWeight);
        Assert.AreEqual(FontWeight.Normal, analyzer.Analyze(lowContrast.Frame, lowContrast.Region).FontWeight);
    }

    [TestMethod]
    public void Analyze_RemovesIsolatedPixelNoise()
    {
        const int width = 80;
        const int height = 36;
        var pixels = CreatePixels(width, height, LightBackground);
        var region = RectangleRegion("noise", 8, 8, 64, 20);
        for (var index = 0; index < 30; index++)
        {
            var x = 10 + index * 2 % 60;
            var y = 10 + index * 7 % 16;
            SetPixel(pixels, width, x, y, DarkForeground);
        }

        var frame = new ImageFrame(width, height, width * 4, 96, 96, pixels);

        var style = new ImageTextStyleAnalyzer().Analyze(frame, region);

        Assert.AreEqual(FontWeight.Normal, style.FontWeight);
    }

    [TestMethod]
    public void Analyze_PreservesBoldClassificationWhenLargeRegionsAreDownsampled()
    {
        const int width = 2100;
        const int height = 220;
        var pixels = CreatePixels(width, height, LightBackground);
        var region = RectangleRegion("large", 50, 30, 2000, 160);
        for (var x = 100; x < 2020; x += 80)
            FillRectangle(pixels, width, height, x, 40, 24, 140, DarkForeground);

        var frame = new ImageFrame(width, height, width * 4, 96, 96, pixels);

        var style = new ImageTextStyleAnalyzer().Analyze(frame, region);

        Assert.IsGreaterThan(1, ImageTextStyleAnalyzer.CalculateSampleStep(2000, 160));
        Assert.AreEqual(FontWeight.Bold, style.FontWeight);
    }

    [TestMethod]
    public void Analyze_UsesBoldForMergedRegionsOnlyWhenEverySourceRegionIsBold()
    {
        const int width = 80;
        const int height = 70;
        var allBoldPixels = CreatePixels(width, height, LightBackground);
        var mixedPixels = CreatePixels(width, height, LightBackground);
        var first = RectangleRegion("first", 8, 8, 64, 20);
        var second = RectangleRegion("second", 8, 38, 64, 20);
        DrawVerticalStrokes(allBoldPixels, width, height, first, 3, DarkForeground);
        DrawVerticalStrokes(allBoldPixels, width, height, second, 3, DarkForeground);
        DrawVerticalStrokes(mixedPixels, width, height, first, 3, DarkForeground);
        DrawVerticalStrokes(mixedPixels, width, height, second, 2, DarkForeground);
        var merged = RectangleRegion("first\nsecond", 8, 8, 64, 50);
        var allBoldFrame = new ImageFrame(width, height, width * 4, 96, 96, allBoldPixels);
        var mixedFrame = new ImageFrame(width, height, width * 4, 96, 96, mixedPixels);
        var analyzer = new ImageTextStyleAnalyzer();

        var boldStyle = analyzer.Analyze(allBoldFrame, merged, [first, second]);
        var mixedStyle = analyzer.Analyze(mixedFrame, merged, [first, second]);

        Assert.AreEqual(FontWeight.Bold, boldStyle.FontWeight);
        Assert.AreEqual(FontWeight.Normal, mixedStyle.FontWeight);
    }

    private static StrokeSample CreateHorizontalStrokeSample(
        Color background,
        Color foreground,
        int strokeWidth,
        int lineHeight = 20)
    {
        const int width = 80;
        var height = lineHeight + 16;
        var pixels = CreatePixels(width, height, background);
        var region = RectangleRegion("sample", 8, 8, 64, lineHeight);
        DrawVerticalStrokes(pixels, width, height, region, strokeWidth, foreground);
        return new StrokeSample(
            new ImageFrame(width, height, width * 4, 96, 96, pixels),
            region);
    }

    private static void DrawVerticalStrokes(
        byte[] pixels,
        int width,
        int height,
        OcrTextRegion region,
        int strokeWidth,
        Color color)
    {
        var left = (int)region.Polygon.Min(point => point.X);
        var top = (int)region.Polygon.Min(point => point.Y);
        var bottom = (int)region.Polygon.Max(point => point.Y);
        for (var x = left + 6; x < left + 58; x += 9)
            FillRectangle(pixels, width, height, x, top + 2, strokeWidth, bottom - top - 4, color);
    }

    private static OcrTextRegion RectangleRegion(
        string text,
        double x,
        double y,
        double width,
        double height) =>
        new(
            text,
            [
                new ImagePoint(x, y),
                new ImagePoint(x + width, y),
                new ImagePoint(x + width, y + height),
                new ImagePoint(x, y + height)
            ],
            0);

    private static OcrTextRegion RotatedRegion(
        string text,
        double centerX,
        double centerY,
        double width,
        double height,
        double angle)
    {
        var radians = angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        ImagePoint Point(double localX, double localY) =>
            new(
                centerX + localX * cosine - localY * sine,
                centerY + localX * sine + localY * cosine);

        return new OcrTextRegion(
            text,
            [
                Point(-width / 2, -height / 2),
                Point(width / 2, -height / 2),
                Point(width / 2, height / 2),
                Point(-width / 2, height / 2)
            ],
            angle);
    }

    private static byte[] CreatePixels(int width, int height, Color color)
    {
        var pixels = new byte[checked(width * height * 4)];
        FillRectangle(pixels, width, height, 0, 0, width, height, color);
        return pixels;
    }

    private static void FillRectangle(
        byte[] pixels,
        int imageWidth,
        int imageHeight,
        int left,
        int top,
        int width,
        int height,
        Color color)
    {
        var right = Math.Min(imageWidth, left + width);
        var bottom = Math.Min(imageHeight, top + height);
        for (var y = Math.Max(0, top); y < bottom; y++)
        {
            for (var x = Math.Max(0, left); x < right; x++)
                SetPixel(pixels, imageWidth, x, y, color);
        }
    }

    private static void SetPixel(byte[] pixels, int imageWidth, int x, int y, Color color)
    {
        var offset = (y * imageWidth + x) * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = byte.MaxValue;
    }

    private sealed record StrokeSample(ImageFrame Frame, OcrTextRegion Region);
}
