using Avalonia.Controls;

namespace EasyChat.Presentation.Foundation.Platform;

public interface IPlatformWindowBehavior
{
    ValueTask ConfigureNoActivateAsync(Window window, CancellationToken cancellationToken = default);
    ValueTask SetClickThroughAsync(Window window, bool enabled, CancellationToken cancellationToken = default);
}
