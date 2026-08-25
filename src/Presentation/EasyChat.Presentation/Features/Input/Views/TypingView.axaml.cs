using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Input;
using EasyChat.Presentation.Foundation.Platform;
using Key = Avalonia.Input.Key;

namespace EasyChat.Presentation.Features.Input.Views;

public partial class TypingView : Window
{
    private const double BaseWindowHeight = 148;
    private SettingsSession? _settings;
    private double _previewHeightLimit;
    private bool _previewDictionaryLaunchPending;

    public TypingView() => InitializeComponent();

    public TypingView(
        TypingViewModel viewModel,
        SettingsSession settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        ApplyConfiguration();
        settings.Changed += OnSettingsChanged;
        viewModel.PreviewDictionaryShown += OnPreviewDictionaryShown;
        viewModel.PreviewDictionaryOpenFailed += OnPreviewDictionaryOpenFailed;
        InputBox.SizeChanged += OnInputBoxSizeChanged;
        FooterContainer.SizeChanged += OnFooterSizeChanged;
        SizeChanged += OnWindowSizeChanged;
        Opened += (_, _) =>
        {
            _previewHeightLimit = Math.Max(MinHeight, BaseWindowHeight) * 2;
            UpdatePreviewBounds();
            InputBox.Focus();
        };
        Closed += (_, _) =>
        {
            settings.Changed -= OnSettingsChanged;
            viewModel.PreviewDictionaryShown -= OnPreviewDictionaryShown;
            viewModel.PreviewDictionaryOpenFailed -= OnPreviewDictionaryOpenFailed;
            InputBox.SizeChanged -= OnInputBoxSizeChanged;
            FooterContainer.SizeChanged -= OnFooterSizeChanged;
            SizeChanged -= OnWindowSizeChanged;
            viewModel.Dispose();
        };
        Deactivated += (_, _) =>
        {
            _ = HandleDeactivatedAsync(viewModel);
        };
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs eventArgs)
    {
        if (eventArgs.Section == SettingsSection.Input)
            ApplyConfiguration();
    }

    private void ApplyConfiguration()
    {
        if (_settings is null)
            return;
        // Rounded, decoration-free windows need per-pixel transparency even when the preference is None.
        TransparencyLevelHint = WindowTransparencyLevels.ForRoundedWindow();
        var background = ParseBrush(_settings.Input.BackgroundColor);
        if (background is not null && this.FindControl<Border>("MainCard") is { } card)
            card.Background = background;
        var foreground = ParseBrush(_settings.Input.FontColor);
        if (foreground is not null && this.FindControl<TextBox>("InputBox") is { } input)
            input.Foreground = foreground;
        UpdatePreviewBounds();
    }

    private void OnInputBoxSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePreviewBounds();

    private void OnFooterSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePreviewBounds();

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePreviewBounds();

    private void UpdatePreviewBounds()
    {
        var inputHeight = InputBox.Bounds.Height;
        if (!double.IsFinite(inputHeight) || inputHeight <= 0)
            return;

        var minimum = Math.Max(80, inputHeight);
        if (_previewHeightLimit <= 0)
            _previewHeightLimit = Math.Max(MinHeight, BaseWindowHeight) * 2;

        // Reserve space for the input and footer so the footer remains visible when the
        // window reaches its maximum height. The preview keeps at least the input height.
        var reservedHeight = inputHeight + FooterContainer.Bounds.Height;
        var availableHeight = double.IsFinite(MaxHeight) && MaxHeight > 0
            ? MaxHeight - reservedHeight
            : _previewHeightLimit;
        var maximum = Math.Max(minimum, Math.Min(_previewHeightLimit, availableHeight));
        if (Math.Abs(PreviewContainer.MinHeight - minimum) > 0.1)
            PreviewContainer.MinHeight = minimum;
        if (Math.Abs(PreviewContainer.MaxHeight - maximum) > 0.1)
            PreviewContainer.MaxHeight = maximum;
    }

    private async void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await SubmitAsync();
    }

    private async void OnSubmitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await SubmitAsync();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnPreviewWordLookupOpening(object? sender, EventArgs e)
    {
        _previewDictionaryLaunchPending = true;
    }

    private void OnPreviewDictionaryShown(object? sender, EventArgs e) =>
        _previewDictionaryLaunchPending = false;

    private void OnPreviewDictionaryOpenFailed(object? sender, EventArgs e) =>
        _previewDictionaryLaunchPending = false;

    private async Task HandleDeactivatedAsync(TypingViewModel viewModel)
    {
        if (this.GetVisualDescendants().OfType<ComboBox>().Any(comboBox => comboBox.IsDropDownOpen))
            return;

        // Pointer release starts the lookup command after this event can already
        // be queued. Let that command create the dictionary before treating the
        // input popup as an outside click.
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.ContextIdle);

        if (_previewDictionaryLaunchPending)
            return;

        if (await viewModel.IsDictionaryWindowVisibleAsync().ConfigureAwait(true))
            return;

        Close();
    }

    private async Task SubmitAsync()
    {
        var input = this.FindControl<TextBox>("InputBox");
        if (input is null)
            return;

        if (string.IsNullOrWhiteSpace(input.Text))
        {
            Close();
            return;
        }

        input.IsEnabled = false;
        if (DataContext is not TypingViewModel viewModel)
        {
            input.IsEnabled = true;
            return;
        }

        var delivered = await viewModel.TranslateAndSendAsync(viewModel.InputText);
        if (!delivered)
        {
            input.IsEnabled = true;
            return;
        }

        Hide();
        Close();
    }

    private static IBrush? ParseBrush(string value)
    {
        try
        {
            return Brush.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
