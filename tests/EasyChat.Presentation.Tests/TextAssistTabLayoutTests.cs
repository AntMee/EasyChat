using System.Xml.Linq;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TextAssistTabLayoutTests
{
    [TestMethod]
    public void TextAssistModeSwitches_UseTabControls()
    {
        var viewsDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "TextAssist",
            "Views");

        foreach (var fileName in new[] { "TextAssistView.axaml", "TextAssistWindowView.axaml" })
        {
            var document = XDocument.Load(Path.Combine(viewsDirectory, fileName));
            var tabControl = document.Descendants()
                .Single(element => element.Name.LocalName == "TabControl");

            Assert.AreEqual(
                "{Binding SelectedTabIndex}",
                tabControl.Attribute("SelectedIndex")?.Value,
                $"{fileName} must bind its mode tabs to SelectedTabIndex.");
            Assert.HasCount(
                2,
                tabControl.Elements().Where(element => element.Name.LocalName == "TabItem"),
                $"{fileName} must expose the translate and correct tabs.");
            Assert.IsFalse(
                document.Descendants().Any(element => element.Name.LocalName == "RadioButton"),
                $"{fileName} must not use radio buttons as page tabs.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EasyChat.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
