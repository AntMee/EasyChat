using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using EasyChat.Contracts.Platform;
using SukiUI.Toasts;

namespace EasyChat.Presentation.Features.Capture;

public interface IScreenRegionPicker
{
    ValueTask<PhysicalScreenRegion?> PickAsync(CancellationToken cancellationToken = default);
}

public sealed class AvaloniaScreenRegionPicker(
    IPlatformAccessUseCases platformAccess,
    CaptureOverlayCoordinator overlays,
    ISukiToastManager toasts) : IScreenRegionPicker
{
    private readonly IPlatformAccessUseCases _platformAccess = platformAccess;
    private readonly CaptureOverlayCoordinator _overlays = overlays;
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

            var outcome = await _overlays.SelectAsync(
                precise: true,
                regionOnly: true,
                cancellationToken: cancellationToken);
            return outcome?.Region;
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

    private void ShowError(string message) => _toasts.CreateToast()
        .WithTitle(Lang.Resources.RequestError)
        .WithContent(message)
        .OfType(NotificationType.Error)
        .Queue();
}
