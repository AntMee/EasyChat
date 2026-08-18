using EasyChat.Infrastructure.Windows.ApplicationStartup;

namespace EasyChat.Infrastructure.Windows.Tests.ApplicationStartup;

[TestClass]
public sealed class WindowsScheduledAutoStartTaskTests
{
    [TestMethod]
    public void Registration_UsesAnElevatedInteractiveLogonTask()
    {
        var startInfo = WindowsScheduledAutoStartTask.CreateRegistrationStartInfo(
            @"\EasyChat.AutoStart.S-1-5-21-123",
            @"C:\Program Files\EasyChat\EasyChat.exe",
            @"CONTOSO\Ada");

        CollectionAssert.AreEqual(
            new[]
            {
                "/Create", "/TN", @"\EasyChat.AutoStart.S-1-5-21-123",
                "/TR", "\"C:\\Program Files\\EasyChat\\EasyChat.exe\" --autostart",
                "/SC", "ONLOGON", "/RU", @"CONTOSO\Ada", "/IT", "/RL", "HIGHEST",
                "/DELAY", "0000:10", "/F"
            },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void TaskName_IsScopedToTheCurrentWindowsUser()
    {
        var taskName = WindowsScheduledAutoStartTask.GetTaskName("S-1-5-21-123");

        Assert.AreEqual(@"\EasyChat.AutoStart.S-1-5-21-123", taskName);
    }

    [TestMethod]
    public void TaskCommand_QuotesTheApplicationPath()
    {
        var command = WindowsScheduledAutoStartTask.BuildTaskCommand(
            @"C:\Program Files\EasyChat\EasyChat.exe");

        Assert.AreEqual("\"C:\\Program Files\\EasyChat\\EasyChat.exe\" --autostart", command);
    }
}
