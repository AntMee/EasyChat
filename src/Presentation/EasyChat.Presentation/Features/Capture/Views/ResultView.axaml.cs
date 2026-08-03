using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings.State;

namespace EasyChat.Presentation.Features.Capture.Views;

public partial class ResultView : Window
{
    private Screen? _screen;

    public ResultView() => InitializeComponent();

    public ResultView(
        SettingsSession settings,
        PhysicalScreenPoint completionPoint)
    {
        InitializeComponent();
        ApplyConfiguration(settings.Result);
        ShowLoading();
        IsVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _screen = Screens.ScreenFromPoint(
            new PixelPoint(completionPoint.X, completionPoint.Y)) ?? Screens.Primary;
        if (_screen is not null)
        {
            TextBlockResult.MaxWidth = _screen.Bounds.Width / _screen.Scaling * 0.8;
            Position = new PixelPoint(_screen.Bounds.X, _screen.Bounds.Y);
        }
        Loaded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ReCenterPosition();
                if (IsLoaded)
                    IsVisible = true;
            });
        };
    }

    public void AppendText(string text) => Dispatcher.UIThread.Post(() =>
    {
        if (LoadingIndicator.IsVisible)
            ShowResult();
        TextBlockResult.Text += text;
        Dispatcher.UIThread.Post(ReCenterPosition);
    });

    public void ShowLoading() => Dispatcher.UIThread.Post(() =>
    {
        LoadingIndicator.IsVisible = true;
        TextBlockResult.IsVisible = false;
    });

    public void ShowResult() => Dispatcher.UIThread.Post(() =>
    {
        LoadingIndicator.IsVisible = false;
        TextBlockResult.IsVisible = true;
        ReCenterPosition();
    });

    public void CloseAfterDelay(int milliseconds) => Dispatcher.UIThread.Post(async void () =>
    {
        await Task.Delay(milliseconds);
        Close();
    });

    private void ApplyConfiguration(LiveResultSettings settings)
    {
        TransparencyLevelHint = settings.TransparencyLevel switch
        {
            "AcrylicBlur" => [WindowTransparencyLevel.AcrylicBlur],
            "Blur" => [WindowTransparencyLevel.Blur],
            _ => [WindowTransparencyLevel.Transparent]
        };
        TrySetBrush(settings.BackgroundColor, brush => MainCard.Background = brush);
        TrySetBrush(settings.WindowBackgroundColor, brush => Background = brush);
        TrySetBrush(settings.FontColor, brush => TextBlockResult.Foreground = brush);
        TextBlockResult.FontSize = settings.FontSize;
        if (!string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            try
            {
                TextBlockResult.FontFamily = new FontFamily(settings.FontFamily);
            }
            catch
            {
            }
        }
    }

    private static void TrySetBrush(string? value, Action<IBrush> apply)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        try
        {
            apply(Brush.Parse(value));
        }
        catch
        {
        }
    }

    private void ReCenterPosition()
    {
        if (_screen is null)
            return;

        var logicalWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        Position = ScreenshotResultPlacement.CenterHorizontallyAtTop(
            _screen.Bounds,
            _screen.Scaling,
            logicalWidth,
            topOffsetDip: -5);
    }
}
