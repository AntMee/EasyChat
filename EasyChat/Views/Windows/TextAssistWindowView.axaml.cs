using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using EasyChat.Common;
using EasyChat.Services.TextAssist;
using EasyChat.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;
using EasyChat.Views.Pages;

namespace EasyChat.Views.Windows;

public partial class TextAssistWindowView : Window
{
    private readonly TextAssistViewModel _viewModel;

    public TextAssistWindowView()
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = Global.Services?.GetRequiredService<TextAssistViewModel>()
                     ?? throw new InvalidOperationException("Text assist view model is unavailable.");
        DataContext = _viewModel;
        EditorHost.Content = new TextAssistView { DataContext = _viewModel };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    public Task InitializeAsync(string text, bool correction)
    {
        return _viewModel.InitializeAsync(text, correction);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
