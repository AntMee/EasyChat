using EasyChat.Contracts.Updates;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shell;
using SukiUI.Dialogs;
using SukiUI.Toasts;

namespace EasyChat.Desktop;

public sealed record DesktopUiContext(
    SettingsSession Settings,
    MainWindowViewModel MainWindowViewModel,
    ISukiDialogManager Dialogs,
    DesktopInteractionLifecycle Interactions,
    IApplicationUpdateService Updates,
    ISukiToastManager Toasts);
