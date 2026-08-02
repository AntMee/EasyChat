using System.Globalization;
using Avalonia;
using Avalonia.ReactiveUI;
using EasyChat.Application.DependencyInjection;
using EasyChat.Contracts.Shell;
using EasyChat.Contracts.Translation;
using EasyChat.DependencyInjection;
using EasyChat.Infrastructure.DependencyInjection;
using EasyChat.Infrastructure.Windows.DependencyInjection;
using EasyChat.Infrastructure.Windows.Input;
using EasyChat.Lang;
using EasyChat.Presentation.DependencyInjection;
using EasyChat.Presentation.Features.Settings.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EasyChat;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "--clipboard-worker", StringComparison.Ordinal))
        {
            WindowsClipboardWorker.Run(args[1]);
            return;
        }

        if (args.Length == 1 && string.Equals(args[0], "--verify-composition", StringComparison.Ordinal))
        {
            using var verificationServices = BuildServices();
            return;
        }

        Velopack.VelopackApp.Build().Run();
        var services = BuildServices();
        InitializeSettings(services);
        BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
    }

    private static ServiceProvider BuildServices()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "Logs", "log_.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
            builder.AddConsole();
            builder.AddDebug();
        });
        services.AddEasyChatInfrastructure();
        services.AddEasyChatWindowsInfrastructure();
        services.AddEasyChatApplication(new TranslationMessages(Resources.RequestError));
        services.AddEasyChatPresentation();
        services.AddEasyChatWindowsDesktop();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static void InitializeSettings(IServiceProvider services)
    {
        var started = services.GetRequiredService<IShellLifecycle>()
            .StartAsync().AsTask().GetAwaiter().GetResult();
        if (started.IsFailure)
            throw new InvalidOperationException(started.Error.Message);
        var attached = services.GetRequiredService<SettingsSession>().AttachCurrent();
        if (attached.IsFailure)
            throw new InvalidOperationException(attached.Error.Message);

        var language = services.GetRequiredService<SettingsSession>().General.DisplayLanguage;
        var culture = string.Equals(language, "Simplified Chinese", StringComparison.Ordinal)
            ? new CultureInfo("zh-CN")
            : new CultureInfo("en-US");
        Resources.Culture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
