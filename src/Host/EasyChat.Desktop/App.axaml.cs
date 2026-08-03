using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using EasyChat.Contracts.Selection;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shell;
using EasyChat.Contracts.Shortcuts;
using EasyChat.Contracts.Updates;
using EasyChat.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.ViewModels;
using EasyChat.Views;
using Material.Icons;
using Material.Icons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;

namespace EasyChat;

public sealed partial class App(IServiceProvider services) : Avalonia.Application
{
    private readonly IServiceProvider _services = services;
    private readonly ILogger<App> _logger = services.GetRequiredService<ILogger<App>>();
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
            _desktop = desktop;
            var settings = _services.GetRequiredService<SettingsSession>();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            _mainWindow = new MainWindow(
                viewModel,
                settings,
                _services.GetRequiredService<ISukiDialogManager>());
            desktop.MainWindow = _mainWindow;
            desktop.Exit += OnExit;
            settings.Changed += OnSettingsChanged;
            UpdateTrayIcon(settings.General.ClosingBehavior);
            StartInteractiveServices();
            _ = CheckForUpdatesAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async void StartInteractiveServices()
    {
        try
        {
            _services.GetRequiredService<ISelectionInteractionUseCases>()
                .Start(_services.GetRequiredService<ISelectionInteractionSink>());
            var report = await _services.GetRequiredService<IShortcutUseCases>().StartAsync();
            foreach (var issue in report.Issues)
                _logger.LogWarning(
                    "Unable to register shortcut {Action} ({Gesture}): {Message}",
                    issue.ActionType,
                    issue.KeyCombination,
                    issue.Error.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to start interactive desktop services.");
        }
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
        var updates = _services.GetRequiredService<IApplicationUpdateService>();
        var result = await updates.CheckAsync();
        if (result.IsFailure || !result.Value.IsUpdateAvailable) return;
        _services.GetRequiredService<ISukiToastManager>()
            .CreateToast()
            .WithTitle(EasyChat.Lang.Resources.NewVersionAvailable)
            .WithContent(string.Format(EasyChat.Lang.Resources.NewVersionContent, result.Value.LatestVersion))
            .WithActionButton(EasyChat.Lang.Resources.Later, _ => { }, true)
            .WithActionButton(EasyChat.Lang.Resources.Update, toast => { _ = DownloadUpdateAsync(updates); }, true)
            .Queue();
    }

    private async Task DownloadUpdateAsync(IApplicationUpdateService updates)
    {
        var progress = new ProgressBar { Value = 0, ShowProgressText = true };
        var toasts = _services.GetRequiredService<ISukiToastManager>();
        var toast = toasts.CreateToast()
            .WithTitle(EasyChat.Lang.Resources.Updating)
            .WithContent(progress)
            .Queue();
        var result = await updates.DownloadAndRestartAsync(new Progress<int>(value => progress.Value = value));
        toasts.Dismiss(toast);
        if (result.IsFailure)
            toasts.CreateToast()
                .WithTitle(EasyChat.Lang.Resources.UpdateFailed)
                .WithContent(EasyChat.Lang.Resources.CheckNetwork)
                .Dismiss().After(TimeSpan.FromSeconds(5))
                .Queue();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        RemoveTrayIcon();
        var settings = _services.GetRequiredService<SettingsSession>();
        settings.Changed -= OnSettingsChanged;
        try
        {
            _services.GetRequiredService<ISelectionInteractionUseCases>().Stop();
            _services.GetRequiredService<IShortcutUseCases>().DisposeAsync().AsTask().GetAwaiter().GetResult();
            _services.GetRequiredService<ISelectionInteractionUseCases>().DisposeAsync().AsTask().GetAwaiter().GetResult();
            _services.GetRequiredService<IShellLifecycle>().StopAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Desktop shutdown cleanup failed.");
        }
        finally
        {
            if (_services is IAsyncDisposable asyncDisposable)
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else if (_services is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
