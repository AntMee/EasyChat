using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Desktop;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class DesktopStartupBehaviorTests
{
    [TestMethod]
    public void AutoStart_HidesOnlyWhenCloseBehaviorUsesTheTray()
    {
        Assert.IsTrue(DesktopStartupBehavior.ShouldStartInTray(
            [ApplicationStartupArguments.AutoStart],
            ClosingBehavior.MinimizeToTray));

        Assert.IsFalse(DesktopStartupBehavior.ShouldStartInTray(
            [ApplicationStartupArguments.AutoStart],
            ClosingBehavior.ExitApp));
    }

    [TestMethod]
    public void ManualStart_ShowsTheMainWindowEvenWhenClosingUsesTheTray()
    {
        Assert.IsFalse(DesktopStartupBehavior.ShouldStartInTray(
            [],
            ClosingBehavior.MinimizeToTray));
    }
}
