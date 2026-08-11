using System.Xml.Linq;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class HomeTabLayoutTests
{
    [TestMethod]
    public void HomeContent_UsesAutomaticVerticalScrolling()
    {
        var document = LoadHomeView();
        var scrollViewer = document.Root!.Elements()
            .Single(element => element.Name.LocalName == "ScrollViewer");

        Assert.AreEqual("Auto", scrollViewer.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.AreEqual("Disabled", scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.IsTrue(scrollViewer.Elements().Any(element => element.Name.LocalName == "Grid"));
    }

    [TestMethod]
    public void HomeEngineSwitch_UsesCompactTabControlPages()
    {
        var document = LoadHomeView();
        var tabControl = document.Descendants()
            .Single(element => element.Name.LocalName == "TabControl");

        Assert.AreEqual("{Binding EngineTabIndex}", tabControl.Attribute("SelectedIndex")?.Value);
        Assert.HasCount(2, tabControl.Elements().Where(element => element.Name.LocalName == "TabItem"));
        Assert.IsFalse(document.Descendants().Any(element => element.Name.LocalName == "RadioButton"));
        Assert.IsFalse(document.Descendants().Any(element => element.Name.LocalName == "Card"
                                                            && element.Attribute("Padding")?.Value == "24"));
    }

    [TestMethod]
    public void HomeShortcutSummary_IsAButtonWithCardVisuals()
    {
        var document = LoadHomeView();
        var shortcutButton = document.Descendants()
            .Single(element => element.Name.LocalName == "Button"
                               && element.Attribute("Margin")?.Value == "8,0,0,0");

        Assert.AreEqual("UniformGrid", shortcutButton.Parent?.Name.LocalName);
        Assert.AreEqual("{Binding NavigateToShortcutsCommand}", shortcutButton.Attribute("Command")?.Value);
        Assert.AreEqual("{DynamicResource CardBackgroundColor}", shortcutButton.Attribute("Background")?.Value);
        Assert.AreEqual("{DynamicResource BorderColor}", shortcutButton.Attribute("BorderBrush")?.Value);
        Assert.AreEqual("1", shortcutButton.Attribute("BorderThickness")?.Value);
        Assert.AreEqual("{DynamicResource LgCornerRadius}", shortcutButton.Attribute("CornerRadius")?.Value);
    }

    private static XDocument LoadHomeView()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Shell",
            "Views",
            "HomeView.axaml");
        return XDocument.Load(path);
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
