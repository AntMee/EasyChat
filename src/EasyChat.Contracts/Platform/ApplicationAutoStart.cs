using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

public interface IApplicationAutoStartService
{
    Result<bool> GetEnabled();

    Result SetEnabled(bool enabled);
}

public static class ApplicationStartupArguments
{
    public const string AutoStart = "--autostart";
}
