using EasyChat.Application.Settings;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Settings.Persistence;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Settings;

[TestClass]
public sealed class SettingsCoordinatorTests
{
    [TestMethod]
    public async Task InitializeAsync_MigratesOnceAndSharesConcurrentInitialization()
    {
        var original = SettingsTestData.CreateBundle(
            nativeLanguageMissing: true,
            textAssistFollowsGlobal: true);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeSettingsPersistenceGateway
        {
            ReadHandler = async cancellationToken =>
            {
                readStarted.TrySetResult();
                await releaseRead.Task.WaitAsync(cancellationToken);
                return Result<SettingsBundle>.Success(original);
            }
        };
        await using var coordinator = new SettingsCoordinator(gateway);

        var first = coordinator.InitializeAsync().AsTask();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.InitializeAsync().AsTask();
        releaseRead.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, gateway.ReadCallCount);
        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.AreSame(results[0].Value, results[1].Value);
        Assert.AreSame(original.General.TargetLanguage, results[0].Value.General.NativeLanguage);
        Assert.IsFalse(results[0].Value.TextAssist.FollowGlobal);
        CollectionAssert.AreEqual(
            new[] { SettingsSection.MachineTranslation, SettingsSection.General },
            gateway.Writes.Select(write => write.Section).ToArray());
    }

    [TestMethod]
    public async Task Update_PublishesImmediatelyAndDebouncesEachSection()
    {
        var gateway = new FakeSettingsPersistenceGateway();
        await using var coordinator = new SettingsCoordinator(
            gateway,
            saveDelay: TimeSpan.FromMilliseconds(40));
        var initialized = await coordinator.InitializeAsync();
        Assert.IsTrue(initialized.IsSuccess);
        gateway.ClearWrites();
        var changes = new List<SettingsChangedEventArgs>();
        coordinator.SettingsChanged += (_, change) => changes.Add(change);

        var first = coordinator.Current with { Proxy = new ProxySettings("http://first") };
        var second = first with { Proxy = new ProxySettings("http://second") };
        Assert.IsTrue(coordinator.Update(SettingsSection.Proxy, first).IsSuccess);
        Assert.IsTrue(coordinator.Update(SettingsSection.Proxy, second).IsSuccess);

        Assert.AreSame(second, coordinator.Current);
        Assert.HasCount(2, changes);
        Assert.AreEqual("http://second", changes[1].Current.Proxy.ProxyUrl);
        await gateway.WaitForWriteCountAsync(1);
        await Task.Delay(80);

        Assert.AreEqual(TimeSpan.FromMilliseconds(500), SettingsCoordinator.DefaultSaveDelay);
        Assert.HasCount(1, gateway.Writes);
        Assert.AreEqual(SettingsSection.Proxy, gateway.Writes[0].Section);
        Assert.AreEqual("http://second", gateway.Writes[0].Settings.Proxy.ProxyUrl);
    }

    [TestMethod]
    public async Task FlushAsync_KeepsFailedSectionDirtyAndRetriesIt()
    {
        var gateway = new FakeSettingsPersistenceGateway();
        await using var coordinator = new SettingsCoordinator(
            gateway,
            saveDelay: TimeSpan.FromMinutes(1));
        Assert.IsTrue((await coordinator.InitializeAsync()).IsSuccess);
        gateway.ClearWrites();
        var expectedError = new Error("settings.write-failed", "disk unavailable");
        gateway.WriteHandler = (_, _, _) =>
            ValueTask.FromResult(Result.Failure(expectedError));
        SettingsSaveFailedEventArgs? failure = null;
        coordinator.SaveFailed += (_, args) => failure = args;
        var changed = coordinator.Current with { Ocr = new OcrSettings(true) };
        coordinator.Update(SettingsSection.Ocr, changed);

        var firstFlush = await coordinator.FlushAsync();

        Assert.IsTrue(firstFlush.IsFailure);
        Assert.AreSame(expectedError, firstFlush.Error);
        Assert.IsNotNull(failure);
        Assert.AreEqual(SettingsSection.Ocr, failure.Section);
        Assert.HasCount(1, gateway.Writes);

        gateway.WriteHandler = null;
        var retry = await coordinator.FlushAsync();

        Assert.IsTrue(retry.IsSuccess);
        Assert.HasCount(2, gateway.Writes);
        Assert.IsTrue(gateway.Writes[1].Settings.Ocr.UseProxy);
    }

    [TestMethod]
    public async Task DisposeAsync_FlushesPendingChangesBeforeClosing()
    {
        var gateway = new FakeSettingsPersistenceGateway();
        var coordinator = new SettingsCoordinator(
            gateway,
            saveDelay: TimeSpan.FromMinutes(1));
        Assert.IsTrue((await coordinator.InitializeAsync()).IsSuccess);
        gateway.ClearWrites();
        var changed = coordinator.Current with { Proxy = new ProxySettings("http://exit") };
        coordinator.Update(SettingsSection.Proxy, changed);

        await coordinator.DisposeAsync();

        Assert.HasCount(1, gateway.Writes);
        Assert.AreEqual("http://exit", gateway.Writes[0].Settings.Proxy.ProxyUrl);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            coordinator.Update(SettingsSection.Proxy, changed));
    }

    private sealed record WriteCall(SettingsSection Section, SettingsBundle Settings);

    private sealed class FakeSettingsPersistenceGateway : ISettingsPersistenceGateway
    {
        private readonly object _sync = new();
        private readonly List<WriteCall> _writes = [];
        private int _readCallCount;

        public Func<CancellationToken, ValueTask<Result<SettingsBundle>>>? ReadHandler { get; init; }

        public Func<SettingsSection, SettingsBundle, CancellationToken, ValueTask<Result>>?
            WriteHandler
        { get; set; }

        public int ReadCallCount => Volatile.Read(ref _readCallCount);

        public IReadOnlyList<WriteCall> Writes
        {
            get
            {
                lock (_sync)
                    return _writes.ToArray();
            }
        }

        public ValueTask<Result<SettingsBundle>> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCallCount);
            return ReadHandler?.Invoke(cancellationToken)
                   ?? ValueTask.FromResult(Result<SettingsBundle>.Success(
                       SettingsTestData.CreateBundle()));
        }

        public ValueTask<Result> WriteAllAsync(
            SettingsBundle settings,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> WriteAsync(
            SettingsSection section,
            SettingsBundle settings,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _writes.Add(new WriteCall(section, settings));

            return WriteHandler?.Invoke(section, settings, cancellationToken)
                   ?? ValueTask.FromResult(Result.Success());
        }

        public void ClearWrites()
        {
            lock (_sync)
                _writes.Clear();
        }

        public async Task WaitForWriteCountAsync(int expectedCount)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Writes.Count < expectedCount)
                await Task.Delay(10, timeout.Token);
        }
    }
}

