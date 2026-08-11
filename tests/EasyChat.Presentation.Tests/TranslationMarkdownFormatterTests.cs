using EasyChat.Presentation.Features.Translation;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TranslationMarkdownFormatterTests
{
    [TestMethod]
    public void PreserveLineBreaks_ConvertsCommonLineEndingsToMarkdownHardBreaks()
    {
        var formatted = TranslationMarkdownFormatter.PreserveLineBreaks("first\r\nsecond\nthird\rfourth");

        Assert.AreEqual("first  \nsecond  \nthird  \nfourth", formatted);
    }
}
