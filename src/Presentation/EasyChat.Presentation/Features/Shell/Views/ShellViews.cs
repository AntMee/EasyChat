using Avalonia.Controls.Notifications;
using Avalonia.Controls;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shell;
using SukiUI.Dialogs;
using SukiUI.Controls;

namespace EasyChat.Presentation.Features.Shell.Views
{
    public partial class MainWindow : SukiWindow
    {
        private readonly Action _ensureTrayVisible = null!;

        public MainWindow() => InitializeComponent();

        public MainWindow(
            MainWindowViewModel viewModel,
            SettingsSession settings,
            ISukiDialogManager dialogs,
            Action ensureTrayVisible)
            : this()
        {
            _ensureTrayVisible = ensureTrayVisible
                ?? throw new ArgumentNullException(nameof(ensureTrayVisible));
            DataContext = viewModel;
            Closing += (_, args) => HandleClosing(args, settings, dialogs);
            viewModel.FullScreenChanged += (_, fullScreen) =>
                WindowState = fullScreen ? WindowState.FullScreen : WindowState.Normal;
            if (viewModel.IsFullScreen)
                WindowState = WindowState.FullScreen;
        }

        public bool IsExiting { get; set; }

        private void HandleClosing(
            WindowClosingEventArgs args,
            SettingsSession settings,
            ISukiDialogManager dialogs)
        {
            if (IsExiting) return;
            switch (settings.General.ClosingBehavior)
            {
                case EasyChat.Contracts.Settings.ClosingBehavior.ExitApp:
                    IsExiting = true;
                    return;
                case EasyChat.Contracts.Settings.ClosingBehavior.MinimizeToTray:
                    args.Cancel = true;
                    _ensureTrayVisible();
                    Hide();
                    return;
                default:
                    args.Cancel = true;
                    dialogs.CreateDialog()
                        .WithTitle(EasyChat.Presentation.Lang.Resources.CloseToTrayPromptTitle)
                        .OfType(NotificationType.Information)
                        .WithViewModel(dialog => new CloseBehaviorDialogViewModel(
                            dialog,
                            settings.General,
                            _ensureTrayVisible,
                            Hide,
                            () =>
                            {
                                IsExiting = true;
                                Close();
                            }))
                        .TryShow();
                    return;
            }
        }
    }
}

namespace EasyChat.Presentation.Features.Shell.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView() => InitializeComponent();
    }

    public partial class AboutView : UserControl
    {
        public AboutView() => InitializeComponent();
    }
}

namespace EasyChat.Presentation.Features.Shell.Views
{
    public partial class CloseBehaviorDialogView : UserControl
    {
        public CloseBehaviorDialogView() => InitializeComponent();
    }
}
