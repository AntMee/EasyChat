using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using EasyChat.Common;
using EasyChat.ViewModels;
using SukiUI.Controls;
using SukiUI.Models;
using EasyChat.Services.Abstractions;
using EasyChat.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;

namespace EasyChat.Views;

public partial class MainWindow : SukiWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Global.Screens = Screens;
    }

    public bool IsExiting { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (IsExiting) return;

        var configService = Global.Services?.GetRequiredService<IConfigurationService>();
        
        switch (configService?.General?.ClosingBehavior)
        {
            case Models.Configuration.WindowClosingBehavior.ExitApp:
                // Let it close
                return;
            
            case Models.Configuration.WindowClosingBehavior.MinimizeToTray:
                e.Cancel = true;
                Hide();
                return;

            default:
                e.Cancel = true;
                ShowCloseBehaviorDialog();
                break;
        }
    }

    private void ShowCloseBehaviorDialog()
    {
        var dialogManager = Global.Services?.GetRequiredService<ISukiDialogManager>();
        
        dialogManager?.CreateDialog()
            .WithTitle(Lang.Resources.CloseToTrayPromptTitle)
            .OfType(NotificationType.Information)
            .WithViewModel(dialog => new CloseBehaviorDialogViewModel(dialog))
            .TryShow();
    }
}
