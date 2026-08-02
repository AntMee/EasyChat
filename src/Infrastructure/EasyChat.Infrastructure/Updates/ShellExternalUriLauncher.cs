using System.Diagnostics;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Updates;

public sealed class ShellExternalUriLauncher : IExternalUriLauncher
{
    public Result Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Result.Success();
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("platform.uri-open-failed", exception.Message));
        }
    }
}
