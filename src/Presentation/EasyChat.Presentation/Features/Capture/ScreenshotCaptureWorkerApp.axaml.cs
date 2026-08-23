using System.Globalization;
using System.Runtime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Capture.Views;

namespace EasyChat.Presentation.Features.Capture;

public sealed record ScreenshotCaptureCommand(
    bool Precise,
    ThemeVariant RequestedTheme,
    string PrimaryColor,
    string CultureName,
    CaptureOverlayAction DefaultAction,
    CaptureToolbarMode ToolbarMode);

public sealed partial class ScreenshotCaptureWorkerApp : Avalonia.Application
{
    private readonly CaptureOverlayCoordinator _overlays;
    private readonly Func<Task<ScreenshotCaptureCommand?>> _receive;
    private readonly Action _ready;
    private readonly Action<ScreenshotSelection?> _complete;
    private readonly Action<Exception> _fail;

    public ScreenshotCaptureWorkerApp()
    {
        _overlays = null!;
        _receive = null!;
        _ready = null!;
        _complete = null!;
        _fail = null!;
    }

    public ScreenshotCaptureWorkerApp(
        CaptureOverlayCoordinator overlays,
        Func<Task<ScreenshotCaptureCommand?>> receive,
        Action ready,
        Action<ScreenshotSelection?> complete,
        Action<Exception> fail)
    {
        _overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
        _receive = receive ?? throw new ArgumentNullException(nameof(receive));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        _fail = fail ?? throw new ArgumentNullException(nameof(fail));
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("The screenshot worker requires a desktop lifetime.");

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnFrameworkInitializationCompleted();
        Dispatcher.UIThread.Post(() => RunAsync(desktop), DispatcherPriority.Background);
    }

    private async void RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            WarmUpOverlayWindow();
            _ready();
            while (await _receive() is { } command)
            {
                await CaptureAsync(command);
                TrimCaptureMemory();
            }
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    private static void WarmUpOverlayWindow()
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(1, 1),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        var screen = new ScreenDescriptor(
            new ScreenId("worker-warmup"),
            new PhysicalScreenRegion(-32_000, -32_000, 1, 1),
            96,
            96,
            IsPrimary: true);
        var window = new OverlayWindowView(screen, bitmap, regionOnly: false)
        {
            Opacity = 0
        };
        try
        {
            window.Show();
        }
        finally
        {
            window.CloseSessionWindow();
        }
    }

    private async Task CaptureAsync(ScreenshotCaptureCommand command)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(command.CultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Lang.Resources.Culture = culture;
            RequestedThemeVariant = command.RequestedTheme;
            ApplyPrimaryColor(command.PrimaryColor);

            var outcome = await _overlays.SelectAsync(
                command.Precise,
                regionOnly: false,
                command.DefaultAction,
                command.ToolbarMode,
                cancellationToken: CancellationToken.None);
            if (outcome is null)
            {
                _complete(null);
                return;
            }
            if (outcome.Image is null)
                throw new InvalidOperationException("Screenshot selection did not produce an image.");

            using (outcome.Image)
            {
                _complete(new ScreenshotSelection(
                    ImageTranslation.AvaloniaImageFrames.ToImageFrame(outcome.Image),
                    outcome.Action,
                    outcome.CompletionPoint));
            }
        }
        catch (Exception exception)
        {
            _fail(exception);
        }
    }

    private static void TrimCaptureMemory()
    {
        // Long screenshots temporarily allocate large frame/result buffers.
        // Reclaim them between commands, rather than making the next capture
        // pay the GC cost and appear to be the point where memory is released.
        const long largeCaptureThreshold = 64L * 1024 * 1024;
        if (GC.GetTotalMemory(forceFullCollection: false) < largeCaptureThreshold)
            return;

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
    }

    private void ApplyPrimaryColor(string value)
    {
        if (!Color.TryParse(value, out var color))
            return;

        Resources["PrimaryColor"] = color;
        Resources["PrimaryColor75"] = WithOpacity(color, 0.75);
        Resources["PrimaryColor50"] = WithOpacity(color, 0.50);
        Resources["PrimaryColor10"] = WithOpacity(color, 0.10);
        Resources["PrimaryForegroundColor"] = ContrastingForeground(color);
    }

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(byte.MaxValue * opacity), color.R, color.G, color.B);

    private static Color ContrastingForeground(Color color)
    {
        var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance > 160 ? Color.Parse("#18181B") : Colors.White;
    }
}
