using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Input;

namespace EasyChat.Infrastructure.Windows.Tests.Input;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsRunningProcessCatalogTests
{
    private readonly WindowsRunningProcessCatalog _catalog = new();

    [TestMethod]
    public async Task ResolveProcessIdentifier_EmptyTargetFails()
    {
        var result = await _catalog.ResolveProcessIdentifierAsync(ExternalTargetToken.None);

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual("running-process.empty-target", result.Error.Code);
    }

    [TestMethod]
    public async Task ResolveProcessIdentifier_NonWindowsTokenFails()
    {
        var result = await _catalog.ResolveProcessIdentifierAsync(new ExternalTargetToken("unix:123"));

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual("running-process.invalid-target", result.Error.Code);
    }

    [TestMethod]
    public async Task ResolveProcessIdentifier_InvalidHandleFailsGracefully()
    {
        // A syntactically valid but nonexistent window handle must not throw and must fail.
        var result = await _catalog.ResolveProcessIdentifierAsync(new ExternalTargetToken("win32:1"));

        Assert.IsTrue(result.IsFailure);
    }
}
