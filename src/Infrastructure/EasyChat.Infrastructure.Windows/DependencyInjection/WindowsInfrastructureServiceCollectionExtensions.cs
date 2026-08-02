using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Capture;
using EasyChat.Infrastructure.Windows.ImageTranslation;
using EasyChat.Infrastructure.Windows.Hotkeys;
using EasyChat.Infrastructure.Windows.Input;
using EasyChat.Infrastructure.Windows.Ocr;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Infrastructure.Windows.DependencyInjection;

public static class WindowsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddEasyChatWindowsInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The Windows infrastructure module can only be registered on Windows.");

        services.AddSingleton<IPlatformCapabilities, WindowsPlatformCapabilities>();
        services.AddSingleton<IScreenCapture, WindowsScreenCapture>();
        services.AddSingleton<IScreenCatalog, WindowsScreenCatalog>();
        services.AddSingleton<IGlobalHotkeys, WindowsGlobalHotkeys>();
        services.AddSingleton<IWindowFocus, WindowsWindowFocus>();
        services.AddSingleton<IWindowInputTransparency, WindowsWindowInputTransparency>();
        services.AddSingleton<ITextSelection, WindowsTextSelection>();
        services.AddSingleton<WindowsClipboardSnapshots>();
        services.AddSingleton<IClipboardSnapshots>(provider =>
            provider.GetRequiredService<WindowsClipboardSnapshots>());
        services.AddSingleton<IClipboardText, WindowsClipboardText>();
        services.AddSingleton<ITextDelivery, WindowsTextDelivery>();
        services.AddSingleton<IImageBackgroundCleaner, WindowsImageBackgroundCleaner>();
        services.AddSingleton<WindowsPaddleOcr>();
        services.AddSingleton<IOcrRecognizer>(provider =>
            provider.GetRequiredService<WindowsPaddleOcr>());
        services.AddSingleton<IOcrModelStore>(provider =>
            provider.GetRequiredService<WindowsPaddleOcr>());
        return services;
    }
}
