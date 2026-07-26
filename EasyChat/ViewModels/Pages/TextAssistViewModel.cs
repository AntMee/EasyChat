using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Constants;
using EasyChat.Lang;
using EasyChat.Models;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.TextAssist;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.Speech.Tts;
using EasyChat.Services.TextAssist;
using Material.Icons;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace EasyChat.ViewModels.Pages;

public sealed class TextAssistViewModel : Page
{
    private bool _isCapturingInput;

    public TextAssistViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ITextAssistDictionaryService dictionaryService,
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        ILogger<TextAssistViewModel>? logger = null) : base(Resources.TextAssist, MaterialIconKind.Translate, 5)
    {
        Translation = new TextAssistTranslationViewModel(configurationService, profileResolver, textAssistService, dictionaryService,
            ttsService, audioPlayer, logger);
        Correction = new TextAssistCorrectionViewModel(configurationService, profileResolver, textAssistService, logger);
        SelectTranslationCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 0; });
        SelectCorrectionCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 1; });
    }

    public TextAssistTranslationViewModel Translation { get; }
    public TextAssistCorrectionViewModel Correction { get; }
    public ReactiveCommand<Unit, Unit> SelectTranslationCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectCorrectionCommand { get; }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
            this.RaisePropertyChanged(nameof(IsTranslationMode));
            this.RaisePropertyChanged(nameof(IsCorrectionMode));
            this.RaisePropertyChanged(nameof(WindowTitle));
            this.RaisePropertyChanged(nameof(WindowIcon));
            Translation.IsActive = value == 0;
            Correction.IsActive = value == 1;
        }
    }

    public bool IsTranslationMode => SelectedTabIndex == 0;
    public bool IsCorrectionMode => SelectedTabIndex == 1;
    public string WindowTitle => IsCorrectionMode ? Resources.TextAssistCorrect : Resources.TextAssistTranslate;
    public MaterialIconKind WindowIcon => IsCorrectionMode ? MaterialIconKind.Spellcheck : MaterialIconKind.Translate;

    public bool IsCapturingInput
    {
        get => _isCapturingInput;
        private set => this.RaiseAndSetIfChanged(ref _isCapturingInput, value);
    }

    public void PrepareForInputCapture(bool correction)
    {
        SelectedTabIndex = correction ? 1 : 0;
        IsCapturingInput = true;
    }

    public async Task InitializeAsync(string text, bool correction)
    {
        SelectedTabIndex = correction ? 1 : 0;
        IsCapturingInput = false;
        if (correction)
        {
            Correction.InputText = text;
            await Correction.RunNowAsync();
        }
        else
        {
            Translation.InputText = text;
            await Translation.RunNowAsync();
        }
    }
}

public sealed class TextAssistTranslationPageViewModel : Page
{
    public TextAssistTranslationPageViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ITextAssistDictionaryService dictionaryService,
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        ILogger<TextAssistTranslationPageViewModel>? logger = null) : base(Resources.TextAssistTranslate, MaterialIconKind.Translate, 5)
    {
        Translation = new TextAssistTranslationViewModel(configurationService, profileResolver, textAssistService, dictionaryService,
            ttsService, audioPlayer, logger);
    }

    public TextAssistTranslationViewModel Translation { get; }
}

public sealed class TextAssistCorrectionPageViewModel : Page
{
    public TextAssistCorrectionPageViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ILogger<TextAssistCorrectionPageViewModel>? logger = null) : base(Resources.TextAssistCorrect, MaterialIconKind.Spellcheck, 6)
    {
        Correction = new TextAssistCorrectionViewModel(configurationService, profileResolver, textAssistService, logger);
    }

    public TextAssistCorrectionViewModel Correction { get; }
}

public abstract class TextAssistEditorViewModel : ViewModelBase
{
    protected readonly IConfigurationService ConfigurationService;
    protected readonly TextAssistProfileResolver ProfileResolver;
    protected readonly ITextAssistService TextAssistService;
    protected readonly ILogger? Logger;
    private CancellationTokenSource? _requestCts;
    private readonly bool _correction;
    private string _sourceLanguageId;
    private string _targetLanguageId;
    private string _provider;
    private CustomAiModel? _selectedAiModel;
    private string _machineProvider;
    private string? _selectedPromptId;
    private bool _isBusy;
    private bool _isActive;
    private string _errorMessage = string.Empty;

