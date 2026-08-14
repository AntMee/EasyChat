using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace EasyChat.Presentation.Features.Settings.Translation.Views;

public partial class AiModelEditDialogView : UserControl
{
    public AiModelEditDialogView()
    {
        InitializeComponent();
        ModelSelector.TemplateApplied += OnModelSelectorTemplateApplied;
    }

    private static void OnModelSelectorTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
        e.NameScope.Find<TextBox>("PART_TextBox")?.Classes.Remove("Clearable");

    private void OnModelSelectorGotFocus(object? sender, RoutedEventArgs e) =>
        OpenModelListIfEmpty(sender);

    private void OnModelSelectorPointerPressed(object? sender, PointerPressedEventArgs e) =>
        OpenModelListIfEmpty(sender);

    private void OnModelDropDownButtonClick(object? sender, RoutedEventArgs e) =>
        ModelSelector.IsDropDownOpen = !ModelSelector.IsDropDownOpen;

    private static void OpenModelListIfEmpty(object? sender)
    {
        if (sender is not AutoCompleteBox selector || !string.IsNullOrWhiteSpace(selector.Text))
            return;

        Dispatcher.UIThread.Post(() => selector.IsDropDownOpen = true, DispatcherPriority.Input);
    }
}

public partial class KeyListEditorView : UserControl
{
    public KeyListEditorView() => InitializeComponent();
}
