using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Navigation;
using Material.Icons;
using ReactiveUI;
using SukiUI.Toasts;

namespace EasyChat.Presentation.Features.Settings;

public sealed class SettingViewModel : NavigationPageViewModel
{
    private readonly SettingsSession _settings;
    private readonly IOcrModelUseCases _ocr;
    private readonly ITranslationUseCases _translation;
    private readonly ITranslationLanguageCatalog _languages;
    private readonly ISettingsDialogCoordinator _dialogs;
    private readonly ISukiToastManager _toasts;
    private readonly Dictionary<OcrModelDownloadItemViewModel, CancellationTokenSource> _downloads = [];
    private bool _isOcrModelListExpanded;
    private bool _isTestingBaidu;
    private bool _isTestingTencent;
    private bool _isTestingGoogle;
    private bool _isTestingDeepL;
    private ObservableCollection<ModelCardItem> _modelCardsWithAddButton = [];
    private ObservableCollection<string> _availableFonts = [];

    public SettingViewModel(
        SettingsSession settings,
        IOcrModelUseCases ocr,
        ITtsUseCases tts,
        ITranslationUseCases translation,
        ITranslationLanguageCatalog languages,
        ISettingsDialogCoordinator dialogs,
        ISukiToastManager toasts)
        : base(Resources.Settings, MaterialIconKind.Settings, 1)
    {
        _settings = settings;
        _ocr = ocr;
        _translation = translation;
        _languages = languages;
        _dialogs = dialogs;
        _toasts = toasts;

        DisplayLanguages = BuildDisplayLanguages();
        NativeLanguages = BuildLanguages(includeAuto: false);
        OcrModelItems = new ObservableCollection<OcrModelDownloadItemViewModel>(
            _ocr.SupportedLanguages.Select(language => new OcrModelDownloadItemViewModel(
                language,
                _ocr.IsModelDownloaded(language),
                _ocr.CanDeleteModels)));

        RefreshModelCards();
        AiModelConf.ConfiguredModels.CollectionChanged += OnModelsChanged;

        TtsProviders = tts.GetProviders().Select(provider => provider.Id).ToList();
        AddModelCommand = ReactiveCommand.Create(() => _dialogs.EditAiModel(null));
        EditModelCommand = ReactiveCommand.Create<CustomAiModelState>(_dialogs.EditAiModel);
        DeleteModelCommand = ReactiveCommand.Create<CustomAiModelState>(_dialogs.DeleteAiModel);
        EditModelKeysCommand = ReactiveCommand.Create<CustomAiModelState>(_dialogs.EditAiModelKeys);
        EditBaiduKeysCommand = ReactiveCommand.Create(_dialogs.EditBaiduKeys);
        EditTencentKeysCommand = ReactiveCommand.Create(_dialogs.EditTencentKeys);
        EditGoogleKeysCommand = ReactiveCommand.Create(_dialogs.EditGoogleKeys);
        EditDeepLKeysCommand = ReactiveCommand.Create(_dialogs.EditDeepLKeys);
        ManageFixedAreasCommand = ReactiveCommand.Create(_dialogs.ManageFixedAreas);
        ConfigureTtsCommand = ReactiveCommand.Create(_dialogs.ConfigureTts);

        TestAiModelConnectionCommand = ReactiveCommand.CreateFromTask<CustomAiModelState>(TestAiModelConnectionAsync);
        TestBaiduConnectionCommand = ReactiveCommand.CreateFromTask(() => TestMachineConnectionAsync("Baidu"));
        TestTencentConnectionCommand = ReactiveCommand.CreateFromTask(() => TestMachineConnectionAsync("Tencent"));
        TestGoogleConnectionCommand = ReactiveCommand.CreateFromTask(() => TestMachineConnectionAsync("Google"));
        TestDeepLConnectionCommand = ReactiveCommand.CreateFromTask(() => TestMachineConnectionAsync("DeepL"));

        DownloadOcrModelCommand = ReactiveCommand.Create<OcrModelDownloadItemViewModel>(StartDownloadOcrModel);
        CancelOcrModelCommand = ReactiveCommand.Create<OcrModelDownloadItemViewModel>(CancelOcrModel);
        DeleteOcrModelCommand = ReactiveCommand.Create<OcrModelDownloadItemViewModel>(DeleteOcrModel);
        ToggleOcrModelListCommand = ReactiveCommand.Create(() =>
        {
            IsOcrModelListExpanded = !IsOcrModelListExpanded;
        });

        Dispatcher.UIThread.Post(LoadAvailableFonts);
    }

