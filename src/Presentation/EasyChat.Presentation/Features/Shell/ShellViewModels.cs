using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Contracts.Updates;
using EasyChat.Presentation.Features.Settings;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Settings.Theme;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Foundation.Navigation;
using EasyChat.Presentation.Shared.Controls;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ShadUI;
using ThemeMode = EasyChat.Contracts.Settings.ThemeMode;

namespace EasyChat.Presentation.Features.Shell
{
    public sealed class PageNavigation
    {
        public event Action<Type, object?>? NavigationRequested;

        public void NavigateTo<TPage>(object? context = null)
            where TPage : NavigationPageViewModel =>
            NavigationRequested?.Invoke(typeof(TPage), context);
    }

    /// <summary>Optional payload when opening Settings from Home / badges.</summary>
    public enum SettingsPane
    {
        General,
        Translation,
        Selection,
        Tts,
        Screenshot,
        Result,
        Input
    }

    public sealed class MainWindowViewModel : ViewModelBase
    {
        public const string UpdateToastManagerKey = "Update";

        private readonly SettingsSession _settings;
        private readonly IExternalUriLauncher _uriLauncher;
        private readonly ShadUI.ToastManager _toasts;
        private NavigationPageViewModel? _activePage;
        private ThemeMode _baseThemeMode;
        private ColorThemeOption? _activeColorTheme;
        private bool _isFullScreen;
        private bool _titleBarVisible;

        public MainWindowViewModel(
            IEnumerable<NavigationPageViewModel> pages,
            PageNavigation navigation,
            SettingsSession settings,
            IExternalUriLauncher uriLauncher,
            ShadUI.ToastManager shadToastManager,
            [FromKeyedServices(UpdateToastManagerKey)] ShadUI.ToastManager updateToastManager,
            ShadUI.DialogManager shadDialogManager)
        {
            _settings = settings;
            _uriLauncher = uriLauncher;
            _toasts = shadToastManager;
            ShadToastManager = shadToastManager;
            UpdateToastManager = updateToastManager;
            ShadDialogManager = shadDialogManager;
            Pages = new ObservableCollection<NavigationPageViewModel>(
                pages.OrderBy(page => page.Index).ThenBy(page => page.DisplayName));
            _activePage = Pages.FirstOrDefault();

            Themes = new ObservableCollection<ColorThemeOption>
            {
                new("Blue", Color.Parse("#3B82F6"), Color.Parse("#60A5FA")),
                new("Purple", Color.Parse("#8B5CF6"), Color.Parse("#A78BFA")),
                new("Red", Color.Parse("#EF4444"), Color.Parse("#F87171")),
                new("Orange", Color.Parse("#F97316"), Color.Parse("#FB923C")),
                new("Green", Color.Parse("#22C55E"), Color.Parse("#4ADE80"))
            };
            _baseThemeMode = settings.General.BaseTheme;
            _isFullScreen = settings.General.FullScreen;
            _titleBarVisible = !_isFullScreen;
            // Keep settings in sync without forcing a second chrome write on startup.
            if (settings.General.TitleBarVisible != _titleBarVisible)
                settings.General.TitleBarVisible = _titleBarVisible;
            ApplyBaseTheme(_baseThemeMode);
            RestoreColorTheme();

            CycleBaseThemeCommand = ReactiveCommand.Create(CycleBaseTheme);
            // Color palette changes remain independent from the base light/dark variant.
            ChangeThemeCommand = ReactiveCommand.Create<ColorThemeOption>(ApplyColorTheme);
            CreateCustomThemeCommand = ReactiveCommand.Create(CreateCustomTheme);
            SelectPageCommand = ReactiveCommand.Create<NavigationPageViewModel>(SelectPage);
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen);
            OpenUrlCommand = ReactiveCommand.Create<string>(OpenUrl);

            navigation.NavigationRequested += NavigateTo;
        }

        public event EventHandler<bool>? FullScreenChanged;

        public ObservableCollection<NavigationPageViewModel> Pages { get; }
        public ObservableCollection<ColorThemeOption> Themes { get; }
        public ShadUI.DialogManager ShadDialogManager { get; }
        public ShadUI.ToastManager ShadToastManager { get; }
        public ShadUI.ToastManager UpdateToastManager { get; }

        public NavigationPageViewModel? ActivePage
        {
            get => _activePage;
            set => this.RaiseAndSetIfChanged(ref _activePage, value);
        }

