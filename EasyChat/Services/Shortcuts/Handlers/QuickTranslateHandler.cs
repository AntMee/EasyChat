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
    private TextAssistWindowView? _window;
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
        _ = ExecuteAsync(parameter?.ReadSelectedText ?? true);
    }

    private async Task ExecuteAsync(bool readSelectedText)
    {
        try
        {
            if (await CloseWindowIfOpenAsync()) return;

            var window = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var createdWindow = new TextAssistWindowView
                {
                    // Keep the source application focused until its selection has
                    // been copied, while still painting this shell immediately.
                    ShowActivated = !readSelectedText
                };
                _window = createdWindow;
                createdWindow.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_window, createdWindow)) _window = null;
                };
                if (readSelectedText) createdWindow.PrepareForInputCapture(false);
                else _ = createdWindow.InitializeAsync(string.Empty, false);
                createdWindow.Show();
                return createdWindow;
            });

            if (!readSelectedText) return;

            var text = string.Empty;
            try
            {
                var snapshot = await _captureService.CaptureViaCopyAsync();
                text = snapshot?.Text ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not capture selected text for quick translation; leaving editor blank.");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_window, window) || !window.IsVisible) return;
                _ = window.InitializeAsync(text, false);
                window.Activate();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the quick translation window.");
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
