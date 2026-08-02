using EasyChat.Contracts.AiModels;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings.Persistence;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Contracts.Updates;
using EasyChat.Infrastructure.AiModels;
using EasyChat.Infrastructure.Settings.Persistence;
using EasyChat.Infrastructure.Speech;
using EasyChat.Infrastructure.Speech.EdgeTts;
using EasyChat.Infrastructure.Translation;
using EasyChat.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Infrastructure.DependencyInjection;

public static class EasyChatInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddEasyChatInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var configurationDirectory = Path.Combine(
            AppContext.BaseDirectory,
#if DEBUG
            "Configuration"
#else
            "..",
            "Configuration"
#endif
        );
        return services.AddEasyChatInfrastructure(configurationDirectory);
    }

    public static IServiceCollection AddEasyChatInfrastructure(
        this IServiceCollection services,
        string configurationDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);

        var fullConfigurationDirectory = Path.GetFullPath(configurationDirectory);
        services.AddSingleton<ISettingsPersistenceGateway>(
            _ => new JsonSettingsPersistenceGateway(fullConfigurationDirectory));
        services.AddHttpClient<IAiModelCatalogTransport, HttpAiModelCatalogTransport>();
        services.AddSingleton<ITranslationProviderFactory, TranslationProviderFactory>();
        services.AddSingleton<ITranslationFailureSink, LoggingTranslationFailureSink>();
        services.AddSingleton<IExternalUriLauncher, ShellExternalUriLauncher>();
        services.AddSingleton<IApplicationUpdateService, VelopackApplicationUpdateService>();
        var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        services.AddSingleton<ITtsSynthesisProvider>(_ => new EdgeTtsProvider(assetsDirectory));
        services.AddSingleton<ITtsOutputWriter, FileTtsOutputWriter>();
        return services;
    }
}
