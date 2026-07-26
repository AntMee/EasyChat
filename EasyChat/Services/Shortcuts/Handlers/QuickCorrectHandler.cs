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
    private TextAssistWindowView? _window;
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
        _ = ExecuteAsync(parameter?.ReadSelectedText ?? true);
    }

    private async Task ExecuteAsync(bool readSelectedText)
    {
        try
        {
            if (await CloseWindowIfOpenAsync()) return;

            var text = string.Empty;
            if (readSelectedText)
            {
                try
                {
                    var snapshot = await _captureService.CaptureViaCopyAsync();
                    text = snapshot?.Text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not capture selected text for quick correction; leaving editor blank.");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var window = new TextAssistWindowView();
                _window = window;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_window, window)) _window = null;
                };
                _ = window.InitializeAsync(text, true);
                window.Show();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the quick correction window.");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private async Task<bool> CloseWindowIfOpenAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window == null) return false;

            var window = _window;
            _window = null;
            window.Close();
            return true;
        });
    }
}
