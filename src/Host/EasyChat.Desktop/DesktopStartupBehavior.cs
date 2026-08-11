using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Platform;

namespace EasyChat.Desktop;

internal static class DesktopStartupBehavior
{
    public static bool ShouldStartInTray(
        IEnumerable<string> arguments,
        ClosingBehavior closingBehavior) =>
        closingBehavior == ClosingBehavior.MinimizeToTray
        && arguments.Any(argument => string.Equals(
            argument,
            ApplicationStartupArguments.AutoStart,
            StringComparison.Ordinal));
}
