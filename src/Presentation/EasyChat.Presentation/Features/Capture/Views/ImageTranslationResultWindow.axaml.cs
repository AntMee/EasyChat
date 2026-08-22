using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Shared.Feedback;
using EasyChat.Presentation.Foundation.UiHost;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Capture.Views;

public partial class ImageTranslationResultWindow : ShadUI.Window
{
    private const double ResizeBorderThickness = 8;
    private static readonly CornerRadius WindowCornerRadius = new(12);
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeftResizeCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightResizeCursor = new(StandardCursorType.TopRightCorner);
    private Bitmap? _bitmap;
    private readonly ILogger<ImageTranslationResultWindow>? _logger;

    public ImageTranslationResultWindow()
    {
        InitializeComponent();
        // ShadUI 0.2.4 resets RootCornerRadius while applying its Windows template.
        Opened += (_, _) =>
        {
            ApplyRootCornerRadius();
            ShadWindowFrameFix.Apply(this);
        };
        AddHandler(PointerPressedEvent, OnResizePointerPressed, RoutingStrategies.Tunnel);
        PointerMoved += OnResizePointerMoved;
        PointerExited += (_, _) => Cursor = null;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && WindowState == WindowState.Normal)
                ApplyRootCornerRadius();
        };
    }

    public ImageTranslationResultWindow(
        Bitmap sourceBitmap,
        PhysicalScreenPoint completionPoint,
        ILogger<ImageTranslationResultWindow> logger)
        : this()
    {
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

    internal void MarkReady()
    {
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

    private void ApplyRootCornerRadius()
    {
        if (WindowState == WindowState.Normal)
            RootCornerRadius = WindowCornerRadius;
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || IsInteractivePointerSource(args.Source))
            return;

        var edge = GetResizeEdge(args.GetPosition(this));
        if (edge is not { } resizeEdge)
            return;

        args.Handled = true;
        BeginResizeDrag(resizeEdge, args);
    }

    private void OnResizePointerMoved(object? sender, PointerEventArgs args)
    {
        Cursor = IsInteractivePointerSource(args.Source)
            ? null
            : GetResizeCursor(GetResizeEdge(args.GetPosition(this)));
    }

    private bool IsInteractivePointerSource(object? source)
    {
        if (source is not Visual visual)
            return false;

        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, this))
                return false;
            if (current is InputElement { Focusable: true })
                return true;

            var typeName = current.GetType().Name;
            if (typeName.Contains("Popup", StringComparison.Ordinal)
                || typeName.Contains("Flyout", StringComparison.Ordinal)
                || typeName.Contains("Overlay", StringComparison.Ordinal)
                || typeName is "ColorPicker" or "ColorSpectrum" or "ColorSlider")
                return true;
        }

        return true;
    }

    private WindowEdge? GetResizeEdge(Point position)
    {
        if (!CanResize || WindowState != WindowState.Normal)
            return null;
        if (position.X < 0 || position.Y < 0
            || position.X >= Bounds.Width || position.Y >= Bounds.Height)
            return null;

        var left = position.X <= ResizeBorderThickness;
        var right = position.X >= Bounds.Width - ResizeBorderThickness;
        var top = position.Y <= ResizeBorderThickness;
        var bottom = position.Y >= Bounds.Height - ResizeBorderThickness;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null
        };
    }

    private static Cursor? GetResizeCursor(WindowEdge? edge) => edge switch
    {
        WindowEdge.West or WindowEdge.East => HorizontalResizeCursor,
        WindowEdge.North or WindowEdge.South => VerticalResizeCursor,
        WindowEdge.NorthWest or WindowEdge.SouthEast => TopLeftResizeCursor,
        WindowEdge.NorthEast or WindowEdge.SouthWest => TopRightResizeCursor,
        _ => null
    };

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
