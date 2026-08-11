using EasyChat.Contracts.Updates;
using EasyChat.Presentation.Features.Capture;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shell;
using ShadUI;

namespace EasyChat.Desktop;

public sealed record DesktopUiContext(
    SettingsSession Settings,
    MainWindowViewModel MainWindowViewModel,
    DesktopInteractionLifecycle Interactions,
    IApplicationUpdateService Updates,
    ToastManager UpdateToasts,
    IScreenshotCaptureSession ScreenshotCapture);
