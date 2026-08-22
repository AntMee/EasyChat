using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Foundation.Platform;

namespace EasyChat.Presentation.Features.Capture.Views;

internal sealed partial class LongScreenshotProgressView : Window
{
    private readonly TextBlock? _statusTextBlock;
    private readonly TextBlock? _detailTextBlock;
    private readonly Button? _startButton;
    private readonly Button? _stopButton;
    private readonly Button? _retryButton;
    private readonly ComboBox? _directionComboBox;
    private readonly Control? _directionSeparator;
    private Bitmap? _previewBitmap;
    private bool _closed;
    private bool _captureSuspended;
    private bool _updatingDirection;
    private IPlatformWindowBehavior? _windowBehavior;
    private bool _excludedFromCapture;
    private double _captureOpacity = 1d;

    public LongScreenshotProgressView()
    {
        InitializeComponent();
        _statusTextBlock = this.FindControl<TextBlock>("StatusTextBlock");
        _detailTextBlock = this.FindControl<TextBlock>("DetailTextBlock");
        _startButton = this.FindControl<Button>("StartButton");
        _stopButton = this.FindControl<Button>("StopButton");
        _retryButton = this.FindControl<Button>("RetryButton");
        _directionComboBox = this.FindControl<ComboBox>("DirectionComboBox");
        _directionSeparator = this.FindControl<Control>("DirectionSeparator");
        Closed += OnClosed;
        Opened += OnOpened;
    }

    internal event Action? StopRequested;
    internal event Action<LongScreenshotDirection>? StartRequested;
    internal event Action? CancelRequested;
    internal event Action? RetryRequested;
    internal event Action<LongScreenshotDirection>? DirectionChanged;

    internal void ConfigureCaptureExclusion(IPlatformWindowBehavior windowBehavior)
    {
        _windowBehavior = windowBehavior ?? throw new ArgumentNullException(nameof(windowBehavior));
    }

    internal void ShowCapturePreview(
        PhysicalScreenPoint anchor,
        PhysicalScreenRegion screenBounds,
        double scaleX,
        double scaleY,
        Bitmap preview,
        int frameCount,
        int dimension,
        LongScreenshotDirection direction,
        bool directionEnabled,
        bool captureStarted)
    {
        SetToolbarEnabled(captureStarted, retryEnabled: false);
        SetDirectionControls(direction, directionEnabled && !captureStarted);
        ShowPreview(
            anchor,
            screenBounds,
            scaleX,
            scaleY,
            preview,
            frameCount,
            dimension,
            captureStarted
                ? direction == LongScreenshotDirection.Vertical
                ? Lang.Resources.LongScreenshot_ScrollVerticalHint
                : Lang.Resources.LongScreenshot_ScrollHorizontalHint
                : Lang.Resources.LongScreenshot_StartHint);
    }

    internal void ShowFinalPreview(
        PhysicalScreenPoint anchor,
        PhysicalScreenRegion screenBounds,
        double scaleX,
        double scaleY,
        Bitmap preview,
        int frameCount,
        int dimension,
        LongScreenshotDirection direction)
    {
        SetToolbarEnabled(captureStarted: false, retryEnabled: true);
        SetDirectionControls(direction, enabled: false);
        ShowPreview(anchor, screenBounds, scaleX, scaleY, preview, frameCount, dimension, Lang.Resources.LongScreenshot_Review);
    }

    internal void SetCaptureStarted()
    {
        SetToolbarEnabled(captureStarted: true, retryEnabled: false);
        SetDirectionControls(
            _directionComboBox?.SelectedIndex == 1
                ? LongScreenshotDirection.Horizontal
                : LongScreenshotDirection.Vertical,
            enabled: false);
    }

    internal Bitmap? TakePreview()
    {
        var bitmap = _previewBitmap;
        _previewBitmap = null;
        PreviewImage.Source = null;
        return bitmap;
    }

    internal bool HideForCapture(PhysicalScreenRegion captureRegion)
    {
        if (_excludedFromCapture)
            return false;
        if (!IsVisible || !Overlaps(captureRegion))
            return false;
        // GDI captures the composed desktop. Hiding and showing the window for
        // every frame produces a visible blink, so keep the native window alive
        // and make its surface transparent for the duration of the capture.
        if (_captureSuspended)
            return true;
        _captureSuspended = true;
        _captureOpacity = Opacity;
        Opacity = 0d;
        return true;
    }

    internal void RestoreAfterCapture(bool wasHidden)
    {
        if (!wasHidden || !_captureSuspended)
            return;
        _captureSuspended = false;
        if (!_closed)
            Opacity = _captureOpacity;
    }

