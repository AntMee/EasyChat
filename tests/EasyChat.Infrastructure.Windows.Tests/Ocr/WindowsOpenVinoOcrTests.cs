using System.Runtime.Versioning;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Ocr;

namespace EasyChat.Infrastructure.Windows.Tests.Ocr;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenVinoOcrTests
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
                    0,
                    0.84)
            ]
        };
        using var ocr = new WindowsOpenVinoOcr(backend);
        var frame = new ImageFrame(1, 1, 4, 96, 96, new byte[] { 1, 2, 3, 255 });

        var result = await ocr.RecognizeAsync(new OcrRecognitionRequest(
            frame,
            OcrLanguages.English,
            true,
            OcrRecognitionMode.Normal));

        Assert.AreEqual(OpenVinoOcrModelCatalog.UniversalV6SmallId, backend.Language?.Package.Package.Id);
        Assert.AreEqual(OcrLanguages.English.Id, backend.Language?.Language.Id);
        Assert.IsTrue(backend.EnableRotation);
        Assert.AreEqual(OcrRecognitionMode.Normal, backend.Mode);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255 }, backend.Pixels);
        Assert.AreEqual("text", result.Text);
        Assert.AreEqual(90d, result.Regions[0].Angle, 0.001);
        Assert.AreEqual(0.84, result.Regions[0].Confidence, 0.001);
    }

    private sealed class FakeOcrBackend : IWindowsOcrBackend
    {
        public IReadOnlyList<WindowsOcrBackendRegion> Regions { get; init; } = [];
        public WindowsOcrLanguageSelection? Language { get; private set; }
        public bool EnableRotation { get; private set; }
        public OcrRecognitionMode? Mode { get; private set; }
        public byte[]? Pixels { get; private set; }

        public bool IsModelAvailable(OpenVinoOcrModelPackageSpec package) => true;

        public Task DownloadModelAsync(
            OpenVinoOcrModelPackageSpec package,
            OcrModelDownloadOptions options,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void DeleteModel(OpenVinoOcrModelPackageSpec package)
        {
        }

        public IReadOnlyList<WindowsOcrBackendRegion> Recognize(
            ImageFrame image,
            WindowsOcrLanguageSelection language,
            bool enableRotation,
            OcrRecognitionMode mode,
            CancellationToken cancellationToken)
        {
            Language = language;
            EnableRotation = enableRotation;
            Mode = mode;
            Pixels = image.Pixels.ToArray();
            return Regions;
        }

        public void Dispose()
        {
        }
    }
}
