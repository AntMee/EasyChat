using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using EasyChat.ViewModels.Pages;

namespace EasyChat.Views.Pages;

public partial class TextAssistView : UserControl
{
    public TextAssistView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopyTranslation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistViewModel vm && !string.IsNullOrWhiteSpace(vm.TranslationResult))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(vm.TranslationResult);
        }
    }

    private async void OnCopyCorrection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TextAssistViewModel vm && !string.IsNullOrWhiteSpace(vm.CorrectedResult))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(vm.CorrectedResult);
        }
    }
}