        public bool TitleBarVisible
        {
            get => _titleBarVisible;
            set
            {
                if (_titleBarVisible == value)
                    return;
                // Paint first — LiveGeneralSettings.Set flushes disk synchronously.
                this.RaiseAndSetIfChanged(ref _titleBarVisible, value);
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_settings.General.TitleBarVisible != value)
                            _settings.General.TitleBarVisible = value;
                    },
                    DispatcherPriority.Background);
            }
        }

        public bool IsFullScreen
        {
            get => _isFullScreen;
            private set => this.RaiseAndSetIfChanged(ref _isFullScreen, value);
        }

        public ThemeMode BaseThemeMode
        {
            get => _baseThemeMode;
            private set
            {
                if (_baseThemeMode == value)
                    return;
                this.RaiseAndSetIfChanged(ref _baseThemeMode, value);
                this.RaisePropertyChanged(nameof(ThemeToggleIcon));
                this.RaisePropertyChanged(nameof(CurrentThemeModeName));
            }
        }

        public MaterialIconKind ThemeToggleIcon => BaseThemeMode switch
        {
            ThemeMode.Light => MaterialIconKind.WeatherSunny,
            ThemeMode.Dark => MaterialIconKind.WeatherNight,
            _ => MaterialIconKind.ThemeLightDark
        };
        public string CurrentThemeModeName => BaseThemeMode switch
        {
            ThemeMode.Light => Resources.LightMode,
            ThemeMode.Dark => Resources.DarkMode,
            _ => Resources.FollowSystemMode
        };

        public ReactiveCommand<Unit, Unit> CycleBaseThemeCommand { get; }
        public ReactiveCommand<ColorThemeOption, Unit> ChangeThemeCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateCustomThemeCommand { get; }
        public ReactiveCommand<NavigationPageViewModel, Unit> SelectPageCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
        public ReactiveCommand<string, Unit> OpenUrlCommand { get; }

        private void RestoreColorTheme()
        {
            if (string.IsNullOrWhiteSpace(_settings.General.ColorTheme))
                return;
            var saved = Themes.FirstOrDefault(theme => string.Equals(
                theme.DisplayName,
                _settings.General.ColorTheme,
                StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                ApplyColorTheme(saved, persist: false, notify: false);
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.General.CustomThemePrimaryColor) ||
                string.IsNullOrWhiteSpace(_settings.General.CustomThemeAccentColor))
            {
                return;
            }

            try
            {
                var custom = new ColorThemeOption(
                    _settings.General.ColorTheme,
                    Color.Parse(_settings.General.CustomThemePrimaryColor),
                    Color.Parse(_settings.General.CustomThemeAccentColor),
                    IsCustom: true);
                Themes.Add(custom);
                ApplyColorTheme(custom, persist: false, notify: false);
            }
            catch (FormatException)
            {
                // Manually edited invalid colors leave ShadUI's default palette active.
            }
        }

        private void NavigateTo(Type pageType, object? context)
        {
            var page = Pages.FirstOrDefault(candidate => candidate.GetType() == pageType);
            if (page is null)
                return;
            ActivePage = page;
            if (page is SettingViewModel settings && context is SettingsPane pane)
                settings.OpenPane(pane);
        }


        private void CycleBaseTheme() => ChangeBaseTheme(BaseThemeMode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System
        });

        private void ChangeBaseTheme(ThemeMode mode)
        {
            if (BaseThemeMode == mode)
                return;

            // Paint first, persist after the frame — FlushSection rebuilds the whole bundle + disk.
            BaseThemeMode = mode;
            ApplyBaseTheme(mode);
            var modeToSave = mode;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (BaseThemeMode != modeToSave
                        || _settings.General.BaseTheme == modeToSave)
                        return;
                    _settings.General.BaseTheme = modeToSave;
                },
                DispatcherPriority.ApplicationIdle);
        }

        private void ApplyBaseTheme(ThemeMode mode)
        {
            var application = Application.Current
                ?? throw new InvalidOperationException("Avalonia application is not initialized.");

            var target = mode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };

            // A variant change is a single paint and does not reapply the color palette.
            if (Equals(application.RequestedThemeVariant, target))
                return;

            application.RequestedThemeVariant = target;
        }

        private void ApplyColorTheme(ColorThemeOption theme) =>
            ApplyColorTheme(theme, persist: true, notify: true);

        private void ApplyColorTheme(ColorThemeOption theme, bool persist, bool notify)
        {
            if (_activeColorTheme == theme)
                return;

            var resources = (Application.Current
                ?? throw new InvalidOperationException("Avalonia application is not initialized."))
                .Resources;
            resources["PrimaryColor"] = theme.PrimaryColor;
            resources["PrimaryColor75"] = WithOpacity(theme.PrimaryColor, 0.75);
            resources["PrimaryColor50"] = WithOpacity(theme.PrimaryColor, 0.50);
            resources["PrimaryColor10"] = WithOpacity(theme.PrimaryColor, 0.10);
            resources["PrimaryForegroundColor"] = ContrastingForeground(theme.PrimaryColor);
            resources["SecondaryColor"] = theme.AccentColor;
            resources["SecondaryColor75"] = WithOpacity(theme.AccentColor, 0.75);
            resources["SecondaryColor50"] = WithOpacity(theme.AccentColor, 0.50);
            resources["SecondaryForegroundColor"] = ContrastingForeground(theme.AccentColor);
            _activeColorTheme = theme;

            if (persist)
            {
                _settings.General.ColorTheme = theme.DisplayName;
                if (theme.IsCustom)
                {
                    _settings.General.CustomThemePrimaryColor = theme.PrimaryColor.ToString();
                    _settings.General.CustomThemeAccentColor = theme.AccentColor.ToString();
                }
            }

            if (notify)
            {
                _toasts.CreateToast(Resources.ColorChangedTitle)
                    .WithContent($"{Resources.ColorChangedContent} {theme.DisplayName}.")
                    .ShowInfo();
            }
        }

        private void SelectPage(NavigationPageViewModel page) => ActivePage = page;

        private static Color WithOpacity(Color color, double opacity) =>
            Color.FromArgb((byte)Math.Round(byte.MaxValue * opacity), color.R, color.G, color.B);

        private static Color ContrastingForeground(Color color)
        {
            var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
            return luminance > 160 ? Color.Parse("#18181B") : Colors.White;
        }

        private void CreateCustomTheme()
        {
            var viewModel = new CustomThemeDialogViewModel(ShadDialogManager, AddCustomTheme);
            ShadDialogManager.CreateDialog(viewModel).Show();
        }

        private void AddCustomTheme(ColorThemeOption theme)
        {
            Themes.Add(theme);
            ApplyColorTheme(theme);
        }

        private void ToggleFullScreen()
        {
            var next = !IsFullScreen;
            // Order: local state → window state event → deferred settings flush.
            // Sync settings flush + badge refresh on this path caused hitch/twitch.
            IsFullScreen = next;
            TitleBarVisible = !next;
            FullScreenChanged?.Invoke(this, next);
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_settings.General.FullScreen != next)
                        _settings.General.FullScreen = next;
                },
                DispatcherPriority.Background);
        }

        private void OpenUrl(string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                _uriLauncher.Open(uri);
        }
    }
}

