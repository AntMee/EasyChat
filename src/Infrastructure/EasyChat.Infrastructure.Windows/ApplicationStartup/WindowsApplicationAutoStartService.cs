using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using Microsoft.Win32;

namespace EasyChat.Infrastructure.Windows.ApplicationStartup;

internal sealed class WindowsApplicationAutoStartService : IApplicationAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EasyChat";

    public Result<bool> GetEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception)
        {
            return Result<bool>.Failure(new Error("autostart.read-failed", exception.Message));
        }
    }

    public Result SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return Result.Success();
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return Result.Failure(new Error(
                    "autostart.process-path-unavailable",
                    "Unable to determine the application executable path."));
            }

            key.SetValue(
                ValueName,
                $"\"{processPath}\" {ApplicationStartupArguments.AutoStart}",
                RegistryValueKind.String);
            return Result.Success();
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("autostart.write-failed", exception.Message));
        }
    }
}
