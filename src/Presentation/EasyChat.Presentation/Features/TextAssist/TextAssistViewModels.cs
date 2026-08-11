using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Threading.Tasks;
using System.Text;
using Avalonia.Threading;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.TextAssist;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Translation;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Foundation.Navigation;
using LiveMarkdown.Avalonia;
using Material.Icons;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace EasyChat.Presentation.Features.TextAssist
{
    public sealed class TextAssistViewModel : NavigationPageViewModel
    {
        private bool _isCapturingInput;
        private int _selectedTabIndex;

        public TextAssistViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            ITranslationWindowCoordinator dictionary,
            ITtsUseCases tts,
            ILoggerFactory loggerFactory)
            : base(Resources.TextAssist, MaterialIconKind.Translate, 5)
        {
            Translation = new TextAssistTranslationViewModel(
                settings, languages, textAssist, dictionary, tts,
                loggerFactory.CreateLogger<TextAssistTranslationViewModel>());
            Correction = new TextAssistCorrectionViewModel(
                settings, languages, textAssist,
                loggerFactory.CreateLogger<TextAssistCorrectionViewModel>());
            SelectTranslationCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 0; });
            SelectCorrectionCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 1; });
        }

        public TextAssistTranslationViewModel Translation { get; }
        public TextAssistCorrectionViewModel Correction { get; }
        public ReactiveCommand<Unit, Unit> SelectTranslationCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectCorrectionCommand { get; }

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
        public bool IsCapturingInput { get => _isCapturingInput; private set => this.RaiseAndSetIfChanged(ref _isCapturingInput, value); }

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

        public void Cancel()
        {
            Translation.CancelCurrent();
            Correction.CancelCurrent();
        }
    }

    public sealed class TextAssistTranslationPageViewModel : NavigationPageViewModel
    {
        public TextAssistTranslationPageViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            ITranslationWindowCoordinator dictionary,
            ITtsUseCases tts,
            ILogger<TextAssistTranslationPageViewModel> logger)
            : base(Resources.TextAssistTranslate, MaterialIconKind.Translate, 5)
        {
            Translation = new TextAssistTranslationViewModel(settings, languages, textAssist, dictionary, tts, logger);
        }

        public TextAssistTranslationViewModel Translation { get; }
    }

    public sealed class TextAssistCorrectionPageViewModel : NavigationPageViewModel
    {
        public TextAssistCorrectionPageViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            ILogger<TextAssistCorrectionPageViewModel> logger)
            : base(Resources.TextAssistCorrect, MaterialIconKind.Spellcheck, 6)
        {
            Correction = new TextAssistCorrectionViewModel(settings, languages, textAssist, logger);
        }

        public TextAssistCorrectionViewModel Correction { get; }
    }

    public abstract class TextAssistEditorViewModel : ViewModelBase
    {
        private readonly SettingsSession _settings;
        private readonly bool _correction;
        private CancellationTokenSource? _request;
        private string _sourceLanguageId;
        private string _targetLanguageId;
        private string _provider;
        private CustomAiModelState? _selectedAiModel;
        private string _machineProvider;
        private string? _selectedPromptId;
        private bool _isConfigurationExpanded;
        private bool _isBusy;
        private bool _isActive;
        private string _errorMessage = string.Empty;

        protected TextAssistEditorViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            bool correction,
            ILogger logger)
        {
            _settings = settings;
            Settings = settings;
            TextAssist = textAssist;
            Logger = logger;
            _correction = correction;
            _isActive = !correction;
            Languages = languages.All.OrderBy(language => language.EnglishName).ToArray();
            AvailableAiModels = settings.AiModel.ConfiguredModels;
            PromptEntries = settings.Prompts.Entries;
            MachineProviders =
            [
                MachineTranslationProviderNames.Baidu,
                MachineTranslationProviderNames.Tencent,
                MachineTranslationProviderNames.Google,
                MachineTranslationProviderNames.DeepL
            ];

            var config = settings.TextAssist;
            config.FollowGlobal = false;
            _sourceLanguageId = config.SourceLanguageId;
            _targetLanguageId = config.TargetLanguageId;
            _provider = config.Provider;
            _selectedAiModel = AvailableAiModels.FirstOrDefault(model => model.Id == config.AiModelId)
                               ?? AvailableAiModels.FirstOrDefault();
            _machineProvider = config.MachineProvider;
            _selectedPromptId = correction ? config.CorrectionPromptId : config.TranslationPromptId;
            _selectedPromptId ??= settings.Prompts.SelectedPromptId;
            _selectedPromptId ??= PromptEntries.FirstOrDefault(prompt => prompt.IsDefault)?.Id;
            _isConfigurationExpanded = correction
                ? config.CorrectionConfigurationExpanded
                : config.TranslationConfigurationExpanded;

            RunCommand = ReactiveCommand.CreateFromTask(ExecuteAsync);
            CancelCommand = ReactiveCommand.Create(Cancel);
            ToggleConfigurationCommand = ReactiveCommand.Create(() => { IsConfigurationExpanded = !IsConfigurationExpanded; });
        }

        protected ITextAssistUseCases TextAssist { get; }
        protected SettingsSession Settings { get; }
        protected ILogger Logger { get; }
        public IReadOnlyList<LanguageSettings> Languages { get; }
        public ObservableCollection<CustomAiModelState> AvailableAiModels { get; }
        public ObservableCollection<PromptEntryState> PromptEntries { get; }
        public IReadOnlyList<string> MachineProviders { get; }
        public IReadOnlyList<string> AvailableProviders { get; } = [TranslationEngineNames.AiModel, TranslationEngineNames.MachineTrans];
        public ReactiveCommand<Unit, Unit> RunCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleConfigurationCommand { get; }

        public bool IsConfigurationExpanded
        {
            get => _isConfigurationExpanded;
            private set
            {
                if (_isConfigurationExpanded == value) return;
                this.RaiseAndSetIfChanged(ref _isConfigurationExpanded, value);
                this.RaisePropertyChanged(nameof(ConfigurationToggleIcon));
                this.RaisePropertyChanged(nameof(ConfigurationToggleText));
                if (_correction) _settings.TextAssist.CorrectionConfigurationExpanded = value;
                else _settings.TextAssist.TranslationConfigurationExpanded = value;
            }
        }

        public MaterialIconKind ConfigurationToggleIcon =>
            IsConfigurationExpanded ? MaterialIconKind.ChevronDoubleDown : MaterialIconKind.ChevronDoubleUp;
        public string ConfigurationToggleText =>
            IsConfigurationExpanded ? Resources.CollapseSettings : Resources.ExpandSettings;
        public bool IsActive { get => _isActive; set => this.RaiseAndSetIfChanged(ref _isActive, value); }
        public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
        public string ErrorMessage { get => _errorMessage; protected set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }

        public LanguageSettings SelectedSourceLanguage
        {
            get => Languages.FirstOrDefault(language => language.Id == _sourceLanguageId) ?? Languages.First(language => language.Id == "auto");
            set
            {
                _sourceLanguageId = value.Id;
                this.RaisePropertyChanged();
                _settings.TextAssist.SourceLanguageId = value.Id;
            }
        }

        public LanguageSettings SelectedTargetLanguage
        {
            get => Languages.FirstOrDefault(language => language.Id == _targetLanguageId) ?? Languages.First(language => language.Id == "zh-Hans");
            set
            {
                _targetLanguageId = value.Id;
                this.RaisePropertyChanged();
                _settings.TextAssist.TargetLanguageId = value.Id;
            }
        }

        public string SelectedProvider
        {
            get => _provider;
            set
            {
                if (_provider == value) return;
                this.RaiseAndSetIfChanged(ref _provider, value);
                this.RaisePropertyChanged(nameof(IsAiProvider));
                this.RaisePropertyChanged(nameof(IsMachineProvider));
                _settings.TextAssist.Provider = value;
            }
        }

        public bool IsAiProvider => SelectedProvider.Equals(TranslationEngineNames.AiModel, StringComparison.OrdinalIgnoreCase);
        public bool IsMachineProvider => SelectedProvider.Equals(TranslationEngineNames.MachineTrans, StringComparison.OrdinalIgnoreCase);

        public CustomAiModelState? SelectedAiModel
        {
            get => _selectedAiModel;
            set
            {
                if (ReferenceEquals(_selectedAiModel, value)) return;
                this.RaiseAndSetIfChanged(ref _selectedAiModel, value);
                _settings.TextAssist.AiModelId = value?.Id;
            }
        }

        public string SelectedMachineProvider
        {
            get => _machineProvider;
            set
            {
                if (_machineProvider == value) return;
                this.RaiseAndSetIfChanged(ref _machineProvider, value);
                _settings.TextAssist.MachineProvider = value;
            }
        }

        public string? SelectedPromptId
        {
            get => _selectedPromptId;
            set
            {
                if (_selectedPromptId == value) return;
                this.RaiseAndSetIfChanged(ref _selectedPromptId, value);
                if (_correction) _settings.TextAssist.CorrectionPromptId = value;
                else _settings.TextAssist.TranslationPromptId = value;
            }
        }

        protected TextAssistProfile ResolveProfile(TextAssistOperation operation)
        {
            var provider = operation == TextAssistOperation.Translation
                ? SelectedProvider
                : TranslationEngineNames.AiModel;
            return new TextAssistProfile(
                ToContract(SelectedSourceLanguage),
                ToContract(SelectedTargetLanguage),
                provider,
                SelectedAiModel?.Id,
                SelectedMachineProvider,
                UsesGlobalConfiguration: false,
                PromptId: SelectedPromptId,
                DetailedExplanation: operation == TextAssistOperation.Translation && IsAiProvider && _settings.TextAssist.DetailedExplanation);
        }

        public Task RunNowAsync() => RunCommand.Execute().ToTask();
        public void CancelCurrent() => _request?.Cancel();
        protected abstract Task RunCoreAsync(CancellationToken cancellationToken);

        private async Task ExecuteAsync()
        {
            var request = new CancellationTokenSource();
            _request?.Cancel();
            _request?.Dispose();
            _request = request;
            IsBusy = true;
            ErrorMessage = string.Empty;
            try
            {
                await RunCoreAsync(request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Text assist request failed.");
                ErrorMessage = exception.Message.Contains("No AI model", StringComparison.OrdinalIgnoreCase)
                    ? Resources.TextAssistNoAiModel
                    : exception.Message;
            }
            finally
            {
                if (ReferenceEquals(_request, request)) _request = null;
                IsBusy = false;
                request.Dispose();
            }
        }

        private void Cancel() => _request?.Cancel();

        private static TranslationLanguage ToContract(LanguageSettings language) => new(
            language.Id,
            language.EnglishName,
            language.LocalizedName,
            language.ProviderCodes,
            language.Icon);
    }

    public sealed class TextAssistTranslationViewModel : TextAssistEditorViewModel
    {
        private readonly ITranslationWindowCoordinator _dictionary;
        private readonly ITtsUseCases _tts;
        private string _inputText = string.Empty;
        private string _translationResult = string.Empty;
        private bool _isSourceSpeaking;
        private bool _isResultSpeaking;
        private bool _detailedExplanation;

        public TextAssistTranslationViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            ITranslationWindowCoordinator dictionary,
            ITtsUseCases tts,
            ILogger logger)
            : base(settings, languages, textAssist, correction: false, logger)
        {
            _dictionary = dictionary;
            _tts = tts;
            _detailedExplanation = settings.TextAssist.DetailedExplanation;
            SpeakSourceCommand = ReactiveCommand.CreateFromTask(() => SpeakAsync(InputText, SelectedSourceLanguage.Id, true));
            SpeakResultCommand = ReactiveCommand.CreateFromTask(() => SpeakAsync(TranslationResult, SelectedTargetLanguage.Id, false));
            SwapContentCommand = ReactiveCommand.Create(SwapContent);
            LookupAnnotationCommand = ReactiveCommand.CreateFromTask<string>(LookupAnnotationAsync);
        }

        public string InputText { get => _inputText; set => this.RaiseAndSetIfChanged(ref _inputText, value); }
        public string TranslationResult { get => _translationResult; private set => this.RaiseAndSetIfChanged(ref _translationResult, value); }
        public ObservableStringBuilder ResultMarkdown { get; } = new();
        public bool IsSourceSpeaking { get => _isSourceSpeaking; private set => this.RaiseAndSetIfChanged(ref _isSourceSpeaking, value); }
        public bool IsResultSpeaking { get => _isResultSpeaking; private set => this.RaiseAndSetIfChanged(ref _isResultSpeaking, value); }
        public bool DetailedExplanation
        {
            get => _detailedExplanation;
            set
            {
                if (_detailedExplanation == value) return;
                this.RaiseAndSetIfChanged(ref _detailedExplanation, value);
                this.RaisePropertyChanged(nameof(ShowAnnotations));
                Settings.TextAssist.DetailedExplanation = value;
            }
        }

        public ObservableCollection<TextAssistAnnotationViewModel> Annotations { get; } = [];
        public bool ShowAnnotations => DetailedExplanation && Annotations.Count > 0;
        public ReactiveCommand<Unit, Unit> SpeakSourceCommand { get; }
        public ReactiveCommand<Unit, Unit> SpeakResultCommand { get; }
        public ReactiveCommand<Unit, Unit> SwapContentCommand { get; }
        public ReactiveCommand<string, Unit> LookupAnnotationCommand { get; }

        protected override async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;
            TranslationResult = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(
                () => ResultMarkdown.Clear(),
                DispatcherPriority.Background,
                cancellationToken);
            Annotations.Clear();
            this.RaisePropertyChanged(nameof(ShowAnnotations));
            var profile = ResolveProfile(TextAssistOperation.Translation) with { DetailedExplanation = DetailedExplanation && IsAiProvider };
            var refresh = new TextAssistStreamRefreshThrottle();
            var pending = new StringBuilder();
            await foreach (var item in TextAssist.StreamAsync(
                               new TextAssistRequest(InputText, TextAssistOperation.Translation, profile),
                               cancellationToken))
            {
                switch (item)
                {
                    case TextAssistTranslationDeltaEvent delta:
                        pending.Append(delta.Text);
                        if (!refresh.ShouldRefresh()) continue;
                        await FlushTranslationChunkAsync(pending, cancellationToken);
                        break;
                    case TextAssistTranslationAnnotationEvent annotation:
                        await Dispatcher.UIThread.InvokeAsync(
                            () =>
                            {
                                Annotations.Add(new TextAssistAnnotationViewModel(annotation));
                                this.RaisePropertyChanged(nameof(ShowAnnotations));
                            },
                            DispatcherPriority.Background,
                            cancellationToken);
                        break;
                }
            }
            if (pending.Length > 0)
                await FlushTranslationChunkAsync(pending, cancellationToken);
        }

        private async Task FlushTranslationChunkAsync(StringBuilder pending, CancellationToken cancellationToken)
        {
            var chunk = pending.ToString();
            pending.Clear();
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    TranslationResult += chunk;
                    ResultMarkdown.Append(chunk);
                },
                DispatcherPriority.Background,
                cancellationToken);
        }

        private void SwapContent()
        {
            CancelCurrent();
            var sourceText = InputText;
            InputText = TranslationResult;
            TranslationResult = sourceText;
            ResultMarkdown.Clear();
            ResultMarkdown.Append(TranslationResult);
            Annotations.Clear();
            this.RaisePropertyChanged(nameof(ShowAnnotations));
            var source = SelectedSourceLanguage;
            SelectedSourceLanguage = SelectedTargetLanguage;
            SelectedTargetLanguage = source;
        }

        private Task LookupAnnotationAsync(string term) => string.IsNullOrWhiteSpace(term)
            ? Task.CompletedTask
            : _dictionary.ShowDictionaryAsync(
                term,
                SelectedSourceLanguage.Id,
                SelectedTargetLanguage.Id,
                centerOnScreen: true).AsTask();

        private async Task SpeakAsync(string text, string languageId, bool source)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (source) IsSourceSpeaking = true; else IsResultSpeaking = true;
                var voice = await _tts.ResolvePreferredVoiceAsync(languageId);
                if (voice.IsFailure)
                {
                    ErrorMessage = voice.Error.Message;
                    return;
                }
                if (string.IsNullOrWhiteSpace(voice.Value))
                {
                    ErrorMessage = Resources.TextAssistNoVoice;
                    return;
                }
                var result = await _tts.EnqueueAsync(new TtsSynthesisRequest(text, voice.Value), interruptCurrent: true);
                if (result.IsFailure) ErrorMessage = result.Error.Message;
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
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
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist,
            ILogger logger)
            : base(settings, languages, textAssist, correction: true, logger)
        {
        }

        public string InputText
        {
            get => _inputText;
            set
            {
                if (_inputText == value) return;
                this.RaiseAndSetIfChanged(ref _inputText, value);
                ResetResults();
            }
        }

        public string CorrectedResult { get => _correctedResult; private set => this.RaiseAndSetIfChanged(ref _correctedResult, value); }
        public ObservableCollection<CorrectionVariant> CorrectionVariants { get; } = [];
        public ObservableCollection<TextAssistIssueViewModel> Issues { get; } = [];
        public ObservableCollection<CorrectionTextSegment> CorrectionSegments { get; } = [];
        public bool HasCorrectedResults => CorrectionVariants.Count > 0;
        public bool HasCorrectionIssues => Issues.Count > 0;
        public bool HasCorrectionOutput => HasCorrectedResults || HasCorrectionIssues;

        protected override async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;
            ResetResults();
            var projection = new TextAssistCorrectionProjection(InputText);
            var refresh = new TextAssistStreamRefreshThrottle();
            await foreach (var item in TextAssist.StreamAsync(
                               new TextAssistRequest(InputText, TextAssistOperation.Correction, ResolveProfile(TextAssistOperation.Correction)),
                               cancellationToken))
            {
                projection.Apply(item);
                if (!refresh.ShouldRefresh()) continue;
                await Dispatcher.UIThread.InvokeAsync(
                    () => ApplyProjection(projection),
                    DispatcherPriority.Background,
                    cancellationToken);
            }
            projection.EnsureComplete();
            await Dispatcher.UIThread.InvokeAsync(
                () => ApplyProjection(projection),
                DispatcherPriority.Background,
                cancellationToken);
        }

        private void ApplyProjection(TextAssistCorrectionProjection projection)
        {
            CorrectedResult = projection.CorrectedText;
            CorrectionVariants.Clear();
            foreach (var pair in projection.CorrectedVariants.OrderBy(pair => pair.Key))
            {
                projection.Translations.TryGetValue(pair.Key, out var translation);
                CorrectionVariants.Add(new CorrectionVariant(pair.Value, translation ?? string.Empty));
            }
            if (!TextAssistCorrectionProjection.HasSameIssueInstances(Issues, projection.Issues))
            {
                Issues.Clear();
                foreach (var issue in projection.Issues) Issues.Add(issue);
                RebuildCorrectionSegments();
            }
            this.RaisePropertyChanged(nameof(HasCorrectedResults));
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            this.RaisePropertyChanged(nameof(HasCorrectionOutput));
        }

        private void ResetResults()
        {
            CorrectedResult = string.Empty;
            CorrectionVariants.Clear();
            Issues.Clear();
            CorrectionSegments.Clear();
            this.RaisePropertyChanged(nameof(HasCorrectedResults));
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            this.RaisePropertyChanged(nameof(HasCorrectionOutput));
        }

        private void RebuildCorrectionSegments()
        {
            CorrectionSegments.Clear();
            var cursor = 0;
            foreach (var issue in Issues.OrderBy(issue => issue.Start))
            {
                if (issue.Start < cursor || issue.Start < 0 || issue.Start >= InputText.Length) continue;
                if (issue.Start > cursor) CorrectionSegments.Add(new CorrectionTextSegment(InputText[cursor..issue.Start], false, null));
                var end = Math.Min(InputText.Length, issue.Start + issue.Length);
                if (end <= issue.Start) continue;
                CorrectionSegments.Add(new CorrectionTextSegment(InputText[issue.Start..end], true, $"{issue.Message}\n{issue.Suggestion}"));
                cursor = end;
            }
            if (cursor < InputText.Length) CorrectionSegments.Add(new CorrectionTextSegment(InputText[cursor..], false, null));
            if (CorrectionSegments.Count == 0 && InputText.Length > 0)
                CorrectionSegments.Add(new CorrectionTextSegment(InputText, false, null));
        }
    }

    public sealed record CorrectionTextSegment(string Text, bool IsIssue, string? Suggestion);
    public sealed record CorrectionVariant(string Text, string Translation);

    public sealed class TextAssistAnnotationViewModel(TextAssistTranslationAnnotationEvent value)
    {
        public string Term => value.Term;
        public string Category => value.Category;
        public string DisplayCategory => TextAssistDisplayNames.AnnotationCategory(value.Category);
        public string Meaning => value.Meaning;
        public string? Note => value.Note;
        public IReadOnlyList<string> RelatedTerms => value.RelatedTerms ?? [];
        public bool HasNote => value.HasNote;
        public bool HasRelatedTerms => value.HasRelatedTerms;
    }

    public sealed class TextAssistIssueViewModel(TextAssistIssueEvent value)
    {
        internal TextAssistIssueEvent Event => value;
        public int Start => value.Start;
        public int Length => value.Length;
        public string Category => value.Category;
        public string DisplayCategory => TextAssistDisplayNames.IssueCategory(value.Category);
        public string Message => value.Message;
        public string Suggestion => value.Suggestion;
    }

    internal static class TextAssistDisplayNames
    {
        public static string AnnotationCategory(string category) => category.ToLowerInvariant() switch
        {
            "important_word" => Resources.TextAssistAnnotationImportantWord,
            "uncommon_word" => Resources.TextAssistAnnotationUncommonWord,
            "collocation" => Resources.TextAssistAnnotationCollocation,
            "usage_tip" => Resources.TextAssistAnnotationUsageTip,
            _ => category
        };

        public static string IssueCategory(string category) => category.ToLowerInvariant() switch
        {
            "grammar" => Resources.TextAssistCategoryGrammar,
            "spelling" => Resources.TextAssistCategorySpelling,
            "word_choice" => Resources.TextAssistCategoryWordChoice,
            "style" => Resources.TextAssistCategoryStyle,
            _ => category
        };
    }

    internal sealed class TextAssistCorrectionProjection(string sourceText)
    {
        private readonly string _sourceText = sourceText ?? string.Empty;
        private readonly int _sourceLength = (sourceText ?? string.Empty).Length;
        private readonly Dictionary<int, StringBuilder> _corrected = [];
        private readonly Dictionary<int, StringBuilder> _translations = [];
        private bool _started;
        private bool _completed;

        public List<TextAssistIssueViewModel> Issues { get; } = [];
        public string CorrectedText => CorrectedVariants.TryGetValue(1, out var value) ? value : string.Empty;
        public IReadOnlyDictionary<int, string> CorrectedVariants => _corrected.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
        public IReadOnlyDictionary<int, string> Translations => _translations.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

        public static bool HasSameIssueInstances(
            IReadOnlyList<TextAssistIssueViewModel> current,
            IReadOnlyList<TextAssistIssueViewModel> next)
        {
            if (current.Count != next.Count) return false;
            for (var index = 0; index < current.Count; index++)
            {
                if (!ReferenceEquals(current[index], next[index]))
                    return false;
            }
            return true;
        }

        public void Apply(TextAssistEvent item)
        {
            switch (item)
            {
                case TextAssistStartedEvent:
                    _started = true;
                    break;
                case TextAssistIssueEvent issue:
                    _started = true;
                    var resolved = TextAssistIssueRangeResolver.Normalize(_sourceText, issue);
                    if (resolved.Start < 0 || resolved.Length <= 0 || resolved.Start > _sourceLength
                        || resolved.Length > _sourceLength - resolved.Start)
                        break;
                    AddIssue(resolved);
                    break;
                case TextAssistCorrectedDeltaEvent delta:
                    _started = true;
                    Append(_corrected, delta.Variant, delta.Text, delta.IsStreamingPartial);
                    break;
                case TextAssistCorrectionTranslationDeltaEvent translation:
                    _started = true;
                    Append(_translations, translation.Variant, translation.Text, translation.IsStreamingPartial);
                    break;
                case TextAssistCompletedEvent:
                    _started = true;
                    _completed = true;
                    break;
            }
        }

        private void AddIssue(TextAssistIssueEvent issue)
        {
            var duplicateIndex = Issues.FindIndex(existing =>
                TextAssistCorrectionIssueRules.HasSameIdentity(existing.Event, issue));
            if (duplicateIndex >= 0)
                return;

            var relatedIndex = Issues.FindIndex(existing =>
                TextAssistCorrectionIssueRules.DescribesSameCorrection(existing.Event, issue)
                && TextAssistCorrectionIssueRules.RangesAreAdjacentOrOverlapping(existing.Event, issue));
            if (relatedIndex >= 0)
            {
                var existing = Issues[relatedIndex].Event;
                var start = Math.Min(existing.Start, issue.Start);
                var end = Math.Max(existing.Start + existing.Length, issue.Start + issue.Length);
                var merged = existing with
                {
                    Start = start,
                    Length = end - start,
                    Original = _sourceText[start..end]
                };
                Issues[relatedIndex] = new TextAssistIssueViewModel(merged);
                return;
            }

            Issues.Add(new TextAssistIssueViewModel(issue));
        }

        public void EnsureComplete()
        {
            if (!_started) throw new InvalidOperationException("Correction stream did not start.");
            if (!_completed) throw new InvalidOperationException("Correction stream did not complete.");
        }

        private static void Append(
            Dictionary<int, StringBuilder> values,
            int variant,
            string text,
            bool isStreamingPartial = false)
        {
            variant = Math.Clamp(variant, 1, 3);
            if (string.IsNullOrEmpty(text))
                return;
            if (!values.TryGetValue(variant, out var builder))
            {
                values[variant] = new StringBuilder(text);
                return;
            }

            if (isStreamingPartial)
            {
                builder.Append(text);
                return;
            }

            var current = builder.ToString();
            if (string.Equals(current, text, StringComparison.Ordinal)
                || current.StartsWith(text, StringComparison.Ordinal))
            {
                return;
            }
            if (text.StartsWith(current, StringComparison.Ordinal))
            {
                builder.Clear();
                builder.Append(text);
                return;
            }

            builder.Append(text.AsSpan(FindSuffixPrefixOverlap(current, text)));
        }

        private static int FindSuffixPrefixOverlap(string current, string next)
        {
            for (var length = Math.Min(current.Length, next.Length); length > 0; length--)
            {
                if (current.AsSpan(current.Length - length).SequenceEqual(next.AsSpan(0, length)))
                    return length;
            }
            return 0;
        }

    }

    internal sealed class TextAssistStreamRefreshThrottle
    {
        private const int RefreshesPerSecond = 30;
        private readonly long _minimumInterval = Math.Max(1, System.Diagnostics.Stopwatch.Frequency / RefreshesPerSecond);
        private long _lastRefresh;

        public bool ShouldRefresh()
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastRefresh != 0 && now - _lastRefresh < _minimumInterval)
                return false;
            _lastRefresh = now;
            return true;
        }
    }
}