namespace EasyChat.Presentation.Features.Shell
{
    public sealed class HomeHealthItem
    {
        public HomeHealthItem(
            string title,
            string description,
            bool isDone,
            MaterialIconKind icon,
            string actionText,
            ReactiveCommand<Unit, Unit> actionCommand)
        {
            Title = title;
            Description = description;
            IsDone = isDone;
            Icon = icon;
            ActionText = actionText;
            ActionCommand = actionCommand;
        }

        public string Title { get; }
        public string Description { get; }
        public bool IsDone { get; }
        public bool NeedsAction => !IsDone;
        public MaterialIconKind Icon { get; }
        public string ActionText { get; }
        public ReactiveCommand<Unit, Unit> ActionCommand { get; }
        public HomeStatusKind StatusKind => IsDone ? HomeStatusKind.Success : HomeStatusKind.Warning;
        public string StatusText => IsDone ? Resources.HomeStatusReady : Resources.HomeStatusNeedsSetup;
    }

    public sealed class HomeQuickAction(
        string title,
        string description,
        MaterialIconKind icon,
        ReactiveCommand<Unit, Unit> command)
    {
        public string Title { get; } = title;
        public string Description { get; } = description;
        public MaterialIconKind Icon { get; } = icon;
        public ReactiveCommand<Unit, Unit> Command { get; } = command;
    }

    public sealed class HomeViewModel : NavigationPageViewModel
    {
        private readonly IApplicationUpdateService _updates;
        private readonly PageNavigation _navigation;
        private readonly SettingsSession _settings;
        private string _latestVersion = "-";
        private IReadOnlyList<HomeHealthItem> _healthItems = [];

