using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EasyChat.Common;
using EasyChat.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.TextAssist;

public sealed class SelectedTextCaptureService : ISelectedTextCaptureService
{
    private readonly IPlatformService _platformService;
    private readonly ILogger<SelectedTextCaptureService> _logger;

    public SelectedTextCaptureService(IPlatformService platformService, ILogger<SelectedTextCaptureService> logger)
    {
        _platformService = platformService;
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

        // Prefer direct edit-control text and WM_COPY now that the full range is
        // selected. CaptureAsync will synthesize Ctrl+C only if both native paths fail.
        return await CaptureAsync(false, cancellationToken);
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
        var backup = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => ClipboardHelper.BackupClipboardAsync(_logger));
        try
        {
            var text = await _platformService.GetSelectedTextAsync(x, y, copyOnly);
            return string.IsNullOrWhiteSpace(text) ? null : new SelectedTextSnapshot(text, x, y);
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => ClipboardHelper.RestoreClipboardAsync(backup, _logger));
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
