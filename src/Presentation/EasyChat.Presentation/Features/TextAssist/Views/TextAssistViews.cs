using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using EasyChat.Controls;
using EasyChat.Lang;
using EasyChat.Presentation.Foundation.Platform;
using EasyChat.Presentation.Shared.Feedback;
using EasyChat.ViewModels.Pages;
using EasyChat.ViewModels.Windows;
using EasyChat.Views.Pages;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;

namespace EasyChat.Views.Pages
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
            CopyFeedback.Show(sender as Control, EasyChat.Lang.Resources.Copied);
        }
    }

    public partial class TextAssistCorrectionView : UserControl
    {
        public TextAssistCorrectionView() => InitializeComponent();

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
            CopyFeedback.Show(sender as Control, EasyChat.Lang.Resources.Copied);
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

namespace EasyChat.Views.Windows
{
    public partial class TextAssistWindowView : SukiWindow
    {
        private TextAssistViewModel? _viewModel;
        private ContentControl? _editorHost;
        private bool _correction;

        public TextAssistWindowView() => InitializeComponent();

        public TextAssistWindowView(TextAssistViewModel viewModel) : this()
        {
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += OnLoaded;
            Closed += (_, _) => viewModel.Cancel();
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
            _editorHost.Content = _correction
                ? new TextAssistCorrectionView { DataContext = _viewModel.Correction, Classes = { "Compact" } }
                : new TextAssistTranslationView { DataContext = _viewModel.Translation, Classes = { "Compact" } };
        }

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { Tag: string edgeName }
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && Enum.TryParse<WindowEdge>(edgeName, out var edge))
                BeginResizeDrag(edge, e);
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }

    public partial class TextAssistActionWindowView : SukiWindow
    {
        public TextAssistActionWindowView() => InitializeComponent();
    }

    public partial class TextAssistResultWindowView : Window
    {
        private TextAssistResultWindowViewModel? _viewModel;
        private IPlatformWindowBehavior? _platformWindowBehavior;
        private ILogger<TextAssistResultWindowView>? _logger;

        public TextAssistResultWindowView() => InitializeComponent();

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
            CopyFeedback.Show(sender as Control, EasyChat.Lang.Resources.Copied);
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { Tag: string edgeName }
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && Enum.TryParse<WindowEdge>(edgeName, out var edge))
                BeginResizeDrag(edge, e);
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
