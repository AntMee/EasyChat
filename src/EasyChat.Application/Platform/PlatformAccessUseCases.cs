using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Platform;

public sealed class PlatformAccessUseCases(
    IPlatformCapabilities capabilities,
    IPlatformPermissionRequester permissions) : IPlatformAccessUseCases
{
    public async ValueTask<Result<CapabilityStatus>> EnsureAvailableAsync(
        PlatformCapability capability,
        CancellationToken cancellationToken = default)
    {
        var status = await capabilities.GetStatusAsync(capability, cancellationToken)
            .ConfigureAwait(false);
        if (status.State == CapabilityState.Available)
            return Result<CapabilityStatus>.Success(status);

        if (status.State == CapabilityState.Unsupported)
            return Failure(capability, "unsupported", status.Reason);

        if (status.RequiredPermission is not { } permission)
        {
            return Failure(
                capability,
                "permission-unspecified",
                status.Reason ?? "The platform did not identify the required permission.");
        }

        var requested = await permissions.RequestAsync(permission, cancellationToken)
            .ConfigureAwait(false);
        if (requested.IsFailure)
            return Result<CapabilityStatus>.Failure(requested.Error);
        if (requested.Value.State != PermissionState.Granted)
        {
            return Failure(
                capability,
                requested.Value.State == PermissionState.Unsupported
                    ? "permission-unsupported"
                    : "permission-denied",
                requested.Value.Reason);
        }

        var refreshed = await capabilities.GetStatusAsync(capability, cancellationToken)
            .ConfigureAwait(false);
        return refreshed.State switch
        {
            CapabilityState.Available => Result<CapabilityStatus>.Success(refreshed),
            CapabilityState.Unsupported => Failure(capability, "unsupported", refreshed.Reason),
            _ => Failure(
                capability,
                "permission-not-effective",
                refreshed.Reason ??
                $"The platform permission '{permission}' was granted but the capability is not available yet.")
        };
    }

    public async ValueTask<Result<PermissionStatus>> EnsurePermissionAsync(
        PlatformPermission permission,
        CancellationToken cancellationToken = default)
    {
        var requested = await permissions.RequestAsync(permission, cancellationToken)
            .ConfigureAwait(false);
        if (requested.IsFailure)
            return Result<PermissionStatus>.Failure(requested.Error);
        if (requested.Value.State == PermissionState.Granted)
            return requested;

        var suffix = requested.Value.State == PermissionState.Unsupported
            ? "unsupported"
            : "denied";
        return Result<PermissionStatus>.Failure(new Error(
            $"platform.permission.{permission.ToString().ToLowerInvariant()}.{suffix}",
            requested.Value.Reason ?? $"The platform permission '{permission}' was not granted."));
    }

    private static Result<CapabilityStatus> Failure(
        PlatformCapability capability,
        string suffix,
        string? reason) =>
        Result<CapabilityStatus>.Failure(new Error(
            $"platform.{capability.ToString().ToLowerInvariant()}.{suffix}",
            reason ?? $"The platform capability '{capability}' is not available."));
}
