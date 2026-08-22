using System.IO.Pipes;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using EasyChat.Contracts.Capture;
using EasyChat.Infrastructure.Windows.Capture;
using EasyChat.Infrastructure.Windows.Input;
using EasyChat.Presentation.Features.Capture;
using EasyChat.Presentation.Foundation.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Desktop.Windows.Capture;

[SupportedOSPlatform("windows")]
internal static class WindowsScreenshotWorker
{
    internal static void Run(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.None);
        pipe.Connect(10_000);
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);

        Task<ScreenshotCaptureCommand?> ReceiveAsync() => Task.Run(() =>
        {
            try
            {
                var request = ScreenshotWorkerProtocol.ReadRequest(reader);
                var theme = request.Theme switch
                {
                    "Dark" => ThemeVariant.Dark,
                    "Light" => ThemeVariant.Light,
                    _ => ThemeVariant.Default
                };
                return new ScreenshotCaptureCommand(
                    request.Precise,
                    theme,
                    request.PrimaryColor,
                    request.CultureName,
                    request.DefaultAction,
                    request.ToolbarMode);
            }
            catch (Exception exception) when (exception is EndOfStreamException
                                              or IOException
                                              or ObjectDisposedException)
            {
                return null;
            }
        });

        void Complete(ScreenshotSelection? selection)
        {
            if (selection is null)
                ScreenshotWorkerProtocol.WriteCancelled(writer);
            else
                ScreenshotWorkerProtocol.WriteSuccess(writer, selection);
        }

        try
        {
            var overlays = new CaptureOverlayCoordinator(
                new WindowsScreenCatalog(),
                new WindowsScreenCapture(),
                new WindowsPointerPosition(),
                new WindowsWindowFocus(),
                new WindowsKeyboardState(),
                new OpenCvLongScreenshotStitcher(),
                CreateWindowBehavior());
            AppBuilder.Configure(() => new ScreenshotCaptureWorkerApp(
                    overlays,
                    ReceiveAsync,
                    () => ScreenshotWorkerProtocol.WriteReady(writer),
                    Complete,
                    exception => ScreenshotWorkerProtocol.WriteFailure(writer, exception)))
                .UsePlatformDetect()
                .With(new Win32PlatformOptions
                {
                    RenderingMode = [Win32RenderingMode.Software]
                })
                .LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception exception)
        {
            try
            {
                ScreenshotWorkerProtocol.WriteFailure(writer, exception);
            }
            catch
            {
                // The owner process has already closed the worker pipe.
            }
        }
    }

    private static IPlatformWindowBehavior CreateWindowBehavior() =>
        new AvaloniaWindowsWindowBehavior(
            new WindowsOwnedWindowBehavior(NullLogger<WindowsOwnedWindowBehavior>.Instance));
}
