using System.Reactive;
using Avalonia.Media;
using EasyChat.Presentation.Foundation.Navigation;
using ReactiveUI;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings.Theme;

public sealed class CustomThemeDialogViewModel : ConventionViewModelBase
{
    private readonly Action<ColorThemeOption> _applyTheme;
    private readonly DialogManager _dialogManager;
    private string _displayName = "Pink";
    private Color _primaryColor = Colors.DeepPink;
    private Color _accentColor = Colors.Pink;

    public CustomThemeDialogViewModel(
        DialogManager dialogManager,
        Action<ColorThemeOption> applyTheme)
    {
        _applyTheme = applyTheme;
        _dialogManager = dialogManager;
        TryCreateThemeCommand = ReactiveCommand.Create(CreateTheme);
        CancelCommand = ReactiveCommand.Create(() => dialogManager.Close(this));
    }

    public string DisplayName { get => _displayName; set => this.RaiseAndSetIfChanged(ref _displayName, value); }
    public Color PrimaryColor { get => _primaryColor; set => this.RaiseAndSetIfChanged(ref _primaryColor, value); }
    public Color AccentColor { get => _accentColor; set => this.RaiseAndSetIfChanged(ref _accentColor, value); }
    public ReactiveCommand<Unit, Unit> TryCreateThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private void CreateTheme()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
            return;
        _applyTheme(new ColorThemeOption(DisplayName, PrimaryColor, AccentColor, IsCustom: true));
        _dialogManager.Close(this);
    }
}
