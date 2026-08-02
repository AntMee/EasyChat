using Avalonia;
using Avalonia.Controls;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Platform;
using EasyChat.ViewModels.Pages;
using EasyChat.Views.Speech;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Speech;

public sealed class SubtitleWindowCoordinator(
    SettingsSession settings,
    IPlatformWindowBehavior platformWindowBehavior,
    IPointerPosition pointer,
    ILoggerFactory loggerFactory)
{
    private SubtitleOverlayWindowView? _window;

    public event EventHandler<bool>? VisibilityChanged;
    public bool IsOpen => _window?.IsVisible == true;

    public void Open(SpeechRecognitionViewModel viewModel)
    {
        if (_window is not null)
            return;
        var window = new SubtitleOverlayWindowView(
            viewModel,
            platformWindowBehavior,
            pointer,
            loggerFactory.CreateLogger<SubtitleOverlayWindowView>());
        var config = settings.SpeechRecognition;
        if (config.WindowWidth > 0 && config.WindowHeight > 0)
        {
            window.Width = config.WindowWidth;
            window.Height = config.WindowHeight;
            window.SizeToContent = SizeToContent.Manual;
        }
        if (config.WindowX >= 0 && config.WindowY >= 0)
        {
            window.Position = new PixelPoint((int)config.WindowX, (int)config.WindowY);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        window.Closing += (_, _) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                viewModel.StoreFloatingWindowBounds(
                    window.Position.X,
                    window.Position.Y,
                    window.Width,
                    window.Height);
            }
        };
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window))
                return;
            _window = null;
            VisibilityChanged?.Invoke(this, false);
        };
        _window = window;
        window.Show();
        VisibilityChanged?.Invoke(this, true);
    }

    public void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
        if (window is not null)
            VisibilityChanged?.Invoke(this, false);
    }
}
