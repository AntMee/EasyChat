using Avalonia.Controls;
using Avalonia.Controls.Templates;
using EasyChat.ViewModels;

namespace EasyChat;

public sealed class ViewLocator : IDataTemplate
{
    private readonly Dictionary<object, Control> _views = new();

    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        var viewName = data.GetType().FullName?.Replace("ViewModel", "View", StringComparison.Ordinal);
        var viewType = viewName is null
            ? null
            : typeof(Presentation.AssemblyMarker).Assembly.GetType(viewName);
        if (viewType is null || !typeof(Control).IsAssignableFrom(viewType))
            return new TextBlock { Text = $"Not Found: {viewName}" };

        if (!_views.TryGetValue(data, out var view))
        {
            view = (Control)Activator.CreateInstance(viewType)!;
            _views.Add(data, view);
        }

        view.DataContext = data;
        return view;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
