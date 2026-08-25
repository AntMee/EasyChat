using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EasyChat.Contracts.Input;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Translation;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Shared.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Input;

public sealed class TypingViewModel : ReactiveObject, IDisposable
{
    private readonly ExternalTargetToken _target;
    private readonly ShortcutParameterSettings? _shortcut;
    private readonly SettingsSession _settings;
    private readonly IInputTranslationUseCases _inputTranslation;
    private readonly ITranslationWindowCoordinator _translationWindows;
    private readonly ILogger<TypingViewModel> _logger;
    private LanguageSettings? _selectedSourceLanguage;
    private LanguageSettings? _selectedTargetLanguage;
    private bool _followGlobalLanguage;
    private string _inputText = string.Empty;
    private string _previewTranslation = string.Empty;
    private string _previewSourceLanguageId = "auto";
    private string _previewTargetLanguageId = "zh-Hans";
    private string _pendingPreviewSourceLanguageId = "auto";
    private string _pendingPreviewTargetLanguageId = "zh-Hans";
    private string _previewText = string.Empty;
    private string? _previewRequestedText;
    private string _previewError = string.Empty;
    private bool _isPreviewLoading;
    private bool _isPreviewCompleted;
    private bool _previewResultStarted;
    private int _previewGeneration;
    private CancellationTokenSource? _previewCancellation;
    private Task? _previewTask;
    private ObservableCollection<TextToken> _previewTokens = [];
    private IReadOnlyDictionary<string, string> _previewWordOverviews =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreviewWordInfo> _previewWords =
        new(StringComparer.OrdinalIgnoreCase);

    public TypingViewModel(
        ExternalTargetToken target,
        ShortcutParameterSettings? shortcut,
        SettingsSession settings,
        TranslationLanguageOptions languages,
        IInputTranslationUseCases inputTranslation,
        ITranslationWindowCoordinator translationWindows,
        ILogger<TypingViewModel> logger)
    {
        _target = target;
        _shortcut = shortcut;
        _settings = settings;
        _inputTranslation = inputTranslation;
        _translationWindows = translationWindows;
        _logger = logger;
        SourceLanguages = languages.All;
        TargetLanguages = languages.All;
        SwapLanguagesCommand = ReactiveCommand.Create(SwapLanguages);
        PreviewWordClickedCommand = ReactiveCommand.CreateFromTask<string>(LookupPreviewWordAsync);
        UpdateFromSettings();
        settings.Changed += OnSettingsChanged;
    }

    public ReactiveCommand<Unit, Unit> SwapLanguagesCommand { get; }
    public ReactiveCommand<string, Unit> PreviewWordClickedCommand { get; }
    public event EventHandler? PreviewDictionaryShown;
    public event EventHandler? PreviewDictionaryOpenFailed;
    public IReadOnlyList<LanguageSettings> SourceLanguages { get; }
    public IReadOnlyList<LanguageSettings> TargetLanguages { get; }

    public LanguageSettings? SelectedSourceLanguage
    {
        get => _followGlobalLanguage ? EffectiveGlobalSourceLanguage() : _selectedSourceLanguage;
        set
        {
            if (_followGlobalLanguage || _selectedSourceLanguage == value)
                return;
            this.RaiseAndSetIfChanged(ref _selectedSourceLanguage, value);
            if (value is not null)
                _settings.Input.TypingSourceLanguage = value.Id;
            InvalidatePreviewDirection();
        }
    }

    public LanguageSettings? SelectedTargetLanguage
    {
        get => _followGlobalLanguage ? EffectiveGlobalTargetLanguage() : _selectedTargetLanguage;
        set
        {
            if (_followGlobalLanguage || _selectedTargetLanguage == value)
                return;
            this.RaiseAndSetIfChanged(ref _selectedTargetLanguage, value);
            if (value is not null)
                _settings.Input.TypingTargetLanguage = value.Id;
            InvalidatePreviewDirection();
        }
    }

    public bool FollowGlobalLanguage
    {
        get => _followGlobalLanguage;
        set
        {
            if (_followGlobalLanguage == value)
                return;
            _followGlobalLanguage = value;
            this.RaisePropertyChanged();
            _settings.Input.FollowGlobalLanguage = value;
            RaiseLanguageProperties();
            InvalidatePreviewDirection();
        }
    }

    public bool IsPreviewEnabled
    {
        get => _settings.Input.IsPreviewEnabled;
        set
        {
            if (_settings.Input.IsPreviewEnabled == value)
                return;
            _settings.Input.IsPreviewEnabled = value;
            this.RaisePropertyChanged();
            if (value)
                SchedulePreview(_inputText);
            else
                ClearPreview();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (_inputText == value)
                return;
            this.RaiseAndSetIfChanged(ref _inputText, value);
            SchedulePreview(value);
        }
    }

