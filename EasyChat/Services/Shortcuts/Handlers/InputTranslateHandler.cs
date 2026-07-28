using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.TextAssist;
using EasyChat.ViewModels.Typing;
using EasyChat.Views.Typing;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.Shortcuts.Handlers;

/// <summary>
/// Handler for the InputTranslate shortcut action.
/// Opens the TypingView for manual text input translation.
/// </summary>
public sealed class InputTranslateHandler : IShortcutActionHandler
{
    private readonly IPlatformService _platformService;
    private readonly ISelectedTextCaptureService _captureService;
    private readonly ILogger<InputTranslateHandler> _logger;

    public string ActionType => "InputTranslate";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting { get; private set; }

    public InputTranslateHandler(
        IPlatformService platformService,
        ISelectedTextCaptureService captureService,
        ILogger<InputTranslateHandler> logger)
    {
        _platformService = platformService;
        _captureService = captureService;
        _logger = logger;
    }

    public void Execute(ShortcutParameter? parameter = null)
    {
        var hwnd = _platformService.GetForegroundWindowHandle();
        if (parameter?.ReplaceCurrentInput == true)
        {
            if (IsExecuting) return;

            IsExecuting = true;
            _ = ReplaceCurrentInputAsync(hwnd, parameter);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var typingView = new TypingView(hwnd, parameter);
            typingView.Show();
        });
    }

    private async Task ReplaceCurrentInputAsync(IntPtr targetHwnd, ShortcutParameter parameter)
    {
        try
        {
            var snapshot = await _captureService.CaptureAllViaCopyAsync();
            if (snapshot == null)
            {
                _logger.LogWarning("Could not read the current input for input translation replacement.");
                return;
            }

            using var viewModel = new TypingViewModel(targetHwnd, parameter);
            await viewModel.TranslateAndSendAsync(snapshot.Text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to translate and replace the current input.");
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
