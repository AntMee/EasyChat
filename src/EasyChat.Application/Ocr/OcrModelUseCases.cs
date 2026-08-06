using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;

namespace EasyChat.Application.Ocr;

public sealed class OcrModelUseCases : IOcrModelUseCases
{
    private readonly IOcrModelStore _models;
    private readonly ISettingsUseCases _settings;

    public OcrModelUseCases(IOcrModelStore models, ISettingsUseCases settings)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<OcrLanguage> SupportedLanguages => _models.SupportedLanguages;

    public bool CanDeleteModels => _models.CanDeleteModels;

    public bool IsModelDownloaded(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return _models.IsModelDownloaded(language);
    }

    public Task DownloadModelAsync(
        OcrLanguage language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(language);
        var settings = _settings.Current;
        return _models.DownloadModelAsync(
            language,
            new OcrModelDownloadOptions(settings.Proxy.ProxyUrl, settings.Ocr.UseProxy),
            progress,
            cancellationToken);
    }

    public void DeleteModel(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _models.DeleteModel(language);
    }
}
