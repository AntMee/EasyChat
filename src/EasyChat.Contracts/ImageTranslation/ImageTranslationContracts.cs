using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Translation;

namespace EasyChat.Contracts.ImageTranslation;

public sealed record ImageTranslationRequest(
    ImageFrame Image,
    OcrRecognitionResult Recognition,
    TranslationLanguage? SourceLanguage,
    TranslationLanguage TargetLanguage);

public sealed record ImageTranslationResult(
    ImageFrame Image,
    IReadOnlyList<string> Warnings,
    int DetectedBlockCount,
    int TranslatedBlockCount);

public sealed record ImageTranslationOverlay(OcrTextRegion Region, string Translation);

public sealed record ImageTranslationRenderResult(
    ImageFrame Image,
    IReadOnlyList<string> Warnings,
    int RenderedBlockCount);

public interface IImageBackgroundCleaner
{
    ImageFrame RemoveText(
        ImageFrame source,
        IReadOnlyList<OcrTextRegion> regions,
        CancellationToken cancellationToken = default);
}

public interface IImageTranslationRenderer
{
    Task<ImageTranslationRenderResult> RenderAsync(
        ImageFrame background,
        IReadOnlyList<ImageTranslationOverlay> overlays,
        CancellationToken cancellationToken = default);
}

public interface IImageTranslationUseCases
{
    Task<ImageTranslationResult> TranslateAsync(
        ImageTranslationRequest request,
        CancellationToken cancellationToken = default);
}
