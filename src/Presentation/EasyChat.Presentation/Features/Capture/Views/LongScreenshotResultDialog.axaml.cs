using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using EasyChat.Contracts.Capture;
using EasyChat.Presentation.Features.ScreenshotOcr.Controls;
using EasyChat.Presentation.Foundation.Platform;

namespace EasyChat.Presentation.Features.Capture.Views;

internal sealed partial class LongScreenshotResultDialog : Window
{
    private readonly OcrImageViewport _viewport;
    private readonly TextBlock _zoomText;
    private CaptureOverlayAction _defaultAction;
    private bool _closing;

    internal LongScreenshotResultDialog()
    {
        InitializeComponent();
        // Decoration-free rounded windows need per-pixel transparency on
        // Windows; otherwise the compositor leaves a dark rectangle at the
        // four corners of the child card.
        TransparencyLevelHint = WindowTransparencyLevels.ForRoundedWindow();
        _viewport = this.FindControl<OcrImageViewport>("ImageViewport")
                    ?? throw new InvalidOperationException("ImageViewport not found.");
        _zoomText = this.FindControl<TextBlock>("ZoomText")
                    ?? throw new InvalidOperationException("ZoomText not found.");
        _viewport.ZoomChanged += OnZoomChanged;
        Closed += OnClosed;
        OnZoomChanged(100);
    }

    internal event Action<CaptureOverlayAction>? ActionRequested;
    internal event Action? ResetRequested;
    internal event Action? CancelRequested;

    internal void SetImage(Bitmap image, CaptureOverlayAction defaultAction)
    {
        ArgumentNullException.ThrowIfNull(image);
        _defaultAction = defaultAction;
        _viewport.SetBitmap(image);
    }

    internal void CloseSessionWindow()
    {
        _closing = true;
        _viewport.ClearBitmap();
        if (IsVisible)
            Close();
    }

    private void ZoomOut_OnClick(object? sender, RoutedEventArgs e) => _viewport.ZoomOut();
    private void ZoomIn_OnClick(object? sender, RoutedEventArgs e) => _viewport.ZoomIn();
    private void Confirm_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(_defaultAction);
    private void Copy_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.CopyOriginal);
    private void CopyTranslated_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.CopyTranslated);
    private void CopyBilingual_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.CopyBilingual);
    private void CopyImageTranslated_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.CopyImageTranslated);
    private void Ocr_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.OcrWorkbench);
    private void LongScreenshot_OnClick(object? sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureOverlayAction.CopyLongScreenshot);
    private void Reset_OnClick(object? sender, RoutedEventArgs e) => ResetRequested?.Invoke();
    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private void OnZoomChanged(double value) => _zoomText.Text = $"{Math.Round(value):0}%";

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewport.ZoomChanged -= OnZoomChanged;
        _viewport.ClearBitmap();
        if (!_closing)
            CancelRequested?.Invoke();
    }
}
