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
            .Single(element => element.Name.LocalName == "SettingsLayout");

        Assert.IsFalse(settingsLayout.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer"));
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
