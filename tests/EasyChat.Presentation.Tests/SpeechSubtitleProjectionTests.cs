using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Features.Speech;
using EasyChat.Presentation.Features.Speech.Views;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Shared.Results;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SpeechSubtitleProjectionTests
{
    [TestMethod]
    public void TemporaryAndFinalUpdatesWithSameId_ReuseOneItemInBothCollections()
    {
        var projection = new SpeechSubtitleProjection();

        var temporary = projection.Update(CreateLine(5, "draft", string.Empty, isTemporary: true))!;
        var final = projection.Update(CreateLine(5, "final", "translation"))!;
        var repeated = projection.Update(CreateLine(5, "final", "translation"))!;

        Assert.AreSame(temporary, final);
        Assert.AreSame(final, repeated);
        Assert.AreEqual("final", temporary.OriginalText);
        Assert.AreEqual("translation", temporary.TranslatedText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(temporary, projection.SubtitleItems[0]);
        Assert.AreSame(temporary, projection.FloatingSubtitles[0]);
    }

    [TestMethod]
    public void StableTimestampsAcrossMidnight_AreInsertedByTimestampThenIdInBothCollections()
    {
        var projection = new SpeechSubtitleProjection();
        var nextDay = TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);

        projection.Update(CreateLine(40, "later id", "translation", timestamp: nextDay));
        projection.Update(CreateLine(30, "earlier id", "translation", timestamp: nextDay));
        projection.Update(CreateLine(50, "previous day", "translation", timestamp: new TimeSpan(23, 59, 0)));

        long[] expectedOrder = [50, 30, 40];
        CollectionAssert.AreEqual(expectedOrder, projection.SubtitleItems.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(expectedOrder, projection.FloatingSubtitles.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [DataRow(FloatingDisplayMode.Segmented, true, true)]
    [DataRow(FloatingDisplayMode.Segmented, false, false)]
    [DataRow(FloatingDisplayMode.AutoScroll, true, false)]
    [DataRow(FloatingDisplayMode.AutoScroll, false, false)]
    public void MaxSentencesPerLineVisibility_RequiresSegmentedMachineTranslation(
        FloatingDisplayMode displayMode,
        bool isMachineTranslation,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            SpeechRecognitionViewModel.ShouldShowMaxSentencesPerLine(
                displayMode,
                isMachineTranslation));
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    public void RealTimePreviewVisibility_RequiresEnabledMachineTranslation(
        bool isTranslationEnabled,
        bool isMachineTranslation,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            SpeechRecognitionViewModel.ShouldShowRealTimePreview(
                isTranslationEnabled,
                isMachineTranslation));
    }

    [TestMethod]
    public void VirtualCableAvailability_RequiresPlaybackAndCaptureEndpoints()
    {
        var playback = new AudioPlaybackDeviceDescriptor(
            new AudioPlaybackDeviceToken("playback"),
            "CABLE Input (VB-Audio Virtual Cable)",
            "CABLE Input (VB-Audio Virtual Cable)",
            null,
            IsVirtualCable: true);
        var capture = new AudioCaptureSourceDescriptor(
            new AudioCaptureSourceToken("capture"),
            AudioCaptureSourceKind.Microphone,
            "CABLE Output (VB-Audio Virtual Cable)",
            "CABLE Output (VB-Audio Virtual Cable)",
            null,
            ReadOnlyMemory<byte>.Empty,
            IsVirtualCable: true);

        Assert.IsFalse(SpeechRecognitionViewModel.HasVirtualCableEndpoints([playback], []));
        Assert.IsFalse(SpeechRecognitionViewModel.HasVirtualCableEndpoints([], [capture]));
        Assert.IsTrue(SpeechRecognitionViewModel.HasVirtualCableEndpoints([playback], [capture]));
    }

    [TestMethod]
    public void MachineEngineOptions_UseConfiguredIdsAndCanonicalProviderNames()
    {
        var machineSettings = CreateLiveMachineTranslationSettings();

        var options = SpeechRecognitionViewModel.CreateMachineEngineOptions(machineSettings);

        CollectionAssert.AreEqual(
            new[]
            {
                MachineTranslationProviderNames.Baidu,
                MachineTranslationProviderNames.Tencent,
                MachineTranslationProviderNames.Google,
                MachineTranslationProviderNames.DeepL
            },
            options.Select(option => option.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "baidu-id", "tencent-id", "google-id", "deepl-id" },
            options.Select(option => option.Id).ToArray());
        Assert.IsTrue(options.All(option => option.IsMachine));
    }

    [TestMethod]
    public void LegacyMachineProviderName_ResolvesProviderAndMigratesPersistedId()
    {
        var settings = CreateLiveSpeechSettings(
            MachineTranslationProviderNames.Google,
            engineType: 0);
        var options = SpeechRecognitionViewModel.CreateMachineEngineOptions(
            CreateLiveMachineTranslationSettings());

        var selected = SpeechRecognitionViewModel.ResolveAndSynchronizeEngineOption(
            options,
            settings.EngineId,
            selectedMachine: true,
            settings);

        Assert.IsNotNull(selected);
        Assert.AreEqual(MachineTranslationProviderNames.Google, selected.Name);
        Assert.AreEqual("google-id", selected.Id);
        Assert.AreEqual("google-id", settings.EngineId);
        Assert.AreEqual(0, settings.EngineType);
        Assert.IsTrue(SpeechRecognitionViewModel.MatchesEngineSelection(
            selected,
            MachineTranslationProviderNames.Google,
            selectedMachine: true));
    }

    [TestMethod]
    public void MissingMachineProviderId_FallsBackToConfiguredBaiduId()
    {
        var settings = CreateLiveSpeechSettings("missing-provider-id", engineType: 0);
        var options = SpeechRecognitionViewModel.CreateMachineEngineOptions(
            CreateLiveMachineTranslationSettings());

        var selected = SpeechRecognitionViewModel.ResolveAndSynchronizeEngineOption(
            options,
            settings.EngineId,
            selectedMachine: true,
            settings);

        Assert.IsNotNull(selected);
        Assert.AreEqual(MachineTranslationProviderNames.Baidu, selected.Name);
        Assert.AreEqual("baidu-id", selected.Id);
        Assert.AreEqual("baidu-id", settings.EngineId);
        Assert.AreEqual(0, settings.EngineType);
    }

    [TestMethod]
    public void MachineTargetLanguageSupport_UsesCanonicalProviderNameInsteadOfConfiguredId()
    {
        var google = SpeechRecognitionViewModel.CreateMachineEngineOptions(
                CreateLiveMachineTranslationSettings())
            .Single(option => option.Name == MachineTranslationProviderNames.Google);
        var supported = CreateLanguage(
            "en",
            new Dictionary<string, string>
            {
                [MachineTranslationProviderNames.Google] = "en"
            });
        var idOnly = CreateLanguage(
            "fr",
            new Dictionary<string, string>
            {
                [google.Id] = "fr"
            });

        Assert.IsTrue(SpeechRecognitionViewModel.SupportsTargetLanguage(supported, google));
        Assert.IsFalse(SpeechRecognitionViewModel.SupportsTargetLanguage(idOnly, google));
    }

    [TestMethod]
    public void MissingAiEngineAtStartup_FallsBackAndSynchronizesPersistedSelection()
    {
        var settings = CreateLiveSpeechSettings("missing-ai-model", engineType: 1);
        SpeechEngineOption[] available =
        [
            new(
                MachineTranslationProviderNames.Baidu,
                "baidu-id",
                IsMachine: true)
        ];

        var selected = SpeechRecognitionViewModel.ResolveAndSynchronizeEngineOption(
            available,
            settings.EngineId,
            selectedMachine: false,
            settings);

        Assert.IsNotNull(selected);
        Assert.AreEqual("baidu-id", selected.Id);
        Assert.IsTrue(selected.IsMachine);
        Assert.AreEqual("baidu-id", settings.EngineId);
        Assert.AreEqual(0, settings.EngineType);
    }

    [TestMethod]
    public void RemovingSelectedAiEngine_FallsBackAndSynchronizesPersistedSelection()
    {
        const string aiModelId = "deepseek-model";
        var settings = CreateLiveSpeechSettings(aiModelId, engineType: 1);
        var selected = new SpeechEngineOption("DeepSeek", aiModelId, IsMachine: false);
        SpeechEngineOption[] remaining =
        [
            new(
                MachineTranslationProviderNames.Baidu,
                "baidu-id",
                IsMachine: true)
        ];

        var fallback = SpeechRecognitionViewModel.ResolveAndSynchronizeEngineOption(
            remaining,
            selected.Id,
            selected.IsMachine,
            settings);

        Assert.IsNotNull(fallback);
        Assert.AreEqual("baidu-id", fallback.Id);
        Assert.IsTrue(fallback.IsMachine);
        Assert.AreEqual("baidu-id", settings.EngineId);
        Assert.AreEqual(0, settings.EngineType);
    }

    [TestMethod]
    public void EngineFallback_SynchronizesUnsupportedTargetLanguageFallback()
    {
        const string aiModelId = "deepseek-model";
        var settings = CreateLiveSpeechSettings(
            aiModelId,
            engineType: 1,
            targetLanguage: "am");
        SpeechEngineOption[] remaining =
        [
            new(
                MachineTranslationProviderNames.Baidu,
                "baidu-id",
                IsMachine: true)
        ];

        var selected = SpeechRecognitionViewModel.ResolveAndSynchronizeEngineOption(
            remaining,
            aiModelId,
            selectedMachine: false,
            settings);
        var engineFellBack = !SpeechRecognitionViewModel.MatchesEngineSelection(
            selected,
            aiModelId,
            selectedMachine: false);
        var target = SpeechRecognitionViewModel.ResolveAndSynchronizeTargetLanguage(
            [CreateLanguage("zh-Hans")],
            settings.TargetLanguage,
            engineFellBack,
            settings);

        Assert.IsTrue(engineFellBack);
        Assert.IsNotNull(target);
        Assert.AreEqual("zh-Hans", target.Id);
        Assert.AreEqual("zh-Hans", settings.TargetLanguage);
    }

    [TestMethod]
    public void UnchangedEngineRefresh_DoesNotPersistUiOnlyTargetResolution()
    {
        const string aiModelId = "deepseek-model";
        var settings = CreateLiveSpeechSettings(
            aiModelId,
            engineType: 1,
            targetLanguage: "am");
        var selected = new SpeechEngineOption("DeepSeek", aiModelId, IsMachine: false);

        var engineFellBack = !SpeechRecognitionViewModel.MatchesEngineSelection(
            selected,
            aiModelId,
            selectedMachine: false);
        var target = SpeechRecognitionViewModel.ResolveAndSynchronizeTargetLanguage(
            [CreateLanguage("zh-Hans")],
            settings.TargetLanguage,
            engineFellBack,
            settings);

        Assert.IsFalse(engineFellBack);
        Assert.IsNotNull(target);
        Assert.AreEqual("zh-Hans", target.Id);
        Assert.AreEqual("am", settings.TargetLanguage);
    }

    [TestMethod]
    public void FloatingRemoval_FadesBeforeRemovalAndLateUpdatesRemainInHistoryOnly()
    {
        var projection = new SpeechSubtitleProjection();
        var initial = CreateLine(42, "first", "translated");

        var item = projection.Update(initial)!;
        var fading = projection.BeginFloatingRemoval(initial.Id);

        Assert.AreSame(item, fading);
        Assert.AreEqual(0, item.Opacity);
        CollectionAssert.Contains(projection.FloatingSubtitles, item);

        projection.Update(CreateLine(initial.Id, "corrected", "updated"));

        Assert.AreEqual("corrected", item.OriginalText);
        Assert.AreEqual("updated", item.TranslatedText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);

        projection.CompleteFloatingRemoval(item);
        projection.Update(CreateLine(initial.Id, "final", "final translation"));

        Assert.AreEqual("final", item.OriginalText);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RemovalBeforeFirstUpdate_PreventsLateSubtitleFromEnteringFloatingCollection()
    {
        var projection = new SpeechSubtitleProjection();

        var fading = projection.BeginFloatingRemoval(7);
        var item = projection.Update(CreateLine(7, "late source", "late translation"))!;

        Assert.IsNull(fading);
        CollectionAssert.Contains(projection.SubtitleItems, item);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RepeatedRemoval_IsIdempotent()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(9, "source", "translation"))!;

        var first = projection.BeginFloatingRemoval(item.Id);
        var repeated = projection.BeginFloatingRemoval(item.Id);
        projection.CompleteFloatingRemoval(item);
        projection.CompleteFloatingRemoval(item);

        Assert.AreSame(item, first);
        Assert.IsNull(repeated);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void Clear_RemovesHistoryAndFloatingSubtitles()
    {
        var projection = new SpeechSubtitleProjection();
        projection.Update(CreateLine(1, "source", "translation"));

        projection.Clear();

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void ClearDuringFade_RemainsEmptyWhenRemovalCompletes()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(11, "source", "translation"))!;
        projection.BeginFloatingRemoval(item.Id);

        projection.Clear();
        projection.CompleteFloatingRemoval(item);

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void FloatingDisplayModes_OnlyAutoScrollModeRequestsScrolling()
    {
        Assert.IsFalse(SubtitleOverlayWindowView.UsesAutoScroll(FloatingDisplayMode.Segmented));
        Assert.IsTrue(SubtitleOverlayWindowView.UsesAutoScroll(FloatingDisplayMode.AutoScroll));
    }

    [TestMethod]
    public void ProtectedOverflowFollowsLatestSubtitleInSegmentedMode()
    {
        Assert.IsFalse(SubtitleOverlayWindowView.ShouldFollowLatest(
            FloatingDisplayMode.Segmented,
            visibleCount: 2,
            completedHistoryLimit: 2));
        Assert.IsTrue(SubtitleOverlayWindowView.ShouldFollowLatest(
            FloatingDisplayMode.Segmented,
            visibleCount: 3,
            completedHistoryLimit: 2));
        Assert.IsTrue(SubtitleOverlayWindowView.ShouldFollowLatest(
            FloatingDisplayMode.AutoScroll,
            visibleCount: 1,
            completedHistoryLimit: 2));
    }

    [TestMethod]
    public void StopLoading_ClearsSpinnerWithoutChangingHistoryOrFloatingMembership()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(new SpeechSubtitleLine(
            13,
            TimeSpan.Zero,
            "source",
            string.Empty,
            string.Empty,
            true,
            true))!;

        projection.StopLoading();

        Assert.IsFalse(item.IsTranslating);
        Assert.HasCount(1, projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
    }

    [TestMethod]
    public void RetractionBeforeFloatingRemoval_RemovesHistoryAndPreservesOldContentUntilFade()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(21, "superseded source", "superseded translation"))!;

        var retracted = projection.Update(CreateLine(item.Id, string.Empty, string.Empty));
        var late = projection.Update(CreateLine(item.Id, "late source", "late translation"));

        Assert.AreSame(item, retracted);
        Assert.IsNull(late);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(item, projection.FloatingSubtitles[0]);
        Assert.AreEqual("superseded source", item.OriginalText);
        Assert.AreEqual("superseded translation", item.TranslatedText);
        Assert.AreEqual(1, item.Opacity);

        var fading = projection.BeginFloatingRemoval(item.Id);
        Assert.AreSame(item, fading);
        Assert.AreEqual(0, item.Opacity);
        projection.CompleteFloatingRemoval(item);

        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    [TestMethod]
    public void FloatingRemovalBeforeRetraction_RemovesHistoryAndBlocksUpdatesDuringAndAfterFade()
    {
        var projection = new SpeechSubtitleProjection();
        var item = projection.Update(CreateLine(22, "superseded source", "superseded translation"))!;

        var fading = projection.BeginFloatingRemoval(item.Id);
        var retracted = projection.Update(CreateLine(item.Id, string.Empty, string.Empty));
        var lateDuringFade = projection.Update(CreateLine(item.Id, "late during fade", "late translation"));

        Assert.AreSame(item, fading);
        Assert.AreSame(item, retracted);
        Assert.IsNull(lateDuringFade);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.HasCount(1, projection.FloatingSubtitles);
        Assert.AreSame(item, projection.FloatingSubtitles[0]);
        Assert.AreEqual("superseded source", item.OriginalText);
        Assert.AreEqual("superseded translation", item.TranslatedText);
        Assert.AreEqual(0, item.Opacity);

        projection.CompleteFloatingRemoval(item);
        var lateAfterFade = projection.Update(CreateLine(item.Id, "late after fade", "late translation"));

        Assert.IsNull(lateAfterFade);
        Assert.IsEmpty(projection.SubtitleItems);
        Assert.IsEmpty(projection.FloatingSubtitles);
    }

    private static SpeechSubtitleLine CreateLine(
        long id,
        string original,
        string translated,
        bool isTemporary = false,
        TimeSpan? timestamp = null) =>
        new(
            id,
            timestamp ?? TimeSpan.FromSeconds(id),
            original,
            translated,
            translated,
            false,
            isTemporary);

    private static LiveSpeechRecognitionSettings CreateLiveSpeechSettings(
        string engineId,
        int engineType,
        string targetLanguage = "zh-Hans") =>
        new(
            new SpeechRecognitionSettings(
                "en-US",
                true,
                true,
                targetLanguage,
                engineId,
                engineType,
                1,
                FloatingDisplayMode.Segmented,
                4,
                0,
                SubtitleSource.Original,
                20,
                "Microsoft YaHei UI",
                "#FFFFFFFF",
                SubtitleSource.Translated,
                16,
                "Microsoft YaHei UI",
                "#FFCCCCCC",
                "#99000000",
                "#00000000",
                0.8,
                false,
                "Horizontal",
                -1,
                -1,
                -1,
                -1),
            _ => Result.Success());

    private static LiveMachineTranslationSettings CreateLiveMachineTranslationSettings() =>
        new(
            new MachineTranslationSettings(
                new BaiduTranslationSettings(false, "baidu-id", []),
                new TencentTranslationSettings(false, "tencent-id", []),
                new GoogleTranslationSettings(false, "google-id", "nmt", []),
                new DeepLTranslationSettings(false, "deepl-id", "latency_optimized", [])),
            _ => Result.Success());

    private static LanguageSettings CreateLanguage(
        string id,
        IReadOnlyDictionary<string, string>? providerCodes = null) => new(
        id,
        id,
        id,
        "unknown.png",
        id,
        id,
        providerCodes ?? new Dictionary<string, string>());
}