internal static class SettingsTestData
{
    public static SettingsBundle CreateBundle(
        bool nativeLanguageMissing = false,
        bool textAssistFollowsGlobal = false)
    {
        var source = CreateLanguage("auto");
        var target = CreateLanguage("zh-Hans");
        return new SettingsBundle(
            new GeneralSettings(
                source,
                target,
                null,
                nativeLanguageMissing ? null : target,
                ClosingBehavior.Ask,
                "AiModel",
                "OpenAI",
                null,
                null,
                null,
                ThemeMode.Light,
                null,
                null,
                null,
                true,
                false),
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
                5000,
                18,
                false,
                50,
                "AcrylicBlur",
                "#00000000",
                "#FFFFFFFF",
                string.Empty,
                "#CC000000",
                ResultWindowMode.Classic,
                ResultReadAloudMode.None),
            new InputSettings(
                "AcrylicBlur",
                "#CC000000",
                "#FFFFFFFF",
                10,
                InputDeliveryMode.Paste,
                true,
                "auto",
                "en",
                true),
            new ScreenshotSettings("Precise", []),
            new SpeechRecognitionSettings(
                string.Empty,
                false,
                false,
                string.Empty,
                string.Empty,
                0,
                1,
                FloatingDisplayMode.Segmented,
                2,
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
            new SelectionTranslationSettings(
                false,
                "AI",
                null,
                null,
                null,
                SelectionTriggerMode.All,
                true,
                false,
                false,
                false),
            new TtsSettings(
                "EdgeTTS",
                new Dictionary<string, IReadOnlyDictionary<string, string>>(
                    StringComparer.Ordinal)),
            new TextAssistSettings(
                textAssistFollowsGlobal,
                "auto",
                "zh-Hans",
                "AiModel",
                null,
                null,
                null,
                null,
                null,
                false,
                true,
                true,
                "Baidu"),
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
}