namespace EasyChat.Presentation.Features.TextAssist
{
    using EasyChat.Presentation.Features.TextAssist;

    public sealed class TextAssistResultWindowViewModel : EasyChat.Presentation.Foundation.Navigation.ViewModelBase
    {
        private readonly SettingsSession _settings;
        private readonly TranslationLanguageOptions _languages;
        private readonly ITextAssistUseCases _textAssist;
        private CancellationTokenSource? _request;
        private string _sourceText = string.Empty;
        private string _result = string.Empty;
        private string _correctedResult = string.Empty;
        private string _correctionTranslation = string.Empty;
        private bool _isCorrectionCorrect;
        private string _errorMessage = string.Empty;
        private bool _isBusy;
        private bool _hasStreamingContent;
        private string _sourceLanguageId;

        public TextAssistResultWindowViewModel(
            SettingsSession settings,
            TranslationLanguageOptions languages,
            ITextAssistUseCases textAssist)
        {
            _settings = settings;
            _languages = languages;
            _textAssist = textAssist;
            _sourceLanguageId = settings.TextAssist.SourceLanguageId;
            Languages = languages.All.OrderBy(language => language.EnglishName).ToArray();
            RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(value => value.IsBusy, busy => !busy));
        }

        public IReadOnlyList<LanguageSettings> Languages { get; }
        public TextAssistOperation Operation { get; private set; }
        public bool ShowLanguageSelector => Operation is TextAssistOperation.Correction or TextAssistOperation.Polish;
        public bool IsCorrection => Operation == TextAssistOperation.Correction;
        public bool IsPolish => Operation == TextAssistOperation.Polish;
        public bool IsSummary => Operation == TextAssistOperation.Summary;
        public bool IsExplanation => Operation == TextAssistOperation.Explanation;
        public bool ShowPlainResult => Operation is TextAssistOperation.Translation
            or TextAssistOperation.Summary
            or TextAssistOperation.Explanation;
        public MaterialIconKind WindowIcon => Operation switch
        {
            TextAssistOperation.Correction => MaterialIconKind.Spellcheck,
            TextAssistOperation.Polish => MaterialIconKind.FormatPaint,
            TextAssistOperation.Summary => MaterialIconKind.TextShort,
            TextAssistOperation.Explanation => MaterialIconKind.LightbulbOnOutline,
            _ => MaterialIconKind.TextBoxEditOutline
        };
        public string Title => Operation switch
        {
            TextAssistOperation.Explanation => Resources.TextAssistExplain,
            TextAssistOperation.Correction => Resources.TextAssistCorrect,
            TextAssistOperation.Polish => Resources.TextAssistPolish,
            TextAssistOperation.Summary => Resources.TextAssistSummary,
            _ => Resources.TextAssistProcessing
        };
        public string PolishExplanationTitle => Resources.TextAssistPolishExplanationTitle;
        public string PolishOriginalLabel => Resources.TextAssistPolishOriginalLabel;
        public string PolishRevisedLabel => Resources.TextAssistPolishRevisedLabel;

