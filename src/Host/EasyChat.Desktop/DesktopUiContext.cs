using EasyChat.Contracts.Updates;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.ViewModels;
using SukiUI.Dialogs;
using SukiUI.Toasts;

namespace EasyChat;

public sealed record DesktopUiContext(
    SettingsSession Settings,
    MainWindowViewModel MainWindowViewModel,
    ISukiDialogManager Dialogs,
    DesktopInteractionLifecycle Interactions,
    IApplicationUpdateService Updates,
    ISukiToastManager Toasts);
