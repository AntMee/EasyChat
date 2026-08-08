using Avalonia.Controls;
using Avalonia.Threading;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shell;
using ShadUI;

namespace EasyChat.Presentation.Features.Shell.Views
{
    public partial class MainWindow : ShadUI.Window
    {
        private readonly Action _ensureTrayVisible = null!;

        public MainWindow() => InitializeComponent();

        public MainWindow(
            MainWindowViewModel viewModel,
            SettingsSession settings,
            ShadUI.DialogManager dialogs,
            Action ensureTrayVisible)
            : this()
        {
            _ensureTrayVisible = ensureTrayVisible
                ?? throw new ArgumentNullException(nameof(ensureTrayVisible));
            DataContext = viewModel;
            Closing += (_, args) => HandleClosing(args, settings, dialogs);
            // Queue WindowState after the current input/layout pass to avoid chrome thrash.
            viewModel.FullScreenChanged += (_, fullScreen) =>
                Dispatcher.UIThread.Post(
                    () => WindowState = fullScreen ? WindowState.FullScreen : WindowState.Normal,
                    DispatcherPriority.Render);
            if (viewModel.IsFullScreen)
                WindowState = WindowState.FullScreen;
        }

        public bool IsExiting { get; set; }

        private void HandleClosing(
            WindowClosingEventArgs args,
            SettingsSession settings,
            ShadUI.DialogManager dialogs)
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
                    // Title is painted inside CloseBehaviorDialogView (ViewModel-only shell).
                    // Window close is already cancelled; Cancel / background click keeps the app open.
                    var viewModel = new CloseBehaviorDialogViewModel(
                        dialogs,
                        settings.General,
                        _ensureTrayVisible,
                        Hide,
                        () =>
                        {
                            IsExiting = true;
                            Close();
                        });
                    dialogs.CreateDialog(viewModel)
                        .Dismissible()
                        .Show();
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
