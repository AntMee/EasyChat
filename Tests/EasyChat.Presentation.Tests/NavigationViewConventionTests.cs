using Avalonia.Controls;
using EasyChat.Presentation.Foundation.Navigation;

namespace EasyChat.Presentation.Tests;

[TestClass]
public sealed class NavigationViewConventionTests
{
    [TestMethod]
    public void EveryConventionViewModel_HasConventionBasedView()
    {
        var assembly = typeof(Presentation.AssemblyMarker).Assembly;
        var viewModelTypes = assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(ConventionViewModelBase)));

        foreach (var viewModelType in viewModelTypes)
        {
            var viewName = ViewTypeConvention.GetViewTypeName(viewModelType);
            var viewType = viewName is null ? null : assembly.GetType(viewName);

            Assert.IsNotNull(viewType, $"{viewModelType.FullName} must have a view named {viewName}.");
            Assert.IsTrue(
                viewType.IsAssignableTo(typeof(Control)),
                $"{viewType.FullName} must derive from {typeof(Control).FullName}.");
        }
    }
}
