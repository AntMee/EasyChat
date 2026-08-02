using EasyChat.Presentation.Foundation.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.DependencyInjection;

public static class WindowsDesktopServiceCollectionExtensions
{
    public static IServiceCollection AddEasyChatWindowsDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPlatformWindowBehavior, AvaloniaWindowsWindowBehavior>();
        return services;
    }
}
