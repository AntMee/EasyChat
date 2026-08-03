using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows;

public sealed class WindowsPlatformPermissionRequester : IPlatformPermissionRequester
{
    public ValueTask<Result<PermissionStatus>> RequestAsync(
        PlatformPermission permission,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<PermissionStatus>.Success(new PermissionStatus(
            permission,
            PermissionState.Granted)));
    }
}
