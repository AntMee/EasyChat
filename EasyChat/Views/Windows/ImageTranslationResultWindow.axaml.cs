using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using EasyChat.Services.Abstractions;
using EasyChat.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;

namespace EasyChat.Views.Windows;

public partial class ImageTranslationResultWindow : SukiWindow
{
    private Bitmap? _bitmap;
    private readonly ILogger<ImageTranslationResultWindow>? _logger;

    public ImageTranslationResultWindow()
    {
        InitializeComponent();
        _logger = Global.Services?.GetService<ILogger<ImageTranslationResultWindow>>();
    }

    public ImageTranslationResultWindow(Bitmap bitmap, string[] warnings)
    {
        InitializeComponent();
        _bitmap = bitmap;
        TranslatedImage.Source = bitmap;

        var visibleWarnings = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Take(3).ToArray();
        if (visibleWarnings.Length > 0)
        {
            WarningText.Text = string.Join("\n", visibleWarnings);
            WarningPanel.IsVisible = true;
        }

        _logger = Global.Services?.GetService<ILogger<ImageTranslationResultWindow>>();
        Closed += (_, _) => _bitmap?.Dispose();
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard != null && _bitmap != null)
                await clipboard.SetValueAsync(DataFormat.Bitmap, _bitmap);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to copy translated image");
        }
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FilePickerSaveOptions
            {
                SuggestedFileName = "translated-image.png",
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG Image") { Patterns = ["*.png"] }
                ]
            };
            var file = await StorageProvider.SaveFilePickerAsync(options);
            if (file == null)
                return;

            await using var stream = await file.OpenWriteAsync();
            _bitmap?.Save(stream, PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save translated image");
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
