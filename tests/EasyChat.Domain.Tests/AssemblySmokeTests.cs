namespace EasyChat.Domain.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void DomainAssembly_IsLoadable() => Assert.IsNotNull(typeof(Domain.AssemblyMarker).Assembly);
}

