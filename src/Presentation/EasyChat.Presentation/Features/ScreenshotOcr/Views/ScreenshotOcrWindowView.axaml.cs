using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.ScreenshotOcr.Controls;
using Material.Icons;
using Material.Icons.Avalonia;

namespace EasyChat.Presentation.Features.ScreenshotOcr.Views;

public sealed partial class ScreenshotOcrWindowView : Window
{
    private readonly ScreenshotOcrWindowViewModel? _viewModel;
    private readonly OcrImageViewport? _viewport;
    private bool _disposed;

    public ScreenshotOcrWindowView() => InitializeComponent();

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
        KeyDown += OnWindowKeyDown;
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

    private async void OnOpened(object? sender, EventArgs e)
    {
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
        await _viewModel.DisposeAsync();
    }

    private void OnZoomChanged(double value)
    {
        if (_viewModel is null)
            return;
        _viewModel.ZoomPercent = value;
    }

    private void Header_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.Source is Visual visual
            && visual is not Button
            && !visual.GetVisualAncestors().OfType<Button>().Any())
        {
            BeginMoveDrag(e);
        }
    }

    private void Resize_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edgeName }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }

    private void Pin_OnClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (this.FindControl<MaterialIcon>("PinIcon") is { } icon)
            icon.Kind = Topmost ? MaterialIconKind.Pin : MaterialIconKind.PinOutline;
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
    private void ZoomOut_OnClick(object? sender, RoutedEventArgs e) => _viewport?.ZoomOut();
    private void ZoomIn_OnClick(object? sender, RoutedEventArgs e) => _viewport?.ZoomIn();
    private async void Recapture_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RecaptureAsync();
    private async void CopyImage_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.CopyImageAsync();
    private async void Undo_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.UndoAsync();
    private async void Redo_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RedoAsync();
    private async void Restore_OnClick(object? sender, RoutedEventArgs e) => await _viewModel!.RestoreOriginalAsync();
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
            Title = "Open image for OCR",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
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
        if (e.Key == Key.Escape)
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
        var dialog = new Window
        {
            Title = "Confirm replacement",
            Width = 430,
            Height = 170,
            MinWidth = 430,
            MinHeight = 170,
            MaxWidth = 430,
            MaxHeight = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = CreateConfirmationContent(message)
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private static Control CreateConfirmationContent(string message)
    {
        var confirm = new Button { Content = "Continue", MinWidth = 92 };
        var cancel = new Button { Content = "Cancel", MinWidth = 92 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 22,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons
            }
        };
        confirm.Click += (_, _) => (TopLevel.GetTopLevel(confirm) as Window)?.Close(true);
        cancel.Click += (_, _) => (TopLevel.GetTopLevel(cancel) as Window)?.Close(false);
        return panel;
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Screenshot OCR",
            Width = 430,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
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
