using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using AvaloniaWindow = Avalonia.Controls.Window;
using AvaloniaWindowState = Avalonia.Controls.WindowState;
using Avalonia.Styling;
using EasyChat.Common;
using EasyChat.Controls.CustomTheme;
using EasyChat.Lang;
using EasyChat.Models;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using Material.Icons;
using ReactiveUI;
using SukiUI;
using SukiUI.Dialogs;
using SukiUI.Enums;
using SukiUI.Models;
using SukiUI.Toasts;

namespace EasyChat.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly SukiTheme _theme;
    private readonly IConfigurationService _configurationService;

    private Page? _activePage;
    private IAvaloniaReadOnlyList<Page> _pages;


    public MainWindowViewModel(
        IEnumerable<Page> pages,
        PageNavigationService pageNavigationService,
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        IConfigurationService configurationService)
    {
        // Sort and assign pages
        var sortedPages = pages.OrderBy(x => x.Index).ThenBy(x => x.DisplayName).ToList();
        _pages = new AvaloniaList<Page>(sortedPages);

        // Use the first page as default active page if available
        if (sortedPages.Any()) _activePage = sortedPages.First();

        ToastManager = toastManager;
        DialogManager = dialogManager;
        _configurationService = configurationService;

        // Global.ToastManager = toastManager; // Removed assignment as it is read-only

        _theme = SukiTheme.GetInstance();
        Themes = _theme.ColorThemes;
        // BackgroundStyles = _theme.BackgroundStyles; // Removed as it might not exist in this version
        var savedBaseTheme = configurationService.General.BaseTheme;
        BaseTheme = savedBaseTheme.Equals(nameof(ThemeVariant.Dark), StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
        _theme.ChangeBaseTheme(BaseTheme);

        if (!string.IsNullOrWhiteSpace(configurationService.General.ColorTheme))
        {
            var savedColorTheme = Themes.FirstOrDefault(x =>
                string.Equals(x.DisplayName, configurationService.General.ColorTheme, StringComparison.OrdinalIgnoreCase));
            if (savedColorTheme != null)
                _theme.ChangeColorTheme(savedColorTheme);
            else if (!string.IsNullOrWhiteSpace(configurationService.General.CustomThemePrimaryColor) &&
                     !string.IsNullOrWhiteSpace(configurationService.General.CustomThemeAccentColor))
            {
                try
                {
                    var customTheme = new SukiColorTheme(
                        configurationService.General.ColorTheme,
                        Color.Parse(configurationService.General.CustomThemePrimaryColor),
                        Color.Parse(configurationService.General.CustomThemeAccentColor));
                    _theme.AddColorTheme(customTheme);
                    _theme.ChangeColorTheme(customTheme);
                }
                catch (FormatException)
                {
                    // Ignore invalid values from a manually edited config.
                }
            }
        }

        TitleBarVisible = configurationService.General.TitleBarVisible;
        IsFullScreen = configurationService.General.FullScreen;

        // Commands
        ToggleBaseThemeCommand = ReactiveCommand.Create(ToggleBaseTheme);
        ChangeThemeCommand = ReactiveCommand.Create<SukiColorTheme>(ChangeTheme);
        CreateCustomThemeCommand = ReactiveCommand.Create(CreateCustomTheme);
        ToggleTitleBarCommand = ReactiveCommand.Create(ToggleTitleBar);
        ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen);
        OpenUrlCommand = ReactiveCommand.Create<string>(OpenUrl);

        // Navigation
        pageNavigationService.NavigationRequested += pageType =>
        {
            var page = Pages.FirstOrDefault(x => x.GetType() == pageType);
            if (page is null || ActivePage?.GetType() == pageType) return;
            ActivePage = page;
        };

        // Theme Events
        _theme.OnBaseThemeChanged += variant =>
        {
            BaseTheme = variant;
            _configurationService.General.BaseTheme = variant == ThemeVariant.Dark
                ? nameof(ThemeVariant.Dark)
                : nameof(ThemeVariant.Light);
            this.RaisePropertyChanged(nameof(ThemeToggleIcon));
            ToastManager.CreateSimpleInfoToast()
                .WithTitle(Resources.ThemeChangedTitle)
                .WithContent($"{Resources.ThemeChangedContent} {variant}.")
                .Queue();
        };

        _theme.OnColorThemeChanged += theme =>
        {
            _configurationService.General.ColorTheme = theme.DisplayName;
            ToastManager.CreateSimpleInfoToast()
                .WithTitle(Resources.ColorChangedTitle)
                .WithContent($"{Resources.ColorChangedContent} {theme.DisplayName}.")
                .Queue();
        };
    }

    public IAvaloniaReadOnlyList<Page> Pages
    {
        get => _pages;
        set => this.RaiseAndSetIfChanged(ref _pages, value);
    }

    public IAvaloniaReadOnlyList<SukiColorTheme> Themes { get; }
    public ISukiDialogManager DialogManager { get; }

    public ISukiToastManager
        ToastManager
    {
        get;
    } // Read-only property in VM interface usually, but if I need to set it... well Global has it. 

    public bool TitleBarVisible
    {
        get;
        set
        {
            if (!this.RaiseAndSetIfChanged(ref field, value)) return;
            _configurationService.General.TitleBarVisible = value;
        }
    } = true;

    public bool IsFullScreen
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public Page? ActivePage
    {
        get => _activePage;
        set => this.RaiseAndSetIfChanged(ref _activePage, value);
    }

    public ThemeVariant BaseTheme
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public MaterialIconKind ThemeToggleIcon =>
        BaseTheme == ThemeVariant.Dark ? MaterialIconKind.WeatherSunny : MaterialIconKind.WeatherNight;

    public SukiBackgroundStyle BackgroundStyle
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = SukiBackgroundStyle.Gradient;

    public bool AnimationsEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? CustomShaderFile
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool TransitionsEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public double TransitionTime
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleBaseThemeCommand { get; }
    public ReactiveCommand<SukiColorTheme, Unit> ChangeThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateCustomThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTitleBarCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
    public ReactiveCommand<string, Unit> OpenUrlCommand { get; }



    private void ToggleBaseTheme()
    {
        _theme.SwitchBaseTheme();
    }

    public void ChangeTheme(SukiColorTheme theme)
    {
        _theme.ChangeColorTheme(theme);
    }

    private void CreateCustomTheme()
    {
        DialogManager.CreateDialog()
            .WithViewModel(dialog => new CustomThemeDialogViewModel(_theme, dialog))
            .TryShow();
    }

    private void ToggleTitleBar()
    {
        TitleBarVisible = !TitleBarVisible;
        ToastManager.CreateSimpleInfoToast()
            .WithTitle($"{Resources.TitleBarTitle} {(TitleBarVisible ? "Visible" : "Hidden")}")
            .WithContent($"{Resources.TitleBarContent} {(TitleBarVisible ? "shown" : "hidden")}.")
            .Queue();
    }

    private void ToggleFullScreen()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not AvaloniaWindow window)
            return;

        window.WindowState = window.WindowState == AvaloniaWindowState.FullScreen
            ? AvaloniaWindowState.Normal
            : AvaloniaWindowState.FullScreen;
        TitleBarVisible = window.WindowState != AvaloniaWindowState.FullScreen;
        IsFullScreen = window.WindowState == AvaloniaWindowState.FullScreen;
        _configurationService.General.FullScreen = IsFullScreen;
    }

    private static void OpenUrl(string url)
    {
        UrlUtilities.OpenUrl(url);
    }
}
