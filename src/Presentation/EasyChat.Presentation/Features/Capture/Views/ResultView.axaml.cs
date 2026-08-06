using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings.State;
using LiveMarkdown.Avalonia;

namespace EasyChat.Presentation.Features.Capture.Views;

public partial class ResultView : Window
{
    private readonly ObservableStringBuilder _markdown = new();
    private Screen? _screen;

    public ResultView()
    {
        InitializeComponent();
        MarkdownResult.MarkdownBuilder = _markdown;
    }

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
            MarkdownResult.MaxWidth = _screen.Bounds.Width / _screen.Scaling * 0.8;
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
        _markdown.Append(text);
        Dispatcher.UIThread.Post(ReCenterPosition);
    });

    public void ShowLoading() => Dispatcher.UIThread.Post(() =>
    {
        _markdown.Clear();
        LoadingIndicator.IsVisible = true;
        MarkdownResult.IsVisible = false;
    });

    public void ShowResult() => Dispatcher.UIThread.Post(() =>
    {
        LoadingIndicator.IsVisible = false;
        MarkdownResult.IsVisible = true;
        ReCenterPosition();
    });

    public void CloseAfterDelay(int milliseconds) => Dispatcher.UIThread.Post(async void () =>
    {
        await Task.Delay(milliseconds);
        Close();
    });

    private void ApplyConfiguration(LiveResultSettings settings)
    {
        TrySetBrush(settings.BackgroundColor, brush => MainCard.Background = brush);
        TrySetBrush(settings.WindowBackgroundColor, brush => WindowBackground.Background = brush);
        TrySetBrush(settings.FontColor, brush => MarkdownResult.SetValue(TextElement.ForegroundProperty, brush));
        MarkdownResult.SetValue(TextElement.FontSizeProperty, settings.FontSize);
        if (!string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            try
            {
                MarkdownResult.SetValue(TextElement.FontFamilyProperty, new FontFamily(settings.FontFamily));
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