    public List<string> DeepLModelTypes { get; } = ["quality_optimized", "prefer_quality_optimized", "latency_optimized"];
    public List<LanguageSettings> DisplayLanguages { get; }
    public List<LanguageSettings> NativeLanguages { get; }
    public List<ClosingBehavior> ClosingBehaviors { get; } = Enum.GetValues<ClosingBehavior>().ToList();
    public List<string> ScreenshotModes { get; } = ["Precise", "Quick"];
    public List<string> MachineTransProviders { get; } = ["Baidu", "Tencent", "Google", "DeepL"];
    public List<string> TranslationEngineTypes { get; } = [Resources.AIEngine, Resources.MachineTranslation];
    public List<SelectionTriggerModeOption> SelectionTriggerModes { get; } =
    [
        new(SelectionTriggerMode.DoubleClick, Resources.SelectionTriggerModeDoubleClick),
        new(SelectionTriggerMode.DragSelection, Resources.SelectionTriggerModeDragSelection),
        new(SelectionTriggerMode.All, Resources.SelectionTriggerModeAll)
    ];
    public List<string> TransparencyLevels { get; } = ["AcrylicBlur", "Blur", "Transparent"];
    public List<InputDeliveryMode> InputDeliveryModes { get; } = Enum.GetValues<InputDeliveryMode>().ToList();
    public List<ResultWindowMode> ResultWindowModes { get; } = Enum.GetValues<ResultWindowMode>().ToList();
    public List<ResultReadAloudMode> ResultReadAloudModes { get; } = Enum.GetValues<ResultReadAloudMode>().ToList();
    public List<string> TtsProviders { get; }

    public LiveGeneralSettings GeneralConf => _settings.General;
    public LiveAiModelSettings AiModelConf => _settings.AiModel;
    public ObservableCollection<CustomAiModelState> ConfiguredModels => AiModelConf.ConfiguredModels;
    public LiveMachineTranslationSettings MachineTransConf => _settings.MachineTranslation;
    public LiveProxySettings ProxyConf => _settings.Proxy;
    public LiveOcrSettings OcrConf => _settings.Ocr;
    public LiveResultSettings ResultConf => _settings.Result;
    public LiveInputSettings InputConf => _settings.Input;
    public LiveScreenshotSettings ScreenshotConf => _settings.Screenshot;
    public LiveSelectionTranslationSettings SelectionTranslationConf => _settings.SelectionTranslation;
    public ObservableCollection<PromptEntryState> PromptEntries => _settings.Prompts.Entries;
    public LiveTtsSettings TtsConf => _settings.Tts;
    public ObservableCollection<OcrModelDownloadItemViewModel> OcrModelItems { get; }
    public ObservableCollection<ModelCardItem> ModelCardsWithAddButton
    {
        get => _modelCardsWithAddButton;
        private set => this.RaiseAndSetIfChanged(ref _modelCardsWithAddButton, value);
    }
    public ObservableCollection<string> AvailableFonts
    {
        get => _availableFonts;
        private set => this.RaiseAndSetIfChanged(ref _availableFonts, value);
    }
    public List<string> AiProviders => ConfiguredModels.Select(model => model.Name).ToList();

