using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using Microsoft.Win32;

namespace EasyChat.Infrastructure.Windows.ApplicationStartup;

internal sealed class WindowsApplicationAutoStartService : IApplicationAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EasyChat";

    private readonly WindowsScheduledAutoStartTask _scheduledTask = new();

    public Result<bool> GetEnabled()
    {
        try
        {
            if (_scheduledTask.IsEnabled())
                return true;

            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
            {
                if (key?.GetValue(ValueName) is not string legacyValue || string.IsNullOrWhiteSpace(legacyValue))
                    return false;
            }

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return Result<bool>.Failure(new Error(
                    "autostart.process-path-unavailable",
                    "Unable to determine the application executable path."));
            }

            var migration = _scheduledTask.SetEnabled(true, processPath);
            if (migration.IsFailure)
                return Result<bool>.Failure(migration.Error);

            DeleteLegacyRunEntry();
            return true;
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
            var processPath = Environment.ProcessPath;
            if (enabled && string.IsNullOrWhiteSpace(processPath))
            {
                return Result.Failure(new Error(
                    "autostart.process-path-unavailable",
                    "Unable to determine the application executable path."));
            }

            var task = _scheduledTask.SetEnabled(enabled, processPath ?? string.Empty);
            if (task.IsFailure)
                return task;

            DeleteLegacyRunEntry();
            return Result.Success();
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("autostart.write-failed", exception.Message));
        }
    }

    private static void DeleteLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
