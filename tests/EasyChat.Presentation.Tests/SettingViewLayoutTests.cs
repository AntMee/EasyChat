using System.Xml.Linq;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class SettingViewLayoutTests
{
    [TestMethod]
    public void SettingsLayout_IsNotWrappedInAnOuterScrollViewer()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "SettingView.axaml");
        var document = XDocument.Load(path);
        var settingsLayout = document.Descendants()
            .Single(element => element.Name.LocalName == "Grid"
                               && element.Attributes().Any(attribute =>
                                   attribute.Name.LocalName == "Name"
                                   && attribute.Value == "SettingsContent"));

        Assert.IsFalse(settingsLayout.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer"));
        Assert.AreEqual("220,*", settingsLayout.Attribute("ColumnDefinitions")?.Value);
        Assert.IsTrue(settingsLayout.Descendants().Any(element => element.Name.LocalName == "ScrollViewer"));
    }

    [TestMethod]
    public void SpeechSettings_AsrModelDownloadsUseCollapsibleListBindings()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "SpeechSettingsView.axaml");
        var document = XDocument.Load(path);

        var modelList = document.Descendants()
            .Single(element => element.Name.LocalName == "ItemsControl"
                               && element.Attribute("ItemsSource")?.Value == "{Binding VisibleAsrModels}");
        var downloads = document.Descendants()
            .Single(element => element.Name.LocalName == "ItemsControl"
                               && element.Attribute("ItemsSource")?.Value == "{Binding VisibleAsrModelItems}");

        Assert.AreEqual("{Binding HasImportedAsrModels}", modelList.Attribute("IsVisible")?.Value);
        var toggle = document.Descendants()
            .Single(element => element.Name.LocalName == "Button"
                               && element.Attribute("Command")?.Value == "{Binding ToggleAsrModelListCommand}");

        Assert.IsNotNull(downloads);
        Assert.AreEqual(
            "{Binding IsAsrModelListToggleVisible}",
            toggle.Attribute("IsVisible")?.Value);
        Assert.AreEqual(
            "{Binding AsrModelListToggleText}",
            toggle.Attribute("ToolTip.Tip")?.Value);
    }

    [TestMethod]
    public void SpeechSettings_AsrModelDownloadsKeepManualImportFallback()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "SpeechSettingsView.axaml");
        var document = XDocument.Load(path);

        var downloads = document.Descendants()
            .Single(element => element.Name.LocalName == "ItemsControl"
                               && element.Attribute("ItemsSource")?.Value == "{Binding VisibleAsrModelItems}");
        var manualDownload = document.Descendants()
            .Single(element => element.Name.LocalName == "Button"
                               && element.Attribute("Command")?.Value == "{Binding OpenAsrModelDownloadsCommand}");

        Assert.IsNotNull(downloads);
        Assert.AreEqual("AsrManualDownloadLink", manualDownload.Attribute("Classes")?.Value);
        Assert.AreEqual("3", manualDownload.Attribute("Grid.Column")?.Value);
        Assert.IsTrue(document.Descendants().Any(element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "{x:Static lang:Resources.AsrModelDownloadNotice}"));
        Assert.AreEqual(1, document.Descendants().Count(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Click")?.Value == "ImportAsrModelFolder_OnClick"));
        Assert.AreEqual(1, document.Descendants().Count(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Click")?.Value == "ImportAsrModelArchive_OnClick"));
    }

    [TestMethod]
    public void ScreenshotSettings_CompactOcrLanguagesOpenDetailsDialog()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "ScreenshotSettingsView.axaml");
        var document = XDocument.Load(path);

        var detailsButton = document.Descendants()
            .Single(element => element.Name.LocalName == "Button"
                               && element.Attribute("Command")?.Value?.Contains(
                                   "ShowOcrModelLanguagesCommand",
                                   StringComparison.Ordinal) == true);

        Assert.AreEqual(
            "{Binding IsSupportedLanguageListCompact}",
            detailsButton.Parent?.Attribute("IsVisible")?.Value);
        Assert.AreEqual("Horizontal", detailsButton.Parent?.Attribute("Orientation")?.Value);
        Assert.AreEqual("28", detailsButton.Attribute("Width")?.Value);
        Assert.AreEqual("28", detailsButton.Attribute("Height")?.Value);
        Assert.AreEqual("Transparent", detailsButton.Attribute("Background")?.Value);
        Assert.AreEqual("0", detailsButton.Attribute("BorderThickness")?.Value);
    }

    [TestMethod]
    public void GeneralSettings_NetworkProxyUsesModeSelectorAndCustomAddress()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "GeneralSettingsView.axaml");
        var document = XDocument.Load(path);

        var proxySelector = document.Descendants()
            .Single(element => element.Name.LocalName == "ComboBox"
                               && element.Attribute("ItemsSource")?.Value == "{Binding NetworkProxyModes}");
        var proxyAddress = document.Descendants()
            .Single(element => element.Name.LocalName == "TextBox"
                               && element.Attribute("Text")?.Value == "{Binding NetworkProxyConf.ProxyUrl}");
        var proxyDescription = document.Descendants()
            .Single(element => element.Name.LocalName == "TextBlock"
                               && element.Attribute("Text")?.Value
                                   == "{x:Static lang:Resources.NetworkProxyDescription}");

        Assert.AreEqual("{Binding NetworkProxyConf.Mode}", proxySelector.Attribute("SelectedValue")?.Value);
        Assert.AreEqual("{Binding Mode}", proxySelector.Attribute("SelectedValueBinding")?.Value);
        Assert.AreEqual("1", proxyAddress.Attribute("Grid.Row")?.Value);
        Assert.AreEqual("Muted", proxyDescription.Attribute("Classes")?.Value);
        Assert.AreEqual("Wrap", proxyDescription.Attribute("TextWrapping")?.Value);
        StringAssert.Contains(
            proxyAddress.Attribute("IsVisible")?.Value,
            "NetworkProxyMode.Custom");
        Assert.IsFalse(document.Descendants().Any(element =>
            element.Name.LocalName == "CheckBox"
            && element.Attribute("IsChecked")?.Value == "{Binding OcrConf.UseProxy}"));
    }

    [TestMethod]
    public void ScreenshotSettings_ExposeClosePreviousOcrWindowToggle()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views",
            "ScreenshotSettingsView.axaml");
        var document = XDocument.Load(path);

        var toggle = document.Descendants()
            .Single(element => element.Name.LocalName == "ToggleSwitch"
                               && element.Attribute("IsChecked")?.Value
                                   == "{Binding ScreenshotConf.ClosePreviousOcrWindow}");
        var label = document.Descendants()
            .Single(element => element.Name.LocalName == "TextBlock"
                               && element.Attribute("Text")?.Value
                                   == "{x:Static lang:Resources.ScreenshotOcrClosePreviousWindow}");

        Assert.AreEqual("", toggle.Attribute("OnContent")?.Value);
        Assert.AreEqual("", toggle.Attribute("OffContent")?.Value);
        Assert.AreEqual("Wrap", label.Attribute("TextWrapping")?.Value);
    }

    [TestMethod]
    public void SettingsForms_FitWithinTheMinimumDetailPaneWidth()
    {
        var root = FindRepositoryRoot();
        var viewsDirectory = Path.Combine(
            root,
            "src",
            "Presentation",
            "EasyChat.Presentation",
            "Features",
            "Settings",
            "Views");

        foreach (var path in Directory.EnumerateFiles(viewsDirectory, "*SettingsView.axaml"))
        {
            var document = XDocument.Load(path);
            var controls = document.Descendants()
                .Where(element => element.Name.LocalName == "ContentControl"
                                  && element.Attribute("Grid.Column")?.Value == "1"
                                  && int.TryParse(element.Attribute("MinWidth")?.Value, out _));

            foreach (var control in controls)
            {
                Assert.IsLessThanOrEqualTo(
                    int.Parse(control.Attribute("MinWidth")!.Value),
                    180,
                    $"{Path.GetFileName(path)} requires more width than the settings detail pane has at the minimum window size.");
            }
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
