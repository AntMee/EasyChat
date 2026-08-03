using EasyChat.Contracts.Selection;
using EasyChat.Contracts.Shortcuts;
using Microsoft.Extensions.Logging;

namespace EasyChat;

public sealed class DesktopInteractionLifecycle(
    ISelectionInteractionUseCases selection,
    ISelectionInteractionSink selectionSink,
    IShortcutUseCases shortcuts,
    ILogger<DesktopInteractionLifecycle> logger) : IAsyncDisposable
{
    private int _started;
    private int _disposed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        try
        {
            selection.Start(selectionSink);
            _ = StartShortcutsAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to start interactive desktop services.");
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) != 0)
            selection.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
        await shortcuts.DisposeAsync().ConfigureAwait(false);
        await selection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StartShortcutsAsync()
    {
        try
        {
            var report = await shortcuts.StartAsync().ConfigureAwait(false);
            foreach (var issue in report.Issues)
            {
                logger.LogWarning(
                    "Unable to register shortcut {Action} ({Gesture}): {Message}",
                    issue.ActionType,
                    issue.KeyCombination,
                    issue.Error.Message);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to start desktop shortcuts.");
        }
    }
}
