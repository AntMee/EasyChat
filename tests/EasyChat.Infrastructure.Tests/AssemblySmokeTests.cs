namespace EasyChat.Infrastructure.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void InfrastructureAssembly_IsLoadable() => Assert.IsNotNull(typeof(Infrastructure.AssemblyMarker).Assembly);
}

