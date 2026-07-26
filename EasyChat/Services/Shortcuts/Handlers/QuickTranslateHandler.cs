using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.TextAssist;
using EasyChat.Views.Windows;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.Shortcuts.Handlers;

public sealed class QuickTranslateHandler : IShortcutActionHandler
{
    private readonly ISelectedTextCaptureService _captureService;
    private readonly ILogger<QuickTranslateHandler> _logger;
    public string ActionType => "QuickTranslate";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting { get; private set; }

    public QuickTranslateHandler(ISelectedTextCaptureService captureService, ILogger<QuickTranslateHandler> logger)
    {
        _captureService = captureService;
        _logger = logger;
    }

    public void Execute(ShortcutParameter? parameter = null)
    {
        if (IsExecuting) return;
        IsExecuting = true;
        _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        TextAssistWindowView? window = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window = new TextAssistWindowView();
                window.Show();
                _ = window.InitializeAsync(string.Empty, false);
            });

            // Opening the editor must not depend on clipboard/selection capture.
            // Capture is best-effort and only fills the editor when text exists.
            var snapshot = await _captureService.CaptureAsync();
            if (window != null && !string.IsNullOrEmpty(snapshot?.Text))
                await Dispatcher.UIThread.InvokeAsync(() => window.InitializeAsync(snapshot.Text, false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not capture selected text for quick translation; leaving editor blank.");
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
