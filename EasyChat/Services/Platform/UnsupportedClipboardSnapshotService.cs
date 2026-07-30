using Microsoft.Extensions.Logging;
using EasyChat.Services.Abstractions;

namespace EasyChat.Services.Platform;

/// <summary>
/// Placeholder for platforms that do not have a clipboard adapter yet.
/// </summary>
public sealed class UnsupportedClipboardSnapshotService : IClipboardSnapshotService
{
    public IClipboardSnapshot? Backup(ILogger? logger = null)
    {
        logger?.LogDebug("Clipboard snapshots are not supported on this platform.");
        return null;
    }

    public void Restore(IClipboardSnapshot? snapshot, ILogger? logger = null)
    {
        snapshot?.Dispose();
    }

    public void RestoreIfUnchanged(
        IClipboardSnapshot? snapshot,
        uint expectedChangeToken,
        ILogger? logger = null)
    {
        snapshot?.Dispose();
    }

    public uint GetChangeToken() => 0;
}
