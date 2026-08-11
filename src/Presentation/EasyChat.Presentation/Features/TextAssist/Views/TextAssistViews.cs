using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using EasyChat.Presentation.Features.TextAssist.Controls;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Foundation.Platform;
using EasyChat.Presentation.Shared.Feedback;
using EasyChat.Presentation.Features.TextAssist;
using EasyChat.Presentation.Features.TextAssist.Views;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.TextAssist.Views
{
    public partial class TextAssistView : UserControl
    {
        public TextAssistView() => InitializeComponent();
    }

    public partial class TextAssistTranslationPageView : UserControl
    {
        public TextAssistTranslationPageView() => InitializeComponent();
    }

    public partial class TextAssistCorrectionPageView : UserControl
    {
        public TextAssistCorrectionPageView() => InitializeComponent();
    }

    public partial class TextAssistTranslationView : UserControl
    {
        public TextAssistTranslationView() => InitializeComponent();

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TextAssistTranslationViewModel viewModel
                || string.IsNullOrWhiteSpace(viewModel.TranslationResult))
                return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(viewModel.TranslationResult);
            CopyFeedback.Show(sender as Control, EasyChat.Presentation.Lang.Resources.Copied);
        }
    }

    public partial class TextAssistCorrectionView : UserControl
    {
        public TextAssistCorrectionView()
        {
            InitializeComponent();
            Loaded += OnCorrectionViewLoaded;
            Unloaded += OnCorrectionViewUnloaded;
            AnnotatedTextBox.TemplateApplied += OnAnnotatedTextBoxTemplateApplied;
        }

        private void OnCorrectionViewLoaded(object? sender, RoutedEventArgs e) => ConnectAnnotationLayout();

        private void OnCorrectionViewUnloaded(object? sender, RoutedEventArgs e)
        {
            var annotation = AnnotationLayer ?? this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
            if (annotation is not null) annotation.LayoutPresenter = null;
        }

        private void OnAnnotatedTextBoxTemplateApplied(object? sender, TemplateAppliedEventArgs e) => ConnectAnnotationLayout();

        private void ConnectAnnotationLayout()
        {
            var annotation = AnnotationLayer ?? this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
            var textBox = AnnotatedTextBox ?? this.FindControl<TextBox>("AnnotatedTextBox");
            if (annotation is null || textBox is null) return;
            annotation.LayoutPresenter = textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        }

        private void OnOriginalPointerMoved(object? sender, PointerEventArgs e)
        {
            var annotation = AnnotationLayer ?? this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
            var hint = CorrectionHint ?? this.FindControl<Border>("CorrectionHint");
            var hintText = CorrectionHintText ?? this.FindControl<TextBlock>("CorrectionHintText");
            if (annotation is null || hint is null || hintText is null) return;
            var issue = annotation.GetIssueAt(e.GetPosition(annotation));
            if (issue is null)
            {
                hint.IsVisible = false;
                return;
            }
            hintText.Text = $"{issue.Message}\n{issue.Suggestion}";
            PositionHint(hint, e);
        }

        private void OnOriginalPointerExited(object? sender, PointerEventArgs e)
        {
            var hint = CorrectionHint ?? this.FindControl<Border>("CorrectionHint");
            if (hint is not null) hint.IsVisible = false;
        }

        private async void OnCopyCorrectionClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: CorrectionVariant variant }) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(variant.Text);
            CopyFeedback.Show(sender as Control, EasyChat.Presentation.Lang.Resources.Copied);
        }

        private void PositionHint(Border hint, PointerEventArgs e)
        {
            var host = hint.Parent as Visual ?? this;
            var pointer = e.GetPosition(host);
            hint.RenderTransform = new TranslateTransform(
                Math.Clamp(pointer.X + 14, 0, Math.Max(0, host.Bounds.Width - hint.Bounds.Width)),
                Math.Clamp(pointer.Y + 14, 0, Math.Max(0, host.Bounds.Height - hint.Bounds.Height)));
            hint.IsVisible = true;
        }
    }
}

namespace EasyChat.Presentation.Features.TextAssist.Views
{
    public partial class TextAssistWindowView : ShadUI.Window
    {
        private const double ResizeBorderThickness = 8;
        private static readonly CornerRadius WindowCornerRadius = new(12);
        private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
        private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
        private static readonly Cursor TopLeftResizeCursor = new(StandardCursorType.TopLeftCorner);
        private static readonly Cursor TopRightResizeCursor = new(StandardCursorType.TopRightCorner);
        private TextAssistViewModel? _viewModel;
        private ContentControl? _editorHost;
        private bool _correction;

