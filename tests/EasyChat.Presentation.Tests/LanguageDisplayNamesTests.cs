using System.Globalization;
using EasyChat.Presentation.Foundation.Localization;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class LanguageDisplayNamesTests
{
    [TestMethod]
    public void ForUi_UsesChineseNameOnlyForChineseUi()
    {
        Assert.AreEqual("英语", LanguageDisplayNames.ForUi("英语", "English", new CultureInfo("zh-CN")));
        Assert.AreEqual("English", LanguageDisplayNames.ForUi("英语", "English", new CultureInfo("en-US")));
        Assert.AreEqual("English", LanguageDisplayNames.ForUi("英语", "English", new CultureInfo("fr-FR")));
    }

    [TestMethod]
    public void ForUi_FallsBackToEnglishWhenChineseNameIsMissing()
    {
        Assert.AreEqual("English", LanguageDisplayNames.ForUi(null, "English", new CultureInfo("zh-CN")));
    }
}