    public LanguageSettings SelectedDisplayLanguage
    {
        get => DisplayLanguages.FirstOrDefault(language => language.EnglishName == GeneralConf.DisplayLanguage)
               ?? DisplayLanguages[0];
        set
        {
            if (value.EnglishName == GeneralConf.DisplayLanguage)
                return;
            GeneralConf.DisplayLanguage = value.EnglishName;
            var culture = value.Id == "zh-Hans" ? new CultureInfo("zh-CN") : new CultureInfo("en-US");
            Resources.Culture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            this.RaisePropertyChanged();
            ShowToast(Resources.LanguageChanged, Resources.RestartToTakeEffect, NotificationType.Success);
        }
    }

    public LanguageSettings SelectedNativeLanguage
    {
        get => NativeLanguages.FirstOrDefault(language => language.Id == GeneralConf.NativeLanguage?.Id)
               ?? NativeLanguages.First();
        set
        {
            if (value.Id == GeneralConf.NativeLanguage?.Id)
                return;
            GeneralConf.NativeLanguage = value;
            this.RaisePropertyChanged();
        }
    }

    public ClosingBehavior SelectedClosingBehavior
    {
        get => GeneralConf.ClosingBehavior;
        set
        {
            GeneralConf.ClosingBehavior = value;
            this.RaisePropertyChanged();
        }
    }

    public string SelectedScreenshotMode
    {
        get => ScreenshotConf.Mode ?? "Precise";
        set
        {
            ScreenshotConf.Mode = value;
            this.RaisePropertyChanged();
        }
    }

    public SelectionTriggerMode SelectedSelectionTriggerMode
    {
        get => SelectionTranslationConf.TriggerMode;
        set
        {
            SelectionTranslationConf.TriggerMode = value;
            this.RaisePropertyChanged();
        }
    }

