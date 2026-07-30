using System;
using Avalonia;
using Avalonia.ReactiveUI;
using EasyChat.Services.Platform;

namespace EasyChat;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "--clipboard-worker", StringComparison.Ordinal))
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsClipboardProcessClient.RunWorker(args[1]);
            }
            return;
        }

        Velopack.VelopackApp.Build()
            .Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }
    
    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }
}
