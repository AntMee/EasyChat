using System;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;

namespace EasyChat.Common;

public static class ClipboardHelper
{
    /// <summary>
    /// Backs up the current clipboard content.
    /// </summary>
    public static async Task<IAsyncDataTransfer?> BackupClipboardAsync(ILogger? logger = null)
    {
        try
        {
            var clipboard = GetClipboard();
            return clipboard == null ? null : await clipboard.TryGetDataAsync();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to back up clipboard");
            return null;
        }
    }

    /// <summary>
    /// Restores the clipboard content from backup.
    /// </summary>
    public static async Task RestoreClipboardAsync(IAsyncDataTransfer? backup, ILogger? logger = null)
    {
        if (backup == null) return;

        try 
        {
            var clipboard = GetClipboard();
            if (clipboard == null)
            {
                backup.Dispose();
                return;
            }

            // SetDataAsync takes ownership and disposes the transfer when it is no longer needed.
            await clipboard.SetDataAsync(backup);
        }
        catch (Exception ex)
        {
            backup.Dispose();
            logger?.LogWarning(ex, "Failed to restore clipboard data");
        }
    }

    private static IClipboard? GetClipboard()
    {
        return (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
    }
}
