using EasyChat.Desktop;
using EasyChat.Desktop.Windows.DependencyInjection;
using EasyChat.Infrastructure.Windows.DependencyInjection;
using EasyChat.Infrastructure.Windows.Input;

namespace EasyChat.Desktop.Windows;

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

        DesktopApplication.Run(
            args,
            services =>
            {
                services.AddEasyChatWindowsInfrastructure();
                services.AddEasyChatWindowsDesktop();
            },
            () => Velopack.VelopackApp.Build().Run());
    }
}
