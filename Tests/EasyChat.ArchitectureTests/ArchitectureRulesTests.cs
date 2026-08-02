using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EasyChat.ArchitectureTests;

[TestClass]
public sealed class ArchitectureRulesTests
{
    [TestMethod]
    public void ProjectLayout_MatchesTheApprovedArchitecture()
    {
        var root = FindRepositoryRoot();
        string[] expected =
        [
            "src/EasyChat.Shared/EasyChat.Shared.csproj",
            "src/EasyChat.Domain/EasyChat.Domain.csproj",
            "src/EasyChat.Contracts/EasyChat.Contracts.csproj",
            "src/EasyChat.Application/EasyChat.Application.csproj",
            "src/Infrastructure/EasyChat.Infrastructure/EasyChat.Infrastructure.csproj",
            "src/Infrastructure/EasyChat.Infrastructure.Windows/EasyChat.Infrastructure.Windows.csproj",
            "src/Presentation/EasyChat.Presentation.Shared/EasyChat.Presentation.Shared.csproj",
            "src/Presentation/EasyChat.Presentation/EasyChat.Presentation.csproj",
            "src/Host/EasyChat.Desktop/EasyChat.Desktop.csproj",
            "src/Host/EasyChat.Desktop.Windows/EasyChat.Desktop.Windows.csproj"
        ];

        foreach (var relative in expected)
            Assert.IsTrue(File.Exists(Path.Combine(root, relative)), relative);

        var actual = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    public void ProductionProjects_FollowTheDependencyGraph()
    {
        var root = FindRepositoryRoot();
        var allowed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["EasyChat.Shared"] = Set(),
            ["EasyChat.Domain"] = Set("EasyChat.Shared"),
            ["EasyChat.Contracts"] = Set("EasyChat.Shared"),
            ["EasyChat.Application"] = Set("EasyChat.Contracts", "EasyChat.Domain", "EasyChat.Shared"),
            ["EasyChat.Infrastructure"] = Set("EasyChat.Contracts", "EasyChat.Shared"),
            ["EasyChat.Infrastructure.Windows"] = Set("EasyChat.Contracts", "EasyChat.Shared"),
            ["EasyChat.Presentation.Shared"] = Set(),
            ["EasyChat.Presentation"] = Set("EasyChat.Contracts", "EasyChat.Presentation.Shared"),
            ["EasyChat.Desktop"] = Set("EasyChat.Application", "EasyChat.Contracts", "EasyChat.Infrastructure", "EasyChat.Presentation"),
            ["EasyChat.Desktop.Windows"] = Set("EasyChat.Desktop", "EasyChat.Infrastructure.Windows")
        };

        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var actual = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(node => Path.GetFileNameWithoutExtension(node.Attribute("Include")!.Value))
                .ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(allowed[name].SetEquals(actual), $"{name}: [{string.Join(", ", actual)}]");
        }
    }

    [TestMethod]
    public void CoreAndContracts_DoNotDependOnFrameworksOrPlatforms()
    {
        var root = FindRepositoryRoot();
        string[] layers = ["Shared", "Domain", "Contracts", "Application"];
        string[] forbidden =
        [
            "Avalonia", "ReactiveUI", "DllImport", "LibraryImport", "Microsoft.Win32",
            "OpenCv", "Paddle", "SoundFlow", "Velopack", "user32", "kernel32", "HWND", "AXUIElement"
        ];

        foreach (var layer in layers)
            foreach (var file in SourceFiles(Path.Combine(root, "src", $"EasyChat.{layer}")))
            {
                var source = File.ReadAllText(file);
                foreach (var token in forbidden)
                    Assert.DoesNotContain(token, source, Path.GetRelativePath(root, file));
            }
    }

    [TestMethod]
    public void RetiredArchitecture_CannotReturn()
    {
        var root = FindRepositoryRoot();
        var retiredFolder = "Compat" + "ibility";
        var retiredTypePrefix = "Leg" + "acy";
        var globalServices = "Global" + ".Services";
        var globalConfig = "Global" + ".Config";
        var platformGodInterface = "IPlatform" + "Service";
        foreach (var file in SourceFiles(Path.Combine(root, "src")))
        {
            var relative = Path.GetRelativePath(root, file);
            var source = File.ReadAllText(file);
            Assert.IsFalse(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals(retiredFolder, StringComparison.OrdinalIgnoreCase)), relative);
            Assert.DoesNotContain(globalServices, source, relative);
            Assert.DoesNotContain(globalConfig, source, relative);
            Assert.DoesNotContain(platformGodInterface, source, relative);
            Assert.IsFalse(Regex.IsMatch(source, $@"\b{retiredTypePrefix}[A-Za-z0-9_]*\b"), relative);
        }
    }

    [TestMethod]
    public void Presentation_UsesFeatureFirstFolders()
    {
        var root = FindRepositoryRoot();
        var presentation = Path.Combine(root, "src", "Presentation", "EasyChat.Presentation");
        string[] forbidden = ["Services", "Models", "Helpers", "Controls", "Converters", "Presentation"];

        foreach (var folder in forbidden)
            Assert.IsFalse(Directory.Exists(Path.Combine(presentation, folder)), folder);
    }

    [TestMethod]
    public void PlatformIndependentProjects_DoNotReferenceNativePackages()
    {
        var root = FindRepositoryRoot();
        string[] forbiddenPackages =
        [
            "GlobalHotKeys.Windows", "OpenCvSharp4.Windows", "Sdcb.PaddleInference",
            "Sdcb.PaddleOCR", "SoundFlow"
        ];

        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Where(path => !path.Contains("Infrastructure.Windows", StringComparison.Ordinal)))
        {
            var document = XDocument.Load(project);
            foreach (var package in forbiddenPackages)
                Assert.IsFalse(document.Descendants("PackageReference").Any(node =>
                    string.Equals(node.Attribute("Include")?.Value, package, StringComparison.OrdinalIgnoreCase)),
                    $"{Path.GetFileName(project)} -> {package}");
        }
    }

    [TestMethod]
    public void WindowsHost_PreservesProductIdentity()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "src", "Host", "EasyChat.Desktop.Windows", "EasyChat.Desktop.Windows.csproj"));
        var properties = document.Descendants()
            .Where(node => node.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(node => node.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

        Assert.AreEqual("EasyChat", properties["AssemblyName"]);
        Assert.AreEqual("WinExe", properties["OutputType"]);
        Assert.AreEqual("1.0.6", properties["Version"]);
    }

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or ".verification"));

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
