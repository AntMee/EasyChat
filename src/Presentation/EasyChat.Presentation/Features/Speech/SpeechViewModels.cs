using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Speech;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Foundation.Navigation;
using Material.Icons;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Speech;

public sealed record SpeechEngineOption(string Name, string Id, bool IsMachine);

public sealed class SpeechAudioSourceItem : ReactiveObject, IDisposable
{
    private bool _isSelected;

    public SpeechAudioSourceItem(AudioCaptureSourceDescriptor source, bool isSelected)
    {
        Token = source.Token;
        Kind = source.Kind;
        Name = source.Name;
        DisplayName = source.Kind == AudioCaptureSourceKind.SystemOutput
            ? Resources.Speech_AllSystemAudio
            : source.DisplayName;
        Title = source.Description ?? string.Empty;
        _isSelected = isSelected;
        if (!source.IconPng.IsEmpty)
        {
            using var stream = new MemoryStream(source.IconPng.ToArray());
            AppIcon = new Bitmap(stream);
        }
    }

    public AudioCaptureSourceToken Token { get; }
    public AudioCaptureSourceKind Kind { get; }
    public string Name { get; }
    public string Title { get; }
    public string DisplayName { get; }
    public Bitmap? AppIcon { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public void Dispose() => AppIcon?.Dispose();
}

public sealed class SpeechSubtitleItemViewModel : ReactiveObject
{
    private string _originalText = string.Empty;
    private string _translatedText = string.Empty;
    private string _displayTranslatedText = string.Empty;
    private bool _isTranslating;

    public SpeechSubtitleItemViewModel(SpeechSubtitleLine subtitle)
    {
        Id = subtitle.Id;
        Timestamp = subtitle.Timestamp;
        Update(subtitle);
    }

    public long Id { get; }
    public TimeSpan Timestamp { get; }
    public string OriginalText { get => _originalText; private set => this.RaiseAndSetIfChanged(ref _originalText, value); }
    public string TranslatedText { get => _translatedText; private set => this.RaiseAndSetIfChanged(ref _translatedText, value); }
    public bool IsTranslating
    {
        get => _isTranslating;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isTranslating, value);
            this.RaisePropertyChanged(nameof(DisplayTranslatedText));
        }
    }
    public string DisplayTranslatedText
    {
        get => string.IsNullOrEmpty(_displayTranslatedText)
               && IsTranslating
               && !string.IsNullOrWhiteSpace(OriginalText)
               && OriginalText is not ("..." or "\u2026")
            ? Resources.Speech_Translating
            : _displayTranslatedText;
        private set => this.RaiseAndSetIfChanged(ref _displayTranslatedText, value);
    }

    public void Update(SpeechSubtitleLine subtitle)
    {
        OriginalText = subtitle.OriginalText;
        TranslatedText = subtitle.TranslatedText;
        DisplayTranslatedText = subtitle.DisplayTranslatedText;
        IsTranslating = subtitle.IsTranslating;
    }
}

public sealed class SpeechRecognitionViewModel : NavigationPageViewModel, IDisposable
{
    private readonly SettingsSession _settings;
    private readonly ISpeechRecognitionUseCases _speech;
    private readonly ISpeechRecognitionModelCatalog _models;
    private readonly IAudioCaptureSourceCatalog _audioSources;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IPlatformAccessUseCases _platformAccess;
    private readonly TranslationLanguageOptions _languages;
    private readonly SubtitleWindowCoordinator _subtitleWindow;
    private readonly ILogger<SpeechRecognitionViewModel> _logger;
    private readonly DispatcherTimer _autoClearTimer = new();
    private CancellationTokenSource? _recognitionCancellation;
    private Task? _recognitionTask;
    private SpeechEngineOption? _selectedEngineOption;
    private LanguageSettings? _selectedTargetLanguage;
    private string _selectedRecognitionLanguage = string.Empty;
    private string _selectedSourcesSummary = Resources.Speech_AllSystemAudio;
    private bool _isSupported;
    private bool _isBusy;
    private bool _isRecording;
    private bool _isFloatingWindowOpen;
    private int _initialized;
    private long _nextErrorId;

