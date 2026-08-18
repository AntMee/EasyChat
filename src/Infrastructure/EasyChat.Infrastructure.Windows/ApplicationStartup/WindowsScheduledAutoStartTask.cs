using System.Diagnostics;
using System.Security.Principal;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows.ApplicationStartup;

internal sealed class WindowsScheduledAutoStartTask
{
    private const string TaskNamePrefix = @"\EasyChat.AutoStart.";

    public bool IsEnabled()
    {
        var taskName = GetTaskName(GetCurrentUserSid());
        return Execute(CreateQueryStartInfo(taskName)).ExitCode == 0;
    }

    public Result SetEnabled(bool enabled, string executablePath)
    {
        try
        {
            var taskName = GetTaskName(GetCurrentUserSid());
            if (!enabled)
            {
                if (!IsEnabled())
                    return Result.Success();

                return ToResult(Execute(CreateDeleteStartInfo(taskName)));
            }

            using var identity = WindowsIdentity.GetCurrent();
            var userName = identity.Name;
            if (string.IsNullOrWhiteSpace(userName))
            {
                return Result.Failure(new Error(
                    "autostart.user-unavailable",
                    "Unable to determine the current Windows user."));
            }

            return ToResult(Execute(CreateRegistrationStartInfo(taskName, executablePath, userName)));
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("autostart.task-failed", exception.Message));
        }
    }

    internal static string GetTaskName(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return TaskNamePrefix + userSid;
    }

    internal static ProcessStartInfo CreateRegistrationStartInfo(
        string taskName,
        string executablePath,
        string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("/Create");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);
        startInfo.ArgumentList.Add("/TR");
        startInfo.ArgumentList.Add(BuildTaskCommand(executablePath));
        startInfo.ArgumentList.Add("/SC");
        startInfo.ArgumentList.Add("ONLOGON");
        startInfo.ArgumentList.Add("/RU");
        startInfo.ArgumentList.Add(userName);
        startInfo.ArgumentList.Add("/IT");
        startInfo.ArgumentList.Add("/RL");
        startInfo.ArgumentList.Add("HIGHEST");
        startInfo.ArgumentList.Add("/DELAY");
        startInfo.ArgumentList.Add("0000:10");
        startInfo.ArgumentList.Add("/F");
        return startInfo;
    }

    internal static string BuildTaskCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" {ApplicationStartupArguments.AutoStart}";
    }

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
               ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");
    }

    private static ProcessStartInfo CreateQueryStartInfo(string taskName)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("/Query");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);
        return startInfo;
    }

    private static ProcessStartInfo CreateDeleteStartInfo(string taskName)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("/Delete");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);
        startInfo.ArgumentList.Add("/F");
        return startInfo;
    }

    private static ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static ScheduledTaskCommandResult Execute(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to start Windows Task Scheduler.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var outputText = output.GetAwaiter().GetResult();
        var errorText = error.GetAwaiter().GetResult();
        return new ScheduledTaskCommandResult(
            process.ExitCode,
            string.IsNullOrWhiteSpace(errorText) ? outputText : errorText);
    }

    private static Result ToResult(ScheduledTaskCommandResult command) => command.ExitCode == 0
        ? Result.Success()
        : Result.Failure(new Error(
            "autostart.task-failed",
            string.IsNullOrWhiteSpace(command.Message)
                ? "Windows Task Scheduler could not update the auto-start task."
                : command.Message.Trim()));

    private sealed record ScheduledTaskCommandResult(int ExitCode, string Message);
}
