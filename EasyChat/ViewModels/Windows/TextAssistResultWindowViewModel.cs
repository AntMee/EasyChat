using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using EasyChat.Lang;
using EasyChat.Models;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.TextAssist;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.TextAssist;
using Material.Icons;
using ReactiveUI;

namespace EasyChat.ViewModels.Windows;

public sealed class TextAssistResultWindowViewModel : ViewModelBase
{
    private readonly IConfigurationService _configuration;
    private readonly TextAssistProfileResolver _profileResolver;
    private readonly ITextAssistService _textAssistService;
    private CancellationTokenSource? _request;
    private string _sourceText = string.Empty;
    private string _result = string.Empty;
    private string _correctedResult = string.Empty;
    private string _correctionTranslation = string.Empty;
    private bool _isCorrectionCorrect;
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private string _sourceLanguageId;

    public TextAssistResultWindowViewModel(
        IConfigurationService configuration,
        TextAssistProfileResolver profileResolver,
        ITextAssistService textAssistService)
    {
        _configuration = configuration;
        _profileResolver = profileResolver;
        _textAssistService = textAssistService;
        _sourceLanguageId = configuration.TextAssist?.SourceLanguageId ?? "auto";
        Languages = LanguageService.GetAllLanguages().OrderBy(x => x.EnglishName).ToList();
        RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(x => x.IsBusy, busy => !busy));
    }

    public IReadOnlyList<LanguageDefinition> Languages { get; }
    public TextAssistOperation Operation { get; private set; }
    public bool ShowLanguageSelector => Operation is TextAssistOperation.Correction or TextAssistOperation.Polish;
    public bool IsCorrection => Operation == TextAssistOperation.Correction;
    public bool IsPolish => Operation == TextAssistOperation.Polish;
    public bool IsSummary => Operation == TextAssistOperation.Summary;
    public bool ShowPlainResult => !IsCorrection;
    public MaterialIconKind WindowIcon => Operation switch
    {
        TextAssistOperation.Correction => MaterialIconKind.Spellcheck,
        TextAssistOperation.Polish => MaterialIconKind.FormatPaint,
        TextAssistOperation.Summary => MaterialIconKind.TextShort,
        _ => MaterialIconKind.TextBoxEditOutline
    };
    public string Title => Operation switch
    {
        TextAssistOperation.Correction => Resources.TextAssistCorrect,
        TextAssistOperation.Polish => IsChineseUi ? "润色" : "Polish",
        TextAssistOperation.Summary => IsChineseUi ? "总结" : "Summarize",
        _ => IsChineseUi ? "文本处理" : "Text assist"
    };
    public string PolishExplanationTitle => IsChineseUi ? "润色说明" : "Polish notes";
    public string PolishOriginalLabel => IsChineseUi ? "原表达" : "Original";
    public string PolishRevisedLabel => IsChineseUi ? "润色后" : "Revised";
    private static bool IsChineseUi => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

    public LanguageDefinition SelectedSourceLanguage
    {
        get => Languages.FirstOrDefault(x => x.Id == _sourceLanguageId) ?? LanguageService.GetLanguage("auto");
        set
        {
            _sourceLanguageId = value.Id;
            if (_configuration.TextAssist != null)
                _configuration.TextAssist.SourceLanguageId = value.Id;
            this.RaisePropertyChanged();
        }
    }

    public string Result { get => _result; private set => this.RaiseAndSetIfChanged(ref _result, value); }
    public string CorrectedResult { get => _correctedResult; private set => this.RaiseAndSetIfChanged(ref _correctedResult, value); }
    public string CorrectionTranslation { get => _correctionTranslation; private set => this.RaiseAndSetIfChanged(ref _correctionTranslation, value); }
    public ObservableCollection<TextAssistIssueEvent> Issues { get; } = [];
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
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
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

    private void RaiseOperationProperties()
    {
        this.RaisePropertyChanged(nameof(ShowLanguageSelector));
        this.RaisePropertyChanged(nameof(IsCorrection));
        this.RaisePropertyChanged(nameof(IsPolish));
        this.RaisePropertyChanged(nameof(IsSummary));
        this.RaisePropertyChanged(nameof(ShowPlainResult));
        this.RaisePropertyChanged(nameof(WindowIcon));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(HasPolishExplanations));
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_sourceText)) return;
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        Result = string.Empty;
        CorrectedResult = string.Empty;
        CorrectionTranslation = string.Empty;
        Issues.Clear();
        PolishExplanations.Clear();
        this.RaisePropertyChanged(nameof(HasCorrectionIssues));
        this.RaisePropertyChanged(nameof(HasPolishExplanations));
        IsCorrectionCorrect = false;
        this.RaisePropertyChanged(nameof(ShowCorrectionResult));
        this.RaisePropertyChanged(nameof(CorrectionStatus));
        this.RaisePropertyChanged(nameof(CorrectionStatusDetail));
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var profile = _profileResolver.Resolve(correction: true);
            if (Operation == TextAssistOperation.Correction)
            {
                var accumulator = new TextAssistCorrectionAccumulator(_sourceText.Length);
                await foreach (var item in _textAssistService.StreamCorrectAsync(_sourceText, profile, token))
                {
                    accumulator.Apply(item);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var translation = accumulator.CorrectedTranslations.TryGetValue(1, out var value) ? value : string.Empty;
                        CorrectedResult = accumulator.CorrectedText;
                        CorrectionTranslation = translation;
                        Issues.Clear();
                        foreach (var issue in accumulator.Issues) Issues.Add(issue);
                        this.RaisePropertyChanged(nameof(HasCorrectionIssues));
                        this.RaisePropertyChanged(nameof(CopyText));
                    });
                }
                accumulator.CompleteImplicitly();
                accumulator.EnsureComplete();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsCorrectionCorrect = Issues.Count == 0 &&
                        string.Equals(CorrectedResult.Trim(), SourceText.Trim(), StringComparison.Ordinal);
                    this.RaisePropertyChanged(nameof(ShowCorrectionResult));
                    this.RaisePropertyChanged(nameof(CorrectionStatus));
                    this.RaisePropertyChanged(nameof(CorrectionStatusDetail));
                    this.RaisePropertyChanged(nameof(CopyText));
                });
            }
            else
            {
                var stream = Operation == TextAssistOperation.Polish
                    ? _textAssistService.StreamPolishAsync(_sourceText, profile, token)
                    : _textAssistService.StreamSummarizeAsync(_sourceText, profile, token);
                await foreach (var item in stream)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        switch (item)
                        {
                            case TextAssistTranslationDeltaEvent delta:
                                Result += delta.Text;
                                this.RaisePropertyChanged(nameof(CopyText));
                                break;
                            case TextAssistPolishExplanationEvent explanation:
                                PolishExplanations.Add(explanation);
                                this.RaisePropertyChanged(nameof(HasPolishExplanations));
                                break;
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    public void Cancel() => _request?.Cancel();
}
