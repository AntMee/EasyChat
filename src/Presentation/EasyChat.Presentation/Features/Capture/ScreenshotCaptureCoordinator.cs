using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Capture.Views;

namespace EasyChat.Presentation.Features.Capture;

public sealed record ScreenshotSelection(
    ImageFrame Image,
    CaptureOverlayAction Action,
    PhysicalScreenPoint CompletionPoint);

public interface IScreenshotCaptureSession
{
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default);

    Task<ScreenshotSelection?> CaptureAsync(
        bool precise,
        CancellationToken cancellationToken = default);
}

public sealed class ScreenshotCaptureCoordinator(
    IPlatformAccessUseCases platformAccess,
    IScreenshotCaptureSession session)
{
    private readonly IPlatformAccessUseCases _platformAccess = platformAccess;
    private readonly IScreenshotCaptureSession _session = session;

    public async Task<ScreenshotSelection?> CaptureAsync(
        string? mode,
        CancellationToken cancellationToken = default)
    {
        var access = await _platformAccess.EnsureAvailableAsync(
            PlatformCapability.ScreenCapture,
            cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
            throw new InvalidOperationException(access.Error.Message);

        return await _session.CaptureAsync(
            precise: !string.Equals(mode, "Quick", StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class InProcessScreenshotCaptureSession(CaptureOverlayCoordinator overlays)
    : IScreenshotCaptureSession
{
    private readonly CaptureOverlayCoordinator _overlays = overlays;

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async Task<ScreenshotSelection?> CaptureAsync(
        bool precise,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _overlays.SelectAsync(
            precise,
            regionOnly: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (outcome is null)
            return null;
        if (outcome.Image is null)
            throw new InvalidOperationException("Screenshot selection did not produce an image.");

        using (outcome.Image)
        {
            return new ScreenshotSelection(
                ImageTranslation.AvaloniaImageFrames.ToImageFrame(outcome.Image),
                outcome.Action,
                outcome.CompletionPoint);
        }
    }
}
