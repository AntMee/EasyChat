using Avalonia.Controls;
using EasyChat.Infrastructure.Windows.Input;
using EasyChat.Presentation.Foundation.Platform;

namespace EasyChat.Desktop.Windows;

internal sealed class AvaloniaWindowsWindowBehavior(
    WindowsOwnedWindowBehavior windows) : IPlatformWindowBehavior
{
    public ValueTask ConfigureNoActivateAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        windows.ConfigureNoActivate(GetHandle(window));
        return ValueTask.CompletedTask;
    }

    public ValueTask BringToFrontWithoutActivatingAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        windows.BringToFrontWithoutActivating(GetHandle(window));
        return ValueTask.CompletedTask;
    }

    public void RestoreForegroundTextInputContext() =>
        windows.RestoreForegroundTextInputContext();

    public ValueTask SetClickThroughAsync(
        Window window,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        windows.SetClickThrough(GetHandle(window), enabled);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TrySetExcludedFromCaptureAsync(
        Window window,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(windows.TrySetExcludedFromCapture(GetHandle(window), enabled));
    }

    private static nint GetHandle(Window window) =>
        window.TryGetPlatformHandle()?.Handle
        ?? throw new InvalidOperationException("The Avalonia window does not have a native handle yet.");
}
