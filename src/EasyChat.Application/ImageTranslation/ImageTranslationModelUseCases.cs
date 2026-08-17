using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Settings;

namespace EasyChat.Application.ImageTranslation;

public sealed class ImageTranslationModelUseCases : IImageTranslationModelUseCases
{
    private readonly IImageTranslationModelStore _models;
    private readonly ISettingsUseCases _settings;

    public ImageTranslationModelUseCases(
        IImageTranslationModelStore models,
        ISettingsUseCases settings)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<ImageTranslationModelPackage> ModelPackages => _models.ModelPackages;

    public bool IsModelDownloaded(ImageTranslationModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _models.IsModelDownloaded(package);
    }

    public Task DownloadModelAsync(
        ImageTranslationModelPackage package,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var proxy = _settings.Current.NetworkProxy;
        return _models.DownloadModelAsync(
            package,
            proxy.Mode,
            proxy.ProxyUrl,
            progress,
            cancellationToken);
    }

    public void DeleteModel(ImageTranslationModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        _models.DeleteModel(package);
    }
}