        public HomeViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            IApplicationUpdateService updates,
            PageNavigation navigation)
            : base(Resources.Home, MaterialIconKind.Home)
        {
            _updates = updates;
            _navigation = navigation;
            _settings = settings;
            GeneralConfig = settings.General;
            ConfiguredModels = settings.AiModel.ConfiguredModels;
            AvailableLanguages = languages.All;
            GeneralConfig.PropertyChanged += OnGeneralPropertyChanged;
            ConfiguredModels.CollectionChanged += (_, _) => RaiseDashboardProperties();
            settings.Shortcut.Entries.CollectionChanged += (_, _) => RaiseDashboardProperties();
            NavigateToSettingsCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<SettingViewModel>(SettingsPane.Translation));
            NavigateToShortcutsCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<EasyChat.Presentation.Features.Shortcuts.ShortcutViewModel>());
            NavigateToSpeechCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<EasyChat.Presentation.Features.Speech.SpeechRecognitionViewModel>());
            NavigateToTextAssistCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<EasyChat.Presentation.Features.TextAssist.TextAssistViewModel>());
            NavigateToPromptsCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<EasyChat.Presentation.Features.Settings.Prompts.PromptViewModel>());
            OpenEngineSettingsCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<SettingViewModel>(SettingsPane.Translation));
            NavigateToAboutCommand = ReactiveCommand.Create(() =>
                _navigation.NavigateTo<AboutViewModel>());
            SwapLanguagesCommand = ReactiveCommand.Create(SwapLanguages);
            DismissOnboardingCommand = ReactiveCommand.Create(() =>
            {
                GeneralConfig.HomeOnboardingDismissed = true;
                this.RaisePropertyChanged(nameof(ShowOnboarding));
            });
            QuickActions =
            [
                new HomeQuickAction(
                    Resources.Page_SpeechRecognition,
                    Resources.HomeQuickSpeechHint,
                    MaterialIconKind.Microphone,
                    NavigateToSpeechCommand),
                new HomeQuickAction(
                    Resources.TextAssist,
                    Resources.HomeQuickTextAssistHint,
                    MaterialIconKind.Translate,
                    NavigateToTextAssistCommand),
                new HomeQuickAction(
                    Resources.Shortcut,
                    Resources.HomeQuickShortcutHint,
                    MaterialIconKind.Keyboard,
                    NavigateToShortcutsCommand),
                new HomeQuickAction(
                    Resources.Prompts,
                    Resources.HomeQuickPromptHint,
                    MaterialIconKind.TextBox,
                    NavigateToPromptsCommand)
            ];
            RefreshHealthItems();
            _ = CheckForUpdateAsync();
        }

        public LiveGeneralSettings GeneralConfig { get; }
        public ObservableCollection<CustomAiModelState> ConfiguredModels { get; }
        public IReadOnlyList<string> MachineTransProviders { get; } = ["Baidu", "Tencent", "Google", "DeepL"];
        public IReadOnlyList<LanguageSettings> AvailableLanguages { get; }
        public LanguageSettings SelectedSourceLanguage
        {
            get => ResolveLanguage(GeneralConfig.SourceLanguage.Id);
            set
            {
                if (value is not null && value.Id != GeneralConfig.SourceLanguage.Id)
                    GeneralConfig.SourceLanguage = value;
            }
        }

        public LanguageSettings SelectedTargetLanguage
        {
            get => ResolveLanguage(GeneralConfig.TargetLanguage.Id);
            set
            {
                if (value is not null && value.Id != GeneralConfig.TargetLanguage.Id)
                    GeneralConfig.TargetLanguage = value;
            }
        }

        public string CurrentVersion => _updates.CurrentVersion;
        public string LatestVersion { get => _latestVersion; private set => this.RaiseAndSetIfChanged(ref _latestVersion, value); }

        public int ConfiguredModelCount => ConfiguredModels.Count;
        public int ShortcutCount => _settings.Shortcut.Entries.Count;
        public bool IsUsingAiEngine =>
            string.Equals(GeneralConfig.TransEngine, "AiModel", StringComparison.OrdinalIgnoreCase);
        public bool IsEngineReady => IsUsingAiEngine
            ? ConfiguredModels.Count > 0 && !string.IsNullOrWhiteSpace(GeneralConfig.UsingAiModelId)
            : !string.IsNullOrWhiteSpace(GeneralConfig.UsingMachineTrans);
        public bool NeedsConfiguration => !IsEngineReady;
        public string EngineStatusText => IsEngineReady ? Resources.HomeStatusReady : Resources.HomeStatusNeedsSetup;
        public HomeStatusKind EngineStatusKind => IsEngineReady ? HomeStatusKind.Success : HomeStatusKind.Warning;
        public string EngineSummaryText => IsUsingAiEngine
            ? (ConfiguredModels.FirstOrDefault(model => model.Id == GeneralConfig.UsingAiModelId)?.Name
               ?? Resources.NotSet)
            : (GeneralConfig.UsingMachineTrans ?? Resources.NotSet);
        public string EngineKindText => IsUsingAiEngine ? Resources.AIEngine : Resources.MachineTranslation;
        public string CapabilitySummaryText =>
            string.Format(Resources.HomeCapabilitySummary, ConfiguredModelCount, ShortcutCount);
        public string SourceLanguageDisplay => DisplayLanguage(SelectedSourceLanguage);
        public string TargetLanguageDisplay => DisplayLanguage(SelectedTargetLanguage);
        public string LanguagePairDisplay => $"{SourceLanguageDisplay}  →  {TargetLanguageDisplay}";

        public bool HasIncompleteHealth => HealthItems.Any(item => !item.IsDone);
        public bool ShowOnboarding => !GeneralConfig.HomeOnboardingDismissed && HasIncompleteHealth;
        public IReadOnlyList<HomeHealthItem> HealthItems
        {
            get => _healthItems;
            private set => this.RaiseAndSetIfChanged(ref _healthItems, value);
        }

        public ReactiveCommand<Unit, Unit> NavigateToSettingsCommand { get; }
        public ReactiveCommand<Unit, Unit> NavigateToShortcutsCommand { get; }
        public ReactiveCommand<Unit, Unit> NavigateToSpeechCommand { get; }
        public ReactiveCommand<Unit, Unit> NavigateToTextAssistCommand { get; }
        public ReactiveCommand<Unit, Unit> NavigateToPromptsCommand { get; }
        public ReactiveCommand<Unit, Unit> NavigateToAboutCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenEngineSettingsCommand { get; }
        public ReactiveCommand<Unit, Unit> SwapLanguagesCommand { get; }
        public ReactiveCommand<Unit, Unit> DismissOnboardingCommand { get; }
        public IReadOnlyList<HomeQuickAction> QuickActions { get; }

        private void OnGeneralPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(LiveGeneralSettings.SourceLanguage))
            {
                this.RaisePropertyChanged(nameof(SelectedSourceLanguage));
                this.RaisePropertyChanged(nameof(SourceLanguageDisplay));
                this.RaisePropertyChanged(nameof(LanguagePairDisplay));
            }
            else if (args.PropertyName == nameof(LiveGeneralSettings.TargetLanguage))
            {
                this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
                this.RaisePropertyChanged(nameof(TargetLanguageDisplay));
                this.RaisePropertyChanged(nameof(LanguagePairDisplay));
            }
            else if (args.PropertyName is nameof(LiveGeneralSettings.TransEngine)
                     or nameof(LiveGeneralSettings.UsingAiModelId)
                     or nameof(LiveGeneralSettings.UsingMachineTrans))
                RaiseDashboardProperties();
            else if (args.PropertyName == nameof(LiveGeneralSettings.HomeOnboardingDismissed))
                this.RaisePropertyChanged(nameof(ShowOnboarding));
        }

        private void RaiseDashboardProperties()
        {
            this.RaisePropertyChanged(nameof(ConfiguredModelCount));
            this.RaisePropertyChanged(nameof(ShortcutCount));
            this.RaisePropertyChanged(nameof(IsUsingAiEngine));
            this.RaisePropertyChanged(nameof(IsEngineReady));
            this.RaisePropertyChanged(nameof(NeedsConfiguration));
            this.RaisePropertyChanged(nameof(EngineStatusText));
            this.RaisePropertyChanged(nameof(EngineStatusKind));
            this.RaisePropertyChanged(nameof(EngineSummaryText));
            this.RaisePropertyChanged(nameof(EngineKindText));
            this.RaisePropertyChanged(nameof(CapabilitySummaryText));
            this.RaisePropertyChanged(nameof(SourceLanguageDisplay));
            this.RaisePropertyChanged(nameof(TargetLanguageDisplay));
            this.RaisePropertyChanged(nameof(LanguagePairDisplay));
            RefreshHealthItems();
            this.RaisePropertyChanged(nameof(HasIncompleteHealth));
            this.RaisePropertyChanged(nameof(ShowOnboarding));
        }

        private static string DisplayLanguage(LanguageSettings language) =>
            LanguageDisplayNames.ForUi(language.ChineseName, language.EnglishName);

        private void RefreshHealthItems()
        {
            HealthItems =
            [
                new HomeHealthItem(
                    Resources.HomeHealthEngineTitle,
                    IsEngineReady ? Resources.HomeHealthEngineDone : Resources.HomeHealthEngineTodo,
                    IsEngineReady,
                    MaterialIconKind.Robot,
                    Resources.HomeHealthActionOpenSettings,
                    NavigateToSettingsCommand),
                new HomeHealthItem(
                    Resources.HomeHealthShortcutTitle,
                    ShortcutCount > 0
                        ? string.Format(Resources.HomeHealthShortcutDone, ShortcutCount)
                        : Resources.HomeHealthShortcutTodo,
                    ShortcutCount > 0,
                    MaterialIconKind.Keyboard,
                    Resources.HomeHealthActionOpenShortcuts,
                    NavigateToShortcutsCommand)
            ];
        }

        private void SwapLanguages()
        {
            var source = GeneralConfig.SourceLanguage;
            GeneralConfig.SourceLanguage = GeneralConfig.TargetLanguage;
            GeneralConfig.TargetLanguage = source;
            this.RaisePropertyChanged(nameof(SelectedSourceLanguage));
            this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
            this.RaisePropertyChanged(nameof(SourceLanguageDisplay));
            this.RaisePropertyChanged(nameof(TargetLanguageDisplay));
            this.RaisePropertyChanged(nameof(LanguagePairDisplay));
        }

        private LanguageSettings ResolveLanguage(string id) =>
            AvailableLanguages.FirstOrDefault(language => language.Id == id)
            ?? AvailableLanguages[0];

        private async Task CheckForUpdateAsync()
        {
            var result = await _updates.CheckAsync();
            LatestVersion = result.IsSuccess ? result.Value.LatestVersion : "Error";
        }
    }

    public sealed class AboutViewModel : NavigationPageViewModel
    {
        private readonly IExternalUriLauncher _uriLauncher;

        public AboutViewModel(
            IApplicationUpdateService updates,
            IExternalUriLauncher uriLauncher)
            : base(Resources.About, MaterialIconKind.InformationOutline, 10)
        {
            ArgumentNullException.ThrowIfNull(updates);
            _uriLauncher = uriLauncher ?? throw new ArgumentNullException(nameof(uriLauncher));
            Version = updates.CurrentVersion;
            OpenUrlCommand = ReactiveCommand.Create<string>(OpenUrl);
        }

        public string Version { get; }
        public ReactiveCommand<string, Unit> OpenUrlCommand { get; }

        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            _uriLauncher.Open(uri);
        }
    }
}

