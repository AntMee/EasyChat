using Avalonia;
using Avalonia.Controls;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Foundation.Platform;

namespace EasyChat.Presentation.Features.Capture.Views;

/// <summary>
/// A separate native surface keeps the selected capture area visible after the
/// interactive overlay is removed. It never owns input, so the selected app
/// remains fully usable while the user scrolls.
/// </summary>
internal sealed partial class LongScreenshotSelectionBorderView : Window
{
    private readonly IPlatformWindowBehavior _windowBehavior;
    private bool _closed;
    private bool _captureSuspended;
    private bool _excludedFromCapture;
    private double _captureOpacity = 1d;

    internal LongScreenshotSelectionBorderView(
        ScreenDescriptor screen,
        PhysicalScreenRegion selection,
        IPlatformWindowBehavior windowBehavior)
    {
        ArgumentNullException.ThrowIfNull(windowBehavior);
        InitializeComponent();
        _windowBehavior = windowBehavior;
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        Position = new PixelPoint(selection.X, selection.Y);
        Width = selection.Width / Math.Max(0.01, screen.ScaleX);
        Height = selection.Height / Math.Max(0.01, screen.ScaleY);
        Opened += OnOpened;
    }

    internal void ShowSessionWindow()
    {
        if (!_closed && !IsVisible)
            Show();
    }

    internal bool HideForCapture()
    {
        if (_excludedFromCapture)
            return false;
        if (_closed || !IsVisible || _captureSuspended)
            return !_closed && _captureSuspended;
        _captureSuspended = true;
        _captureOpacity = Opacity;
        Opacity = 0;
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

    internal void CloseSessionWindow()
    {
        _closed = true;
        _captureSuspended = false;
        if (IsVisible)
            Close();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _windowBehavior.ConfigureNoActivateAsync(this);
            await _windowBehavior.SetClickThroughAsync(this, enabled: true);
            _excludedFromCapture = await _windowBehavior.TrySetExcludedFromCaptureAsync(this, enabled: true);
        }
        catch
        {
            // The border remains visually useful on a platform that does not
            // provide the native capture-exclusion/click-through bridge.
            _excludedFromCapture = false;
        }
    }
}
