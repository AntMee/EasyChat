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

    public async Task<SelectedTextSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await WaitForModifierKeysReleasedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var (x, y) = _platformService.GetCursorPosition();
        var backup = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => ClipboardHelper.BackupClipboardAsync(_logger));
        try
        {
            var text = await _platformService.GetSelectedTextAsync(x, y);
            return string.IsNullOrWhiteSpace(text) ? null : new SelectedTextSnapshot(text, x, y);
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => ClipboardHelper.RestoreClipboardAsync(backup, _logger));
        }
    }

    private static async Task WaitForModifierKeysReleasedAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var control = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (!control && !alt && !shift) return;
            await Task.Delay(10, cancellationToken);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
