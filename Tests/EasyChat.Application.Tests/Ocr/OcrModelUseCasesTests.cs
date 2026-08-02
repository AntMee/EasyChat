using EasyChat.Application.Ocr;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Ocr;

[TestClass]
public sealed class OcrModelUseCasesTests
{
    [TestMethod]
    public async Task DownloadModelAsync_AppliesCurrentProxyPolicy()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            Proxy = new ProxySettings("http://127.0.0.1:7890"),
            Ocr = new OcrSettings(true)
        };
        var store = new FakeOcrModelStore();
        var useCases = new OcrModelUseCases(store, new FakeSettingsUseCases(bundle));

        await useCases.DownloadModelAsync(OcrLanguages.English);

        Assert.AreEqual(OcrLanguages.English, store.DownloadedLanguage);
        Assert.AreEqual("http://127.0.0.1:7890", store.Options?.ProxyUrl);
        Assert.IsTrue(store.Options?.UseProxy);
    }

    private sealed class FakeOcrModelStore : IOcrModelStore
    {
        public IReadOnlyList<OcrLanguage> SupportedLanguages => OcrLanguages.Supported;
        public bool CanDeleteModels => true;
        public OcrLanguage? DownloadedLanguage { get; private set; }
        public OcrModelDownloadOptions? Options { get; private set; }

        public bool IsModelDownloaded(OcrLanguage language) => false;

        public Task DownloadModelAsync(
            OcrLanguage language,
            OcrModelDownloadOptions options,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadedLanguage = language;
            Options = options;
            return Task.CompletedTask;
        }

        public void DeleteModel(OcrLanguage language)
        {
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
