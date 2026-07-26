using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EasyChat.ViewModels.Pages;

namespace EasyChat.Views.Pages;

public partial class TextAssistTranslationView : UserControl
{
    private bool _selectionHooksAttached;

    public TextAssistTranslationView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_selectionHooksAttached || SourceTextBox == null || ResultTextBox == null) return;
        _selectionHooksAttached = true;
        SourceTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        ResultTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        UpdateSelectionButtons();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TextAssistTranslationViewModel vm || string.IsNullOrWhiteSpace(vm.TranslationResult)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(vm.TranslationResult);
    }

    private async void OnSourceDictionaryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistTranslationViewModel vm)
            await OpenSelectionAsync(vm, SourceTextBox);
    }

    private async void OnResultDictionaryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistTranslationViewModel vm)
            await OpenSelectionAsync(vm, ResultTextBox, true);
    }

    private async void OnSourceSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistTranslationViewModel vm)
            await OpenSelectionAsync(vm, SourceTextBox);
    }

    private async void OnResultSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistTranslationViewModel vm)
            await OpenSelectionAsync(vm, ResultTextBox, true);
    }

    private void OnSourcePointerReleased(object? sender, PointerReleasedEventArgs e) => QueueSelectionUpdate();
    private void OnResultPointerReleased(object? sender, PointerReleasedEventArgs e) => QueueSelectionUpdate();

    private void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
            QueueSelectionUpdate();
    }

    private void QueueSelectionUpdate() => Dispatcher.UIThread.Post(UpdateSelectionButtons);

    private void UpdateSelectionButtons()
    {
        if (SourceSelectionButton != null)
            SourceSelectionButton.IsVisible = !string.IsNullOrWhiteSpace(SourceTextBox?.SelectedText);
        if (ResultSelectionButton != null)
            ResultSelectionButton.IsVisible = !string.IsNullOrWhiteSpace(ResultTextBox?.SelectedText);
    }

    private async void OnSourceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not TextAssistTranslationViewModel vm) return;
        var word = ExtractWord(SourceTextBox.Text ?? string.Empty, SourceTextBox.CaretIndex);
        if (!string.IsNullOrWhiteSpace(word)) await vm.OpenDictionaryAsync(word);
    }

    private async void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not TextAssistTranslationViewModel vm) return;
        var word = ExtractWord(ResultTextBox.Text ?? string.Empty, ResultTextBox.CaretIndex);
        if (!string.IsNullOrWhiteSpace(word)) await vm.OpenResultDictionaryAsync(word);
    }

    private static async System.Threading.Tasks.Task OpenSelectionAsync(TextAssistTranslationViewModel vm, TextBox? box, bool result = false)
    {
        var selected = box?.SelectedText;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (result) await vm.OpenResultDictionaryAsync(selected);
            else await vm.OpenDictionaryAsync(selected);
        }
    }

    private static string ExtractWord(string text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && !char.IsPunctuation(text[start - 1])) start--;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsPunctuation(text[end])) end++;
        return text[start..end];
    }
}
