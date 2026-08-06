using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Translation;

namespace EasyChat.Contracts.Capture;

public interface IScreenshotUseCases
{
    ValueTask<OcrRecognitionResult> RecognizeAsync(
        ImageFrame image,
        bool enableRotation,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TranslationEvent> TranslateTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<ImageTranslationResult> TranslateImageAsync(
        ImageFrame image,
        OcrRecognitionResult recognition,
        CancellationToken cancellationToken = default);
}
