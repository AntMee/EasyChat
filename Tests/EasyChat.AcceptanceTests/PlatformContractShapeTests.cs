using EasyChat.Contracts.Platform;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class PlatformContractShapeTests
{
    [TestMethod]
    public void CapabilityStatus_RequiresAnExplicitState()
    {
        var status = new CapabilityStatus(
            PlatformCapability.ScreenCapture,
            CapabilityState.PermissionRequired,
            PlatformPermission.ScreenRecording);

        Assert.AreEqual(CapabilityState.PermissionRequired, status.State);
        Assert.AreEqual(PlatformPermission.ScreenRecording, status.RequiredPermission);
    }

    [TestMethod]
    public void ExternalTargetToken_IsOpaqueToConsumers()
    {
        var token = new ExternalTargetToken("platform-owned-value");

        Assert.IsFalse(token.IsEmpty);
        Assert.AreEqual("platform-owned-value", token.Value);
    }
}