    protected TextAssistEditorViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        bool correction,
        ILogger? logger)
    {
        ConfigurationService = configurationService;
        ProfileResolver = profileResolver;
        TextAssistService = textAssistService;
        _correction = correction;
        _isActive = !correction;
        Logger = logger;
        Languages = LanguageService.GetAllLanguages().OrderBy(x => x.EnglishName).ToList();
        AvailableAiModels = new ObservableCollection<CustomAiModel>(configurationService.AiModel?.ConfiguredModels ?? []);
        MachineProviders = [Constant.MachineTranslationProviders.Baidu, Constant.MachineTranslationProviders.Tencent,
            Constant.MachineTranslationProviders.Google, Constant.MachineTranslationProviders.DeepL];
        PromptEntries = new ObservableCollection<PromptEntry>(configurationService.Prompts?.Entries ?? []);

        var config = configurationService.TextAssist ?? new TextAssistConfig();
        // Text Assist always owns its settings. Keep the legacy config field
        // disabled so older configuration files cannot re-enable global mode.
        config.FollowGlobal = false;
        _sourceLanguageId = config.SourceLanguageId;
        _targetLanguageId = config.TargetLanguageId;
        _provider = config.Provider;
        _selectedAiModel = AvailableAiModels.FirstOrDefault(x => x.Id == config.AiModelId) ?? AvailableAiModels.FirstOrDefault();
        _machineProvider = config.MachineProvider;
        _selectedPromptId = _correction ? config.CorrectionPromptId : config.TranslationPromptId;
        _selectedPromptId ??= configurationService.Prompts?.SelectedPromptId;
        _selectedPromptId ??= PromptEntries.FirstOrDefault(x => x.IsDefault)?.Id;

        RunCommand = ReactiveCommand.CreateFromTask(ExecuteAsync,
            this.WhenAnyValue(x => x.IsBusy, busy => !busy));
        CancelCommand = ReactiveCommand.Create(Cancel);
    }

    public IReadOnlyList<LanguageDefinition> Languages { get; }
    public ObservableCollection<CustomAiModel> AvailableAiModels { get; }
    public IReadOnlyList<string> MachineProviders { get; }
    public IReadOnlyList<string> AvailableProviders { get; } = [TextAssistConstants.AiProvider, TextAssistConstants.MachineProvider];
    public ObservableCollection<PromptEntry> PromptEntries { get; }
    public ReactiveCommand<Unit, Unit> RunCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public bool IsCorrection => _correction;

    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        protected set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public LanguageDefinition SelectedSourceLanguage
    {
        get => Languages.FirstOrDefault(x => x.Id == _sourceLanguageId) ?? LanguageService.GetLanguage("auto");
        set
        {
            _sourceLanguageId = value.Id;
            this.RaisePropertyChanged();
            PersistSettings();
        }
    }

    public LanguageDefinition SelectedTargetLanguage
    {
        get => Languages.FirstOrDefault(x => x.Id == _targetLanguageId) ?? LanguageService.GetLanguage("zh-Hans");
        set
        {
            _targetLanguageId = value.Id;
            this.RaisePropertyChanged();
            PersistSettings();
        }
    }

    public string SelectedProvider
    {
        get => _provider;
        set
        {
            if (string.Equals(_provider, value, StringComparison.Ordinal)) return;
            _provider = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsAiProvider));
            this.RaisePropertyChanged(nameof(IsMachineProvider));
            PersistSettings();
        }
    }

    public bool IsAiProvider => SelectedProvider.Equals(TextAssistConstants.AiProvider, StringComparison.OrdinalIgnoreCase);
    public bool IsMachineProvider => SelectedProvider.Equals(TextAssistConstants.MachineProvider, StringComparison.OrdinalIgnoreCase);

    public CustomAiModel? SelectedAiModel
    {
        get => _selectedAiModel;
        set
        {
            if (ReferenceEquals(_selectedAiModel, value)) return;
            _selectedAiModel = value;
            this.RaisePropertyChanged();
            PersistSettings();
        }
    }

    public string SelectedMachineProvider
    {
        get => _machineProvider;
        set
        {
            if (string.Equals(_machineProvider, value, StringComparison.Ordinal)) return;
            _machineProvider = value;
            this.RaisePropertyChanged();
            PersistSettings();
        }
    }

    public string? SelectedPromptId
    {
        get => _selectedPromptId;
        set
        {
            if (string.Equals(_selectedPromptId, value, StringComparison.Ordinal)) return;
            _selectedPromptId = value;
            this.RaisePropertyChanged();
            PersistSettings();
        }
    }

    protected TextAssistProfile ResolveProfile()
    {
        PersistSettings();
        return ProfileResolver.Resolve(_correction);
    }

    private void PersistSettings()
    {
        var config = ConfigurationService.TextAssist;
        if (config == null) return;
        config.FollowGlobal = false;
        config.SourceLanguageId = _sourceLanguageId;
        config.TargetLanguageId = _targetLanguageId;
        config.Provider = _provider;
        config.AiModelId = _selectedAiModel?.Id;
        config.MachineProvider = _machineProvider;
        if (_correction) config.CorrectionPromptId = _selectedPromptId;
        else config.TranslationPromptId = _selectedPromptId;
    }

    private async Task ExecuteAsync()
    {
        var cts = new CancellationTokenSource();
        _requestCts?.Cancel();
        _requestCts = cts;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await RunCoreAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Text assist request failed");
            ErrorMessage = ex.Message.Contains("No AI model", StringComparison.OrdinalIgnoreCase)
                ? Resources.TextAssistNoAiModel
                : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_requestCts, cts)) _requestCts = null;
            IsBusy = false;
            cts.Dispose();
        }
    }

    public Task RunNowAsync() => ExecuteAsync();

    private void Cancel() => _requestCts?.Cancel();
    protected abstract Task RunCoreAsync(CancellationToken cancellationToken);
}

