using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Shared.Feedback;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Capture.Views;

public partial class ImageTranslationResultWindow : ShadUI.Window
{
    private Bitmap? _bitmap;
    private readonly ILogger<ImageTranslationResultWindow>? _logger;

    public ImageTranslationResultWindow() => InitializeComponent();

    public ImageTranslationResultWindow(
        Bitmap sourceBitmap,
        PhysicalScreenPoint completionPoint,
        ILogger<ImageTranslationResultWindow> logger)
    {
        InitializeComponent();
        _bitmap = sourceBitmap;
        _logger = logger;
        TranslatedImage.Source = sourceBitmap;
        PositionOnScreen(completionPoint);

        Closed += (_, _) =>
        {
            _bitmap?.Dispose();
            _bitmap = null;
        };
    }

    internal void ShowResult(Bitmap bitmap, IReadOnlyList<string> warnings)
    {
        var previous = _bitmap;
        _bitmap = bitmap;
        TranslatedImage.Source = bitmap;
        previous?.Dispose();

        ShowWarnings(warnings);
        LoadingPanel.IsVisible = false;
        CopyButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
    }

    internal void ShowFailure(string message)
    {
        ShowWarnings([message]);
        LoadingPanel.IsVisible = false;
    }

    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        var visibleWarnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Take(3)
            .ToArray();
        WarningText.Text = string.Join("\n", visibleWarnings);
        WarningPanel.IsVisible = visibleWarnings.Length > 0;
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null || _bitmap is null)
                return;
            await clipboard.SetValueAsync(DataFormat.Bitmap, _bitmap);
            CopyFeedback.Show(sender as Control, EasyChat.Presentation.Lang.Resources.Copied);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to copy translated image.");
        }
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "translated-image.png",
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }
                ]
            });
            if (file is null || _bitmap is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            _bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Failed to save translated image.");
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void PositionOnScreen(PhysicalScreenPoint completionPoint)
    {
        var screen = Screens.ScreenFromPoint(
            new PixelPoint(completionPoint.X, completionPoint.Y)) ?? Screens.Primary;
        if (screen is null)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        var fitted = ScreenshotResultPlacement.FitLogicalSize(
            screen.WorkingArea,
            screen.Scaling,
            Width,
            Height,
            marginDip: 16);
        MinWidth = Math.Min(MinWidth, fitted.Width);
        MinHeight = Math.Min(MinHeight, fitted.Height);
        Width = fitted.Width;
        Height = fitted.Height;
        Position = ScreenshotResultPlacement.Center(
            screen.WorkingArea,
            screen.Scaling,
            fitted.Width,
            fitted.Height);
    }
}
