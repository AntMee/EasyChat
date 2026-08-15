using EasyChat.Contracts.Settings;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Settings.State;

public sealed class LiveSpeechRecognitionSettings : LiveSettingsSection
{
    private string _recognitionLanguage;
    private bool _isTranslationEnabled;
    private bool _isTranslatedSpeechEnabled;
    private bool _isRealTimePreviewEnabled;
    private string _targetLanguage;
    private string _engineId;
    private int _engineType;
    private string? _promptId;
    private int _maxSentencesPerLine;
    private FloatingDisplayMode _floatingDisplayMode;
    private int _maxFloatingHistory;
    private int _autoClearInterval;
    private SubtitleSource _mainSubtitleSource;
    private double _primaryFontSize;
    private string _primaryFontFamily;
    private string _primaryFontColor;
    private SubtitleSource _secondarySubtitleSource;
    private double _secondaryFontSize;
    private string _secondaryFontFamily;
    private string _secondaryFontColor;
    private string _backgroundColor;
    private string _subtitleBackgroundColor;
    private double _windowOpacity;
    private bool _isFloatingWindowLocked;
    private string _floatingWindowOrientation;
    private double _windowX;
    private double _windowY;
    private double _windowWidth;
    private double _windowHeight;

    public LiveSpeechRecognitionSettings(
        SpeechRecognitionSettings value,
        Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.SpeechRecognition, commit)
    {
        _recognitionLanguage = value.RecognitionLanguage;
        _isTranslationEnabled = value.IsTranslationEnabled;
        _isTranslatedSpeechEnabled = value.IsTranslatedSpeechEnabled;
        _isRealTimePreviewEnabled = value.IsRealTimePreviewEnabled;
        _targetLanguage = value.TargetLanguage;
        _engineId = value.EngineId;
        _engineType = value.EngineType;
        _promptId = value.PromptId;
        _maxSentencesPerLine = value.MaxSentencesPerLine;
        _floatingDisplayMode = value.FloatingDisplayMode;
        _maxFloatingHistory = value.MaxFloatingHistory;
        _autoClearInterval = value.AutoClearInterval;
        _mainSubtitleSource = value.MainSubtitleSource;
        _primaryFontSize = value.PrimaryFontSize;
        _primaryFontFamily = value.PrimaryFontFamily;
        _primaryFontColor = value.PrimaryFontColor;
        _secondarySubtitleSource = value.SecondarySubtitleSource;
        _secondaryFontSize = value.SecondaryFontSize;
        _secondaryFontFamily = value.SecondaryFontFamily;
        _secondaryFontColor = value.SecondaryFontColor;
        _backgroundColor = value.BackgroundColor;
        _subtitleBackgroundColor = value.SubtitleBackgroundColor;
        _windowOpacity = value.WindowOpacity;
        _isFloatingWindowLocked = value.IsFloatingWindowLocked;
        _floatingWindowOrientation = value.FloatingWindowOrientation;
        _windowX = value.WindowX;
        _windowY = value.WindowY;
        _windowWidth = value.WindowWidth;
        _windowHeight = value.WindowHeight;
    }