    private void ShowPreview(
        PhysicalScreenPoint anchor,
        PhysicalScreenRegion screenBounds,
        double scaleX,
        double scaleY,
        Bitmap preview,
        int frameCount,
        int dimension,
        string status)
    {
        if (_closed)
        {
            preview.Dispose();
            return;
        }
        FitWithinScreen(screenBounds, scaleX, scaleY);
        var safeScaleX = scaleX > 0 ? scaleX : 1d;
        var safeScaleY = scaleY > 0 ? scaleY : 1d;
        const int margin = 12;
        var width = Math.Max(1, (int)Math.Ceiling(Width * safeScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(Height * safeScaleY));
        var maxLeft = Math.Max(screenBounds.X + margin, screenBounds.X + screenBounds.Width - width - margin);
        var maxTop = Math.Max(screenBounds.Y + margin, screenBounds.Y + screenBounds.Height - height - margin);
        var targetPosition = new PixelPoint(
            Math.Clamp(anchor.X, screenBounds.X + margin, maxLeft),
            Math.Clamp(anchor.Y, screenBounds.Y + margin, maxTop));
        if (Position != targetPosition)
            Position = targetPosition;
        var previous = _previewBitmap;
        _previewBitmap = preview;
        PreviewImage.Source = preview;
        if (previous is not null)
        {
            // Let Avalonia finish the current image draw before releasing the
            // backing bitmap. Disposing it synchronously can invalidate a
            // compositor pass and present as a one-frame flash.
            _ = ReleasePreviewAfterRenderAsync(previous);
        }
        if (_statusTextBlock is not null)
            _statusTextBlock.Text = status;
        if (_detailTextBlock is not null)
            _detailTextBlock.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Lang.Resources.LongScreenshot_Progress,
                frameCount,
                dimension);
        if (!IsVisible)
            Show();
    }

    internal void CloseSessionWindow()
    {
        _closed = true;
        _captureSuspended = false;
        var preview = _previewBitmap;
        _previewBitmap = null;
        preview?.Dispose();
        if (IsVisible)
            Close();
    }

    private void StartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var direction = _directionComboBox?.SelectedIndex == 1
            ? LongScreenshotDirection.Horizontal
            : LongScreenshotDirection.Vertical;
        StartRequested?.Invoke(direction);
        SetToolbarEnabled(captureStarted: true, retryEnabled: false);
    }

    private void StopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke();
        SetToolbarEnabled(captureStarted: false, retryEnabled: false);
    }

    private void RetryButton_OnClick(object? sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    private void DirectionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingDirection ||
            _closed ||
            _directionComboBox?.SelectedIndex is not (0 or 1))
            return;

        DirectionChanged?.Invoke(
            _directionComboBox.SelectedIndex == 0
                ? LongScreenshotDirection.Vertical
                : LongScreenshotDirection.Horizontal);
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private void InputElement_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        CancelButton_OnClick(sender, e);
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var unexpectedClose = !_closed;
        _closed = true;
        var preview = _previewBitmap;
        _previewBitmap = null;
        preview?.Dispose();
        if (unexpectedClose)
            CancelRequested?.Invoke();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_windowBehavior is null)
            return;

        try
        {
            _excludedFromCapture = await _windowBehavior.TrySetExcludedFromCaptureAsync(this, enabled: true);
        }
        catch
        {
            _excludedFromCapture = false;
        }
    }

    private void SetToolbarEnabled(bool captureStarted, bool retryEnabled)
    {
        if (_startButton is not null)
            _startButton.IsEnabled = !captureStarted;
        if (_stopButton is not null)
            _stopButton.IsEnabled = captureStarted;
        if (_retryButton is not null)
            _retryButton.IsEnabled = retryEnabled;
    }

    private void SetDirectionControls(LongScreenshotDirection direction, bool enabled)
    {
        if (_directionComboBox is not null)
        {
            _updatingDirection = true;
            _directionComboBox.SelectedIndex =
                direction == LongScreenshotDirection.Vertical ? 0 : 1;
            _directionComboBox.IsEnabled = enabled;
            _updatingDirection = false;
        }
        if (_directionSeparator is not null)
            _directionSeparator.IsVisible = true;
    }

    private static async Task ReleasePreviewAfterRenderAsync(Bitmap bitmap)
    {
        await Task.Delay(120).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(
            bitmap.Dispose,
            DispatcherPriority.Background);
    }

    private void FitWithinScreen(PhysicalScreenRegion screenBounds, double scaleX, double scaleY)
    {
        const double desiredWidth = 260;
        const double desiredHeight = 320;
        const double margin = 12;
        var safeScaleX = scaleX > 0 ? scaleX : 1d;
        var safeScaleY = scaleY > 0 ? scaleY : 1d;
        var maximumWidth = Math.Max(MinWidth, screenBounds.Width / safeScaleX - margin * 2);
        var maximumHeight = Math.Max(MinHeight, screenBounds.Height / safeScaleY - margin * 2);
        var width = Math.Min(desiredWidth, maximumWidth);
        var height = Math.Min(desiredHeight, maximumHeight);
        if (Math.Abs(Width - width) > 0.1)
            Width = width;
        if (Math.Abs(Height - height) > 0.1)
            Height = height;
    }

    private bool Overlaps(PhysicalScreenRegion region)
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1d;
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var right = checked(Position.X + width);
        var bottom = checked(Position.Y + height);
        return Position.X < checked(region.X + region.Width) && right > region.X &&
               Position.Y < checked(region.Y + region.Height) && bottom > region.Y;
    }
}
