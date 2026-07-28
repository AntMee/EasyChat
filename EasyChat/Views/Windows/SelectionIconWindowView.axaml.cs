using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using EasyChat.Common;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using Material.Icons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;

namespace EasyChat.Views.Windows;

public partial class SelectionIconWindowView : Window
{
    public event EventHandler? TranslateClicked;
    public event EventHandler? CorrectionClicked;
    public event EventHandler? PolishClicked;
    public event EventHandler? SummaryClicked;
    
    private Loading? _loadingSpinner;
    private Control? _toolbar;
    private Button? _translateButton;
    private Button? _correctionButton;
    private Button? _polishButton;
    private Button? _summaryButton;
    private bool _isLoading;

    public SelectionIconWindowView()
    {
        InitializeComponent();
        
        // Apply no-activate style when window is opened (prevents focus stealing)
        Opened += (_, _) => ApplyNoActivateStyle();
    }

    private void ApplyNoActivateStyle()
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle != null && handle != IntPtr.Zero)
        {
            var focusService = Global.Services?.GetService<IFocusService>();
            focusService?.SetWindowNoActivate(handle.Value);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        
        // Find named controls
        _loadingSpinner = this.FindControl<Loading>("LoadingSpinner");
        _toolbar = this.FindControl<Control>("Toolbar");
        _translateButton = this.FindControl<Button>("TranslateButton");
        _correctionButton = this.FindControl<Button>("CorrectionButton");
        _polishButton = this.FindControl<Button>("PolishButton");
        _summaryButton = this.FindControl<Button>("SummaryButton");
    }

    public void ApplyConfiguration(SelectionTranslationConfig config)
    {
        if (_translateButton != null) _translateButton.IsVisible = config.TranslationEnabled;
        if (_correctionButton != null) _correctionButton.IsVisible = config.CorrectionEnabled;
        if (_polishButton != null) _polishButton.IsVisible = config.PolishEnabled;
        if (_summaryButton != null) _summaryButton.IsVisible = config.SummaryEnabled;

        var count = (config.TranslationEnabled ? 1 : 0) +
                    (config.CorrectionEnabled ? 1 : 0) +
                    (config.PolishEnabled ? 1 : 0) +
                    (config.SummaryEnabled ? 1 : 0);
        var compact = count == 1;
        var buttonWidth = compact ? 24 : 34;
        var buttonHeight = compact ? 24 : 32;
        foreach (var button in new[] { _translateButton, _correctionButton, _polishButton, _summaryButton })
        {
            if (button == null) continue;
            button.Width = buttonWidth;
            button.Height = buttonHeight;
            button.Padding = compact ? new Avalonia.Thickness(3) : new Avalonia.Thickness(5);
            if (button.Content is MaterialIcon icon)
            {
                icon.Width = compact ? 18 : 20;
                icon.Height = compact ? 18 : 20;
            }
        }

        Width = Math.Max(32, 8 + count * buttonWidth + Math.Max(0, count - 1) * 3);
        Height = compact ? 32 : 40;
    }
    
    /// <summary>
    /// Shows the loading spinner and hides the translate icon
    /// </summary>
    public void ShowLoading()
    {
        _isLoading = true;
        if (_toolbar != null) _toolbar.IsVisible = false;
        if (_loadingSpinner != null) _loadingSpinner.IsVisible = true;
    }
    
    /// <summary>
    /// Hides the loading spinner and shows the translate icon
    /// </summary>
    public void HideLoading()
    {
        _isLoading = false;
        if (_toolbar != null) _toolbar.IsVisible = true;
        if (_loadingSpinner != null) _loadingSpinner.IsVisible = false;
    }
    
    /// <summary>
    /// Gets whether the icon is currently in loading state
    /// </summary>
    public bool IsLoading => _isLoading;
    
    private bool CanInvoke()
    {
        return !_isLoading;
    }

    private void OnTranslateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (CanInvoke()) TranslateClicked?.Invoke(this, EventArgs.Empty); }
    private void OnCorrectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (CanInvoke()) CorrectionClicked?.Invoke(this, EventArgs.Empty); }
    private void OnPolishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (CanInvoke()) PolishClicked?.Invoke(this, EventArgs.Empty); }
    private void OnSummaryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { if (CanInvoke()) SummaryClicked?.Invoke(this, EventArgs.Empty); }
}
