using System.Runtime.CompilerServices;
using EasyChat.Contracts.Capture;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Capture;

public sealed class ScreenshotUseCases(
    ISettingsUseCases settings,
    ITranslationLanguageCatalog languages,
    IOcrRecognitionUseCases ocr,
    ITranslationUseCases translation,
    IImageTranslationUseCases imageTranslation) : IScreenshotUseCases
{
    private readonly ISettingsUseCases _settings = settings;
    private readonly ITranslationLanguageCatalog _languages = languages;
    private readonly IOcrRecognitionUseCases _ocr = ocr;
    private readonly ITranslationUseCases _translation = translation;
    private readonly IImageTranslationUseCases _imageTranslation = imageTranslation;

    public ValueTask<OcrRecognitionResult> RecognizeAsync(
        ImageFrame image,
        bool enableRotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var language = ResolveOcrLanguage(_settings.Current.General.SourceLanguage.Id);
        return _ocr.RecognizeAsync(
            new OcrRecognitionRequest(image, language, enableRotation),
            cancellationToken);
    }

    public async IAsyncEnumerable<TranslationEvent> TranslateTextAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var general = _settings.Current.General;
        var request = new TranslationRequest(
            text,
            _languages.Get(general.SourceLanguage.Id),
            _languages.Get(general.TargetLanguage.Id));
        await foreach (var item in _translation.StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<ImageTranslationResult> TranslateImageAsync(
        ImageFrame image,
        OcrRecognitionResult recognition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(recognition);
        var general = _settings.Current.General;
        return _imageTranslation.TranslateAsync(
            new ImageTranslationRequest(
                image,
                recognition,
                _languages.Get(general.SourceLanguage.Id),
                _languages.Get(general.TargetLanguage.Id)),
            cancellationToken);
    }

    private static OcrLanguage? ResolveOcrLanguage(string languageId)
    {
        if (string.Equals(languageId, OcrLanguages.Auto.Id, StringComparison.OrdinalIgnoreCase))
            return OcrLanguages.Auto;

        return OcrLanguages.Supported.FirstOrDefault(language =>
            string.Equals(language.Id, languageId, StringComparison.OrdinalIgnoreCase));
    }
}
