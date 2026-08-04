using EasyChat.Application.DependencyInjection;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.DependencyInjection;
using EasyChat.Shared.Results;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class RealMachineTranslationSmokeTests
{
    private const string AppIdVariable = "EASYCHAT_TEST_BAIDU_APP_ID";
    private const string AppKeyVariable = "EASYCHAT_TEST_BAIDU_APP_KEY";

    [TestMethod]
    [TestCategory("Live")]
    public async Task ConfiguredBaiduId_TranslatesThroughApplicationResolver()
    {
        var credentials = ReadCredentialsOrMarkInconclusive();
        const string providerId = "live-baidu-provider";
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            "EasyChat.LiveMachineTranslation",
            Guid.NewGuid().ToString("N"));

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEasyChatInfrastructure(configurationDirectory);
            services.AddEasyChatApplication(new TranslationMessages("request failed"));
            await using var provider = services.BuildServiceProvider();
            var persistedSettings = provider.GetRequiredService<ISettingsUseCases>();
            var initialized = await persistedSettings.InitializeAsync();
            Assert.IsTrue(initialized.IsSuccess, initialized.Error.Message);

            var currentMachine = persistedSettings.Current.MachineTranslation;
            var settings = new InMemorySettingsUseCases(
                persistedSettings.Current with
                {
                    MachineTranslation = currentMachine with
                    {
                        Baidu = new BaiduTranslationSettings(
                            UseProxy: false,
                            providerId,
                            [credentials])
                    }
                });
            var translation = new TranslationUseCases(
                settings,
                provider.GetRequiredService<ITranslationProviderFactory>(),
                provider.GetRequiredService<ITranslationFailureSink>(),
                new TranslationMessages("request failed"));
            var selection = new TranslationProviderSelection(
                TranslationEngineNames.MachineTrans,
                MachineProviderId: providerId);
            var session = translation.Prepare(selection);
            using var sessionLifetime = session as IDisposable;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var response = await session.TranslateAsync(
                new TranslationRequest(
                    "Good morning.",
                    new TranslationLanguage(
                        "en-US",
                        "English",
                        ProviderCodes: new Dictionary<string, string>
                        {
                            [MachineTranslationProviderNames.Baidu] = "en"
                        }),
                    new TranslationLanguage(
                        "zh-Hans",
                        "Simplified Chinese",
                        ProviderCodes: new Dictionary<string, string>
                        {
                            [MachineTranslationProviderNames.Baidu] = "zh"
                        })),
                timeout.Token);

            Assert.IsFalse(string.IsNullOrWhiteSpace(response.Text));
            Assert.IsTrue(
                response.Text.Any(IsCjk),
                "Baidu completed without returning a Chinese translation.");
        }
        finally
        {
            if (Directory.Exists(configurationDirectory))
                Directory.Delete(configurationDirectory, recursive: true);
        }
    }

    private static BaiduCredentialSettings ReadCredentialsOrMarkInconclusive()
    {
        var appId = Environment.GetEnvironmentVariable(AppIdVariable);
        var appKey = Environment.GetEnvironmentVariable(AppKeyVariable);
        var missing = new[]
        {
            (Name: AppIdVariable, Value: appId),
            (Name: AppKeyVariable, Value: appKey)
        }.Where(variable => string.IsNullOrWhiteSpace(variable.Value))
            .Select(variable => variable.Name)
            .ToArray();

        if (missing.Length > 0)
            Assert.Inconclusive($"Set {string.Join(", ", missing)} to run the live Baidu smoke test.");

        return new BaiduCredentialSettings(appId!, appKey!);
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';

    private sealed class InMemorySettingsUseCases(SettingsBundle current) : ISettingsUseCases
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
        public SettingsBundle Current { get; private set; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings)
        {
            Current = settings;
            return Result.Success();
        }

        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
