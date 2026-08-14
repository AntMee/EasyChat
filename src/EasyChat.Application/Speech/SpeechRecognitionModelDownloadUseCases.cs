using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;

namespace EasyChat.Application.Speech;

public sealed class SpeechRecognitionModelDownloadUseCases : ISpeechRecognitionModelDownloadUseCases
{
    private readonly ISpeechRecognitionModelDownloadStore _models;
    private readonly ISettingsUseCases _settings;

    public SpeechRecognitionModelDownloadUseCases(
        ISpeechRecognitionModelDownloadStore models,
        ISettingsUseCases settings)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<SpeechRecognitionModelDownloadPackage> ModelPackages => _models.ModelPackages;

    public Task<SpeechRecognitionModelImportResult> DownloadModelAsync(
        SpeechRecognitionModelDownloadPackage package,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var proxy = _settings.Current.NetworkProxy;
        return _models.DownloadModelAsync(
            package,
            new SpeechRecognitionModelDownloadOptions(proxy.Mode, proxy.ProxyUrl),
            progress,
            cancellationToken);
    }
}
