using EasyChat.Models.Configuration;

namespace EasyChat.Tests.Models.Configuration;

[TestClass]
public sealed class SelectionToolbarConfigTests
{
    [TestMethod]
    public void Defaults_KeepLegacyTranslationEnabledOnly()
    {
        var config = new SelectionTranslationConfig();

        Assert.IsTrue(config.TranslationEnabled);
        Assert.IsFalse(config.CorrectionEnabled);
        Assert.IsFalse(config.PolishEnabled);
        Assert.IsFalse(config.SummaryEnabled);
    }
}
