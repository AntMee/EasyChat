using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.TextAssist;
using EasyChat.Views.Windows;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.Shortcuts.Handlers;

public sealed class QuickCorrectHandler : IShortcutActionHandler
{
    private readonly ISelectedTextCaptureService _captureService;
    private readonly ILogger<QuickCorrectHandler> _logger;
    public string ActionType => "QuickCorrect";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting { get; private set; }

    public QuickCorrectHandler(ISelectedTextCaptureService captureService, ILogger<QuickCorrectHandler> logger)
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
                _ = window.InitializeAsync(string.Empty, true);
            });

            // Opening the editor must not depend on clipboard/selection capture.
            var snapshot = await _captureService.CaptureAsync();
            if (window != null && !string.IsNullOrEmpty(snapshot?.Text))
                await Dispatcher.UIThread.InvokeAsync(() => window.InitializeAsync(snapshot.Text, true));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not capture selected text for quick correction; leaving editor blank.");
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
