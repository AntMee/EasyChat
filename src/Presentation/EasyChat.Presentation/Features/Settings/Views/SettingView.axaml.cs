using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace EasyChat.Presentation.Features.Settings.Views;

public partial class SettingView : UserControl
{
    private bool _isLoaded;

    public SettingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_isLoaded)
            return;

        _isLoaded = true;
        await Task.Delay(200);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SettingsContent.IsVisible = true;
            LoadingOverlay.IsVisible = false;
        });
    }
}
