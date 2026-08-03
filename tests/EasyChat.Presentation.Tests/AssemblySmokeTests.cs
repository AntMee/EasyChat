namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class AssemblySmokeTests
{
    [TestMethod]
    public void PresentationAssembly_IsLoadable() => Assert.IsNotNull(typeof(Presentation.AssemblyMarker).Assembly);
}

