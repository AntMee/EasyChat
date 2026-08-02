using EasyChat.Application.Settings;
using EasyChat.Application.Shell;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shell;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Application.DependencyInjection;

public static class EasyChatApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddEasyChatApplication(
        this IServiceCollection services,
        TranslationMessages translationMessages)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(translationMessages);

        services.AddSingleton(translationMessages);
        services.AddSingleton<ISettingsUseCases, SettingsCoordinator>();
        services.AddSingleton<ITranslationUseCases, TranslationUseCases>();
        services.AddSingleton<IShellLifecycle, ShellLifecycle>();
        return services;
    }
}