    public SpeechRecognitionViewModel(
        SettingsSession settings,
        ISpeechRecognitionUseCases speech,
        ISpeechRecognitionModelCatalog models,
        IAudioCaptureSourceCatalog audioSources,
        IPlatformCapabilities capabilities,
        IPlatformAccessUseCases platformAccess,
        TranslationLanguageOptions languages,
        SubtitleWindowCoordinator subtitleWindow,
        ILogger<SpeechRecognitionViewModel> logger)
        : base(Resources.Page_SpeechRecognition, MaterialIconKind.Microphone, 4)
    {
        _settings = settings;
        _speech = speech;
        _models = models;
        _audioSources = audioSources;
        _capabilities = capabilities;
        _platformAccess = platformAccess;
        _languages = languages;
        _subtitleWindow = subtitleWindow;
        _logger = logger;

        RecognitionLanguages = [];
        EngineOptions = [];
        TargetLanguages = [];
        AvailableFonts = new ObservableCollection<string>(
            FontManager.Current.SystemFonts.Select(font => font.Name).Order(StringComparer.CurrentCulture));
        AudioSources = [];
        SubtitleItems = [];
        FloatingSubtitles = [];

        LoadEngineOptions();
        _selectedEngineOption = EngineOptions.FirstOrDefault(option =>
            option.Id == settings.SpeechRecognition.EngineId
            && option.IsMachine == (settings.SpeechRecognition.EngineType == 0))
            ?? EngineOptions.FirstOrDefault(option => option.Id == MachineTranslationProviderNames.Baidu)
            ?? EngineOptions.FirstOrDefault();
        UpdateTargetLanguages(commitSelection: false);

        ToggleRecordingCommand = ReactiveCommand.CreateFromTask(ToggleRecordingAsync);
        RefreshSourcesCommand = ReactiveCommand.CreateFromTask(RefreshSourcesAsync);
        ClearHistoryCommand = ReactiveCommand.Create(ClearHistory);
        ToggleFloatingWindowCommand = ReactiveCommand.Create(ToggleFloatingWindow);
        ToggleLockCommand = ReactiveCommand.Create(() =>
        {
            IsFloatingWindowLocked = !IsFloatingWindowLocked;
        });
        UnlockFloatingWindowCommand = ReactiveCommand.Create(() =>
        {
            IsFloatingWindowLocked = false;
        });
        IncreaseFontSizeCommand = ReactiveCommand.Create(() =>
        {
            PrimaryFontSize = Math.Min(100, PrimaryFontSize + 2);
            SecondaryFontSize = Math.Min(100, SecondaryFontSize + 2);
        });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() =>
        {
            PrimaryFontSize = Math.Max(10, PrimaryFontSize - 2);
            SecondaryFontSize = Math.Max(10, SecondaryFontSize - 2);
        });

