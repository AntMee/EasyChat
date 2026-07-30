using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EasyChat.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.TextAssist;

public sealed class SelectedTextCaptureService : ISelectedTextCaptureService
{
    private readonly IPlatformService _platformService;
    private readonly IClipboardSnapshotService _clipboardSnapshotService;
    private readonly ILogger<SelectedTextCaptureService> _logger;

    public SelectedTextCaptureService(
        IPlatformService platformService,
        IClipboardSnapshotService clipboardSnapshotService,
        ILogger<SelectedTextCaptureService> logger)
    {
        _platformService = platformService;
        _clipboardSnapshotService = clipboardSnapshotService;
        _logger = logger;
    }

    public Task<SelectedTextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        return CaptureAsync(false, cancellationToken);
    }

    public Task<SelectedTextSnapshot?> CaptureViaCopyAsync(CancellationToken cancellationToken = default)
    {
        return CaptureAsync(true, cancellationToken);
    }

    public async Task<SelectedTextSnapshot?> CaptureAllViaCopyAsync(CancellationToken cancellationToken = default)
    {
        if (!await WaitForModifierKeysReleasedAsync(cancellationToken))
        {
            _logger.LogDebug("Input capture cancelled because shortcut modifier keys were not released.");
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!_platformService.TrySelectAllText())
        {
            _logger.LogDebug("Native select-all is unavailable; falling back to Ctrl+A.");
            await _platformService.SendKeyCombinationAsync("Ctrl + A");
        }
        await Task.Delay(50, cancellationToken);

        // This operation explicitly opts into clipboard capture because the input
        // translation workflow is designed around copying the complete field.
        return await CaptureAsync(true, cancellationToken);
    }

    private async Task<SelectedTextSnapshot?> CaptureAsync(bool copyOnly, CancellationToken cancellationToken)
    {
        if (!await WaitForModifierKeysReleasedAsync(cancellationToken))
        {
            _logger.LogDebug("Text capture cancelled because shortcut modifier keys were not released.");
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var (x, y) = _platformService.GetCursorPosition();
        IClipboardSnapshot? backup = null;
        uint? clipboardSequenceAfterCopy = null;
        try
        {
            // Standard edit controls can be read without touching the clipboard.
            // The native clipboard fallback is delayed until this path fails.
            var text = copyOnly
                ? null
                : await _platformService.GetSelectedTextDirectAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                backup = await Task.Run(() => _clipboardSnapshotService.Backup(_logger));
                if (backup == null)
                {
                    _logger.LogWarning("Clipboard capture skipped because a complete OLE snapshot was unavailable.");
                    return null;
                }

                text = await _platformService.GetSelectedTextAsync(x, y, copyOnly: true);
                clipboardSequenceAfterCopy = _clipboardSnapshotService.GetChangeToken();
            }

            return string.IsNullOrWhiteSpace(text) ? null : new SelectedTextSnapshot(text, x, y);
        }
        finally
        {
            if (backup != null)
            {
                var expectedSequence = clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken();
                await Task.Run(() => _clipboardSnapshotService.RestoreIfUnchanged(
                    backup,
                    expectedSequence,
                    _logger));
            }
        }
    }

    private static async Task<bool> WaitForModifierKeysReleasedAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 200; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var control = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            var leftWindows = (GetAsyncKeyState(0x5B) & 0x8000) != 0;
            var rightWindows = (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            var c = (GetAsyncKeyState(0x43) & 0x8000) != 0;
            if (!control && !alt && !shift && !leftWindows && !rightWindows && !c) return true;
            await Task.Delay(10, cancellationToken);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
