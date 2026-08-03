using System.Diagnostics;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class MasterBaselineInventoryTests
{
    private const string MasterBaseline = "b6f0594f1d9e7bc0af505f98b2cad5f6cf5b4aa9";

    [TestMethod]
    public void MasterUiInventory_IsLockedToTheApprovedBaseline()
    {
        var root = FindRepositoryRoot();
        var files = RunGit(root, "ls-tree", "-r", "--name-only", MasterBaseline, "--", "EasyChat")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(42, files.Count(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(40, files.Count(path =>
            path.StartsWith("EasyChat/Views/", StringComparison.Ordinal) &&
            path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(524, files.Count(path => path.StartsWith("EasyChat/Assets/", StringComparison.Ordinal)));
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"safe.directory={workingDirectory.Replace('\\', '/')}");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        return output;
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
