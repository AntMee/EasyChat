using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shell;
using ShadUI;

namespace EasyChat.Presentation.Features.Shell.Views
{
    public partial class MainWindow : ShadUI.Window
    {
        private const double ResizeBorderThickness = 8;
        private static readonly CornerRadius WindowCornerRadius = new(12);
        private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
        private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
        private static readonly Cursor TopLeftResizeCursor = new(StandardCursorType.TopLeftCorner);
        private static readonly Cursor TopRightResizeCursor = new(StandardCursorType.TopRightCorner);
        private readonly Action _ensureTrayVisible = null!;
        private MainWindowViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            // ShadUI 0.2.4 resets RootCornerRadius while applying its Windows template.
            Opened += (_, _) => ApplyRootCornerRadius();
            AddHandler(PointerPressedEvent, OnResizePointerPressed, RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnEscapeKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyUpEvent, OnEscapeKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            PointerMoved += OnResizePointerMoved;
            PointerExited += (_, _) => Cursor = null;
            PropertyChanged += (_, args) =>
            {
                if (args.Property == WindowStateProperty && WindowState == WindowState.Normal)
                    ApplyRootCornerRadius();
            };
        }

        public MainWindow(
            MainWindowViewModel viewModel,
            SettingsSession settings,
            ShadUI.DialogManager dialogs,
            Action ensureTrayVisible)
            : this()
        {
            _ensureTrayVisible = ensureTrayVisible
                ?? throw new ArgumentNullException(nameof(ensureTrayVisible));
            _viewModel = viewModel;
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

        private void OnEscapeKey(object? sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape || _viewModel?.IsFullScreen != true)
                return;

            _viewModel.ExitFullScreen();
            WindowState = WindowState.Normal;
            args.Handled = true;
        }

        private void ApplyRootCornerRadius()
        {
            if (WindowState == WindowState.Normal)
                RootCornerRadius = WindowCornerRadius;
        }

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs args)
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                || IsInteractivePointerSource(args.Source))
                return;

            var edge = GetResizeEdge(args.GetPosition(this));
            if (edge is not { } resizeEdge)
                return;

            args.Handled = true;
            BeginResizeDrag(resizeEdge, args);
        }

        private void OnResizePointerMoved(object? sender, PointerEventArgs args)
        {
            Cursor = IsInteractivePointerSource(args.Source)
                ? null
                : GetResizeCursor(GetResizeEdge(args.GetPosition(this)));
        }

        private bool IsInteractivePointerSource(object? source)
        {
            if (source is not Visual visual)
                return false;

            for (var current = visual; current is not null; current = current.GetVisualParent())
            {
                if (ReferenceEquals(current, this))
                    return false;
                if (current is InputElement { Focusable: true })
                    return true;

                var typeName = current.GetType().Name;
                if (typeName.Contains("Popup", StringComparison.Ordinal)
                    || typeName.Contains("Flyout", StringComparison.Ordinal)
                    || typeName.Contains("Overlay", StringComparison.Ordinal)
                    || typeName is "ColorPicker" or "ColorSpectrum" or "ColorSlider")
                    return true;
            }

            // Native Popup/Flyout roots can be separate from the owner window.
            return true;
        }

        private WindowEdge? GetResizeEdge(Point position)
        {
            if (!CanResize || WindowState != WindowState.Normal)
                return null;

            // Pointer events from a Popup/Flyout can still reach the window while
            // their coordinates are outside this client area. They must not be
            // interpreted as a request to resize the window edge.
            if (position.X < 0 || position.Y < 0
                || position.X >= Bounds.Width || position.Y >= Bounds.Height)
                return null;

            var left = position.X <= ResizeBorderThickness;
            var right = position.X >= Bounds.Width - ResizeBorderThickness;
            var top = position.Y <= ResizeBorderThickness;
            var bottom = position.Y >= Bounds.Height - ResizeBorderThickness;

            return (left, right, top, bottom) switch
            {
                (true, _, true, _) => WindowEdge.NorthWest,
                (_, true, true, _) => WindowEdge.NorthEast,
                (true, _, _, true) => WindowEdge.SouthWest,
                (_, true, _, true) => WindowEdge.SouthEast,
                (true, _, _, _) => WindowEdge.West,
                (_, true, _, _) => WindowEdge.East,
                (_, _, true, _) => WindowEdge.North,
                (_, _, _, true) => WindowEdge.South,
                _ => null
            };
        }

        private static Cursor? GetResizeCursor(WindowEdge? edge) => edge switch
        {
            WindowEdge.West or WindowEdge.East => HorizontalResizeCursor,
            WindowEdge.North or WindowEdge.South => VerticalResizeCursor,
            WindowEdge.NorthWest or WindowEdge.SouthEast => TopLeftResizeCursor,
            WindowEdge.NorthEast or WindowEdge.SouthWest => TopRightResizeCursor,
            _ => null
        };

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
