namespace EasyChat.Application.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void ApplicationAssembly_IsLoadable() => Assert.IsNotNull(typeof(Application.AssemblyMarker).Assembly);
}

