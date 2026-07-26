using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EasyChat.ViewModels.Pages;

namespace EasyChat.Views.Pages;

public partial class TextAssistTranslationView : UserControl
{
    public TextAssistTranslationView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TextAssistTranslationViewModel vm || string.IsNullOrWhiteSpace(vm.TranslationResult)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(vm.TranslationResult);
    }

}
