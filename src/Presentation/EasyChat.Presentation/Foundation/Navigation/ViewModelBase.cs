using Material.Icons;
using ReactiveUI;

namespace EasyChat.ViewModels;

public abstract class ViewModelBase : ReactiveObject;

public abstract class NavigationPageViewModel(
    string displayName,
    MaterialIconKind icon,
    int index = 0) : ViewModelBase
{
    private string _displayName = displayName;
    private MaterialIconKind _icon = icon;
    private int _index = index;

    public string DisplayName
    {
        get => _displayName;
        set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    public MaterialIconKind Icon
    {
        get => _icon;
        set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    public int Index
    {
        get => _index;
        set => this.RaiseAndSetIfChanged(ref _index, value);
    }
}
