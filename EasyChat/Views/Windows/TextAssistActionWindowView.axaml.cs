using System;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using EasyChat.Common;
using EasyChat.Models;
using EasyChat.ViewModels.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Views.Windows;

public partial class TextAssistActionWindowView : SukiUI.Controls.SukiWindow
{
    private readonly TextAssistResultWindowViewModel _viewModel;

    public TextAssistActionWindowView()
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = Global.Services?.GetRequiredService<TextAssistResultWindowViewModel>()
                     ?? throw new InvalidOperationException("Text assist view model is unavailable.");
        DataContext = _viewModel;
    }

    public Task InitializeAsync(string text, TextAssistOperation operation, bool run)
    {
        _viewModel.Prepare(operation);
        _viewModel.SourceText = text;
        return run ? _viewModel.InitializeAsync(text, operation) : Task.CompletedTask;
    }
}
