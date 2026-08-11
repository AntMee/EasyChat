using Avalonia.Media;

namespace EasyChat.Presentation.Features.Settings.Theme;

public sealed record ColorThemeOption(
    string DisplayName,
    Color PrimaryColor,
    Color AccentColor,
    bool IsCustom = false)
{
    public IBrush PrimaryBrush { get; } = new SolidColorBrush(PrimaryColor);
}
