using EasyChat.Application.DependencyInjection;
using EasyChat.Application.Settings;
using EasyChat.Application.Shell;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shell;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.DependencyInjection;
using EasyChat.Infrastructure.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class CompositionRegistrationTests
{
    [TestMethod]
    public async Task BatchTwoModules_BuildAndResolveToOwnedImplementations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<ILogger<LoggingTranslationFailureSink>>(
            NullLogger<LoggingTranslationFailureSink>.Instance);
        services.AddEasyChatInfrastructure(Path.Combine(
            Path.GetTempPath(),
            "EasyChat.RefactorV2.Acceptance",
            "Configuration"));
        services.AddEasyChatApplication(new TranslationMessages("request failed"));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.IsInstanceOfType<SettingsCoordinator>(
            provider.GetRequiredService<ISettingsUseCases>());
        Assert.IsInstanceOfType<TranslationUseCases>(
            provider.GetRequiredService<ITranslationUseCases>());
        Assert.IsInstanceOfType<ShellLifecycle>(
            provider.GetRequiredService<IShellLifecycle>());
    }
}
