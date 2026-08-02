namespace EasyChat.Infrastructure.Windows.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void WindowsAssembly_IsLoadable() => Assert.IsNotNull(typeof(Infrastructure.Windows.AssemblyMarker).Assembly);
}

