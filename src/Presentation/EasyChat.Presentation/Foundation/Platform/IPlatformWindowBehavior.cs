using Avalonia.Controls;

namespace EasyChat.Presentation.Foundation.Platform;

public interface IPlatformWindowBehavior
{
    ValueTask ConfigureNoActivateAsync(Window window, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a floating window above other topmost windows without changing the
    /// foreground window or its text-input context.
    /// </summary>
    ValueTask BringToFrontWithoutActivatingAsync(Window window, CancellationToken cancellationToken = default);

    ValueTask SetClickThroughAsync(Window window, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to prevent a window from appearing in OS-level screen capture.
    /// Unsupported platforms return <see langword="false"/> so callers can
    /// use a visual fallback without depending on native APIs.
    /// </summary>
    ValueTask<bool> TrySetExcludedFromCaptureAsync(
        Window window,
        bool enabled,
        CancellationToken cancellationToken = default);
}