    public string RecognitionLanguage { get => _recognitionLanguage; set => Set(ref _recognitionLanguage, value); }
    public bool IsTranslationEnabled { get => _isTranslationEnabled; set => Set(ref _isTranslationEnabled, value); }
    public bool IsTranslatedSpeechEnabled { get => _isTranslatedSpeechEnabled; set => Set(ref _isTranslatedSpeechEnabled, value); }
    public bool IsRealTimePreviewEnabled { get => _isRealTimePreviewEnabled; set => Set(ref _isRealTimePreviewEnabled, value); }
    public string TargetLanguage { get => _targetLanguage; set => Set(ref _targetLanguage, value); }
    public string EngineId { get => _engineId; set => Set(ref _engineId, value); }
    public int EngineType { get => _engineType; set => Set(ref _engineType, value); }
    public string? PromptId { get => _promptId; set => Set(ref _promptId, value); }
    public int MaxSentencesPerLine { get => _maxSentencesPerLine; set => Set(ref _maxSentencesPerLine, value); }
    public FloatingDisplayMode FloatingDisplayMode { get => _floatingDisplayMode; set => Set(ref _floatingDisplayMode, value); }
    public int MaxFloatingHistory { get => _maxFloatingHistory; set => Set(ref _maxFloatingHistory, value); }
    public int AutoClearInterval { get => _autoClearInterval; set => Set(ref _autoClearInterval, value); }
    public SubtitleSource MainSubtitleSource { get => _mainSubtitleSource; set => Set(ref _mainSubtitleSource, value); }
    public double PrimaryFontSize { get => _primaryFontSize; set => Set(ref _primaryFontSize, value); }
    public string PrimaryFontFamily { get => _primaryFontFamily; set => Set(ref _primaryFontFamily, value); }
    public string PrimaryFontColor { get => _primaryFontColor; set => Set(ref _primaryFontColor, value); }
    public SubtitleSource SecondarySubtitleSource { get => _secondarySubtitleSource; set => Set(ref _secondarySubtitleSource, value); }
    public double SecondaryFontSize { get => _secondaryFontSize; set => Set(ref _secondaryFontSize, value); }
    public string SecondaryFontFamily { get => _secondaryFontFamily; set => Set(ref _secondaryFontFamily, value); }
    public string SecondaryFontColor { get => _secondaryFontColor; set => Set(ref _secondaryFontColor, value); }
    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public string SubtitleBackgroundColor { get => _subtitleBackgroundColor; set => Set(ref _subtitleBackgroundColor, value); }
    public double WindowOpacity { get => _windowOpacity; set => Set(ref _windowOpacity, value); }
    public bool IsFloatingWindowLocked { get => _isFloatingWindowLocked; set => Set(ref _isFloatingWindowLocked, value); }
    public string FloatingWindowOrientation { get => _floatingWindowOrientation; set => Set(ref _floatingWindowOrientation, value); }
    public double WindowX { get => _windowX; set => Set(ref _windowX, value); }
    public double WindowY { get => _windowY; set => Set(ref _windowY, value); }
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    public SpeechRecognitionSettings ToContract() => new(
        RecognitionLanguage, IsTranslationEnabled, IsRealTimePreviewEnabled, TargetLanguage,
        EngineId, EngineType, MaxSentencesPerLine, FloatingDisplayMode, MaxFloatingHistory,
        AutoClearInterval, MainSubtitleSource, PrimaryFontSize, PrimaryFontFamily,
        PrimaryFontColor, SecondarySubtitleSource, SecondaryFontSize, SecondaryFontFamily,
        SecondaryFontColor, BackgroundColor, SubtitleBackgroundColor, WindowOpacity,
        IsFloatingWindowLocked, FloatingWindowOrientation, WindowX, WindowY, WindowWidth,
        WindowHeight, PromptId, IsTranslatedSpeechEnabled);

    public void Apply(SpeechRecognitionSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var changed = false;

        void SetValue<T>(ref T field, T next, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, next))
                return;
            field = next;
            changed = true;
            this.RaisePropertyChanged(propertyName);
        }

        SetValue(ref _recognitionLanguage, value.RecognitionLanguage, nameof(RecognitionLanguage));
        SetValue(ref _isTranslationEnabled, value.IsTranslationEnabled, nameof(IsTranslationEnabled));
        SetValue(ref _isTranslatedSpeechEnabled, value.IsTranslatedSpeechEnabled, nameof(IsTranslatedSpeechEnabled));
        SetValue(ref _isRealTimePreviewEnabled, value.IsRealTimePreviewEnabled, nameof(IsRealTimePreviewEnabled));
        SetValue(ref _targetLanguage, value.TargetLanguage, nameof(TargetLanguage));
        SetValue(ref _engineId, value.EngineId, nameof(EngineId));
        SetValue(ref _engineType, value.EngineType, nameof(EngineType));
        SetValue(ref _promptId, value.PromptId, nameof(PromptId));
        SetValue(ref _maxSentencesPerLine, value.MaxSentencesPerLine, nameof(MaxSentencesPerLine));
        SetValue(ref _floatingDisplayMode, value.FloatingDisplayMode, nameof(FloatingDisplayMode));
        SetValue(ref _maxFloatingHistory, value.MaxFloatingHistory, nameof(MaxFloatingHistory));
        SetValue(ref _autoClearInterval, value.AutoClearInterval, nameof(AutoClearInterval));
        SetValue(ref _mainSubtitleSource, value.MainSubtitleSource, nameof(MainSubtitleSource));
        SetValue(ref _primaryFontSize, value.PrimaryFontSize, nameof(PrimaryFontSize));
        SetValue(ref _primaryFontFamily, value.PrimaryFontFamily, nameof(PrimaryFontFamily));
        SetValue(ref _primaryFontColor, value.PrimaryFontColor, nameof(PrimaryFontColor));
        SetValue(ref _secondarySubtitleSource, value.SecondarySubtitleSource, nameof(SecondarySubtitleSource));
        SetValue(ref _secondaryFontSize, value.SecondaryFontSize, nameof(SecondaryFontSize));
        SetValue(ref _secondaryFontFamily, value.SecondaryFontFamily, nameof(SecondaryFontFamily));
        SetValue(ref _secondaryFontColor, value.SecondaryFontColor, nameof(SecondaryFontColor));
        SetValue(ref _backgroundColor, value.BackgroundColor, nameof(BackgroundColor));
        SetValue(ref _subtitleBackgroundColor, value.SubtitleBackgroundColor, nameof(SubtitleBackgroundColor));
        SetValue(ref _windowOpacity, value.WindowOpacity, nameof(WindowOpacity));
        SetValue(ref _isFloatingWindowLocked, value.IsFloatingWindowLocked, nameof(IsFloatingWindowLocked));
        SetValue(ref _floatingWindowOrientation, value.FloatingWindowOrientation, nameof(FloatingWindowOrientation));
        SetValue(ref _windowX, value.WindowX, nameof(WindowX));
        SetValue(ref _windowY, value.WindowY, nameof(WindowY));
        SetValue(ref _windowWidth, value.WindowWidth, nameof(WindowWidth));
        SetValue(ref _windowHeight, value.WindowHeight, nameof(WindowHeight));

        if (changed)
            Commit();
    }
}

