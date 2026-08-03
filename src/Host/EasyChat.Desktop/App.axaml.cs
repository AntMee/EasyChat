using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Views;
using Material.Icons;
using Material.Icons.Avalonia;
using ReactiveUI;
using SukiUI.Toasts;

namespace EasyChat;

public sealed partial class App(Func<DesktopUiContext> createUiContext) : Avalonia.Application
{
    public App()
        : this(null!) =>
        throw new InvalidOperationException(
            "App must be created by DesktopApplication with explicit dependencies.");

    private DesktopUiContext? _ui;
    private TrayIcon? _trayIcon;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (SynchronizationContext.Current is { } synchronizationContext)
            RxApp.MainThreadScheduler = new SynchronizationContextScheduler(synchronizationContext);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var ui = createUiContext();
            _ui = ui;
            _desktop = desktop;
            _mainWindow = new MainWindow(ui.MainWindowViewModel, ui.Settings, ui.Dialogs);
            desktop.MainWindow = _mainWindow;
            desktop.Exit += OnExit;
            ui.Settings.Changed += OnSettingsChanged;
            UpdateTrayIcon(ui.Settings.General.ClosingBehavior);
            ui.Interactions.Start();
            _ = CheckForUpdatesAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.Section == SettingsSection.General)
            UpdateTrayIcon(args.Current.General.ClosingBehavior);
    }

    private void UpdateTrayIcon(ClosingBehavior behavior)
    {
        if (behavior == ClosingBehavior.MinimizeToTray)
            EnsureTrayIcon();
        else
            RemoveTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        using var stream = AssetLoader.Open(new Uri("avares://EasyChat.Desktop/Assets/easychat-logo.ico"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = EasyChat.Lang.Resources.AppName,
            Menu = CreateTrayMenu()
        };
        _trayIcon.Clicked += OnTrayShow;
        var icons = GetValue(TrayIcon.IconsProperty) ?? new TrayIcons();
        if (GetValue(TrayIcon.IconsProperty) is null)
            SetValue(TrayIcon.IconsProperty, icons);
        icons.Add(_trayIcon);
    }

    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem(EasyChat.Lang.Resources.TrayShow);
        show.Click += OnTrayShow;
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem(EasyChat.Lang.Resources.TrayExit);
        exit.Click += OnTrayExit;
        menu.Items.Add(exit);
        return menu;
    }

    private void RemoveTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.Clicked -= OnTrayShow;
        if (GetValue(TrayIcon.IconsProperty) is { } icons)
            icons.Remove(_trayIcon);
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void OnTrayShow(object? sender, EventArgs args)
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnTrayExit(object? sender, EventArgs args)
    {
        if (_mainWindow is not null) _mainWindow.IsExiting = true;
        _desktop?.Shutdown();
    }

    private async Task CheckForUpdatesAsync()
    {
        var ui = RequireUi();
        var result = await ui.Updates.CheckAsync();
        if (result.IsFailure || !result.Value.IsUpdateAvailable) return;
        ui.Toasts
            .CreateToast()
            .WithTitle(EasyChat.Lang.Resources.NewVersionAvailable)
            .WithContent(string.Format(EasyChat.Lang.Resources.NewVersionContent, result.Value.LatestVersion))
            .WithActionButton(EasyChat.Lang.Resources.Later, _ => { }, true)
            .WithActionButton(EasyChat.Lang.Resources.Update, toast => { _ = DownloadUpdateAsync(ui); }, true)
            .Queue();
    }

    private static async Task DownloadUpdateAsync(DesktopUiContext ui)
    {
        var progress = new ProgressBar { Value = 0, ShowProgressText = true };
        var toast = ui.Toasts.CreateToast()
            .WithTitle(EasyChat.Lang.Resources.Updating)
            .WithContent(progress)
            .Queue();
        var result = await ui.Updates.DownloadAndRestartAsync(new Progress<int>(value => progress.Value = value));
        ui.Toasts.Dismiss(toast);
        if (result.IsFailure)
            ui.Toasts.CreateToast()
                .WithTitle(EasyChat.Lang.Resources.UpdateFailed)
                .WithContent(EasyChat.Lang.Resources.CheckNetwork)
                .Dismiss().After(TimeSpan.FromSeconds(5))
                .Queue();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        RemoveTrayIcon();
        if (_ui is { } ui)
        {
            ui.Settings.Changed -= OnSettingsChanged;
            ui.Interactions.Stop();
            _ui = null;
        }
    }

    private DesktopUiContext RequireUi() =>
        _ui ?? throw new InvalidOperationException("Desktop UI has not been initialized.");
}
