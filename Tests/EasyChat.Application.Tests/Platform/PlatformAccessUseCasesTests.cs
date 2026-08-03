using EasyChat.Application.Platform;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Platform;

[TestClass]
public sealed class PlatformAccessUseCasesTests
{
    [TestMethod]
    public async Task PermissionGatedCapability_BecomesAvailableAfterPermissionGrant()
    {
        var gate = new PermissionGate();
        var useCases = new PlatformAccessUseCases(
            new PermissionGatedCapabilities(gate),
            new GrantedPermissions(gate));

        var result = await useCases.EnsureAvailableAsync(PlatformCapability.ScreenCapture);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(CapabilityState.Available, result.Value.State);
        Assert.IsNull(result.Value.RequiredPermission);
    }

    [TestMethod]
    public async Task GrantedPermission_StillRequiresCapabilityRecheck()
    {
        var useCases = new PlatformAccessUseCases(
            new PermissionGatedCapabilities(new PermissionGate()),
            new GrantedPermissions());

        var result = await useCases.EnsureAvailableAsync(PlatformCapability.ScreenCapture);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(
            "platform.screencapture.permission-not-effective",
            result.Error.Code);
    }

    private sealed class PermissionGate
    {
        public bool IsGranted { get; set; }
    }

    private sealed class PermissionGatedCapabilities(PermissionGate gate) : IPlatformCapabilities
    {
        public ValueTask<CapabilityStatus> GetStatusAsync(
            PlatformCapability capability,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CapabilityStatus(
                capability,
                gate.IsGranted ? CapabilityState.Available : CapabilityState.PermissionRequired,
                gate.IsGranted ? null : PlatformPermission.ScreenRecording));
    }

    private sealed class GrantedPermissions(PermissionGate? gate = null) : IPlatformPermissionRequester
    {
        public ValueTask<Result<PermissionStatus>> RequestAsync(
            PlatformPermission permission,
            CancellationToken cancellationToken = default)
        {
            if (gate is not null)
                gate.IsGranted = true;
            return ValueTask.FromResult(Result<PermissionStatus>.Success(new PermissionStatus(
                permission,
                PermissionState.Granted)));
        }
    }
}
