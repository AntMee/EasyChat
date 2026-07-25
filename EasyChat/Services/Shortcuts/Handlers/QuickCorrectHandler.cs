using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.TextAssist;
using EasyChat.Views.Windows;

namespace EasyChat.Services.Shortcuts.Handlers;

public sealed class QuickCorrectHandler : IShortcutActionHandler
{
    private readonly ISelectedTextCaptureService _captureService;
    public string ActionType => "QuickCorrect";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting { get; private set; }

    public QuickCorrectHandler(ISelectedTextCaptureService captureService)
    {
        _captureService = captureService;
    }

    public void Execute(ShortcutParameter? parameter = null)
    {
        if (IsExecuting) return;
        IsExecuting = true;
        _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        try
        {
            var snapshot = await _captureService.CaptureAsync();
            if (snapshot == null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var window = new TextAssistWindowView();
                window.Show();
                _ = window.InitializeAsync(snapshot.Text, true);
            });
        }
        finally
        {
            IsExecuting = false;
        }
    }
}
