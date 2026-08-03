using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Media;
using Avalonia.Styling;
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
using Material.Icons;
using ReactiveUI;
using SukiUI;
using SukiUI.Dialogs;
using SukiUI.Models;
using SukiUI.Toasts;

namespace EasyChat.Presentation.Features.Shell
{
    public sealed class PageNavigation
    {
        public event Action<Type>? NavigationRequested;
        public void NavigateTo<TPage>() where TPage : NavigationPageViewModel =>
            NavigationRequested?.Invoke(typeof(TPage));
    }

    public sealed class MainWindowViewModel : ViewModelBase
    {
        private readonly SukiTheme _theme;
        private readonly SettingsSession _settings;
        private readonly IExternalUriLauncher _uriLauncher;
        private NavigationPageViewModel? _activePage;
        private ThemeVariant _baseTheme;
        private bool _isFullScreen;

        public MainWindowViewModel(
            IEnumerable<NavigationPageViewModel> pages,
            PageNavigation navigation,
            SettingsSession settings,
            IExternalUriLauncher uriLauncher,
            ISukiToastManager toastManager,
            ISukiDialogManager dialogManager)
        {
            _settings = settings;
            _uriLauncher = uriLauncher;
            ToastManager = toastManager;
            DialogManager = dialogManager;
            Pages = new ObservableCollection<NavigationPageViewModel>(
                pages.OrderBy(page => page.Index).ThenBy(page => page.DisplayName));
            _activePage = Pages.FirstOrDefault();

            _theme = SukiTheme.GetInstance();
            Themes = _theme.ColorThemes;
            _baseTheme = settings.General.BaseTheme.Equals(
                nameof(ThemeVariant.Dark),
                StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
            _isFullScreen = settings.General.FullScreen;
            _theme.ChangeBaseTheme(_baseTheme);
            RestoreColorTheme();

            ToggleBaseThemeCommand = ReactiveCommand.Create(_theme.SwitchBaseTheme);
            ChangeThemeCommand = ReactiveCommand.Create<SukiColorTheme>(_theme.ChangeColorTheme);
            CreateCustomThemeCommand = ReactiveCommand.Create(CreateCustomTheme);
            ToggleFullScreenCommand = ReactiveCommand.Create(ToggleFullScreen);
            OpenUrlCommand = ReactiveCommand.Create<string>(OpenUrl);

            navigation.NavigationRequested += NavigateTo;
            _theme.OnBaseThemeChanged += OnBaseThemeChanged;
            _theme.OnColorThemeChanged += OnColorThemeChanged;
        }

        public event EventHandler<bool>? FullScreenChanged;

        public ObservableCollection<NavigationPageViewModel> Pages { get; }
        public IReadOnlyList<SukiColorTheme> Themes { get; }
        public ISukiDialogManager DialogManager { get; }
        public ISukiToastManager ToastManager { get; }

        public NavigationPageViewModel? ActivePage
        {
            get => _activePage;
            set => this.RaiseAndSetIfChanged(ref _activePage, value);
        }

        public bool TitleBarVisible
        {
            get => _settings.General.TitleBarVisible;
            set
            {
                if (_settings.General.TitleBarVisible == value)
                    return;
                _settings.General.TitleBarVisible = value;
                this.RaisePropertyChanged();
            }
        }

        public bool IsFullScreen
        {
            get => _isFullScreen;
            private set => this.RaiseAndSetIfChanged(ref _isFullScreen, value);
        }

        public ThemeVariant BaseTheme
        {
            get => _baseTheme;
            private set => this.RaiseAndSetIfChanged(ref _baseTheme, value);
        }

        public MaterialIconKind ThemeToggleIcon =>
            BaseTheme == ThemeVariant.Dark ? MaterialIconKind.WeatherSunny : MaterialIconKind.WeatherNight;

        public ReactiveCommand<Unit, Unit> ToggleBaseThemeCommand { get; }
        public ReactiveCommand<SukiColorTheme, Unit> ChangeThemeCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateCustomThemeCommand { get; }
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
                _theme.ChangeColorTheme(saved);
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.General.CustomThemePrimaryColor) ||
                string.IsNullOrWhiteSpace(_settings.General.CustomThemeAccentColor))
            {
                return;
            }

