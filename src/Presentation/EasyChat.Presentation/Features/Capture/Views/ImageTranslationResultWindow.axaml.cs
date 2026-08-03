using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using EasyChat.Presentation.Shared.Feedback;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;

namespace EasyChat.Presentation.Features.Capture.Views;

public partial class ImageTranslationResultWindow : SukiWindow
{
    private Bitmap? _bitmap;
    private readonly ILogger<ImageTranslationResultWindow>? _logger;

    public ImageTranslationResultWindow() => InitializeComponent();

    public ImageTranslationResultWindow(
        Bitmap bitmap,
        IReadOnlyList<string> warnings,
        ILogger<ImageTranslationResultWindow> logger)
    {
        InitializeComponent();
        _bitmap = bitmap;
        _logger = logger;
        TranslatedImage.Source = bitmap;

        var visibleWarnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Take(3)
            .ToArray();
        if (visibleWarnings.Length > 0)
        {
            WarningText.Text = string.Join("\n", visibleWarnings);
            WarningPanel.IsVisible = true;
        }

        Closed += (_, _) =>
        {
            _bitmap?.Dispose();
            _bitmap = null;
        };
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
}
