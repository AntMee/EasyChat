using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyChat.Presentation.Features.ScreenshotOcr;

namespace EasyChat.Presentation.Features.ScreenshotOcr.Controls;

public sealed partial class LanguageSelector : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<ScreenshotOcrLanguageOption>> LanguagesProperty =
        AvaloniaProperty.Register<LanguageSelector, IReadOnlyList<ScreenshotOcrLanguageOption>>(
            nameof(Languages),
            []);

    public static readonly StyledProperty<ScreenshotOcrLanguageOption?> SelectedLanguageProperty =
        AvaloniaProperty.Register<LanguageSelector, ScreenshotOcrLanguageOption?>(nameof(SelectedLanguage));

    public IReadOnlyList<ScreenshotOcrLanguageOption> Languages
    {
        get => GetValue(LanguagesProperty);
        set => SetValue(LanguagesProperty, value);
    }

    public ScreenshotOcrLanguageOption? SelectedLanguage
    {
        get => GetValue(SelectedLanguageProperty);
        set => SetValue(SelectedLanguageProperty, value);
    }

    private void DropDownButton_OnClick(object? sender, RoutedEventArgs e) =>
        LanguageAutoCompleteBox.ToggleDropDown();

    public LanguageSelector() => InitializeComponent();
}