            try
            {
                var custom = new SukiColorTheme(
                    _settings.General.ColorTheme,
                    Color.Parse(_settings.General.CustomThemePrimaryColor),
                    Color.Parse(_settings.General.CustomThemeAccentColor));
                _theme.AddColorTheme(custom);
                _theme.ChangeColorTheme(custom);
            }
            catch (FormatException)
            {
                // Manually edited invalid colors fall back to SukiUI's active theme.
            }
        }

        private void NavigateTo(Type pageType)
        {
            var page = Pages.FirstOrDefault(candidate => candidate.GetType() == pageType);
            if (page is not null)
                ActivePage = page;
        }

        private void OnBaseThemeChanged(ThemeVariant variant)
        {
            BaseTheme = variant;
            _settings.General.BaseTheme = variant == ThemeVariant.Dark
                ? nameof(ThemeVariant.Dark)
                : nameof(ThemeVariant.Light);
            this.RaisePropertyChanged(nameof(ThemeToggleIcon));
            ToastManager.CreateSimpleInfoToast()
                .WithTitle(Resources.ThemeChangedTitle)
                .WithContent($"{Resources.ThemeChangedContent} {variant}.")
                .Queue();
        }

        private void OnColorThemeChanged(SukiColorTheme theme)
        {
            _settings.General.ColorTheme = theme.DisplayName;
            ToastManager.CreateSimpleInfoToast()
                .WithTitle(Resources.ColorChangedTitle)
                .WithContent($"{Resources.ColorChangedContent} {theme.DisplayName}.")
                .Queue();
        }

        private void CreateCustomTheme() => DialogManager.CreateDialog()
            .WithViewModel(dialog => new CustomThemeDialogViewModel(_theme, dialog, _settings.General))
            .TryShow();

        private void ToggleFullScreen()
        {
            IsFullScreen = !IsFullScreen;
            _settings.General.FullScreen = IsFullScreen;
            TitleBarVisible = !IsFullScreen;
            FullScreenChanged?.Invoke(this, IsFullScreen);
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
    public sealed class HomeViewModel : NavigationPageViewModel
    {
        private readonly IApplicationUpdateService _updates;
        private string _latestVersion = "-";

        public HomeViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            IApplicationUpdateService updates)
            : base(Resources.Home, MaterialIconKind.Home)
        {
            _updates = updates;
            GeneralConfig = settings.General;
            ConfiguredModels = settings.AiModel.ConfiguredModels;
            AvailableLanguages = languages.All;
            GeneralConfig.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LiveGeneralSettings.SourceLanguage))
                    this.RaisePropertyChanged(nameof(SelectedSourceLanguage));
                else if (args.PropertyName == nameof(LiveGeneralSettings.TargetLanguage))
                    this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
            };
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
        public AboutViewModel() : base(Resources.About, MaterialIconKind.InformationOutline, 10) { }
    }
}

namespace EasyChat.Presentation.Features.Shell
{
    public sealed class CloseBehaviorDialogViewModel : ConventionViewModelBase
    {
        private readonly ISukiDialog _dialog;
        private readonly LiveGeneralSettings _settings;
        private readonly Action _minimize;
        private readonly Action _exit;
        private bool _isRemember;

        public CloseBehaviorDialogViewModel(
            ISukiDialog dialog,
            LiveGeneralSettings settings,
            Action minimize,
            Action exit)
        {
            _dialog = dialog;
            _settings = settings;
            _minimize = minimize;
            _exit = exit;
            MinimizeCommand = ReactiveCommand.Create(Minimize);
            ExitAppCommand = ReactiveCommand.Create(Exit);
        }

        public bool IsRemember { get => _isRemember; set => this.RaiseAndSetIfChanged(ref _isRemember, value); }
        public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }
        public ReactiveCommand<Unit, Unit> ExitAppCommand { get; }

        private void Minimize()
        {
            if (IsRemember)
                _settings.ClosingBehavior = ClosingBehavior.MinimizeToTray;
            _minimize();
            _dialog.Dismiss();
        }

        private void Exit()
        {
            if (IsRemember)
                _settings.ClosingBehavior = ClosingBehavior.ExitApp;
            _exit();
            _dialog.Dismiss();
        }
    }
}
