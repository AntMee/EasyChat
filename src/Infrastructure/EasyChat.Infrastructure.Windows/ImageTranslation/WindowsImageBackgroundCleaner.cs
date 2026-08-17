using System.Runtime.Versioning;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.ApplicationData;

namespace EasyChat.Infrastructure.Windows.ImageTranslation;

[SupportedOSPlatform("windows")]
public sealed class WindowsImageBackgroundCleaner : IImageBackgroundCleaner
{
    private readonly IApplicationDataPaths _applicationData;

    public WindowsImageBackgroundCleaner(IApplicationDataPaths applicationData)
    {
        _applicationData = applicationData ?? throw new ArgumentNullException(nameof(applicationData));
    }

    public Task<ImageFrame> RemoveTextAsync(
        ImageFrame source,
        IReadOnlyList<OcrTextRegion> regions,
        ImageTextEraseMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.PixelFormat != ImagePixelFormat.Bgra32)
            throw new NotSupportedException($"Pixel format '{source.PixelFormat}' is not supported.");
        if (regions.Count == 0)
            return Task.FromResult(source);
        if (mode == ImageTextEraseMode.Precise
            && !WindowsImageTranslationModelStore.AreModelFilesInstalled(
                _applicationData.ImageTranslationModelsDirectory))
        {
            throw new ImageTranslationModelNotDownloadedException(
                WindowsImageTranslationModelStore.AotGanModelPackage);
        }

        return Task.FromResult(WindowsImageBackgroundCleanerWorkerClient.RemoveText(
            source,
            regions,
            mode,
            _applicationData.ImageTranslationModelsDirectory,
            cancellationToken));
    }
}