        public LanguageSettings SelectedSourceLanguage
        {
            get => Languages.FirstOrDefault(language => language.Id == _sourceLanguageId) ?? Languages.First(language => language.Id == "auto");
            set
            {
                _sourceLanguageId = value.Id;
                _settings.TextAssist.SourceLanguageId = value.Id;
                this.RaisePropertyChanged();
            }
        }

        public string Result { get => _result; private set => this.RaiseAndSetIfChanged(ref _result, value); }
        public ObservableStringBuilder ResultMarkdown { get; } = new();
        public string CorrectedResult { get => _correctedResult; private set => this.RaiseAndSetIfChanged(ref _correctedResult, value); }
        public string CorrectionTranslation { get => _correctionTranslation; private set => this.RaiseAndSetIfChanged(ref _correctionTranslation, value); }
        public ObservableCollection<TextAssistIssueViewModel> Issues { get; } = [];
        public ObservableCollection<TextAssistPolishExplanationEvent> PolishExplanations { get; } = [];
        public bool HasCorrectionIssues => Issues.Count > 0;
        public bool HasPolishExplanations => IsPolish && PolishExplanations.Count > 0;
        public bool IsCorrectionCorrect { get => _isCorrectionCorrect; private set => this.RaiseAndSetIfChanged(ref _isCorrectionCorrect, value); }
        public bool ShowCorrectionResult => IsCorrection && !IsCorrectionCorrect;
        public string CorrectionStatus => IsCorrectionCorrect ? "未发现问题" : string.Empty;
        public string CorrectionStatusDetail => IsCorrectionCorrect ? "选中的文本语法、拼写和表达均正确。" : string.Empty;
        public string CopyText => IsCorrection ? (IsCorrectionCorrect ? SourceText : CorrectedResult) : Result;
        public string SourceText { get => _sourceText; set => this.RaiseAndSetIfChanged(ref _sourceText, value); }
        public string ErrorMessage { get => _errorMessage; private set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                this.RaiseAndSetIfChanged(ref _isBusy, value);
                this.RaisePropertyChanged(nameof(ShowLoadingIndicator));
            }
        }
        public bool HasStreamingContent
        {
            get => _hasStreamingContent;
            private set
            {
                if (_hasStreamingContent == value) return;
                this.RaiseAndSetIfChanged(ref _hasStreamingContent, value);
                this.RaisePropertyChanged(nameof(ShowLoadingIndicator));
            }
        }
        public bool ShowLoadingIndicator => IsBusy && !HasStreamingContent;
        public ReactiveCommand<Unit, Unit> RetryCommand { get; }

        public Task InitializeAsync(string sourceText, TextAssistOperation operation)
        {
            SourceText = sourceText;
            Operation = operation;
            RaiseOperationProperties();
            return RunAsync();
        }

        public void Prepare(TextAssistOperation operation)
        {
            Operation = operation;
            RaiseOperationProperties();
        }

        public void Cancel() => _request?.Cancel();

        private async Task RunAsync()
        {
            if (string.IsNullOrWhiteSpace(SourceText)) return;
            _request?.Cancel();
            _request?.Dispose();
            _request = new CancellationTokenSource();
            var token = _request.Token;
            await ResetResultsAsync();
            IsBusy = true;
            try
            {
                var profile = _textAssist.ResolveProfile(Operation) with
                {
                    Source = ToContract(SelectedSourceLanguage)
                };
                var correction = Operation == TextAssistOperation.Correction
                    ? new TextAssistCorrectionProjection(SourceText)
                    : null;
                var refresh = new TextAssistStreamRefreshThrottle();
                var pending = new StringBuilder();
                await foreach (var item in _textAssist.StreamAsync(
                                   new TextAssistRequest(SourceText, Operation, profile), token))
                {
                    if (correction is null)
                    {
                        if (item is not TextAssistTranslationDeltaEvent delta)
                        {
                            await Dispatcher.UIThread.InvokeAsync(
                                () => ApplyPlain(item),
                                DispatcherPriority.Background,
                                token);
                            continue;
                        }

                        pending.Append(delta.Text);
                        if (!refresh.ShouldRefresh()) continue;
                        await FlushPlainChunkAsync(pending, token);
                        continue;
                    }

                    correction.Apply(item);
                    if (!refresh.ShouldRefresh()) continue;
                    await Dispatcher.UIThread.InvokeAsync(
                        () => ApplyCorrection(correction),
                        DispatcherPriority.Background,
                        token);
                }
                if (correction is null && pending.Length > 0)
                    await FlushPlainChunkAsync(pending, token);
                correction?.EnsureComplete();
                if (correction is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ApplyCorrection(correction);
                        IsCorrectionCorrect = Issues.Count == 0
                                              && string.Equals(CorrectedResult.Trim(), SourceText.Trim(), StringComparison.Ordinal);
                        RaiseCorrectionProperties();
                    }, DispatcherPriority.Background, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ApplyCorrection(TextAssistCorrectionProjection correction)
        {
            CorrectedResult = correction.CorrectedText;
            CorrectionTranslation = correction.Translations.TryGetValue(1, out var translation) ? translation : string.Empty;
            if (!TextAssistCorrectionProjection.HasSameIssueInstances(Issues, correction.Issues))
            {
                Issues.Clear();
                foreach (var issue in correction.Issues) Issues.Add(issue);
            }
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            this.RaisePropertyChanged(nameof(CopyText));
            UpdateStreamingContent();
        }

        private void ApplyPlain(TextAssistEvent item)
        {
            switch (item)
            {
                case TextAssistTranslationDeltaEvent delta:
                    Result += delta.Text;
                    ResultMarkdown.Append(delta.Text);
                    this.RaisePropertyChanged(nameof(CopyText));
                    UpdateStreamingContent();
                    break;
                case TextAssistPolishExplanationEvent explanation:
                    PolishExplanations.Add(explanation);
                    this.RaisePropertyChanged(nameof(HasPolishExplanations));
                    UpdateStreamingContent();
                    break;
            }
        }

        private async Task FlushPlainChunkAsync(StringBuilder pending, CancellationToken token)
        {
            var chunk = pending.ToString();
            pending.Clear();
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    Result += chunk;
                    ResultMarkdown.Append(chunk);
                    this.RaisePropertyChanged(nameof(CopyText));
                    UpdateStreamingContent();
                },
                DispatcherPriority.Background,
                token);
        }

        private async Task ResetResultsAsync()
        {
            Result = string.Empty;
            if (Dispatcher.UIThread.CheckAccess())
                ResultMarkdown.Clear();
            else
                await Dispatcher.UIThread.InvokeAsync(() => ResultMarkdown.Clear());
            CorrectedResult = string.Empty;
            CorrectionTranslation = string.Empty;
            Issues.Clear();
            PolishExplanations.Clear();
            IsCorrectionCorrect = false;
            ErrorMessage = string.Empty;
            HasStreamingContent = false;
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            this.RaisePropertyChanged(nameof(HasPolishExplanations));
            RaiseCorrectionProperties();
        }

        private void UpdateStreamingContent() => HasStreamingContent =
            !string.IsNullOrWhiteSpace(Result)
            || !string.IsNullOrWhiteSpace(CorrectedResult)
            || !string.IsNullOrWhiteSpace(CorrectionTranslation)
            || Issues.Count > 0
            || PolishExplanations.Count > 0;

        private void RaiseOperationProperties()
        {
            this.RaisePropertyChanged(nameof(ShowLanguageSelector));
            this.RaisePropertyChanged(nameof(IsCorrection));
            this.RaisePropertyChanged(nameof(IsPolish));
            this.RaisePropertyChanged(nameof(IsSummary));
            this.RaisePropertyChanged(nameof(IsExplanation));
            this.RaisePropertyChanged(nameof(ShowPlainResult));
            this.RaisePropertyChanged(nameof(WindowIcon));
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(HasPolishExplanations));
        }

        private void RaiseCorrectionProperties()
        {
            this.RaisePropertyChanged(nameof(ShowCorrectionResult));
            this.RaisePropertyChanged(nameof(CorrectionStatus));
            this.RaisePropertyChanged(nameof(CorrectionStatusDetail));
            this.RaisePropertyChanged(nameof(CopyText));
        }

        private static TranslationLanguage ToContract(LanguageSettings language) => new(
            language.Id,
            language.EnglishName,
            language.LocalizedName,
            language.ProviderCodes,
            language.Icon);
    }
}