    public string PreviewTranslation
    {
        get => _previewTranslation;
        private set
        {
            this.RaiseAndSetIfChanged(ref _previewTranslation, value);
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    }

    public ObservableCollection<TextToken> PreviewTokens
    {
        get => _previewTokens;
        private set => this.RaiseAndSetIfChanged(ref _previewTokens, value);
    }

    public IReadOnlyDictionary<string, string> PreviewWordOverviews
    {
        get => _previewWordOverviews;
        private set => this.RaiseAndSetIfChanged(ref _previewWordOverviews, value);
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => this.RaiseAndSetIfChanged(ref _isPreviewLoading, value);
    }

    public bool HasPreview => !string.IsNullOrEmpty(PreviewTranslation);

    public string PreviewError
    {
        get => _previewError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _previewError, value);
            this.RaisePropertyChanged(nameof(HasPreviewError));
        }
    }

    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);

    public ValueTask<bool> IsDictionaryWindowVisibleAsync(CancellationToken cancellationToken = default) =>
        _translationWindows.IsVisibleAsync(cancellationToken);

    public async Task<bool> TranslateAndSendAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (IsPreviewEnabled)
        {
            var previewReady = await EnsurePreviewAsync(text, cancellationToken).ConfigureAwait(false);
            if (!previewReady)
                return false;

            var previewResult = await _inputTranslation.DeliverTranslatedAsync(
                new InputTranslatedDeliveryRequest(
                    PreviewTranslation,
                    _target,
                    _shortcut?.ReplaceCurrentInput ?? false,
                    _shortcut?.InputTranslateBeforeKey,
                    _shortcut?.InputTranslateAfterKey),
                cancellationToken).ConfigureAwait(false);
            if (previewResult.IsFailure)
                _logger.LogWarning("Input preview delivery failed: {Error}", previewResult.Error.Message);
            return !previewResult.IsFailure;
        }

        var sourceId = FollowGlobalLanguage ? null : _selectedSourceLanguage?.Id;
        var targetId = FollowGlobalLanguage ? null : _selectedTargetLanguage?.Id;
        var result = await _inputTranslation.TranslateAndDeliverAsync(
            new InputTranslationRequest(
                text,
                _target,
                sourceId,
                targetId,
                _shortcut?.ReplaceCurrentInput ?? false,
                _shortcut?.InputTranslateBeforeKey,
                _shortcut?.InputTranslateAfterKey),
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            _logger.LogWarning("Input translation failed: {Error}", result.Error.Message);
        return !result.IsFailure;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
    }

    private void SwapLanguages()
    {
        if (FollowGlobalLanguage)
            return;

        var source = _selectedSourceLanguage;
        _selectedSourceLanguage = _selectedTargetLanguage;
        _selectedTargetLanguage = source;
        if (_selectedSourceLanguage is not null)
            _settings.Input.TypingSourceLanguage = _selectedSourceLanguage.Id;
        if (_selectedTargetLanguage is not null)
            _settings.Input.TypingTargetLanguage = _selectedTargetLanguage.Id;
        RaiseLanguageProperties();
        InvalidatePreviewDirection();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs eventArgs)
    {
        if (eventArgs.Section is SettingsSection.Input or SettingsSection.General)
            UpdateFromSettings();
    }

    private void UpdateFromSettings()
    {
        _followGlobalLanguage = _settings.Input.FollowGlobalLanguage;
        _selectedSourceLanguage = SourceLanguages.FirstOrDefault(language => language.Id == _settings.Input.TypingSourceLanguage)
                                  ?? SourceLanguages.FirstOrDefault();
        _selectedTargetLanguage = TargetLanguages.FirstOrDefault(language => language.Id == _settings.Input.TypingTargetLanguage)
                                  ?? TargetLanguages.FirstOrDefault();
        RaiseLanguageProperties();
        this.RaisePropertyChanged(nameof(FollowGlobalLanguage));
        this.RaisePropertyChanged(nameof(IsPreviewEnabled));
        if (IsPreviewEnabled && !string.IsNullOrEmpty(_inputText))
            InvalidatePreviewDirection();
        else if (!IsPreviewEnabled)
            ClearPreview();
    }

    private void RaiseLanguageProperties()
    {
        this.RaisePropertyChanged(nameof(SelectedSourceLanguage));
        this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
    }

    private LanguageSettings? EffectiveGlobalSourceLanguage()
    {
        var sourceId = _settings.General.SourceLanguage.Id;
        var targetId = _settings.General.TargetLanguage.Id;
        if (_settings.Input.ReverseTranslateLanguage)
            (sourceId, targetId) = (targetId, sourceId);
        return SourceLanguages.FirstOrDefault(language => language.Id == sourceId)
               ?? SourceLanguages.FirstOrDefault();
    }

    private LanguageSettings? EffectiveGlobalTargetLanguage()
    {
        var sourceId = _settings.General.SourceLanguage.Id;
        var targetId = _settings.General.TargetLanguage.Id;
        if (_settings.Input.ReverseTranslateLanguage)
            (sourceId, targetId) = (targetId, sourceId);
        return TargetLanguages.FirstOrDefault(language => language.Id == targetId)
               ?? TargetLanguages.FirstOrDefault();
    }

    private void InvalidatePreviewDirection()
    {
        _previewRequestedText = null;
        if (IsPreviewEnabled)
            SchedulePreview(_inputText);
    }

    private void SchedulePreview(string text)
    {
        if (!IsPreviewEnabled)
            return;
        if (string.IsNullOrEmpty(text))
        {
            ClearPreview();
            return;
        }
        if (string.Equals(_previewRequestedText, text, StringComparison.Ordinal))
            return;

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;
        var generation = ++_previewGeneration;
        _previewRequestedText = text;
        _previewTask = RunPreviewAsync(text, token, TimeSpan.FromMilliseconds(300), generation);
    }

    private async Task<bool> EnsurePreviewAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsPreviewEnabled)
            return false;
        if (!string.Equals(_previewText, text, StringComparison.Ordinal)
            || !_isPreviewCompleted)
        {
            if (!string.Equals(_previewRequestedText, text, StringComparison.Ordinal))
            {
                _previewCancellation?.Cancel();
                _previewCancellation?.Dispose();
                _previewCancellation = new CancellationTokenSource();
                var generation = ++_previewGeneration;
                _previewRequestedText = text;
                _previewTask = RunPreviewAsync(text, _previewCancellation.Token, TimeSpan.Zero, generation);
            }

            if (_previewTask is not null)
                await _previewTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return string.Equals(_previewText, text, StringComparison.Ordinal)
               && _isPreviewCompleted
               && !string.IsNullOrWhiteSpace(PreviewTranslation);
    }

    private async Task RunPreviewAsync(
        string text,
        CancellationToken cancellationToken,
        TimeSpan delay,
        int generation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                if (generation != _previewGeneration || !string.Equals(_inputText, text, StringComparison.Ordinal))
                    return;
                PreviewError = string.Empty;
                _isPreviewCompleted = false;
                _previewResultStarted = false;
                IsPreviewLoading = true;
            }, cancellationToken).ConfigureAwait(false);

            // Let the application resolve the global direction, including its optional reversal.
            var sourceId = FollowGlobalLanguage ? null : _selectedSourceLanguage?.Id;
            var targetId = FollowGlobalLanguage ? null : _selectedTargetLanguage?.Id;
            await foreach (var item in _inputTranslation.StreamPreviewAsync(
                               new InputTranslationPreviewRequest(text, sourceId, targetId),
                               cancellationToken).ConfigureAwait(false))
            {
                await OnUiAsync(() => ApplyPreviewEvent(item, text, generation), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await OnUiAsync(() =>
            {
                if (generation != _previewGeneration || !string.Equals(_inputText, text, StringComparison.Ordinal))
                    return;
                PreviewError = exception.Message;
                IsPreviewLoading = false;
                _isPreviewCompleted = false;
            }, CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(exception, "Input preview failed");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await OnUiAsync(() =>
                {
                    if (generation == _previewGeneration
                        && string.Equals(_inputText, text, StringComparison.Ordinal))
                    {
                        IsPreviewLoading = false;
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void ApplyPreviewEvent(InputTranslationPreviewEvent item, string text, int generation)
    {
        if (generation != _previewGeneration
            || !string.Equals(_inputText, text, StringComparison.Ordinal))
        {
            return;
        }

        switch (item)
        {
            case InputTranslationPreviewStartedEvent started:
                _pendingPreviewSourceLanguageId = started.SourceLanguageId;
                _pendingPreviewTargetLanguageId = started.TargetLanguageId;
                break;
            case InputTranslationPreviewSourceDetectedEvent detected:
                if (!string.IsNullOrWhiteSpace(detected.LanguageId))
                    _pendingPreviewSourceLanguageId = detected.LanguageId;
                break;
            case InputTranslationPreviewDeltaEvent delta:
                BeginPreviewResult();
                PreviewTranslation += delta.Text;
                PreviewTokens = new ObservableCollection<TextToken>(
                    TranslationTextTokenizer.Tokenize(PreviewTranslation, _previewTargetLanguageId));
                EnsureFallbackWordOverviews();
                break;
            case InputTranslationPreviewWordEvent word:
                BeginPreviewResult();
                AddWordOverview(word);
                break;
            case InputTranslationPreviewCompletedEvent:
                _previewText = _previewResultStarted ? text : string.Empty;
                _isPreviewCompleted = _previewResultStarted && !string.IsNullOrWhiteSpace(PreviewTranslation);
                break;
            case InputTranslationPreviewFailedEvent failed:
                PreviewError = failed.Error.Message;
                _isPreviewCompleted = false;
                break;
        }
    }

    private void BeginPreviewResult()
    {
        if (_previewResultStarted)
            return;

        // Preserve the previous result until the first event for this request arrives.
        PreviewTranslation = string.Empty;
        PreviewTokens = [];
        _previewWords.Clear();
        PreviewWordOverviews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _previewSourceLanguageId = _pendingPreviewSourceLanguageId;
        _previewTargetLanguageId = _pendingPreviewTargetLanguageId;
        _previewText = string.Empty;
        _previewResultStarted = true;
    }

    private void AddWordOverview(InputTranslationPreviewWordEvent wordInfo)
    {
        if (string.IsNullOrWhiteSpace(wordInfo.Word))
            return;

        var word = wordInfo.Word;
        if (_previewWords.TryGetValue(word, out var existing))
        {
            word = existing.Word;
            wordInfo = new InputTranslationPreviewWordEvent(
                word,
                PreferValue(wordInfo.Meaning, existing.Meaning),
                PreferValue(wordInfo.Phonetic, existing.Phonetic),
                PreferValue(wordInfo.PartOfSpeech, existing.PartOfSpeech),
                MergeValues(existing.Forms, wordInfo.Forms),
                MergeValues(existing.Meanings, wordInfo.Meanings));
        }

        _previewWords[word] = new PreviewWordInfo(
            word,
            wordInfo.Meaning,
            wordInfo.Phonetic,
            wordInfo.PartOfSpeech,
            MergeValues([], wordInfo.Forms),
            MergeValues([], wordInfo.Meanings));
        var meanings = MergeValues(
            string.IsNullOrWhiteSpace(wordInfo.Meaning) ? [] : [wordInfo.Meaning],
            wordInfo.Meanings);
        var overview = string.Join(
            Environment.NewLine,
            new[]
            {
                word,
                string.IsNullOrWhiteSpace(wordInfo.Phonetic) ? null : $"{Resources.PreviewPhonetic}: {wordInfo.Phonetic}",
                string.IsNullOrWhiteSpace(wordInfo.PartOfSpeech) ? null : $"{Resources.PreviewPartOfSpeech}: {wordInfo.PartOfSpeech}",
                wordInfo.Forms is { Count: > 0 }
                    ? $"{Resources.PreviewForms}: {string.Join(", ", wordInfo.Forms)}"
                    : null,
                meanings.Count == 0 ? null : $"{Resources.PreviewMeaning}: {string.Join("; ", meanings)}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(overview))
            return;
        var updated = new Dictionary<string, string>(PreviewWordOverviews, StringComparer.OrdinalIgnoreCase)
        {
            [word] = overview
        };
        PreviewWordOverviews = updated;
    }

    private void EnsureFallbackWordOverviews()
    {
        if (PreviewTokens.Count == 0)
            return;

        var updated = new Dictionary<string, string>(PreviewWordOverviews, StringComparer.OrdinalIgnoreCase);
        foreach (var token in PreviewTokens.Where(token => token.IsWord))
        {
            if (!updated.ContainsKey(token.Text))
                updated[token.Text] = token.Text;
        }

        if (updated.Count != PreviewWordOverviews.Count)
            PreviewWordOverviews = updated;
    }

    private static string? PreferValue(string? current, string? previous) =>
        string.IsNullOrWhiteSpace(current) ? previous : current;

    private static IReadOnlyList<string> MergeValues(
        IReadOnlyList<string> previous,
        IReadOnlyList<string>? current)
    {
        return previous
            .Concat(current ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task LookupPreviewWordAsync(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        try
        {
            await _translationWindows.ShowDictionaryAsync(
                word,
                _previewTargetLanguageId,
                _previewSourceLanguageId,
                centerOnScreen: true);
            PreviewDictionaryShown?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to open preview dictionary for {Word}", word);
            PreviewDictionaryOpenFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearPreview()
    {
        ++_previewGeneration;
        _previewCancellation?.Cancel();
        PreviewTranslation = string.Empty;
        PreviewTokens = [];
        _previewWords.Clear();
        PreviewWordOverviews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        PreviewError = string.Empty;
        _previewText = string.Empty;
        _previewRequestedText = null;
        _isPreviewCompleted = false;
        _previewResultStarted = false;
        _pendingPreviewSourceLanguageId = "auto";
        _pendingPreviewTargetLanguageId = "zh-Hans";
        IsPreviewLoading = false;
    }

    private sealed record PreviewWordInfo(
        string Word,
        string? Meaning,
        string? Phonetic,
        string? PartOfSpeech,
        IReadOnlyList<string> Forms,
        IReadOnlyList<string> Meanings);

    private static async Task OnUiAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }
}
