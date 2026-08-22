using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Capture;

namespace EasyChat.Infrastructure.Windows.Tests.Capture;

[TestClass]
public sealed class OpenCvLongScreenshotStitcherTests
{
    [TestMethod]
    public void Match_VerticalFrames_UsesNativeRegistration()
    {
        var stitcher = new OpenCvLongScreenshotStitcher();

        var placement = stitcher.Match(
            CreateFrame(width: 64, height: 120, axisOffset: 0, LongScreenshotAxis.Vertical),
            CreateFrame(width: 64, height: 120, axisOffset: 50, LongScreenshotAxis.Vertical),
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(70, placement.Overlap, $"Actual overlap={placement.Overlap}, confidence={placement.Confidence}");
        Assert.IsGreaterThan(0.5d, placement.Confidence);
        Assert.IsGreaterThanOrEqualTo(0, placement.SeamStart);
    }

    [TestMethod]
    public void Match_HorizontalFrames_UsesNativeRegistration()
    {
        var stitcher = new OpenCvLongScreenshotStitcher();

        var placement = stitcher.Match(
            CreateFrame(width: 120, height: 64, axisOffset: 0, LongScreenshotAxis.Horizontal),
            CreateFrame(width: 120, height: 64, axisOffset: 50, LongScreenshotAxis.Horizontal),
            LongScreenshotAxis.Horizontal);

        Assert.AreEqual(70, placement.Overlap);
        Assert.IsGreaterThan(0.5d, placement.Confidence);
        Assert.IsGreaterThanOrEqualTo(0, placement.SeamStart);
    }

    [TestMethod]
    public void Match_LargerComposedTail_StillFindsViewportOverlap()
    {
        var stitcher = new OpenCvLongScreenshotStitcher();

        var placement = stitcher.Match(
            CreateFrame(width: 64, height: 240, axisOffset: 0, LongScreenshotAxis.Vertical),
            CreateFrame(width: 64, height: 120, axisOffset: 170, LongScreenshotAxis.Vertical),
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(70, placement.Overlap, $"Actual overlap={placement.Overlap}, confidence={placement.Confidence}");
        Assert.IsGreaterThan(0.5d, placement.Confidence);
    }

    [TestMethod]
    public void Match_LowTextureGradient_RejectsAmbiguousTemplateShift()
    {
        var stitcher = new OpenCvLongScreenshotStitcher();

        var placement = stitcher.Match(
            CreateGradientFrame(width: 32, height: 120, axisOffset: 0),
            CreateGradientFrame(width: 32, height: 120, axisOffset: 50),
            LongScreenshotAxis.Vertical);

        Assert.AreEqual(70, placement.Overlap);
    }

    private static ImageFrame CreateFrame(int width, int height, int axisOffset, LongScreenshotAxis axis)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var coordinate = axis == LongScreenshotAxis.Vertical ? y + axisOffset : x + axisOffset;
                var fixedCoordinate = axis == LongScreenshotAxis.Vertical ? x : y;
                var pixel = y * stride + x * 4;
                pixels[pixel] = Noise(coordinate, fixedCoordinate, 0x9E3779B9u);
                pixels[pixel + 1] = Noise(coordinate, fixedCoordinate, 0x85EBCA6Bu);
                pixels[pixel + 2] = Noise(coordinate, fixedCoordinate, 0xC2B2AE35u);
                pixels[pixel + 3] = 255;
            }
        }
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static byte Noise(int coordinate, int fixedCoordinate, uint salt)
    {
        var value = unchecked((uint)coordinate * 0x9E3779B9u) ^
                    unchecked((uint)fixedCoordinate * 0x85EBCA6Bu) ^
                    salt;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return (byte)(value >> 24);
    }

    private static ImageFrame CreateGradientFrame(int width, int height, int axisOffset)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(y + axisOffset);
                var pixel = y * stride + x * 4;
                pixels[pixel] = value;
                pixels[pixel + 1] = value;
                pixels[pixel + 2] = value;
                pixels[pixel + 3] = 255;
            }
        }
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }
}