        public TextAssistWindowView()
        {
            InitializeComponent();
            // ShadUI 0.2.4 resets RootCornerRadius while applying its Windows template.
            Opened += (_, _) => ApplyRootCornerRadius();
            AddHandler(PointerPressedEvent, OnResizePointerPressed, RoutingStrategies.Tunnel);
            PointerMoved += OnResizePointerMoved;
            PointerExited += (_, _) => Cursor = null;
            PropertyChanged += (_, args) =>
            {
                if (args.Property == WindowStateProperty && WindowState == WindowState.Normal)
                    ApplyRootCornerRadius();
            };
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

        public TextAssistWindowView(TextAssistViewModel viewModel) : this()
        {
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += OnLoaded;
            Closed += (_, _) =>
            {
                viewModel.Cancel();
            };
            KeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        }

        public Task InitializeAsync(string text, bool correction)
        {
            _correction = correction;
            ApplyEditor();
            return _viewModel!.InitializeAsync(text, correction);
        }

        public void PrepareForInputCapture(bool correction)
        {
            _correction = correction;
            _viewModel!.PrepareForInputCapture(correction);
            ApplyEditor();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _editorHost ??= this.FindControl<ContentControl>("EditorHost");
            ApplyEditor();
        }

        private void ApplyEditor()
        {
            if (_editorHost is null || _viewModel is null) return;
            _editorHost.Margin = _correction
                ? new Thickness(25)
                : new Thickness(24, 20, 24, 20);
            _editorHost.Content = _correction
                ? new TextAssistCorrectionView { DataContext = _viewModel.Correction }
                : new TextAssistTranslationView { DataContext = _viewModel.Translation };
        }
    }

    public partial class TextAssistActionWindowView : Window
    {
        public TextAssistActionWindowView() => InitializeComponent();

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TextAssistResultWindowViewModel viewModel)
                return;
            var text = viewModel.CopyText;
            if (string.IsNullOrWhiteSpace(text))
                return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;
            await clipboard.SetTextAsync(text);
            CopyFeedback.Show(sender as Control, EasyChat.Presentation.Lang.Resources.Copied);
        }
    }

    public partial class TextAssistResultWindowView : Window
    {
        private TextAssistResultWindowViewModel? _viewModel;
        private IPlatformWindowBehavior? _platformWindowBehavior;
        private ILogger<TextAssistResultWindowView>? _logger;

        public TextAssistResultWindowView()
        {
            InitializeComponent();
            PointerPressed += OnSurfacePointerPressed;
        }

        public TextAssistResultWindowView(
            TextAssistResultWindowViewModel viewModel,
            IPlatformWindowBehavior platformWindowBehavior,
            ILogger<TextAssistResultWindowView> logger)
            : this()
        {
            _viewModel = viewModel;
            _platformWindowBehavior = platformWindowBehavior;
            _logger = logger;
            DataContext = viewModel;
            Opened += OnOpened;
            Closed += OnClosed;
            KeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        public Task InitializeAsync(string text, EasyChat.Contracts.TextAssist.TextAssistOperation operation) =>
            _viewModel!.InitializeAsync(text, operation);

        private async void OnOpened(object? sender, EventArgs e)
        {
            try
            {
                await _platformWindowBehavior!.ConfigureNoActivateAsync(this);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Unable to configure the text assist result window as non-activating.");
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _viewModel?.Cancel();
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_viewModel?.CopyText)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(_viewModel.CopyText);
            CopyFeedback.Show(sender as Control, EasyChat.Presentation.Lang.Resources.Copied);
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

        private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && !IsInteractivePointerSource(e.Source))
                BeginMoveDrag(e);
        }

        private static bool IsInteractivePointerSource(object? source)
        {
            if (source is not Visual visual)
                return false;
            if (visual is InputElement { Focusable: true })
                return true;

            for (var current = visual; current is not null; current = current.GetVisualParent())
            {
                if (current is InputElement { Focusable: true })
                    return true;

                var typeName = current.GetType().Name;
                if (typeName.Contains("Popup", StringComparison.Ordinal)
                    || typeName.Contains("Flyout", StringComparison.Ordinal)
                    || typeName.Contains("Overlay", StringComparison.Ordinal)
                    || typeName is "ColorPicker" or "ColorSpectrum" or "ColorSlider")
                    return true;
            }

            return false;
        }

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { Tag: string edgeName }
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && Enum.TryParse<WindowEdge>(edgeName, out var edge))
            {
                BeginResizeDrag(edge, e);
                e.Handled = true;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TextAssistResultWindowViewModel.IsCorrectionCorrect))
                Height = _viewModel!.IsCorrectionCorrect ? 220 : 420;
        }

        private void OnOriginalPointerMoved(object? sender, PointerEventArgs e)
        {
            var annotation = this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
            var hint = this.FindControl<Border>("CorrectionHint");
            var hintText = this.FindControl<TextBlock>("CorrectionHintText");
            if (annotation is null || hint is null || hintText is null) return;
            var issue = annotation.GetIssueAt(e.GetPosition(annotation));
            if (issue is null)
            {
                hint.IsVisible = false;
                return;
            }
            hintText.Text = $"{issue.Message}\n{issue.Suggestion}";
            var host = hint.Parent as Visual ?? this;
            var pointer = e.GetPosition(host);
            hint.RenderTransform = new TranslateTransform(
                Math.Clamp(pointer.X + 12, 0, Math.Max(0, host.Bounds.Width - hint.Bounds.Width)),
                Math.Clamp(pointer.Y + 12, 0, Math.Max(0, host.Bounds.Height - hint.Bounds.Height)));
            hint.IsVisible = true;
        }

        private void OnOriginalPointerExited(object? sender, PointerEventArgs e)
        {
            var hint = this.FindControl<Border>("CorrectionHint");
            if (hint is not null) hint.IsVisible = false;
        }
    }
}
