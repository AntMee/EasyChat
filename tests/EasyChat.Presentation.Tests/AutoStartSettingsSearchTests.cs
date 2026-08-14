using EasyChat.Presentation.Features.Settings;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class AutoStartSettingsSearchTests
{
    [TestMethod]
    public void GeneralSearch_IncludesAutoStartKeywords()
    {
        Assert.IsTrue(SettingsSearch.Matches("auto start", SettingsSearch.GeneralSearchFields));
        Assert.IsTrue(SettingsSearch.Matches("startup", SettingsSearch.GeneralSearchFields));
        Assert.IsTrue(SettingsSearch.Matches("开机", SettingsSearch.GeneralSearchFields));
    }
}
