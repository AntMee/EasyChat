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
using SukiUI.Controls;

namespace EasyChat.Views.Windows;

public partial class TextAssistWindowView : SukiWindow
{
    private readonly TextAssistViewModel _viewModel;
    private ContentControl? _editorHost;
    private bool _correction;

    public TextAssistWindowView()
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = Global.Services?.GetRequiredService<TextAssistViewModel>()
                     ?? throw new InvalidOperationException("Text assist view model is unavailable.");
        DataContext = _viewModel;
        Loaded += (_, _) =>
        {
            _editorHost ??= this.FindControl<ContentControl>("EditorHost");
            ApplyEditor();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    public Task InitializeAsync(string text, bool correction)
    {
        _correction = correction;
        ApplyEditor();
        return _viewModel.InitializeAsync(text, correction);
    }

    public void PrepareForInputCapture(bool correction)
    {
        _correction = correction;
        _viewModel.PrepareForInputCapture(correction);
        ApplyEditor();
    }

    private void ApplyEditor()
    {
        if (_editorHost == null) return;
        _editorHost.Content = _correction
            ? new TextAssistCorrectionView { DataContext = _viewModel.Correction }
            : new TextAssistTranslationView { DataContext = _viewModel.Translation };
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string edgeName }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || !Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            return;
        }

        BeginResizeDrag(edge, e);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
