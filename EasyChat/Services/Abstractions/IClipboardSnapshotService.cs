using Microsoft.Extensions.Logging;

namespace EasyChat.Services.Abstractions;

/// <summary>
/// Captures and restores the clipboard without exposing platform clipboard APIs.
/// </summary>
public interface IClipboardSnapshotService
{
    IClipboardSnapshot? Backup(ILogger? logger = null);

    void Restore(IClipboardSnapshot? snapshot, ILogger? logger = null);

    void RestoreIfUnchanged(
        IClipboardSnapshot? snapshot,
        uint expectedChangeToken,
        ILogger? logger = null);

    /// <summary>
    /// Returns a platform-specific token that changes when clipboard content changes.
    /// </summary>
    uint GetChangeToken();
}
