using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Foundation.Localization;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TranslationLanguageOptionsTests
{
    [TestMethod]
    public void NormalizeId_ResolvesProviderCodeToCanonicalLanguageId()
    {
        var options = new TranslationLanguageOptions(new StubLanguageCatalog());

        Assert.AreEqual("zh-Hans", options.NormalizeId("zh-CN"));
    }

    [TestMethod]
    public void NormalizeId_PreservesCanonicalCasingAndUnknownIds()
    {
        var options = new TranslationLanguageOptions(new StubLanguageCatalog());

        Assert.AreEqual("zh-Hans", options.NormalizeId("ZH-HANS"));
        Assert.AreEqual("custom", options.NormalizeId("custom"));
    }

    private sealed class StubLanguageCatalog : ITranslationLanguageCatalog
    {
        public IReadOnlyList<TranslationLanguage> All { get; } =
        [
            new(
                "zh-Hans",
                "Simplified Chinese",
                "简体中文",
                new Dictionary<string, string> { ["Google"] = "zh-CN" })
        ];

        public TranslationLanguage Get(string id) =>
            All.First(language => language.Id == id);
    }
}
