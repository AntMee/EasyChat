using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EasyChat.Views.Pages;

/// <summary>
/// Compact translation editor used by the QuickTranslate shortcut.
/// Selection translation is intentionally owned by the standalone shortcut
/// action and is not part of this view.
/// </summary>
public partial class TextAssistShortcutTranslationView : UserControl
{
    public TextAssistShortcutTranslationView() => AvaloniaXamlLoader.Load(this);
}