    public string SelectedSelectionTranslationEngine
    {
        get => SelectionTranslationConf.Provider == TranslationEngineNames.AiModel
            ? Resources.AIEngine
            : Resources.MachineTranslation;
        set
        {
            SelectionTranslationConf.Provider = value == Resources.AIEngine
                ? TranslationEngineNames.AiModel
                : TranslationEngineNames.MachineTrans;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsAiTranslationSelected));
            this.RaisePropertyChanged(nameof(IsMachineTranslationSelected));
        }
    }

    public bool IsAiTranslationSelected => SelectionTranslationConf.Provider == TranslationEngineNames.AiModel;
    public bool IsMachineTranslationSelected => !IsAiTranslationSelected;

    public string SelectedMachineTranslationProvider
    {
        get => SelectionTranslationConf.MachineProvider ?? "Baidu";
        set
        {
            SelectionTranslationConf.MachineProvider = value;
            this.RaisePropertyChanged();
        }
    }

    public string SelectedTtsProvider
    {
        get => TtsConf.Provider;
        set
        {
            TtsConf.Provider = value;
            this.RaisePropertyChanged();
        }
    }

    public IEnumerable<OcrModelDownloadItemViewModel> VisibleOcrModelItems =>
        IsOcrModelListExpanded ? OcrModelItems : OcrModelItems.Take(3);
    public bool IsOcrModelListExpanded
    {
        get => _isOcrModelListExpanded;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isOcrModelListExpanded, value);
            this.RaisePropertyChanged(nameof(VisibleOcrModelItems));
            this.RaisePropertyChanged(nameof(OcrModelListToggleIcon));
            this.RaisePropertyChanged(nameof(OcrModelListToggleText));
        }
    }
    public MaterialIconKind OcrModelListToggleIcon => IsOcrModelListExpanded
        ? MaterialIconKind.ExpandLess
        : MaterialIconKind.ExpandMore;
    public bool IsOcrModelListToggleVisible => OcrModelItems.Count > 3;
    public string OcrModelListToggleText => IsOcrModelListExpanded
        ? Resources.ShowLessOcrModels
        : Resources.ShowMoreOcrModels;

    public bool IsTestingBaidu { get => _isTestingBaidu; private set => this.RaiseAndSetIfChanged(ref _isTestingBaidu, value); }
    public bool IsTestingTencent { get => _isTestingTencent; private set => this.RaiseAndSetIfChanged(ref _isTestingTencent, value); }
    public bool IsTestingGoogle { get => _isTestingGoogle; private set => this.RaiseAndSetIfChanged(ref _isTestingGoogle, value); }
    public bool IsTestingDeepL { get => _isTestingDeepL; private set => this.RaiseAndSetIfChanged(ref _isTestingDeepL, value); }

    public ReactiveCommand<Unit, Unit> ManageFixedAreasCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfigureTtsCommand { get; }
    public ReactiveCommand<OcrModelDownloadItemViewModel, Unit> DownloadOcrModelCommand { get; }
    public ReactiveCommand<OcrModelDownloadItemViewModel, Unit> CancelOcrModelCommand { get; }
    public ReactiveCommand<OcrModelDownloadItemViewModel, Unit> DeleteOcrModelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleOcrModelListCommand { get; }
    public ReactiveCommand<Unit, Unit> AddModelCommand { get; }
    public ReactiveCommand<CustomAiModelState, Unit> EditModelCommand { get; }
    public ReactiveCommand<CustomAiModelState, Unit> DeleteModelCommand { get; }
    public ReactiveCommand<CustomAiModelState, Unit> EditModelKeysCommand { get; }
    public ReactiveCommand<Unit, Unit> EditBaiduKeysCommand { get; }
    public ReactiveCommand<Unit, Unit> EditTencentKeysCommand { get; }
    public ReactiveCommand<Unit, Unit> EditGoogleKeysCommand { get; }
    public ReactiveCommand<Unit, Unit> EditDeepLKeysCommand { get; }
    public ReactiveCommand<CustomAiModelState, Unit> TestAiModelConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> TestBaiduConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> TestTencentConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> TestGoogleConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> TestDeepLConnectionCommand { get; }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshModelCards();
        this.RaisePropertyChanged(nameof(AiProviders));
    }

    private void RefreshModelCards()
    {
        var cards = ConfiguredModels.Select(model => new ModelCardItem(model)).ToList();
        cards.Add(new ModelCardItem(null));
        ModelCardsWithAddButton = new ObservableCollection<ModelCardItem>(cards);
    }

    private void LoadAvailableFonts()
    {
        AvailableFonts = new ObservableCollection<string>(
            Avalonia.Media.FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Order(StringComparer.CurrentCulture));
    }

    private void StartDownloadOcrModel(OcrModelDownloadItemViewModel item) => _ = DownloadOcrModelAsync(item);

    private async Task DownloadOcrModelAsync(OcrModelDownloadItemViewModel item)
    {
        if (item.IsDownloading || item.IsDownloaded || _downloads.ContainsKey(item))
            return;

        var cancellation = new CancellationTokenSource();
        _downloads.Add(item, cancellation);
        item.StartDownload();
        try
        {
            await _ocr.DownloadModelAsync(item.Language, new Progress<double>(item.SetProgress), cancellation.Token);
            item.CompleteDownload();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            item.CancelDownload();
        }
        catch (Exception exception)
        {
            item.FailDownload(exception.Message);
        }
        finally
        {
            _downloads.Remove(item);
            cancellation.Dispose();
        }
    }

    private void CancelOcrModel(OcrModelDownloadItemViewModel item)
    {
        if (_downloads.TryGetValue(item, out var cancellation))
            cancellation.Cancel();
    }

    private void DeleteOcrModel(OcrModelDownloadItemViewModel item)
    {
        try
        {
            _ocr.DeleteModel(item.Language);
            item.MarkDeleted();
        }
        catch (Exception exception)
        {
            item.FailDownload(exception.Message);
        }
    }

    private Task TestAiModelConnectionAsync(CustomAiModelState model) => TestConnectionAsync(
        model.Name,
        new TranslationProviderSelection(TranslationEngineNames.AiModel, AiModelId: model.Id),
        testing => model.IsTesting = testing);

    private Task TestMachineConnectionAsync(string provider)
    {
        Action<bool> state = provider switch
        {
            "Baidu" => value => IsTestingBaidu = value,
            "Tencent" => value => IsTestingTencent = value,
            "Google" => value => IsTestingGoogle = value,
            _ => value => IsTestingDeepL = value
        };
        return TestConnectionAsync(
            provider,
            new TranslationProviderSelection(TranslationEngineNames.MachineTrans, MachineProviderName: provider),
            state);
    }

    private async Task TestConnectionAsync(
        string providerName,
        TranslationProviderSelection provider,
        Action<bool> setTesting)
    {
        setTesting(true);
        try
        {
            var result = await _translation.TranslateAsync(new TranslationRequest(
                "Hello",
                _languages.Get("en"),
                _languages.Get("zh-Hans"),
                Provider: provider));
            if (result.IsSuccess)
                ShowToast(providerName, Resources.ConnectionSuccess, NotificationType.Success);
            else
                ShowToast(Resources.ConnectionFailed, $"{providerName}: {result.Error.Message}", NotificationType.Error);
        }
        catch (Exception exception)
        {
            ShowToast(Resources.ConnectionFailed, $"{providerName}: {exception.Message}", NotificationType.Error);
        }
        finally
        {
            setTesting(false);
        }
    }

    private List<LanguageSettings> BuildDisplayLanguages() => BuildLanguages(includeAuto: false)
        .Where(language => language.Id is "en" or "zh-Hans")
        .ToList();

    private List<LanguageSettings> BuildLanguages(bool includeAuto)
    {
        var existing = new[] { GeneralConf.SourceLanguage, GeneralConf.TargetLanguage, GeneralConf.NativeLanguage }
            .Where(language => language is not null)
            .Cast<LanguageSettings>();
        return existing.Concat(_languages.All.Select(ToSettingsLanguage))
            .Where(language => includeAuto || language.Id != "auto")
            .DistinctBy(language => language.Id)
            .OrderBy(language => language.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static LanguageSettings ToSettingsLanguage(TranslationLanguage language)
    {
        var localized = language.NativeName ?? language.EnglishName;
        var display = language.NativeName is { Length: > 0 } && language.NativeName != language.EnglishName
            ? $"{language.NativeName} ({language.EnglishName})"
            : language.EnglishName;
        return new LanguageSettings(
            language.Id,
            localized,
            language.EnglishName,
            language.Icon ?? "unknown.png",
            localized,
            display,
            language.ProviderCodes ?? new Dictionary<string, string>());
    }

    private void ShowToast(string title, string content, NotificationType type) =>
        _toasts.CreateSimpleInfoToast().OfType(type).WithTitle(title).WithContent(content).Queue();
}

public sealed class ModelCardItem(CustomAiModelState? model)
{
    public CustomAiModelState? Model { get; } = model;
    public bool IsAddButton => Model is null;
    public bool IsModelCard => Model is not null;
    public string Name => Model?.Name ?? string.Empty;
    public AiModelType ModelType => Model?.ModelType ?? AiModelType.Custom;
    public string ApiUrl => Model?.ApiUrl ?? string.Empty;
    public string ModelName => Model?.Model ?? string.Empty;
}

public sealed record SelectionTriggerModeOption(SelectionTriggerMode Value, string DisplayName);

public interface ISettingsDialogCoordinator
{
    void EditAiModel(CustomAiModelState? model);
    void DeleteAiModel(CustomAiModelState model);
    void EditAiModelKeys(CustomAiModelState model);
    void EditBaiduKeys();
    void EditTencentKeys();
    void EditGoogleKeys();
    void EditDeepLKeys();
    void ManageFixedAreas();
    void ConfigureTts();
}
