using EasyChat.Application.Speech;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Speech;

[TestClass]
public sealed class SpeechRecognitionModelDownloadUseCasesTests
{
    [TestMethod]
    public async Task DownloadModelAsync_AppliesCurrentProxyPolicy()
    {
        var settings = SettingsTestData.CreateBundle() with
        {
            Proxy = new ProxySettings("http://127.0.0.1:7890")
        };
        var store = new FakeSpeechRecognitionModelDownloadStore();
        var useCases = new SpeechRecognitionModelDownloadUseCases(
            store,
            new FakeSettingsUseCases(settings));
        var package = store.ModelPackages[0];

        await useCases.DownloadModelAsync(package);

        Assert.AreEqual(package.Id, store.DownloadedPackage?.Id);
        Assert.AreEqual(NetworkProxyMode.Custom, store.Options?.ProxyMode);
        Assert.AreEqual("http://127.0.0.1:7890", store.Options?.ProxyUrl);
    }

    private sealed class FakeSpeechRecognitionModelDownloadStore : ISpeechRecognitionModelDownloadStore
    {
        public IReadOnlyList<SpeechRecognitionModelDownloadPackage> ModelPackages { get; } =
        [
            new("en-US", new Uri("https://example.com/en-US.zip"))
        ];

        public SpeechRecognitionModelDownloadPackage? DownloadedPackage { get; private set; }
        public SpeechRecognitionModelDownloadOptions? Options { get; private set; }

        public Task<SpeechRecognitionModelImportResult> DownloadModelAsync(
            SpeechRecognitionModelDownloadPackage package,
            SpeechRecognitionModelDownloadOptions options,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedPackage = package;
            Options = options;
            return Task.FromResult(new SpeechRecognitionModelImportResult([], []));
        }
    }

    private sealed class FakeSettingsUseCases(SettingsBundle current) : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }

        public bool IsInitialized => true;
        public SettingsBundle Current { get; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings) => Result.Success();
        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
