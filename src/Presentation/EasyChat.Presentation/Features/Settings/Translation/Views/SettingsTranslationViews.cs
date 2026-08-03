using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace EasyChat.Presentation.Features.Settings.Translation.Views;

public partial class AiModelEditDialogView : UserControl
{
    public AiModelEditDialogView() => InitializeComponent();

    private void OnModelSelectorGotFocus(object? sender, RoutedEventArgs e) =>
        OpenModelListIfEmpty(sender);

    private void OnModelSelectorPointerPressed(object? sender, PointerPressedEventArgs e) =>
        OpenModelListIfEmpty(sender);

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