namespace EasyChat.Presentation.Features.Shell
{
    public sealed class CloseBehaviorDialogViewModel : ConventionViewModelBase
    {
        private readonly ShadUI.DialogManager _dialogManager;
        private readonly LiveGeneralSettings _settings;
        private readonly Action _ensureTrayVisible;
        private readonly Action _minimize;
        private readonly Action _exit;
        private bool _isRemember;

        public CloseBehaviorDialogViewModel(
            ShadUI.DialogManager dialogManager,
            LiveGeneralSettings settings,
            Action ensureTrayVisible,
            Action minimize,
            Action exit)
        {
            _dialogManager = dialogManager;
            _settings = settings;
            _ensureTrayVisible = ensureTrayVisible
                ?? throw new ArgumentNullException(nameof(ensureTrayVisible));
            _minimize = minimize;
            _exit = exit;
            MinimizeCommand = ReactiveCommand.Create(Minimize);
            ExitAppCommand = ReactiveCommand.Create(Exit);
            // Close was already cancelled on the window; cancel only dismisses the prompt.
            CancelCommand = ReactiveCommand.Create(() => _dialogManager.Close(this));
        }

        public bool IsRemember { get => _isRemember; set => this.RaiseAndSetIfChanged(ref _isRemember, value); }
        public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitAppCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        private void Minimize()
        {
            if (IsRemember)
                _settings.ClosingBehavior = ClosingBehavior.MinimizeToTray;
            _ensureTrayVisible();
            _minimize();
            _dialogManager.Close(this);
        }

        private void Exit()
        {
            if (IsRemember)
                _settings.ClosingBehavior = ClosingBehavior.ExitApp;
            _exit();
            _dialogManager.Close(this);
        }
    }
}
