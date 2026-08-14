using System.Reactive;
using Avalonia.Media;
using EasyChat.Presentation.Foundation.Navigation;
using EasyChat.Presentation.Lang;
using ReactiveUI;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings.Theme;

public sealed class CustomThemeDialogViewModel : ConventionViewModelBase
{
    private readonly Action<ColorThemeOption> _applyTheme;
    private readonly DialogManager _dialogManager;
    private readonly string _themeId;
    private string _displayName;
    private Color _primaryColor;
    private Color _accentColor;

    public CustomThemeDialogViewModel(
        DialogManager dialogManager,
        Action<ColorThemeOption> applyTheme,
        ColorThemeOption? existing = null)
    {
        _applyTheme = applyTheme;
        _dialogManager = dialogManager;
        _themeId = existing?.Id ?? $"custom:{Guid.NewGuid():N}";
        IsEditing = existing is not null;
        _displayName = existing?.DisplayName ?? "Pink";
        _primaryColor = existing?.PrimaryColor ?? Colors.DeepPink;
        _accentColor = existing?.AccentColor ?? Colors.Pink;
        TryCreateThemeCommand = ReactiveCommand.Create(CreateTheme);
        CancelCommand = ReactiveCommand.Create(() => dialogManager.Close(this));
    }

    public bool IsEditing { get; }
    public string DialogTitle => IsEditing ? Resources.Edit : Resources.CreateCustom;
    public string SubmitLabel => IsEditing ? Resources.Save : Resources.Create;
    public string DisplayName { get => _displayName; set => this.RaiseAndSetIfChanged(ref _displayName, value); }
    public Color PrimaryColor { get => _primaryColor; set => this.RaiseAndSetIfChanged(ref _primaryColor, value); }
    public Color AccentColor { get => _accentColor; set => this.RaiseAndSetIfChanged(ref _accentColor, value); }
    public ReactiveCommand<Unit, Unit> TryCreateThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private void CreateTheme()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
            return;
        _applyTheme(new ColorThemeOption(
            _themeId,
            DisplayName,
            PrimaryColor,
            AccentColor,
            IsCustom: true));
        _dialogManager.Close(this);
    }
}
