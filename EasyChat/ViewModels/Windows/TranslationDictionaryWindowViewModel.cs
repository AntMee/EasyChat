using System;
using EasyChat.Services.Languages;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Lang;
using EasyChat.Models.Translation.Selection;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Speech.Tts;
using EasyChat.Services.Text;
using EasyChat.Services.Translation.Selection;
using ReactiveUI;

namespace EasyChat.ViewModels.Windows;

public class TranslationDictionaryWindowViewModel : ViewModelBase
{
    private readonly ISelectionTranslationProvider _translationProvider;
    private readonly AiSelectionTranslationProvider _dictionaryProvider;
    private readonly IConfigurationService _configurationService;
    private readonly ITtsService _ttsService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly ITokenizerFactory _tokenizerFactory;
    private TaskCompletionSource<bool>? _initializationTcs;
    private bool _isShowingLookupResult;
    private int _loadingOperationCount;

    private string _sourceText = string.Empty;
    public string SourceText
    {
        get => _sourceText;
        set => this.RaiseAndSetIfChanged(ref _sourceText, value);
    }

    private string _translationResult = string.Empty;
    public string TranslationResult
    {
        get => _translationResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _translationResult, value);
            this.RaisePropertyChanged(nameof(ShowTranslationSkeleton));
        }
    }

    private string? _sentenceTranslationSnapshot;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(ShowDictionarySkeleton));
            this.RaisePropertyChanged(nameof(ShowTranslationSkeleton));
        }
    }

    private bool _isWordMode;
    public bool IsWordMode
    {
        get => _isWordMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isWordMode, value);
            this.RaisePropertyChanged(nameof(ShowDictionarySkeleton));
            this.RaisePropertyChanged(nameof(ShowTranslationSkeleton));
        }
    }

    private DictionaryResult? _dictionaryResult;
    public DictionaryResult? DictionaryResult
    {
        get => _dictionaryResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _dictionaryResult, value);
            this.RaisePropertyChanged(nameof(ShowDictionarySkeleton));
        }
    }

    public bool ShowDictionarySkeleton =>
        IsWordMode && IsLoading && (DictionaryResult is null || DictionaryResult.Parts.Count == 0);

    public bool ShowTranslationSkeleton =>
        !IsWordMode && IsLoading && string.IsNullOrEmpty(TranslationResult);

    private ObservableCollection<TextToken> _sourceTokens = [];
    public ObservableCollection<TextToken> SourceTokens
    {
        get => _sourceTokens;
        set => this.RaiseAndSetIfChanged(ref _sourceTokens, value);
    }

    // Independent Loading States for Main UI Elements
    private bool _isWordTtsLoading;
    public bool IsWordTtsLoading
    {
        get => _isWordTtsLoading;
        set => this.RaiseAndSetIfChanged(ref _isWordTtsLoading, value);
    }

    private bool _isSourceTtsLoading;
    public bool IsSourceTtsLoading
    {
        get => _isSourceTtsLoading;
        set => this.RaiseAndSetIfChanged(ref _isSourceTtsLoading, value);
    }

    private bool _isResultTtsLoading;
    public bool IsResultTtsLoading
    {
        get => _isResultTtsLoading;
        set => this.RaiseAndSetIfChanged(ref _isResultTtsLoading, value);
    }

    public ReactiveCommand<string, Unit> LookupWordCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToSentenceModeCommand { get; }
    public ReactiveCommand<object?, Unit> PlayTtsCommand { get; }
    public ReactiveCommand<object?, Unit> PlaySourceAudioCommand { get; }
    public ReactiveCommand<object?, Unit> PlayTargetAudioCommand { get; }

    private bool _canNavigateBack;

    private bool _showBackButton;
    public bool ShowBackButton
    {
        get => _showBackButton;
        set => this.RaiseAndSetIfChanged(ref _showBackButton, value);
    }

    private bool _showCloseButton;
    public bool ShowCloseButton
    {
        get => _showCloseButton;
        set => this.RaiseAndSetIfChanged(ref _showCloseButton, value);
    }

    private bool _isScreenshotMode;
    public bool IsScreenshotMode
    {
        get => _isScreenshotMode;
        set => this.RaiseAndSetIfChanged(ref _isScreenshotMode, value);
    }

    public TranslationDictionaryWindowViewModel(
        ISelectionTranslationProvider translationProvider,
        AiSelectionTranslationProvider dictionaryProvider,
        IConfigurationService configurationService,
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        ITokenizerFactory tokenizerFactory)
    {
        _translationProvider = translationProvider;
        _dictionaryProvider = dictionaryProvider;
        _configurationService = configurationService;
        _ttsService = ttsService;
        _audioPlayer = audioPlayer;
        _tokenizerFactory = tokenizerFactory;

        LookupWordCommand = ReactiveCommand.CreateFromTask<string>(LookupWordAsync);
        SwitchToSentenceModeCommand = ReactiveCommand.Create(SwitchToSentenceMode);
        PlayTtsCommand = ReactiveCommand.CreateFromTask<object?>(PlayTtsAsync);
        PlaySourceAudioCommand = ReactiveCommand.CreateFromTask<object?>(PlaySourceAudioAsync);
        PlayTargetAudioCommand = ReactiveCommand.CreateFromTask<object?>(PlayTargetAudioAsync);

        // React to SourceText changes
        this.WhenAnyValue(x => x.SourceText)
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Select(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }
                
                // Use factory to get appropriate tokenizer
                var tokenizer = _tokenizerFactory.GetTokenizer(_currentSourceLang);
                var tokens = tokenizer.Tokenize(text);
                
                return new { Text = text, Tokens = tokens };
            })
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(result =>
            {
                if (result == null)
                {
                    SourceTokens = new ObservableCollection<TextToken>();
                    IsWordMode = false;
                    _initializationTcs?.TrySetResult(true);
                    return;
                }

                SourceTokens = new ObservableCollection<TextToken>(result.Tokens);
            });
            
        // Update ShowBackButton when mode changes
        this.WhenAnyValue(x => x.IsWordMode)
            .Subscribe(_ => UpdateShowBackButton());
    }
    
    /// <summary>
    /// Initializes the ViewModel with source text and waits for data processing to complete.
    /// This allows the caller to await until the UI is ready to be displayed.
    /// </summary>
    public async Task InitializeAsync(string text)
    {
        var source = _configurationService.General?.SourceLanguage.Id ?? LanguageKeys.AutoId;
        var target = _configurationService.General?.TargetLanguage.Id ?? "zh-Hans";
        await InitializeAsync(text, source, target);
    }

    public async Task InitializeAsync(string text, string sourceLanguageId, string targetLanguageId)
    {
        _initializationTcs = new TaskCompletionSource<bool>();
        _currentSourceLang = string.IsNullOrWhiteSpace(sourceLanguageId) ? LanguageKeys.AutoId : sourceLanguageId;
        _currentTargetLang = string.IsNullOrWhiteSpace(targetLanguageId) ? "zh-Hans" : targetLanguageId;
        SourceText = text;
        
        BeginLoading();

        try 
        {
            await PerformTranslationAsync(text);
        }
        catch (Exception ex)
        {
            // Error handling (maybe show error state)
            TranslationResult = ex.Message.Contains("No active AI model", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No AI model", StringComparison.OrdinalIgnoreCase)
                ? Resources.TextAssistNoAiModel
                : Resources.SelectionTranslate_Failed + ex.Message;
        }
        finally
        {
            EndLoading();
            _initializationTcs.TrySetResult(true);
        }
    }

    public async Task InitializeDictionaryAsync(string text, string sourceLanguageId, string targetLanguageId)
    {
        _initializationTcs = new TaskCompletionSource<bool>();
        _currentSourceLang = string.IsNullOrWhiteSpace(sourceLanguageId) ? LanguageKeys.AutoId : sourceLanguageId;
        _currentTargetLang = string.IsNullOrWhiteSpace(targetLanguageId) ? "zh-Hans" : targetLanguageId;
        SourceText = text;
        _isShowingLookupResult = true;
        IsWordMode = true;
        _canNavigateBack = false;
        TranslationResult = string.Empty;
        DictionaryResult = new DictionaryResult { Word = text };
        BeginLoading();

        try
        {
            await PerformTranslationAsync(text, isLookup: true, forceDictionary: true);
        }
        catch (Exception ex)
        {
            TranslationResult = ex.Message.Contains("No active AI model", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No AI model", StringComparison.OrdinalIgnoreCase)
                ? Resources.TextAssistNoAiModel
                : Resources.SelectionTranslate_Failed + ex.Message;
        }
        finally
        {
            EndLoading();
            _initializationTcs.TrySetResult(true);
        }
    }

    private string _currentSourceLang = "en";
    private string _currentTargetLang = "zh-CN";

    private async Task PerformTranslationAsync(
        string text,
        bool canNavigateBack = false,
        bool isLookup = false,
        bool forceDictionary = false)
    {
        var sourceDefinition = LanguageService.GetLanguage(_currentSourceLang);
        var targetDefinition = LanguageService.GetLanguage(_currentTargetLang);
        var sourceLang = sourceDefinition.EnglishName;
        var targetLang = targetDefinition.EnglishName;

        if (sourceLang == LanguageKeys.Auto.EnglishName) _currentSourceLang = "en"; // Default fallback for TTS if auto?

        var stream = forceDictionary
            ? _dictionaryProvider.StreamLookupWordAsync(text, sourceLang, targetLang)
            : _translationProvider.StreamTranslateAsync(text, sourceLang, targetLang);
        await foreach (var translationEvent in stream)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ApplyStreamingEvent(translationEvent, canNavigateBack, isLookup));
        }
    }

    private void ApplyStreamingEvent(SelectionTranslationStreamEvent translationEvent, bool canNavigateBack, bool isLookup)
    {
        switch (translationEvent)
        {
            case SelectionTranslationStartedEvent started:
                // The initial sentence request may still be streaming while a word lookup
                // is open. Keep collecting it in the background without stealing the view.
                if (!isLookup && _isShowingLookupResult && started.Mode == SelectionTranslationMode.Sentence)
                {
                    _sentenceTranslationSnapshot = string.Empty;
                    break;
                }

                IsWordMode = started.Mode == SelectionTranslationMode.Word;
                _canNavigateBack = IsWordMode ? canNavigateBack : true;
                TranslationResult = string.Empty;
                var pendingWord = IsWordMode ? DictionaryResult?.Word : null;
                DictionaryResult = IsWordMode ? new DictionaryResult { Word = pendingWord ?? string.Empty } : null;
                if (!IsWordMode && !isLookup)
                {
                    _sentenceTranslationSnapshot = string.Empty;
                }
                break;
            case SelectionTranslationSourceDetectedEvent detected:
                UpdateDetectedSourceLanguage(detected.Language);
                break;
            case SelectionTranslationDeltaEvent delta:
                if (!isLookup)
                {
                    _sentenceTranslationSnapshot = (_sentenceTranslationSnapshot ?? string.Empty) + delta.Text;
                    if (!IsWordMode)
                    {
                        TranslationResult = _sentenceTranslationSnapshot;
                    }
                }
                else
                {
                    TranslationResult += delta.Text;
                }
                break;
            case SelectionTranslationWordHeaderEvent header:
                EnsureDictionaryResult().Word = header.Word;
                EnsureDictionaryResult().Phonetic = header.Phonetic ?? string.Empty;
                break;
            case SelectionTranslationDefinitionEvent definition:
                var dictionary = EnsureDictionaryResult();
                var part = dictionary.Parts.FirstOrDefault(item => item.PartOfSpeech == (definition.Pos ?? string.Empty));
                if (part == null)
                {
                    part = new DictionaryPart { PartOfSpeech = definition.Pos ?? string.Empty };
                    dictionary.Parts.Add(part);
                }
                part.Definitions.Add(definition.Meaning);
                this.RaisePropertyChanged(nameof(ShowDictionarySkeleton));
                break;
            case SelectionTranslationFormEvent form:
                EnsureDictionaryResult().Forms.Add(new DictionaryForm { Label = form.Label, Word = form.Word });
                break;
            case SelectionTranslationTipsEvent tips:
                EnsureDictionaryResult().Tips = tips.Text;
                break;
            case SelectionTranslationExampleEvent example:
                EnsureDictionaryResult().Examples.Add(new DictionaryExample
                {
                    Origin = example.Origin,
                    Translation = example.Translation
                });
                break;
        }
    }

    private DictionaryResult EnsureDictionaryResult()
    {
        return DictionaryResult ??= new DictionaryResult();
    }

    private void UpdateDetectedSourceLanguage(string detected)
    {
        if (string.IsNullOrWhiteSpace(detected) || _currentSourceLang == detected)
        {
            return;
        }

        _currentSourceLang = detected;
        var tokenizer = _tokenizerFactory.GetTokenizer(_currentSourceLang);
        SourceTokens = new ObservableCollection<TextToken>(tokenizer.Tokenize(SourceText));
    }

    private void UpdateShowBackButton()
    {
        ShowBackButton = IsWordMode && _canNavigateBack;
    }

    private async Task LookupWordAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;

        // Switch immediately so the clicked word is visible while the lookup runs.
        _sentenceTranslationSnapshot = TranslationResult;
        _isShowingLookupResult = true;
        IsWordMode = true;
        _canNavigateBack = true;
        TranslationResult = string.Empty;
        DictionaryResult = new DictionaryResult { Word = word };
        BeginLoading();
        
        try
        {
            await PerformTranslationAsync(word, canNavigateBack: true, isLookup: true);
        }
        catch (Exception ex)
        {
            TranslationResult = ex.Message.Contains("No active AI model", StringComparison.OrdinalIgnoreCase)
                ? Resources.TextAssistNoAiModel
                : Resources.SelectionTranslate_Failed + ex.Message;
        }
        finally
        {
            EndLoading();
        }
    }
    
    private void SwitchToSentenceMode()
    {
        _isShowingLookupResult = false;
        IsWordMode = false;
        if (_sentenceTranslationSnapshot != null)
        {
            TranslationResult = _sentenceTranslationSnapshot;
        }
    }

    private void BeginLoading()
    {
        _loadingOperationCount++;
        IsLoading = true;
    }

    private void EndLoading()
    {
        _loadingOperationCount = Math.Max(0, _loadingOperationCount - 1);
        IsLoading = _loadingOperationCount > 0;
    }

    private async Task PlayTtsAsync(object? parameter)
    {
        string? textToSpeak;
        string langId;
        Action<bool>? setLoading = null;

        if (parameter is string text)
        {
            textToSpeak = text;
            langId = _currentSourceLang;
            setLoading = val => IsWordTtsLoading = val;
        }
        else if (IsWordMode && DictionaryResult != null)
        {
            textToSpeak = DictionaryResult.Word;
            langId = _currentSourceLang;
            setLoading = val => IsWordTtsLoading = val;
        }
        else
        {
            textToSpeak = TranslationResult;
            langId = _currentTargetLang;
            // No specific loading indicator for generic fallback yet, or reuse Result
        }

        await PlayTtsWithLanguageAsync(textToSpeak, langId, setLoading);
    }

    private async Task PlaySourceAudioAsync(object? parameter)
    {
        string? textToSpeak = null;
        Action<bool>? setLoading = null;

        if (parameter is DictionaryForm form)
        {
            textToSpeak = form.Word;
            setLoading = val => form.IsLoading = val;
        }
        else if (parameter is DictionaryExample example)
        {
            textToSpeak = example.Origin;
            setLoading = val => example.IsOriginLoading = val;
        }
        else if (parameter is string text)
        {
            textToSpeak = text;
            setLoading = val => IsSourceTtsLoading = val;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeak))
        {
            await PlayTtsWithLanguageAsync(textToSpeak, _currentSourceLang, setLoading);
        }
    }

    private async Task PlayTargetAudioAsync(object? parameter)
    {
        string? textToSpeak = null;
        Action<bool>? setLoading = null;

        if (parameter is DictionaryExample example)
        {
            textToSpeak = example.Translation;
            setLoading = val => example.IsTranslationLoading = val;
        }
        else if (parameter is string text)
        {
            textToSpeak = text;
            setLoading = val => IsResultTtsLoading = val;
        }

        if (!string.IsNullOrWhiteSpace(textToSpeak))
        {
            await PlayTtsWithLanguageAsync(textToSpeak, _currentTargetLang, setLoading);
        }
    }

    private async Task PlayTtsWithLanguageAsync(string? text, string langId, Action<bool>? setLoadingState)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            _audioPlayer.Stop(); // Ensure previous is stopped
            setLoadingState?.Invoke(true);

            // Get Voice
            var voiceId = TtsHelper.GetPreferredVoiceId(_ttsService, _configurationService, langId);

            if (voiceId != null)
            {
                var stream = await _ttsService.StreamAsync(text, voiceId);
                if (stream != null)
                {
                    _audioPlayer.Enqueue(stream);
                }
            }
        }
        catch (Exception)
        {
            // Ignore for now
        }
        finally
        {
            setLoadingState?.Invoke(false);
        }
    }
}
