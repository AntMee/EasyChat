using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.ScreenshotOcr.Controls;
using EasyChat.Presentation.Foundation.UiHost;
using LangResources = EasyChat.Presentation.Lang.Resources;
using Material.Icons;
using Material.Icons.Avalonia;
using ShadUI;
using Avalonia.VisualTree;

namespace EasyChat.Presentation.Features.ScreenshotOcr.Views;

public sealed partial class ScreenshotOcrWindowView : ShadUI.Window
{
    private const double ResizeBorderThickness = 8;
    private static readonly CornerRadius WindowCornerRadius = new(12);
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeftResizeCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightResizeCursor = new(StandardCursorType.TopRightCorner);
    private readonly ScreenshotOcrWindowViewModel? _viewModel;
    private readonly OcrImageViewport? _viewport;
    private ScreenshotOcrResetConfirmationDialogViewModel? _resetConfirmation;
    private bool _isPinned;
    private bool _disposed;

    public ScreenshotOcrWindowView()
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

        // Native Popup/Flyout roots can be separate from the owner window.
        return true;
    }

    private WindowEdge? GetResizeEdge(Point position)
    {
        if (!CanResize || WindowState != WindowState.Normal)
            return null;

        // Pointer events from a Popup/Flyout can still reach the window while
        // their coordinates are outside this client area. They must not be
        // interpreted as a request to resize the window edge.
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

    internal ScreenshotOcrWindowView(ScreenshotOcrWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewport = this.FindControl<OcrImageViewport>("Viewport")
                    ?? throw new InvalidOperationException("OCR viewport was not found.");
        _viewport.SetBitmap(viewModel.Bitmap);
        _viewport.SetRegions(viewModel.Regions);
        _viewport.SelectionChanged += viewModel.SetSelectedRegions;
        _viewport.ZoomChanged += OnZoomChanged;
        viewModel.BitmapChanged += _viewport.SetBitmap;
        viewModel.RegionsChanged += _viewport.SetRegions;
        viewModel.ConfirmResetAsync = ShowResetConfirmationAsync;
        Opened += OnOpened;
        Closed += OnClosed;
        AddHandler(
            KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    internal void PositionNear(PhysicalScreenPoint point)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var width = Math.Min(area.Width - 32, (int)Math.Ceiling(Width * screen.Scaling));
        var height = Math.Min(area.Height - 32, (int)Math.Ceiling(Height * screen.Scaling));
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    internal void ShowInForeground()
    {
        Topmost = true;
        Show();
        Activate();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            Topmost = _isPinned;
            Activate();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Activate();
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => Activate(),
            Avalonia.Threading.DispatcherPriority.Input);
        if (_viewModel is not null)
            await _viewModel.InitializeAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed || _viewModel is null || _viewport is null)
            return;
        _disposed = true;
        _viewport.SelectionChanged -= _viewModel.SetSelectedRegions;
        _viewport.ZoomChanged -= OnZoomChanged;
        _viewModel.BitmapChanged -= _viewport.SetBitmap;
        _viewModel.RegionsChanged -= _viewport.SetRegions;
        _resetConfirmation?.Cancel();
        _viewModel.DialogManager.Dispose();
        await _viewModel.DisposeAsync();
    }

    private void OnZoomChanged(double value)
    {
        if (_viewModel is null)
            return;
        _viewModel.ZoomPercent = value;
    }

    private void Pin_OnClick(object? sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        if (this.FindControl<MaterialIcon>("PinIcon") is { } icon)
            icon.Kind = _isPinned ? MaterialIconKind.Pin : MaterialIconKind.PinOutline;
    }

    private void ZoomOut_OnClick(object? sender, RoutedEventArgs e) => _viewport?.ZoomOut();
    private void ZoomIn_OnClick(object? sender, RoutedEventArgs e) => _viewport?.ZoomIn();
    private async void Recapture_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RecaptureAsync();
    private async void CopyImage_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.CopyImageAsync();
    private async void Undo_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.UndoAsync();
    private async void Redo_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RedoAsync();
    private async void Restore_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RestoreOriginalAsync();
    private async void TranslateImage_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.TranslateImageAsync();
    private async void Retry_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RetryAsync();
    private async void CopyText_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.CopyCurrentTextAsync();
    private async void TranslateText_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.TranslateTextAsync();
    private void ShowOriginal_OnClick(object? sender, RoutedEventArgs e) => _viewModel?.ShowOriginal();
    private async void CopySelection_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.CopySelectedTextAsync();
    private async void ReplaceSelection_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.ReplaceSelectedWithTranslationAsync();

    private async void TranslateSelection_OnClick(object? sender, RoutedEventArgs e) =>
        await _viewModel!.ShowSelectedTranslationAsync(GetPopupAnchor(sender));

    private async void ExplainSelection_OnClick(object? sender, RoutedEventArgs e) =>
        await _viewModel!.ExplainSelectedAsync(GetPopupAnchor(sender));

    private async void OpenImage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangResources.ScreenshotOcr_OpenImageDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LangResources.ScreenshotOcr_ImageFileType)
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var image = await ScreenshotOcrImageFileLoader.LoadAsync(
                path,
                _viewModel.ValidateImageDimensions);
            await _viewModel.ReplaceImageAsync(image);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private PhysicalScreenPoint GetPopupAnchor(object? sender)
    {
        var point = sender is Control control
            ? control.TranslatePoint(control.Bounds.BottomRight, this) ?? Bounds.Center
            : Bounds.Center;
        var screen = this.PointToScreen(point);
        return new PhysicalScreenPoint(screen.X, screen.Y);
    }

    private async Task<bool> ShowResetConfirmationAsync(string message)
    {
        var confirmation = new ScreenshotOcrResetConfirmationDialogViewModel(
            _viewModel!.DialogManager,
            message);
        _resetConfirmation = confirmation;
        try
        {
            _viewModel.DialogManager.CreateDialog(confirmation)
                .WithCancelCallback(confirmation.CompleteCancellation)
                .Dismissible()
                .Show();
            return await confirmation.Result;
        }
        finally
        {
            if (ReferenceEquals(_resetConfirmation, confirmation))
                _resetConfirmation = null;
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new Avalonia.Controls.Window
        {
            Title = LangResources.Action_ScreenshotOcr,
            Width = 430,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = new Button { Content = LangResources.Close, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                close
            }
        };
        await dialog.ShowDialog(this);
    }
}
