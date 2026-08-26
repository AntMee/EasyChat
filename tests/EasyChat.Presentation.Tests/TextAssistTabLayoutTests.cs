using System.Xml.Linq;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class TextAssistTabLayoutTests
{
    [TestMethod]
    public void TextAssistPage_UsesTabs_ButHotkeyWindowIsModeSpecific()
    {
        var viewsDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "TextAssist",
            "Views");

        foreach (var fileName in new[] { "TextAssistView.axaml" })
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

        var hotkeyWindow = XDocument.Load(Path.Combine(viewsDirectory, "TextAssistWindowView.axaml"));
        Assert.AreEqual(
            "clr-namespace:ShadUI;assembly=ShadUI",
            hotkeyWindow.Root?.Name.NamespaceName,
            "Hotkey Text Assist windows must use the ShadUI window control.");
        Assert.IsFalse(
            hotkeyWindow.Descendants().Any(element => element.Name.LocalName == "TabControl"),
            "Hotkey Text Assist windows are opened in one fixed mode and must not expose mode tabs.");
    }

    [TestMethod]
    public void TranslationConfiguration_InsetsDetailedNotesToggleFromRightEdge()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "TextAssist",
            "Views",
            "TextAssistTranslationView.axaml"));

        var detailedNotesToggle = document.Descendants()
            .Single(element => element.Name.LocalName == "ToggleSwitch"
                               && element.Attribute("IsChecked")?.Value == "{Binding DetailedExplanation}");

        Assert.AreEqual("0,0,8,0", detailedNotesToggle.Attribute("Margin")?.Value);
    }

    [TestMethod]
    public void SelectionTextAssistResultWindow_ShowsSkeletonsForEmptyLoadingAreas()
    {
        var document = LoadSelectionTextAssistResultWindow();

        var skeletons = document.Descendants()
            .Where(element => element.Name.LocalName == "StackPanel"
                              && element.Attribute("Classes")?.Value == "TextAssistResultSkeleton")
            .ToArray();

        Assert.HasCount(3, skeletons,
            "Correction, polish, and plain text result layouts must each provide a loading skeleton.");
        Assert.IsTrue(skeletons.All(element =>
                element.Attribute("IsVisible")?.Value == "{Binding ShowLoadingSkeleton}"
                || element.Parent?.Attribute("IsVisible")?.Value == "{Binding ShowLoadingSkeleton}"),
            "Every result skeleton must be controlled by the empty-loading state.");
        Assert.IsFalse(document.Descendants().Any(element =>
                element.Name.LocalName == "Loading"
                && element.Attribute("IsVisible")?.Value == "{Binding ShowLoadingIndicator}"),
            "The empty result area must use a skeleton instead of the centered loading spinner.");
    }

    [TestMethod]
    public void SelectionTextAssistResultWindow_InsetsScrollableContentFromOverlayScrollBars()
    {
        var document = LoadSelectionTextAssistResultWindow();
        var resultScrollViewers = document.Descendants()
            .Where(element => element.Name.LocalName == "ScrollViewer")
            .ToArray();

        Assert.HasCount(2, resultScrollViewers,
            "The specialized and plain result layouts must each own one scroll viewport.");
        Assert.IsTrue(resultScrollViewers.All(element =>
                element.Attribute("Padding")?.Value == "0,0,12,0"),
            "Scrollable result content must reserve space for the overlay vertical scroll bar.");
        Assert.IsTrue(resultScrollViewers.All(element =>
                element.Attribute("HorizontalScrollBarVisibility")?.Value == "Disabled"),
            "Result text must wrap instead of introducing horizontal scrolling.");
    }

    private static XDocument LoadSelectionTextAssistResultWindow() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "Presentation",
        "EasyChat.Presentation",
        "Features",
        "TextAssist",
        "Views",
        "TextAssistResultWindowView.axaml"));

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