public sealed class TextAssistTranslationViewModel : TextAssistEditorViewModel
{
    private readonly ITextAssistDictionaryService _dictionaryService;
    private readonly ITtsService _ttsService;
    private readonly IAudioPlayer _audioPlayer;
    private string _inputText = string.Empty;
    private string _translationResult = string.Empty;
    private bool _isSourceSpeaking;
    private bool _isResultSpeaking;
    private bool _detailedExplanation;

    public TextAssistTranslationViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ITextAssistDictionaryService dictionaryService,
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        ILogger? logger) : base(configurationService, profileResolver, textAssistService, false, logger)
    {
        _dictionaryService = dictionaryService;
        _ttsService = ttsService;
        _audioPlayer = audioPlayer;
        _detailedExplanation = configurationService.TextAssist?.DetailedExplanation ?? false;
        Annotations = [];
        SpeakSourceCommand = ReactiveCommand.CreateFromTask(() => SpeakAsync(InputText, SelectedSourceLanguage.Id, true));
        SpeakResultCommand = ReactiveCommand.CreateFromTask(() => SpeakAsync(TranslationResult, SelectedTargetLanguage.Id, false));
        SwapContentCommand = ReactiveCommand.Create(SwapContent);
        LookupAnnotationCommand = ReactiveCommand.CreateFromTask<string>(LookupAnnotationAsync);
    }

    public string InputText
    {
        get => _inputText;
        set => this.RaiseAndSetIfChanged(ref _inputText, value);
    }

    public string TranslationResult
    {
        get => _translationResult;
        private set => this.RaiseAndSetIfChanged(ref _translationResult, value);
    }

    public bool IsSourceSpeaking
    {
        get => _isSourceSpeaking;
        private set => this.RaiseAndSetIfChanged(ref _isSourceSpeaking, value);
    }

    public bool IsResultSpeaking
    {
        get => _isResultSpeaking;
        private set => this.RaiseAndSetIfChanged(ref _isResultSpeaking, value);
    }

    public bool DetailedExplanation
    {
        get => _detailedExplanation;
        set
        {
            if (_detailedExplanation == value) return;
            this.RaiseAndSetIfChanged(ref _detailedExplanation, value);
            this.RaisePropertyChanged(nameof(ShowAnnotations));
            if (ConfigurationService.TextAssist != null)
                ConfigurationService.TextAssist.DetailedExplanation = value;
        }
    }

    public ObservableCollection<TextAssistTranslationAnnotationEvent> Annotations { get; }
    public bool ShowAnnotations => DetailedExplanation && Annotations.Count > 0;

    public ReactiveCommand<Unit, Unit> SpeakSourceCommand { get; }
    public ReactiveCommand<Unit, Unit> SpeakResultCommand { get; }
    public ReactiveCommand<Unit, Unit> SwapContentCommand { get; }
    public ReactiveCommand<string, Unit> LookupAnnotationCommand { get; }

    private void SwapContent()
    {
        var sourceText = InputText;
        InputText = TranslationResult;
        TranslationResult = sourceText;
        Annotations.Clear();
        this.RaisePropertyChanged(nameof(ShowAnnotations));
        var source = SelectedSourceLanguage;
        SelectedSourceLanguage = SelectedTargetLanguage;
        SelectedTargetLanguage = source;
    }

    protected override async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;
        TranslationResult = string.Empty;
        Annotations.Clear();
        this.RaisePropertyChanged(nameof(ShowAnnotations));
        var profile = ResolveProfile();
        await foreach (var item in TextAssistService.StreamTranslateAsync(InputText, profile, cancellationToken))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                switch (item)
                {
                    case TextAssistTranslationDeltaEvent delta:
                        TranslationResult += delta.Text;
                        break;
                    case TextAssistTranslationAnnotationEvent annotation:
                        Annotations.Add(annotation);
                        this.RaisePropertyChanged(nameof(ShowAnnotations));
                        break;
                }
            });
        }
    }

    private Task LookupAnnotationAsync(string term)
    {
        return string.IsNullOrWhiteSpace(term)
            ? Task.CompletedTask
            : _dictionaryService.OpenAsync(term, SelectedSourceLanguage.Id, SelectedTargetLanguage.Id);
    }

    private async Task SpeakAsync(string text, string languageId, bool source)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (source) IsSourceSpeaking = true; else IsResultSpeaking = true;
            _audioPlayer.Stop();
            var voiceId = TtsHelper.GetPreferredVoiceId(_ttsService, ConfigurationService, languageId);
            if (string.IsNullOrWhiteSpace(voiceId))
            {
                ErrorMessage = Resources.TextAssistNoVoice;
                return;
            }
            var stream = await _ttsService.StreamAsync(text, voiceId);
            if (stream != null) _audioPlayer.Enqueue(stream);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (source) IsSourceSpeaking = false; else IsResultSpeaking = false;
        }
    }
}

