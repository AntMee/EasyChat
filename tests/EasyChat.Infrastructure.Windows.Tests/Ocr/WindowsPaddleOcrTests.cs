using System.Runtime.Versioning;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Ocr;
using OpenCvSharp;

namespace EasyChat.Infrastructure.Windows.Tests.Ocr;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsPaddleOcrTests
{
    [TestMethod]
    public async Task RecognizeAsync_MapsBgraPixelsLanguageAndTextGeometry()
    {
        var backend = new FakeOcrBackend
        {
            Regions =
            [
                new WindowsOcrBackendRegion(
                    " text ",
                    [new WindowsOcrPoint(0, 0), new WindowsOcrPoint(0, 10)],
                    0)
            ]
        };
        backend.AvailableModels.Add(WindowsOcrModel.English);
        using var ocr = new WindowsPaddleOcr(backend);
        var frame = new ImageFrame(1, 1, 4, 96, 96, new byte[] { 1, 2, 3, 255 });

        var result = await ocr.RecognizeAsync(new OcrRecognitionRequest(
            frame,
            OcrLanguages.English,
            true));

        Assert.AreEqual(WindowsOcrModel.English, backend.Language?.Model);
        Assert.IsTrue(backend.EnableRotation);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, backend.Pixel);
        Assert.AreEqual("text", result.Text);
        Assert.AreEqual(90d, result.Regions[0].Angle, 0.001);
    }

    private sealed class FakeOcrBackend : IWindowsOcrBackend
    {
        public bool CanDeleteModels => true;
        public HashSet<WindowsOcrModel> AvailableModels { get; } = [];
        public IReadOnlyList<WindowsOcrBackendRegion> Regions { get; init; } = [];
        public WindowsOcrLanguageSelection? Language { get; private set; }
        public bool EnableRotation { get; private set; }
        public byte[]? Pixel { get; private set; }

        public bool IsModelAvailable(WindowsOcrLanguageSelection language) =>
            AvailableModels.Contains(language.Model);

        public Task DownloadModelAsync(
            WindowsOcrLanguageSelection language,
            OcrModelDownloadOptions options,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            AvailableModels.Add(language.Model);
            return Task.CompletedTask;
        }

        public void DeleteModel(WindowsOcrLanguageSelection language) =>
            AvailableModels.Remove(language.Model);

        public IReadOnlyList<WindowsOcrBackendRegion> Recognize(
            Mat image,
            WindowsOcrLanguageSelection language,
            bool enableRotation,
            CancellationToken cancellationToken)
        {
            Language = language;
            EnableRotation = enableRotation;
            var pixel = image.At<Vec3b>(0, 0);
            Pixel = [pixel.Item0, pixel.Item1, pixel.Item2];
            return Regions;
        }

        public void Dispose()
        {
        }
    }
}
