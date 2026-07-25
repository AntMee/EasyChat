using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Lang;
using EasyChat.Constants;
using EasyChat.Models;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.TextAssist;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.TextAssist;
using Material.Icons;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace EasyChat.ViewModels.Pages;

public sealed class TextAssistViewModel : Page
{
    private readonly IConfigurationService _configurationService;
    private readonly TextAssistProfileResolver _profileResolver;
    private readonly ITextAssistService _textAssistService;
    private readonly ILogger<TextAssistViewModel>? _logger;
    private CancellationTokenSource? _requestCts;
    private string _inputText = string.Empty;
    private string _translationResult = string.Empty;
    private string _correctedResult = string.Empty;
    private string _errorMessage = string.Empty;
    private int _selectedTabIndex;
    private bool _isBusy;
    private bool _followGlobal;
    private LanguageDefinition _selectedSourceLanguage;
    private LanguageDefinition _selectedTargetLanguage;
    private string _selectedProvider;
    private CustomAiModel? _selectedAiModel;
    private string _selectedMachineProvider;

    public TextAssistViewModel(
        IConfigurationService configurationService,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService,
        ILogger<TextAssistViewModel>? logger = null) : base(Resources.TextAssist, MaterialIconKind.Translate, 5)
    {
        _configurationService = configurationService;
        _profileResolver = profileResolver;
        _textAssistService = textAssistService;
        _logger = logger;
        Languages = LanguageService.GetAllLanguages().OrderBy(x => x.EnglishName).ToList();
        AvailableAiModels = new ObservableCollection<CustomAiModel>(_configurationService.AiModel?.ConfiguredModels ?? []);
        MachineProviders = [Constant.MachineTranslationProviders.Baidu, Constant.MachineTranslationProviders.Tencent,
            Constant.MachineTranslationProviders.Google, Constant.MachineTranslationProviders.DeepL];

        var config = _configurationService.TextAssist!;
        _followGlobal = config.FollowGlobal;
        _selectedSourceLanguage = Languages.FirstOrDefault(x => x.Id == config.SourceLanguageId) ?? LanguageService.GetLanguage("auto");
        _selectedTargetLanguage = Languages.FirstOrDefault(x => x.Id == config.TargetLanguageId) ?? LanguageService.GetLanguage("zh-Hans");
        _selectedProvider = config.Provider;
        _selectedAiModel = AvailableAiModels.FirstOrDefault(x => x.Id == config.AiModelId)
                           ?? AvailableAiModels.FirstOrDefault();
        _selectedMachineProvider = config.MachineProvider;

        if (_followGlobal) LoadGlobalSettings();

        RunCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(x => x.IsBusy, busy => !busy));
        CancelCommand = ReactiveCommand.Create(CancelRequest);
        SelectTranslationCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 0; });
        SelectCorrectionCommand = ReactiveCommand.Create(() => { SelectedTabIndex = 1; });
    }

    public IReadOnlyList<LanguageDefinition> Languages { get; }
    public ObservableCollection<CustomAiModel> AvailableAiModels { get; }
    public IReadOnlyList<string> MachineProviders { get; }
    public IReadOnlyList<string> AvailableProviders { get; } = [TextAssistConstants.AiProvider, TextAssistConstants.MachineProvider];
    public ObservableCollection<TextAssistIssueEvent> Issues { get; } = [];
    public ObservableCollection<CorrectionTextSegment> CorrectionSegments { get; } = [];
    public ReactiveCommand<Unit, Unit> RunCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectTranslationCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectCorrectionCommand { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (string.Equals(_inputText, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _inputText, value);
            Issues.Clear();
            CorrectedResult = string.Empty;
            RebuildCorrectionSegments();
        }
    }

    public string TranslationResult
    {
        get => _translationResult;
        private set => this.RaiseAndSetIfChanged(ref _translationResult, value);
    }

    public string CorrectedResult
    {
        get => _correctedResult;
        private set => this.RaiseAndSetIfChanged(ref _correctedResult, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
            this.RaisePropertyChanged(nameof(IsTranslationMode));
            this.RaisePropertyChanged(nameof(IsCorrectionMode));
        }
    }

    public bool IsTranslationMode => SelectedTabIndex == 0;
    public bool IsCorrectionMode => SelectedTabIndex == 1;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
        }
    }

    public bool FollowGlobal
    {
        get => _followGlobal;
        set
        {
            this.RaiseAndSetIfChanged(ref _followGlobal, value);
            _configurationService.TextAssist!.FollowGlobal = value;
            if (value) LoadGlobalLanguages();
            else LoadIndependentSettings();
        }
    }

    public LanguageDefinition SelectedSourceLanguage
    {
        get => _selectedSourceLanguage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSourceLanguage, value);
            if (!FollowGlobal) _configurationService.TextAssist!.SourceLanguageId = value.Id;
        }
    }

    public LanguageDefinition SelectedTargetLanguage
    {
        get => _selectedTargetLanguage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTargetLanguage, value);
            if (!FollowGlobal) _configurationService.TextAssist!.TargetLanguageId = value.Id;
        }
    }

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProvider, value);
            this.RaisePropertyChanged(nameof(IsAiProvider));
            this.RaisePropertyChanged(nameof(IsMachineProvider));
            if (!FollowGlobal) _configurationService.TextAssist!.Provider = value;
        }
    }

    public bool IsAiProvider => SelectedProvider.Equals(TextAssistConstants.AiProvider, StringComparison.OrdinalIgnoreCase);
    public bool IsMachineProvider => SelectedProvider.Equals(TextAssistConstants.MachineProvider, StringComparison.OrdinalIgnoreCase);

    public CustomAiModel? SelectedAiModel
    {
        get => _selectedAiModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAiModel, value);
            if (!FollowGlobal) _configurationService.TextAssist!.AiModelId = value?.Id;
        }
    }

    public string SelectedMachineProvider
    {
        get => _selectedMachineProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMachineProvider, value);
            if (!FollowGlobal) _configurationService.TextAssist!.MachineProvider = value;
        }
    }

    public async Task InitializeAsync(string text, bool correction)
    {
        InputText = text;
        SelectedTabIndex = correction ? 1 : 0;
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;
        _requestCts?.Cancel();
        var requestCts = new CancellationTokenSource();
        _requestCts = requestCts;
        var token = requestCts.Token;
        IsBusy = true;
        ErrorMessage = string.Empty;
        TranslationResult = string.Empty;
        CorrectedResult = string.Empty;
        Issues.Clear();
        RebuildCorrectionSegments();

        try
        {
            var profile = _profileResolver.Resolve(IsCorrectionMode);
            if (IsCorrectionMode)
            {
                var accumulator = new TextAssistCorrectionAccumulator(
                    InputText.Length,
                    issue => _logger?.LogWarning(
                        "Ignoring out-of-range correction issue at {Start} with length {Length}",
                        issue.Start,
                        issue.Length));
                await foreach (var item in _textAssistService.StreamCorrectAsync(InputText, profile, token))
                {
                    accumulator.Apply(item);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CorrectedResult = accumulator.CorrectedText;
                        Issues.Clear();
                        foreach (var issue in accumulator.Issues) Issues.Add(issue);
                        RebuildCorrectionSegments();
                    });
                }
                accumulator.EnsureComplete();
            }
            else
            {
                await foreach (var item in _textAssistService.StreamTranslateAsync(InputText, profile, token))
                {
                    if (item is TextAssistTranslationDeltaEvent delta)
                        await Dispatcher.UIThread.InvokeAsync(() => TranslationResult += delta.Text);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_requestCts, requestCts))
            {
                _requestCts = null;
                IsBusy = false;
            }
            requestCts.Dispose();
        }
    }

    private void CancelRequest()
    {
        _requestCts?.Cancel();
    }

    private void LoadGlobalLanguages()
    {
        var global = _configurationService.General;
        if (global == null) return;
        SelectedSourceLanguage = Languages.FirstOrDefault(x => x.Id == global.SourceLanguage.Id) ?? SelectedSourceLanguage;
        SelectedTargetLanguage = Languages.FirstOrDefault(x => x.Id == global.TargetLanguage.Id) ?? SelectedTargetLanguage;
        LoadGlobalSettings();
    }

    private void LoadGlobalSettings()
    {
        var global = _configurationService.General;
        if (global == null) return;
        _selectedProvider = global.TransEngine ?? TextAssistConstants.AiProvider;
        _selectedAiModel = AvailableAiModels.FirstOrDefault(x => x.Id == global.UsingAiModelId)
                           ?? AvailableAiModels.FirstOrDefault(x => x.Name == global.UsingAiModel)
                           ?? _selectedAiModel;
        _selectedMachineProvider = global.UsingMachineTransId ?? global.UsingMachineTrans ?? _selectedMachineProvider;
        this.RaisePropertyChanged(nameof(SelectedProvider));
        this.RaisePropertyChanged(nameof(SelectedAiModel));
        this.RaisePropertyChanged(nameof(SelectedMachineProvider));
        this.RaisePropertyChanged(nameof(IsAiProvider));
        this.RaisePropertyChanged(nameof(IsMachineProvider));
    }

    private void LoadIndependentSettings()
    {
        var config = _configurationService.TextAssist;
        if (config == null) return;
        _selectedSourceLanguage = Languages.FirstOrDefault(x => x.Id == config.SourceLanguageId) ?? _selectedSourceLanguage;
        _selectedTargetLanguage = Languages.FirstOrDefault(x => x.Id == config.TargetLanguageId) ?? _selectedTargetLanguage;
        _selectedProvider = config.Provider;
        _selectedAiModel = AvailableAiModels.FirstOrDefault(x => x.Id == config.AiModelId) ?? _selectedAiModel;
        _selectedMachineProvider = config.MachineProvider;
        this.RaisePropertyChanged(nameof(SelectedSourceLanguage));
        this.RaisePropertyChanged(nameof(SelectedTargetLanguage));
        this.RaisePropertyChanged(nameof(SelectedProvider));
        this.RaisePropertyChanged(nameof(SelectedAiModel));
        this.RaisePropertyChanged(nameof(SelectedMachineProvider));
        this.RaisePropertyChanged(nameof(IsAiProvider));
        this.RaisePropertyChanged(nameof(IsMachineProvider));
    }

    public bool HasCorrectionIssues => CorrectionSegments.Any(x => x.IsIssue);

    private void RebuildCorrectionSegments()
    {
        CorrectionSegments.Clear();
        var source = InputText ?? string.Empty;
        if (source.Length == 0)
        {
            this.RaisePropertyChanged(nameof(HasCorrectionIssues));
            return;
        }

        var cursor = 0;
        foreach (var issue in Issues.OrderBy(x => x.Start))
        {
            if (issue.Start < cursor || issue.Start < 0 || issue.Start > source.Length)
                continue;

            if (issue.Start > cursor)
                CorrectionSegments.Add(new CorrectionTextSegment(source[cursor..issue.Start], false, null));

            var end = Math.Min(source.Length, issue.Start + issue.Length);
            if (end > issue.Start)
            {
                CorrectionSegments.Add(new CorrectionTextSegment(
                    source[issue.Start..end],
                    true,
                    $"{issue.Message}\n{issue.Suggestion}"));
                cursor = end;
            }
        }

        if (cursor < source.Length)
            CorrectionSegments.Add(new CorrectionTextSegment(source[cursor..], false, null));

        if (CorrectionSegments.Count == 0)
            CorrectionSegments.Add(new CorrectionTextSegment(source, false, null));
        this.RaisePropertyChanged(nameof(HasCorrectionIssues));
    }
}

public sealed record CorrectionTextSegment(string Text, bool IsIssue, string? Suggestion);
