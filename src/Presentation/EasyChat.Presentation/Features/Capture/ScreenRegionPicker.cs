using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.ImageTranslation;
using EasyChat.Presentation.Features.Capture.Views;
using SukiUI.Toasts;

namespace EasyChat.Presentation.Features.Capture;

public interface IScreenRegionPicker
{
    ValueTask<PhysicalScreenRegion?> PickAsync(CancellationToken cancellationToken = default);
}

public sealed class AvaloniaScreenRegionPicker(
    IPlatformAccessUseCases platformAccess,
    IScreenCatalog screens,
    IScreenCapture capture,
    ISukiToastManager toasts) : IScreenRegionPicker
{
    private readonly IPlatformAccessUseCases _platformAccess = platformAccess;
    private readonly IScreenCatalog _screens = screens;
    private readonly IScreenCapture _capture = capture;
    private readonly ISukiToastManager _toasts = toasts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<PhysicalScreenRegion?> PickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var previousState = mainWindow?.WindowState ?? WindowState.Normal;
        try
        {
            var access = await _platformAccess.EnsureAvailableAsync(
                PlatformCapability.ScreenCapture,
                cancellationToken);
            if (access.IsFailure)
            {
                ShowError(access.Error.Message);
                return null;
            }

            if (mainWindow is not null)
            {
                mainWindow.WindowState = WindowState.Minimized;
                await Task.Delay(300, cancellationToken);
            }

            var availableScreens = await _screens.GetScreensAsync(cancellationToken);
            if (availableScreens.Count == 0)
                return null;
            var bounds = Union(availableScreens.Select(screen => screen.Bounds));
            var captured = await _capture.CaptureAsync(
                new ScreenCaptureRequest(ScreenCaptureTarget.Region, Region: bounds),
                cancellationToken);
            if (captured.IsFailure)
            {
                ShowError(captured.Error.Message);
                return null;
            }

            var completion = new TaskCompletionSource<PhysicalScreenRegion?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var overlay = new OverlayWindowView(
                bounds,
                AvaloniaImageFrames.ToBitmap(captured.Value),
                precise: true,
                regionOnly: true);
            overlay.RegionSelected += region => completion.TrySetResult(region);
            overlay.SelectionCanceled += () => completion.TrySetResult(null);
            using var cancellationRegistration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() =>
                {
                    completion.TrySetCanceled(cancellationToken);
                    overlay.Close();
                }));
            overlay.Show();
            overlay.Activate();
            return await completion.Task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return null;
        }
        finally
        {
            if (mainWindow is not null)
            {
                mainWindow.WindowState = previousState == WindowState.Minimized
                    ? WindowState.Normal
                    : previousState;
                mainWindow.Show();
                mainWindow.Activate();
            }
            _gate.Release();
        }
    }

    private static PhysicalScreenRegion Union(IEnumerable<PhysicalScreenRegion> regions)
    {
        var all = regions.ToArray();
        var left = all.Min(region => region.X);
        var top = all.Min(region => region.Y);
        var right = all.Max(region => region.X + region.Width);
        var bottom = all.Max(region => region.Y + region.Height);
        return new PhysicalScreenRegion(left, top, right - left, bottom - top);
    }

    private void ShowError(string message) => _toasts.CreateToast()
        .WithTitle(Lang.Resources.RequestError)
        .WithContent(message)
        .OfType(NotificationType.Error)
        .Queue();
}
