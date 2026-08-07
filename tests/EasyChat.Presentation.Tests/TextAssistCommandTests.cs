using System.Reactive.Linq;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.TextAssist;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.TextAssist;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TextAssistCommandTests
{
    [TestMethod]
    public void CorrectionProjection_IgnoresRepeatedIdenticalIssues()
    {
        var projection = new TextAssistCorrectionProjection(12);
        var issue = new TextAssistIssueEvent(2, 3, "grammar", "Wrong form", "Use the correct form");

        projection.Apply(new TextAssistStartedEvent("correction", "English", null));
        projection.Apply(issue);
        projection.Apply(issue);
        projection.Apply(new TextAssistCompletedEvent());

        projection.EnsureComplete();
        Assert.HasCount(1, projection.Issues);
    }

    [TestMethod]
    public void CorrectionProjection_MergesRepeatedCumulativeAndOverlappingPayloads()
    {
        var projection = new TextAssistCorrectionProjection(12);

        projection.Apply(new TextAssistStartedEvent("correction", "English", null));
        projection.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        projection.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        projection.Apply(new TextAssistCorrectedDeltaEvent("Fixed text"));
        projection.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        projection.Apply(new TextAssistCorrectedDeltaEvent(" text with detail"));
        projection.Apply(new TextAssistCorrectionTranslationDeltaEvent("Corrected"));
        projection.Apply(new TextAssistCorrectionTranslationDeltaEvent("Corrected translation"));
        projection.Apply(new TextAssistCorrectionTranslationDeltaEvent(" translation"));
        projection.Apply(new TextAssistCompletedEvent());

        projection.EnsureComplete();
        Assert.AreEqual("Fixed text with detail", projection.CorrectedText);
        Assert.AreEqual("Corrected translation", projection.Translations[1]);
    }

    [TestMethod]
    public void CorrectionProjection_PreservesRepeatedStreamingFragments()
    {
        var projection = new TextAssistCorrectionProjection(12);
        var fragment = new TextAssistCorrectedDeltaEvent("very ") { IsStreamingPartial = true };

        projection.Apply(new TextAssistStartedEvent("correction", "English", null));
        projection.Apply(fragment);
        projection.Apply(fragment);
        projection.Apply(new TextAssistCompletedEvent());

        projection.EnsureComplete();
        Assert.AreEqual("very very ", projection.CorrectedText);
    }

    [TestMethod]
    public async Task AutomaticRun_CompletesCommandLifecycleAndAllowsManualRun()
    {
        var viewModel = CreateViewModel();
        var executionStates = new List<bool>();
        using var subscription = viewModel.RunCommand.IsExecuting.Subscribe(executionStates.Add);

        await viewModel.RunNowAsync();

        Assert.AreEqual(1, viewModel.ExecutionCount);
        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsTrue(((System.Windows.Input.ICommand)viewModel.RunCommand).CanExecute(null));
        CollectionAssert.Contains(executionStates, true);
        Assert.IsFalse(executionStates[^1]);

        await viewModel.RunCommand.Execute();

        Assert.AreEqual(2, viewModel.ExecutionCount);
        Assert.IsTrue(((System.Windows.Input.ICommand)viewModel.RunCommand).CanExecute(null));
    }

    private static TestTextAssistEditorViewModel CreateViewModel()
    {
        var settingsUseCases = new StubSettingsUseCases(CreateSettings());
        var settings = new SettingsSession(settingsUseCases);
        Assert.IsTrue(settings.AttachCurrent().IsSuccess);
        var languages = new TranslationLanguageOptions(new StubLanguageCatalog());
        return new TestTextAssistEditorViewModel(settings, languages);
    }

    private static SettingsBundle CreateSettings()
    {
        var source = CreateLanguage("auto");
        var target = CreateLanguage("zh-Hans");
        return new SettingsBundle(
            new GeneralSettings(
                source, target, null, target, ClosingBehavior.Ask, TranslationEngineNames.AiModel,
                null, null, null, null, ThemeMode.Light, null, null, null, true, false),
            new AiModelSettings([]),
            new MachineTranslationSettings(
                new BaiduTranslationSettings(false, "baidu", []),
                new TencentTranslationSettings(false, "tencent", []),
                new GoogleTranslationSettings(false, "google", "nmt", []),
                new DeepLTranslationSettings(false, "deepl", "latency_optimized", [])),
            new ProxySettings(string.Empty),
            new ShortcutSettings([]),
            new PromptSettings(string.Empty, []),
            new ResultSettings(
                5000, 18, false, 50, "AcrylicBlur", "#00000000", "#FFFFFFFF",
                string.Empty, "#CC000000", ResultWindowMode.Classic, ResultReadAloudMode.None),
            new InputSettings(
                "AcrylicBlur", "#CC000000", "#FFFFFFFF", 10, InputDeliveryMode.Paste,
                true, "auto", "en", true),
            new ScreenshotSettings("Precise", []),
            new SpeechRecognitionSettings(
                string.Empty, false, false, string.Empty, string.Empty, 0, 1,
                FloatingDisplayMode.Segmented, 2, 0, SubtitleSource.Original, 20,
                "Microsoft YaHei UI", "#FFFFFFFF", SubtitleSource.Translated, 16,
                "Microsoft YaHei UI", "#FFCCCCCC", "#99000000", "#00000000", 0.8,
                false, "Horizontal", -1, -1, -1, -1),
            new SelectionTranslationSettings(
                false, "AI", null, null, null, SelectionTriggerMode.All,
                true, false, false, false),
            new TtsSettings(
                "EdgeTTS",
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)),
            new TextAssistSettings(
                false, "auto", "zh-Hans", TranslationEngineNames.AiModel, null,
                null, null, null, null, false, true, true, "Baidu"),
            new OcrSettings(false));
    }

    private static LanguageSettings CreateLanguage(string id) => new(
        id,
        id,
        id,
        string.Empty,
        id,
        id,
        new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class TestTextAssistEditorViewModel(
        SettingsSession settings,
        TranslationLanguageOptions languages)
        : TextAssistEditorViewModel(
            settings,
            languages,
            new UnusedTextAssistUseCases(),
            correction: false,
            NullLogger.Instance)
    {
        public int ExecutionCount { get; private set; }

        protected override async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            await Task.Yield();
        }
    }

    private sealed class StubLanguageCatalog : ITranslationLanguageCatalog
    {
        public IReadOnlyList<TranslationLanguage> All { get; } =
        [
            new("auto", "Auto Detect"),
            new("zh-Hans", "Chinese (Simplified)")
        ];

        public TranslationLanguage Get(string id) =>
            All.First(language => language.Id == id);
    }

    private sealed class UnusedTextAssistUseCases : ITextAssistUseCases
    {
        public TextAssistProfile ResolveProfile(TextAssistOperation operation) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TextAssistEvent> StreamAsync(
            TextAssistRequest request,
            CancellationToken cancellationToken = default) => EmptyEvents();

        private static async IAsyncEnumerable<TextAssistEvent> EmptyEvents()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubSettingsUseCases(SettingsBundle current) : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }

        public bool IsInitialized => true;
        public SettingsBundle Current { get; private set; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section, settings));
            return Result.Success();
        }

        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync()
        {
            SettingsChanged = null;
            return ValueTask.CompletedTask;
        }
    }
}
