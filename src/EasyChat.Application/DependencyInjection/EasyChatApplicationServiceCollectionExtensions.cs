using EasyChat.Application.ImageTranslation;
using EasyChat.Application.Input;
using EasyChat.Application.Ocr;
using EasyChat.Application.Settings;
using EasyChat.Application.Shortcuts;
using EasyChat.Application.Shell;
using EasyChat.Application.Translation;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Input;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shortcuts;
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
        services.AddSingleton<IOcrRecognitionUseCases, OcrRecognitionUseCases>();
        services.AddSingleton<IOcrModelUseCases, OcrModelUseCases>();
        services.AddSingleton<IImageTranslationUseCases, ImageTranslationUseCases>();
        services.AddSingleton<IInputDeliveryUseCases, InputDeliveryUseCases>();
        services.AddSingleton<IShortcutUseCases, ShortcutCoordinator>();
        services.AddSingleton<IShellLifecycle, ShellLifecycle>();
        return services;
    }
}
