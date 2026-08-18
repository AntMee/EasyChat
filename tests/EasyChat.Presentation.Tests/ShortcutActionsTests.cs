using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Selection;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.SelectionTranslation;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Shortcuts;
using EasyChat.Presentation.Features.Translation;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class ShortcutActionsTests
{
    [TestMethod]
    public async Task SelectionToolbarShortcut_RespectsExplanationSetting()
    {
        var settings = CreateSettingsSession(explanationEnabled: false);
        var sink = new RecordingSelectionSink();
        var action = new SelectionTranslateShortcutAction(
            new SelectedTextUseCases(),
            sink,
            new UnusedTranslationWindowCoordinator(),
            new FixedPointerPosition(),
            settings,
            NullLogger<SelectionTranslateShortcutAction>.Instance);

        await action.ExecuteAsync(
            new ShortcutParameterSettings(
                Engine: string.Empty,
                EngineId: null,
                Source: null,
                Target: null,
                Value: null,
                ReadSelectedText: null,
                InputTranslateBeforeKey: null,
                InputTranslateAfterKey: null,
                ReplaceCurrentInput: null,
                TextAssistMode: null,
                ShowSelectionToolbar: true));

        Assert.IsNotNull(sink.Capture);
        Assert.IsTrue(sink.Capture.Toolbar.Translation);
        Assert.IsTrue(sink.Capture.Toolbar.Correction);
        Assert.IsFalse(sink.Capture.Toolbar.Polish);
        Assert.IsTrue(sink.Capture.Toolbar.Summary);
        Assert.IsFalse(sink.Capture.Toolbar.Explanation);
    }

    private static SettingsSession CreateSettingsSession(bool explanationEnabled)
    {
        var source = CreateLanguage("auto");
        var target = CreateLanguage("zh-Hans");
        var bundle = new SettingsBundle(
            new GeneralSettings(
                source, target, null, target, ClosingBehavior.Ask, "AiModel",
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
                true, true, false, true, explanationEnabled),
            new TtsSettings(
                "EdgeTTS",
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)),
            new TextAssistSettings(
                "auto", "zh-Hans", "AiModel", null,
                null, null, null, null, false, true, true, "Baidu"),
            new OcrSettings(false));
        var session = new SettingsSession(new StubSettingsUseCases(bundle));
        Assert.IsTrue(session.AttachCurrent().IsSuccess);
        return session;
    }

    private static LanguageSettings CreateLanguage(string id) => new(
        id,
        id,
        id,
        string.Empty,
        id,
        id,
        new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class SelectedTextUseCases : ISelectedTextUseCases
    {
        public ValueTask<Result<SelectedText>> CaptureAsync(
            SelectedTextCaptureCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SelectedText>.Success(new SelectedText(
                "selected",
                new ExternalTargetToken("source"),
                "test",
                new PhysicalScreenPoint(12, 34))));
    }

    private sealed class RecordingSelectionSink : ISelectionInteractionSink
    {
        public SelectionCapture? Capture { get; private set; }

        public ValueTask<SelectionSurfaceState> InspectSurfaceAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(default(SelectionSurfaceState));

        public ValueTask OnMonitoringStartedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnExternalPointerPressedAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnSelectionCapturedAsync(
            SelectionCapture capture,
            CancellationToken cancellationToken = default)
        {
            Capture = capture;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedPointerPosition : IPointerPosition
    {
        public PhysicalScreenPoint GetCurrent() => new(12, 34);
    }

    private sealed class UnusedTranslationWindowCoordinator : ITranslationWindowCoordinator
    {
        public ValueTask PrewarmAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ShowSentenceAsync(
            string text,
            PhysicalScreenPoint? anchor = null,
            bool showCloseButton = true,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ShowDictionaryAsync(
            string text,
            string sourceLanguageId,
            string targetLanguageId,
            bool centerOnScreen = false,
            PhysicalScreenPoint? anchor = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> ContainsAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsVisibleAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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
