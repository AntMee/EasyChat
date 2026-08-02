using EasyChat.Contracts.ImageTranslation;
using EasyChat.Presentation.ImageTranslation;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Presentation.DependencyInjection;

public static class EasyChatPresentationServiceCollectionExtensions
{
    public static IServiceCollection AddEasyChatPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IImageTranslationRenderer, AvaloniaImageTranslationRenderer>();
        return services;
    }
}