public sealed class LiveTextAssistSettings : LiveSettingsSection
{
    private bool _followGlobal;
    private string _sourceLanguageId;
    private string _targetLanguageId;
    private string _provider;
    private string? _aiModelId;
    private string? _translationPromptId;
    private string? _correctionPromptId;
    private string? _polishPromptId;
    private string? _summaryPromptId;
    private bool _detailedExplanation;
    private bool _translationConfigurationExpanded;
    private bool _correctionConfigurationExpanded;
    private string _machineProvider;

    public LiveTextAssistSettings(TextAssistSettings value, Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.TextAssist, commit)
    {
        _followGlobal = value.FollowGlobal;
        _sourceLanguageId = value.SourceLanguageId;
        _targetLanguageId = value.TargetLanguageId;
        _provider = value.Provider;
        _aiModelId = value.AiModelId;
        _translationPromptId = value.TranslationPromptId;
        _correctionPromptId = value.CorrectionPromptId;
        _polishPromptId = value.PolishPromptId;
        _summaryPromptId = value.SummaryPromptId;
        _detailedExplanation = value.DetailedExplanation;
        _translationConfigurationExpanded = value.TranslationConfigurationExpanded;
        _correctionConfigurationExpanded = value.CorrectionConfigurationExpanded;
        _machineProvider = value.MachineProvider;
    }

    public bool FollowGlobal { get => _followGlobal; set => Set(ref _followGlobal, value); }
    public string SourceLanguageId { get => _sourceLanguageId; set => Set(ref _sourceLanguageId, value); }
    public string TargetLanguageId { get => _targetLanguageId; set => Set(ref _targetLanguageId, value); }
    public string Provider { get => _provider; set => Set(ref _provider, value); }
    public string? AiModelId { get => _aiModelId; set => Set(ref _aiModelId, value); }
    public string? TranslationPromptId { get => _translationPromptId; set => Set(ref _translationPromptId, value); }
    public string? CorrectionPromptId { get => _correctionPromptId; set => Set(ref _correctionPromptId, value); }
    public string? PolishPromptId { get => _polishPromptId; set => Set(ref _polishPromptId, value); }
    public string? SummaryPromptId { get => _summaryPromptId; set => Set(ref _summaryPromptId, value); }
    public bool DetailedExplanation { get => _detailedExplanation; set => Set(ref _detailedExplanation, value); }
    public bool TranslationConfigurationExpanded { get => _translationConfigurationExpanded; set => Set(ref _translationConfigurationExpanded, value); }
    public bool CorrectionConfigurationExpanded { get => _correctionConfigurationExpanded; set => Set(ref _correctionConfigurationExpanded, value); }
    public string MachineProvider { get => _machineProvider; set => Set(ref _machineProvider, value); }

    public TextAssistSettings ToContract() => new(
        FollowGlobal, SourceLanguageId, TargetLanguageId, Provider, AiModelId,
        TranslationPromptId, CorrectionPromptId, PolishPromptId, SummaryPromptId,
        DetailedExplanation, TranslationConfigurationExpanded, CorrectionConfigurationExpanded,
        MachineProvider);
}
