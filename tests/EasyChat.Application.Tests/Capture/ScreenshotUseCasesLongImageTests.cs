using EasyChat.Application.Capture;
using EasyChat.Application.Tests;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;

namespace EasyChat.Application.Tests.Capture;

[TestClass]
public sealed class ScreenshotUseCasesLongImageTests
{
    [TestMethod]
    public async Task RecognizeAsync_LongVerticalImage_TilesOffsetsAndDeduplicatesOverlap()
    {
        var recognizer = new TileRecognizer(vertical: true);
        var useCases = CreateUseCases(recognizer);

        var result = await useCases.RecognizeAsync(CreateVerticalFrame(16, 4_000), enableRotation: false);

        Assert.HasCount(3, recognizer.Requests);
        Assert.HasCount(4, result.Regions);
        CollectionAssert.AreEqual(new[] { "top", "repeat", "middle", "bottom" }, result.Regions.Select(region => region.Text).ToArray());
        Assert.AreEqual(1_900d, result.Regions[1].Polygon[0].Y);
        Assert.AreEqual(0.9d, result.Regions[1].Confidence);
        Assert.AreEqual(2_156d, result.Regions[2].Polygon[0].Y);
        Assert.AreEqual(3_812d, result.Regions[3].Polygon[0].Y);
    }

    [TestMethod]
    public async Task RecognizeAsync_LongHorizontalImage_OffsetsPolygonsOnTheHorizontalAxis()
    {
        var recognizer = new TileRecognizer(vertical: false);
        var useCases = CreateUseCases(recognizer);

        var result = await useCases.RecognizeAsync(CreateHorizontalFrame(4_000, 16), enableRotation: false);

        Assert.HasCount(3, recognizer.Requests);
        CollectionAssert.AreEqual(new[] { "left", "repeat", "right" }, result.Regions.Select(region => region.Text).ToArray());
        Assert.AreEqual(1_900d, result.Regions[1].Polygon[0].X);
        Assert.AreEqual(3_812d, result.Regions[2].Polygon[0].X);
    }

    [TestMethod]
    public async Task RecognizeAsync_ShortImage_CallsOcrOnceWithoutCropping()
    {
        var recognizer = new TileRecognizer(vertical: true);
        var useCases = CreateUseCases(recognizer);
        var image = CreateVerticalFrame(16, 128);

        await useCases.RecognizeAsync(image, enableRotation: false);

        Assert.HasCount(1, recognizer.Requests);
        Assert.AreSame(image, recognizer.Requests[0].Image);
    }

    private static ScreenshotUseCases CreateUseCases(TileRecognizer recognizer) => new(
        new MutableSettingsUseCases(SettingsTestData.CreateBundle()),
        null!,
        recognizer,
        null!,
        null!);

    private static ImageFrame CreateVerticalFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                pixels[row * stride + column * 4] = (byte)row;
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private static ImageFrame CreateHorizontalFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                pixels[row * stride + column * 4] = (byte)column;
        return new ImageFrame(width, height, stride, 96, 96, pixels);
    }

    private sealed class TileRecognizer(bool vertical) : IOcrRecognitionUseCases
    {
        internal List<OcrRecognitionRequest> Requests { get; } = [];

        public ValueTask<OcrRecognitionResult> RecognizeAsync(
            OcrRecognitionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var offset = request.Image.Pixels.Span[0];
            return ValueTask.FromResult(new OcrRecognitionResult(vertical
                ? VerticalRegions(offset)
                : HorizontalRegions(offset)));
        }

        private static IReadOnlyList<OcrTextRegion> VerticalRegions(byte offset) => offset switch
        {
            0 => [Region("top", 10, 100), Region("repeat", 10, 1_900, 0.6)],
            64 => [Region("repeat", 10, 44, 0.9), Region("middle", 10, 300)],
            _ => [Region("bottom", 10, 100)]
        };

        private static IReadOnlyList<OcrTextRegion> HorizontalRegions(byte offset) => offset switch
        {
            0 => [Region("left", 100, 10), Region("repeat", 1_900, 10)],
            64 => [Region("repeat", 44, 10, 0.9)],
            _ => [Region("right", 100, 10)]
        };

        private static OcrTextRegion Region(string text, double x, double y, double confidence = 1d) => new(
            text,
            [new ImagePoint(x, y), new ImagePoint(x + 20, y), new ImagePoint(x + 20, y + 20), new ImagePoint(x, y + 20)],
            0,
            confidence);
    }
}
