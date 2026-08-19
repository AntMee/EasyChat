using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections;

namespace EasyChat.Presentation.Features.Settings.Translation.Views;

public partial class AiModelEditDialogView : UserControl
{
    private int _modelListOpenRequestVersion;
    private AutoCompleteFilterMode? _modelListFilterMode;

    public AiModelEditDialogView()
    {
        InitializeComponent();
        ModelSelector.TemplateApplied += OnModelSelectorTemplateApplied;
        ModelSelector.DropDownClosed += OnModelSelectorDropDownClosed;
    }

    private static void OnModelSelectorTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
        e.NameScope.Find<TextBox>("PART_TextBox")?.Classes.Remove("Clearable");

    private void OnModelSelectorGotFocus(object? sender, RoutedEventArgs e) =>
        OpenModelListIfEmpty(sender);

    private void OnModelSelectorPointerPressed(object? sender, PointerPressedEventArgs e) =>
        OpenModelListIfEmpty(sender);

    private void OnModelDropDownButtonClick(object? sender, RoutedEventArgs e)
    {
        _modelListOpenRequestVersion++;
        if (ModelSelector.IsDropDownOpen)
        {
            ModelSelector.IsDropDownOpen = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(ModelSelector.Text) && !HasMatchingModel(ModelSelector.Text))
        {
            _modelListFilterMode = ModelSelector.FilterMode;
            ModelSelector.FilterMode = AutoCompleteFilterMode.None;
        }

        ModelSelector.IsDropDownOpen = true;
    }

    private void OnModelSelectorDropDownClosed(object? sender, EventArgs e)
    {
        if (_modelListFilterMode is not { } filterMode)
            return;

        _modelListFilterMode = null;
        ModelSelector.FilterMode = filterMode;
    }

    private bool HasMatchingModel(string model) =>
        ModelSelector.ItemsSource is IEnumerable models
        && models.Cast<object?>().OfType<string>().Any(item => item.Contains(model, StringComparison.Ordinal));

    private void OpenModelListIfEmpty(object? sender)
    {
        if (sender is not AutoCompleteBox selector || !string.IsNullOrWhiteSpace(selector.Text))
            return;

        var requestVersion = ++_modelListOpenRequestVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (requestVersion == _modelListOpenRequestVersion)
                selector.IsDropDownOpen = true;
        }, DispatcherPriority.Input);
    }
}

public partial class KeyListEditorView : UserControl
{
    public KeyListEditorView() => InitializeComponent();
}