        _autoClearTimer.Tick += (_, _) => ClearHistory();
        UpdateAutoClearTimer();
        _subtitleWindow.VisibilityChanged += OnSubtitleWindowVisibilityChanged;
        _models.ModelsChanged += OnModelsChanged;
        _settings.AiModel.ConfiguredModels.CollectionChanged += (_, _) => LoadEngineOptions();
    }

    public ObservableCollection<string> RecognitionLanguages { get; }
    public ObservableCollection<SpeechEngineOption> EngineOptions { get; }
    public ObservableCollection<LanguageSettings> TargetLanguages { get; }
    public ObservableCollection<string> AvailableFonts { get; }
    public ObservableCollection<SpeechAudioSourceItem> AudioSources { get; }
    public ObservableCollection<SpeechSubtitleItemViewModel> SubtitleItems { get; }
    public ObservableCollection<SpeechSubtitleItemViewModel> FloatingSubtitles { get; }

    public IReadOnlyList<string> OrientationOptions { get; } = ["Horizontal", "Vertical"];
    public IReadOnlyList<KeyValuePair<FloatingDisplayMode, string>> DisplayModeOptions { get; } =
    [
        new(FloatingDisplayMode.Segmented, Resources.Speech_DisplayMode_Segmented),
        new(FloatingDisplayMode.AutoScroll, Resources.Speech_DisplayMode_AutoScroll)
    ];
    public IReadOnlyList<KeyValuePair<SubtitleSource, string>> MainSourceOptions { get; } =
    [
        new(SubtitleSource.Original, Resources.Subtitle_Source_Original),
        new(SubtitleSource.Translated, Resources.Subtitle_Source_Translated)
    ];
    public IReadOnlyList<KeyValuePair<SubtitleSource, string>> SecondarySourceOptions { get; } =
    [
        new(SubtitleSource.None, Resources.Subtitle_Source_None),
        new(SubtitleSource.Original, Resources.Subtitle_Source_Original),
        new(SubtitleSource.Translated, Resources.Subtitle_Source_Translated)
    ];

    public ReactiveCommand<Unit, Unit> ToggleRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshSourcesCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFloatingWindowCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLockCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlockFloatingWindowCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }

    public bool IsSupported { get => _isSupported; private set { this.RaiseAndSetIfChanged(ref _isSupported, value); this.RaisePropertyChanged(nameof(IsNotSupported)); } }
    public bool IsNotSupported => !IsSupported;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRecording, value);
            this.RaisePropertyChanged(nameof(RecordingText));
            this.RaisePropertyChanged(nameof(RecordingIcon));
        }
    }
    public string RecordingText => IsRecording ? Resources.Speech_Stop : Resources.Speech_Start;
    public MaterialIconKind RecordingIcon => IsRecording ? MaterialIconKind.MicrophoneOff : MaterialIconKind.Microphone;
    public bool IsFloatingWindowOpen { get => _isFloatingWindowOpen; private set => this.RaiseAndSetIfChanged(ref _isFloatingWindowOpen, value); }
    public string SelectedSourcesSummary { get => _selectedSourcesSummary; private set => this.RaiseAndSetIfChanged(ref _selectedSourcesSummary, value); }
    public string SelectedRecognitionLanguage
    {
        get => _selectedRecognitionLanguage;
        set
        {
            if (_selectedRecognitionLanguage == value) return;
            this.RaiseAndSetIfChanged(ref _selectedRecognitionLanguage, value);
            _settings.SpeechRecognition.RecognitionLanguage = value;
        }
    }
    public SpeechEngineOption? SelectedEngineOption
    {
        get => _selectedEngineOption;
        set
        {
            if (_selectedEngineOption == value) return;
            this.RaiseAndSetIfChanged(ref _selectedEngineOption, value);
            if (value is not null)
            {
                _settings.SpeechRecognition.EngineId = value.Id;
                _settings.SpeechRecognition.EngineType = value.IsMachine ? 0 : 1;
            }
            UpdateTargetLanguages(commitSelection: true);
        }
    }
    public LanguageSettings? SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set
        {
            if (_selectedTargetLanguage == value) return;
            this.RaiseAndSetIfChanged(ref _selectedTargetLanguage, value);
            if (value is not null)
                _settings.SpeechRecognition.TargetLanguage = value.Id;
        }
    }

    public bool IsTranslationEnabled { get => _settings.SpeechRecognition.IsTranslationEnabled; set => Set(value, _settings.SpeechRecognition.IsTranslationEnabled, next => _settings.SpeechRecognition.IsTranslationEnabled = next); }
    public bool IsRealTimePreviewEnabled { get => _settings.SpeechRecognition.IsRealTimePreviewEnabled; set => Set(value, _settings.SpeechRecognition.IsRealTimePreviewEnabled, next => _settings.SpeechRecognition.IsRealTimePreviewEnabled = next); }
    public int AutoClearInterval { get => _settings.SpeechRecognition.AutoClearInterval; set { Set(value, _settings.SpeechRecognition.AutoClearInterval, next => _settings.SpeechRecognition.AutoClearInterval = next); UpdateAutoClearTimer(); } }
    public int MaxSentencesPerLine { get => _settings.SpeechRecognition.MaxSentencesPerLine; set => Set(value, _settings.SpeechRecognition.MaxSentencesPerLine, next => _settings.SpeechRecognition.MaxSentencesPerLine = next); }
    public FloatingDisplayMode FloatingDisplayMode { get => _settings.SpeechRecognition.FloatingDisplayMode; set { Set(value, _settings.SpeechRecognition.FloatingDisplayMode, next => _settings.SpeechRecognition.FloatingDisplayMode = next); this.RaisePropertyChanged(nameof(IsSegmentedMode)); } }
    public bool IsSegmentedMode => FloatingDisplayMode == FloatingDisplayMode.Segmented;
    public int MaxFloatingHistory { get => _settings.SpeechRecognition.MaxFloatingHistory; set => Set(value, _settings.SpeechRecognition.MaxFloatingHistory, next => _settings.SpeechRecognition.MaxFloatingHistory = next); }
    public SubtitleSource MainSubtitleSource { get => _settings.SpeechRecognition.MainSubtitleSource; set => Set(value, _settings.SpeechRecognition.MainSubtitleSource, next => _settings.SpeechRecognition.MainSubtitleSource = next); }
    public double PrimaryFontSize { get => _settings.SpeechRecognition.PrimaryFontSize; set => Set(value, _settings.SpeechRecognition.PrimaryFontSize, next => _settings.SpeechRecognition.PrimaryFontSize = next); }
    public string PrimaryFontFamily { get => _settings.SpeechRecognition.PrimaryFontFamily; set => Set(value, _settings.SpeechRecognition.PrimaryFontFamily, next => _settings.SpeechRecognition.PrimaryFontFamily = next); }
    public string PrimaryFontColor { get => _settings.SpeechRecognition.PrimaryFontColor; set => Set(value, _settings.SpeechRecognition.PrimaryFontColor, next => _settings.SpeechRecognition.PrimaryFontColor = next); }
    public SubtitleSource SecondarySubtitleSource { get => _settings.SpeechRecognition.SecondarySubtitleSource; set => Set(value, _settings.SpeechRecognition.SecondarySubtitleSource, next => _settings.SpeechRecognition.SecondarySubtitleSource = next); }
    public double SecondaryFontSize { get => _settings.SpeechRecognition.SecondaryFontSize; set => Set(value, _settings.SpeechRecognition.SecondaryFontSize, next => _settings.SpeechRecognition.SecondaryFontSize = next); }
    public string SecondaryFontFamily { get => _settings.SpeechRecognition.SecondaryFontFamily; set => Set(value, _settings.SpeechRecognition.SecondaryFontFamily, next => _settings.SpeechRecognition.SecondaryFontFamily = next); }
    public string SecondaryFontColor { get => _settings.SpeechRecognition.SecondaryFontColor; set => Set(value, _settings.SpeechRecognition.SecondaryFontColor, next => _settings.SpeechRecognition.SecondaryFontColor = next); }
    public string BackgroundColor { get => _settings.SpeechRecognition.BackgroundColor; set => Set(value, _settings.SpeechRecognition.BackgroundColor, next => _settings.SpeechRecognition.BackgroundColor = next); }
    public string SubtitleBackgroundColor { get => _settings.SpeechRecognition.SubtitleBackgroundColor; set => Set(value, _settings.SpeechRecognition.SubtitleBackgroundColor, next => _settings.SpeechRecognition.SubtitleBackgroundColor = next); }
    public double WindowOpacity { get => _settings.SpeechRecognition.WindowOpacity; set => Set(value, _settings.SpeechRecognition.WindowOpacity, next => _settings.SpeechRecognition.WindowOpacity = next); }
    public bool IsFloatingWindowLocked { get => _settings.SpeechRecognition.IsFloatingWindowLocked; set => Set(value, _settings.SpeechRecognition.IsFloatingWindowLocked, next => _settings.SpeechRecognition.IsFloatingWindowLocked = next); }
    public string FloatingWindowOrientation { get => _settings.SpeechRecognition.FloatingWindowOrientation; set => Set(value, _settings.SpeechRecognition.FloatingWindowOrientation, next => _settings.SpeechRecognition.FloatingWindowOrientation = next); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        var capability = await _capabilities.GetStatusAsync(
            PlatformCapability.SpeechRecognition,
            cancellationToken);
        IsSupported = capability.State != CapabilityState.Unsupported;
        if (!IsSupported)
            return;

        await RefreshRecognitionLanguagesAsync(cancellationToken);
        await RefreshSourcesAsync(cancellationToken);
    }

    public void StoreFloatingWindowBounds(int x, int y, double width, double height)
    {
        _settings.SpeechRecognition.WindowX = x;
        _settings.SpeechRecognition.WindowY = y;
        _settings.SpeechRecognition.WindowWidth = width;
        _settings.SpeechRecognition.WindowHeight = height;
    }

    public void Dispose()
    {
        _recognitionCancellation?.Cancel();
        _recognitionCancellation?.Dispose();
        _autoClearTimer.Stop();
        _subtitleWindow.VisibilityChanged -= OnSubtitleWindowVisibilityChanged;
        _models.ModelsChanged -= OnModelsChanged;
        _subtitleWindow.Close();
        foreach (var source in AudioSources)
            source.Dispose();
    }

    private void OnModelsChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _initialized) == 0 || !IsSupported)
            return;
        Dispatcher.UIThread.Post(() => _ = RefreshRecognitionLanguagesAfterChangeAsync());
    }

    private async Task RefreshRecognitionLanguagesAfterChangeAsync()
    {
        try
        {
            await RefreshRecognitionLanguagesAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to refresh speech recognition models.");
        }
    }

    private async Task RefreshRecognitionLanguagesAsync(CancellationToken cancellationToken = default)
    {
        var models = await _models.GetModelsAsync(cancellationToken);
        var current = SelectedRecognitionLanguage;
        RecognitionLanguages.Clear();
        foreach (var model in models)
            RecognitionLanguages.Add(model.Id);

        var configured = string.IsNullOrWhiteSpace(current)
            ? _settings.SpeechRecognition.RecognitionLanguage
            : current;
        SelectedRecognitionLanguage = RecognitionLanguages.FirstOrDefault(language => language == configured)
            ?? RecognitionLanguages.FirstOrDefault(language => language.Contains("zh", StringComparison.OrdinalIgnoreCase))
            ?? RecognitionLanguages.FirstOrDefault()
            ?? string.Empty;
    }

    private async Task ToggleRecordingAsync()
    {
        if (IsBusy || !IsSupported)
            return;
        if (IsRecording)
        {
            IsBusy = true;
            _recognitionCancellation?.Cancel();
            if (_recognitionTask is not null)
            {
                try { await _recognitionTask; }
                catch (OperationCanceledException) { }
            }
            IsBusy = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedRecognitionLanguage))
            return;

        _recognitionCancellation?.Dispose();
        _recognitionCancellation = new CancellationTokenSource();
        var command = new SpeechRecognitionCommand(
            SelectedRecognitionLanguage,
            SelectedRecognitionLanguage,
            AudioSources.Where(source => source.IsSelected)
                .Select(source => new AudioCaptureSourceReference(source.Token, source.Kind))
                .ToArray());
        IsRecording = true;
        _recognitionTask = ConsumeRecognitionAsync(command, _recognitionCancellation.Token);
    }

    private async Task ConsumeRecognitionAsync(
        SpeechRecognitionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _speech.RecognizeAsync(command, cancellationToken)
                               .ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Apply(item));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Speech recognition failed.");
            await Dispatcher.UIThread.InvokeAsync(() => AddError(exception.Message));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRecording = false;
                IsBusy = false;
            });
        }
    }

    private void Apply(SpeechSessionEvent item)
    {
        switch (item)
        {
            case SpeechSessionStartedEvent:
                IsRecording = true;
                IsBusy = false;
                break;
            case SpeechSubtitleChangedEvent changed:
                UpdateSubtitle(changed.Subtitle);
                RestartAutoClearTimer();
                break;
            case SpeechFloatingSubtitleRemovedEvent removed:
                var floating = FloatingSubtitles.FirstOrDefault(line => line.Id == removed.SubtitleId);
                if (floating is not null)
                    FloatingSubtitles.Remove(floating);
                break;
            case SpeechSessionErrorEvent error:
                AddError(error.Message);
                break;
            case SpeechSessionStoppedEvent:
                IsRecording = false;
                IsBusy = false;
                break;
        }
    }

    private void UpdateSubtitle(SpeechSubtitleLine subtitle)
    {
        var item = SubtitleItems.FirstOrDefault(line => line.Id == subtitle.Id);
        if (item is null)
        {
            item = new SpeechSubtitleItemViewModel(subtitle);
            SubtitleItems.Add(item);
        }
        else
        {
            item.Update(subtitle);
        }

        if (!FloatingSubtitles.Contains(item))
            FloatingSubtitles.Add(item);
    }

    private void AddError(string message)
    {
        var line = new SpeechSubtitleLine(
            Interlocked.Decrement(ref _nextErrorId),
            DateTime.Now.TimeOfDay,
            message,
            string.Empty,
            string.Empty,
            false,
            false);
        var item = new SpeechSubtitleItemViewModel(line);
        SubtitleItems.Add(item);
        FloatingSubtitles.Add(item);
    }

    private async Task RefreshSourcesAsync(CancellationToken cancellationToken = default)
    {
        var access = await _platformAccess.EnsureAvailableAsync(
            PlatformCapability.AudioCaptureSources,
            cancellationToken);
        if (access.IsFailure)
        {
            _logger.LogWarning(
                "Audio capture sources are unavailable: {Message}",
                access.Error.Message);
            return;
        }

        var selected = AudioSources.Where(source => source.IsSelected)
            .Select(source => source.Token)
            .ToHashSet();
        foreach (var source in AudioSources)
        {
            source.PropertyChanged -= OnSourcePropertyChanged;
            source.Dispose();
        }
        AudioSources.Clear();

        var available = await _audioSources.GetSourcesAsync(cancellationToken);
        foreach (var descriptor in available)
        {
            var item = new SpeechAudioSourceItem(
                descriptor,
                selected.Count == 0
                    ? descriptor.Kind == AudioCaptureSourceKind.SystemOutput
                    : selected.Contains(descriptor.Token));
            item.PropertyChanged += OnSourcePropertyChanged;
            AudioSources.Add(item);
        }
        UpdateSourceSummary();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SpeechAudioSourceItem.IsSelected))
            UpdateSourceSummary();
    }

    private void UpdateSourceSummary()
    {
        var selected = AudioSources.Where(source => source.IsSelected).ToArray();
        SelectedSourcesSummary = selected.Length switch
        {
            0 => Resources.Speech_AllSystemAudio,
            1 => selected[0].Name,
            _ => string.Format(Resources.Speech_SelectedAppsCount, selected.Length)
        };
    }

    private void LoadEngineOptions()
    {
        var selectedId = _selectedEngineOption?.Id ?? _settings.SpeechRecognition.EngineId;
        var selectedMachine = _selectedEngineOption?.IsMachine
                              ?? _settings.SpeechRecognition.EngineType == 0;
        EngineOptions.Clear();
        EngineOptions.Add(new SpeechEngineOption(MachineTranslationProviderNames.Baidu, MachineTranslationProviderNames.Baidu, true));
        EngineOptions.Add(new SpeechEngineOption(MachineTranslationProviderNames.Tencent, MachineTranslationProviderNames.Tencent, true));
        EngineOptions.Add(new SpeechEngineOption(MachineTranslationProviderNames.Google, MachineTranslationProviderNames.Google, true));
        EngineOptions.Add(new SpeechEngineOption(MachineTranslationProviderNames.DeepL, MachineTranslationProviderNames.DeepL, true));
        foreach (var model in _settings.AiModel.ConfiguredModels)
            EngineOptions.Add(new SpeechEngineOption(model.Name, model.Id, false));
        _selectedEngineOption = EngineOptions.FirstOrDefault(option =>
            option.Id == selectedId && option.IsMachine == selectedMachine)
            ?? EngineOptions.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedEngineOption));
        UpdateTargetLanguages(commitSelection: false);
    }

    private void UpdateTargetLanguages(bool commitSelection)
    {
        var targetId = _selectedTargetLanguage?.Id ?? _settings.SpeechRecognition.TargetLanguage;
        TargetLanguages.Clear();
        foreach (var language in _languages.All.Where(language =>
                     _selectedEngineOption?.IsMachine != true
                     || language.Id == "auto"
                     || language.ProviderCodes.ContainsKey(_selectedEngineOption.Id)))
        {
            TargetLanguages.Add(language);
        }
        _selectedTargetLanguage = TargetLanguages.FirstOrDefault(language => language.Id == targetId)
                                  ?? TargetLanguages.FirstOrDefault(language => language.Id == "zh-Hans")
                                  ?? TargetLanguages.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
        if (commitSelection && _selectedTargetLanguage is not null)
            _settings.SpeechRecognition.TargetLanguage = _selectedTargetLanguage.Id;
    }

    private void ToggleFloatingWindow()
    {
        if (_subtitleWindow.IsOpen)
            _subtitleWindow.Close();
        else
            _subtitleWindow.Open(this);
    }

    private void OnSubtitleWindowVisibilityChanged(object? sender, bool isOpen) =>
        IsFloatingWindowOpen = isOpen;

    private void ClearHistory()
    {
        _autoClearTimer.Stop();
        SubtitleItems.Clear();
        FloatingSubtitles.Clear();
    }

    private void UpdateAutoClearTimer()
    {
        _autoClearTimer.Stop();
        if (AutoClearInterval > 0)
            _autoClearTimer.Interval = TimeSpan.FromSeconds(AutoClearInterval);
    }

    private void RestartAutoClearTimer()
    {
        if (AutoClearInterval <= 0)
            return;
        _autoClearTimer.Stop();
        _autoClearTimer.Start();
    }

    private void Set<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
            return;
        apply(value);
        this.RaisePropertyChanged(propertyName);
    }
}
