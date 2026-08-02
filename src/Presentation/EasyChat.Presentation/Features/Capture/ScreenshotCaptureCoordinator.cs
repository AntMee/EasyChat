using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.ImageTranslation;
using EasyChat.Views.Overlay;

namespace EasyChat.Presentation.Features.Capture;

public sealed record ScreenshotSelection(Bitmap Image, CaptureOverlayAction Action) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

public sealed class ScreenshotCaptureCoordinator(
    IScreenCatalog screens,
    IScreenCapture capture)
{
    private readonly IScreenCatalog _screens = screens;
    private readonly IScreenCapture _capture = capture;

    public async Task<ScreenshotSelection?> CaptureAsync(
        string? mode,
        CancellationToken cancellationToken = default)
    {
        var availableScreens = await _screens.GetScreensAsync(cancellationToken)
            .ConfigureAwait(false);
        if (availableScreens.Count == 0)
            throw new InvalidOperationException("No display screen is available.");

        var bounds = Union(availableScreens.Select(screen => screen.Bounds));
        var captured = await _capture.CaptureAsync(
            new ScreenCaptureRequest(ScreenCaptureTarget.Region, Region: bounds),
            cancellationToken).ConfigureAwait(false);
        if (captured.IsFailure)
            throw new InvalidOperationException(captured.Error.Message);

        using var bitmap = AvaloniaImageFrames.ToBitmap(captured.Value);
        var completion = new TaskCompletionSource<ScreenshotSelection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = await OnUiAsync(() =>
        {
            var view = new OverlayWindowView(
                bounds,
                bitmap,
                precise: !string.Equals(mode, "Quick", StringComparison.OrdinalIgnoreCase));
            view.SelectionCompleted += (image, action) =>
                completion.TrySetResult(new ScreenshotSelection(image, action));
            view.SelectionCanceled += () => completion.TrySetResult(null);
            view.Show();
            view.Activate();
            return view;
        }, cancellationToken);

        using var cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                overlay.Close();
            }));
        return await completion.Task.ConfigureAwait(false);
    }

    private static ScreenRegion Union(IEnumerable<ScreenRegion> regions)
    {
        var all = regions.ToArray();
        var left = all.Min(region => region.X);
        var top = all.Min(region => region.Y);
        var right = all.Max(region => region.X + region.Width);
        var bottom = all.Max(region => region.Y + region.Height);
        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private static async ValueTask<T> OnUiAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
            return action();
        return await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
    }
}
