using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyChat.Contracts.Selection;
using EasyChat.Presentation.Foundation.Platform;
using Material.Icons.Avalonia;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;

namespace EasyChat.Presentation.Features.SelectionTranslation.Views;

public partial class SelectionIconWindowView : Window
{
    private IPlatformWindowBehavior? _platformWindowBehavior;
    private ILogger<SelectionIconWindowView>? _logger;
    private readonly Loading? _loadingSpinner;
    private readonly Control? _toolbar;
    private readonly Button? _translateButton;
    private readonly Button? _correctionButton;
    private readonly Button? _polishButton;
    private readonly Button? _summaryButton;
    private readonly Button? _explanationButton;
    private bool _isLoading;

    public SelectionIconWindowView()
    {
        InitializeComponent();
        _loadingSpinner = this.FindControl<Loading>("LoadingSpinner");
        _toolbar = this.FindControl<Control>("Toolbar");
        _translateButton = this.FindControl<Button>("TranslateButton");
        _correctionButton = this.FindControl<Button>("CorrectionButton");
        _polishButton = this.FindControl<Button>("PolishButton");
        _summaryButton = this.FindControl<Button>("SummaryButton");
        _explanationButton = this.FindControl<Button>("ExplanationButton");
    }

    public SelectionIconWindowView(
        IPlatformWindowBehavior platformWindowBehavior,
        ILogger<SelectionIconWindowView> logger)
        : this()
    {
        _platformWindowBehavior = platformWindowBehavior;
        _logger = logger;
        Opened += OnOpened;
    }

    public event EventHandler? TranslateClicked;
    public event EventHandler? CorrectionClicked;
    public event EventHandler? PolishClicked;
    public event EventHandler? SummaryClicked;
    public event EventHandler? ExplanationClicked;

    public bool IsLoading => _isLoading;

    public void Configure(SelectionToolbarOptions options)
    {
        if (_translateButton is not null) _translateButton.IsVisible = options.Translation;
        if (_correctionButton is not null) _correctionButton.IsVisible = options.Correction;
        if (_polishButton is not null) _polishButton.IsVisible = options.Polish;
        if (_summaryButton is not null) _summaryButton.IsVisible = options.Summary;
        if (_explanationButton is not null) _explanationButton.IsVisible = options.Explanation;

        var count = (options.Translation ? 1 : 0)
                    + (options.Correction ? 1 : 0)
                    + (options.Polish ? 1 : 0)
                    + (options.Summary ? 1 : 0)
                    + (options.Explanation ? 1 : 0);
        var compact = count == 1;
        var buttonWidth = compact ? 24 : 34;
        var buttonHeight = compact ? 24 : 32;
        foreach (var button in new[]
                 {
                     _translateButton,
                     _correctionButton,
                     _polishButton,
                     _summaryButton,
                     _explanationButton
                 })
        {
            if (button is null)
                continue;
            button.Width = buttonWidth;
            button.Height = buttonHeight;
            button.Padding = compact ? new Thickness(3) : new Thickness(5);
            if (button.Content is MaterialIcon icon)
            {
                icon.Width = compact ? 18 : 20;
                icon.Height = compact ? 18 : 20;
            }
        }

        Width = Math.Max(32, 8 + count * buttonWidth + Math.Max(0, count - 1) * 3);
        Height = compact ? 32 : 40;
    }

    public void ShowLoading()
    {
        _isLoading = true;
        if (_toolbar is not null) _toolbar.IsVisible = false;
        if (_loadingSpinner is not null) _loadingSpinner.IsVisible = true;
    }

    public void HideLoading()
    {
        _isLoading = false;
        if (_toolbar is not null) _toolbar.IsVisible = true;
        if (_loadingSpinner is not null) _loadingSpinner.IsVisible = false;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _platformWindowBehavior!.ConfigureNoActivateAsync(this);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Unable to configure the selection toolbar as non-activating.");
        }
    }

    private bool CanInvoke() => !_isLoading;
    private void OnTranslateClick(object? sender, RoutedEventArgs e) { if (CanInvoke()) TranslateClicked?.Invoke(this, EventArgs.Empty); }
    private void OnCorrectionClick(object? sender, RoutedEventArgs e) { if (CanInvoke()) CorrectionClicked?.Invoke(this, EventArgs.Empty); }
    private void OnPolishClick(object? sender, RoutedEventArgs e) { if (CanInvoke()) PolishClicked?.Invoke(this, EventArgs.Empty); }
    private void OnSummaryClick(object? sender, RoutedEventArgs e) { if (CanInvoke()) SummaryClicked?.Invoke(this, EventArgs.Empty); }
    private void OnExplanationClick(object? sender, RoutedEventArgs e) { if (CanInvoke()) ExplanationClicked?.Invoke(this, EventArgs.Empty); }
}
