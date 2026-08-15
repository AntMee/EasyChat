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

    [TestMethod]
    public void ToPlainText_RemovesMarkdownSyntaxAndKeepsReadableContent()
    {
        const string markdown = """
            # Translation title

            A **bold** [link](https://example.com) and `code` value.

            - First item
            - Second item
            """;

        var text = MarkdownPlainTextFormatter.ToPlainText(markdown);

        Assert.Contains("Translation title", text);
        Assert.Contains("A bold link and code value.", text);
        Assert.Contains("First item", text);
        Assert.Contains("Second item", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("[link]", text);
        Assert.DoesNotContain("`code`", text);
        Assert.Contains("\n", text);
    }

    [TestMethod]
    public void ToPlainText_NormalizesLineEndingsForClipboardInteroperability()
    {
        var text = MarkdownPlainTextFormatter.ToPlainText("first  \r\nsecond");

        Assert.AreEqual("first\nsecond", text);
    }
}
