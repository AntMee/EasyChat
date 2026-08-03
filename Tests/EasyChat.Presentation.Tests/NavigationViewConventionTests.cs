using Avalonia.Controls;
using EasyChat.Presentation.Foundation.Navigation;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class NavigationViewConventionTests
{
    [TestMethod]
    public void EveryNavigationPageViewModel_HasConventionBasedView()
    {
        var assembly = typeof(Presentation.AssemblyMarker).Assembly;
        var pageTypes = assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(NavigationPageViewModel)));

        foreach (var pageType in pageTypes)
        {
            var viewName = ViewTypeConvention.GetViewTypeName(pageType);
            var viewType = viewName is null ? null : assembly.GetType(viewName);

            Assert.IsNotNull(viewType, $"{pageType.FullName} must have a view named {viewName}.");
            Assert.IsTrue(
                viewType.IsAssignableTo(typeof(Control)),
                $"{viewType.FullName} must derive from {typeof(Control).FullName}.");
        }
    }
}
