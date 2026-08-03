using Avalonia.Media.Imaging;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Capture.Views;

namespace EasyChat.Presentation.Features.Capture;

public sealed record ScreenshotSelection(
    Bitmap Image,
    CaptureOverlayAction Action,
    PhysicalScreenPoint CompletionPoint) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

public sealed class ScreenshotCaptureCoordinator(CaptureOverlayCoordinator overlays)
{
    private readonly CaptureOverlayCoordinator _overlays = overlays;

    public async Task<ScreenshotSelection?> CaptureAsync(
        string? mode,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _overlays.SelectAsync(
            precise: !string.Equals(mode, "Quick", StringComparison.OrdinalIgnoreCase),
            regionOnly: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (outcome is null)
            return null;
        if (outcome.Image is null)
            throw new InvalidOperationException("Screenshot selection did not produce an image.");
        return new ScreenshotSelection(
            outcome.Image,
            outcome.Action,
            outcome.CompletionPoint);
    }
}