public sealed class TextAssistCorrectionViewModel : TextAssistEditorViewModel
{
    private string _inputText = string.Empty;
    private string _correctedResult = string.Empty;

    public TextAssistCorrectionViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ILogger? logger) : base(configurationService, profileResolver, textAssistService, true, logger)
    {
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (string.Equals(_inputText, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _inputText, value);
            Issues.Clear();
            CorrectionSegments.Clear();
            CorrectionVariants.Clear();
            CorrectedResult = string.Empty;
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            this.RaisePropertyChanged(nameof(HasCorrectedResults));
        }
    }

    public string CorrectedResult
    {
        get => _correctedResult;
        private set => this.RaiseAndSetIfChanged(ref _correctedResult, value);
    }

    public ObservableCollection<CorrectionVariant> CorrectionVariants { get; } = [];
    public bool HasCorrectedResults => CorrectionVariants.Count > 0;

    public ObservableCollection<TextAssistIssueEvent> Issues { get; } = [];
    public ObservableCollection<CorrectionTextSegment> CorrectionSegments { get; } = [];
    public bool HasCorrectionIssues => Issues.Count > 0;

    protected override async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;
        CorrectedResult = string.Empty;
        Issues.Clear();
        CorrectionSegments.Clear();
        CorrectionVariants.Clear();
        this.RaisePropertyChanged(nameof(HasCorrectedResults));
        var profile = ResolveProfile();
        var accumulator = new TextAssistCorrectionAccumulator(InputText.Length);
        await foreach (var item in TextAssistService.StreamCorrectAsync(InputText, profile, cancellationToken))
        {
            accumulator.Apply(item);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CorrectedResult = accumulator.CorrectedText;
                CorrectionVariants.Clear();
                foreach (var pair in accumulator.CorrectedVariants.OrderBy(x => x.Key))
                {
                    accumulator.CorrectedTranslations.TryGetValue(pair.Key, out var translation);
                    CorrectionVariants.Add(new CorrectionVariant(pair.Value, translation ?? string.Empty));
                }
                this.RaisePropertyChanged(nameof(HasCorrectedResults));
                Issues.Clear();
                foreach (var issue in accumulator.Issues) Issues.Add(issue);
                RebuildCorrectionSegments();
            });
        }
        accumulator.CompleteImplicitly();
        accumulator.EnsureComplete();
    }

    private void RebuildCorrectionSegments()
    {
        CorrectionSegments.Clear();
        var source = InputText;
        var cursor = 0;
        foreach (var issue in Issues.OrderBy(x => x.Start))
        {
            if (issue.Start < cursor || issue.Start < 0 || issue.Start >= source.Length) continue;
            if (issue.Start > cursor) CorrectionSegments.Add(new CorrectionTextSegment(source[cursor..issue.Start], false, null));
            var end = Math.Min(source.Length, issue.Start + issue.Length);
            if (end > issue.Start)
            {
                CorrectionSegments.Add(new CorrectionTextSegment(source[issue.Start..end], true,
                    $"{issue.Message}\n{issue.Suggestion}"));
                cursor = end;
            }
        }
        if (cursor < source.Length) CorrectionSegments.Add(new CorrectionTextSegment(source[cursor..], false, null));
        if (CorrectionSegments.Count == 0 && source.Length > 0)
            CorrectionSegments.Add(new CorrectionTextSegment(source, false, null));
        this.RaisePropertyChanged(nameof(HasCorrectionIssues));
    }
}

public sealed record CorrectionTextSegment(string Text, bool IsIssue, string? Suggestion);
public sealed record CorrectionVariant(string Text, string Translation);
