namespace EasyChat.Presentation.Foundation.Navigation;

public static class ViewTypeConvention
{
    public static string? GetViewTypeName(Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        return viewModelType.Namespace is null
            ? null
            : $"{viewModelType.Namespace}.Views.{viewModelType.Name.Replace("ViewModel", "View", StringComparison.Ordinal)}";
    }
}
