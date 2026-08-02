using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyChat.Presentation.Features.Settings.State;

namespace EasyChat.Views.Result;

public partial class ResultView : Window
{
    private Screen? _screen;

    public ResultView() => InitializeComponent();

    public ResultView(SettingsSession settings)
    {
        InitializeComponent();
        ApplyConfiguration(settings.Result);
        ShowLoading();
        IsVisible = false;
        Loaded += (_, _) =>
        {
            _screen = GetScreen();
            if (_screen is not null)
                TextBlockResult.MaxWidth = _screen.Bounds.Width / _screen.Scaling * 0.8;
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

    private Screen? GetScreen() => Screens.All.FirstOrDefault(screen =>
        screen.Bounds.Contains(new PixelPoint(Position.X, Position.Y))) ?? Screens.Primary;

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    private void ReCenterPosition()
    {
        if (_screen is null)
            return;
        var x = _screen.Bounds.Width / _screen.Scaling / 2 - Width / 2;
        Position = new PixelPoint((int)x, -5);
    }
}
