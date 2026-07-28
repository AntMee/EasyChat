using System;
using System.Threading.Tasks;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using EasyChat.Common;
using EasyChat.Controls;
using EasyChat.Models;
using EasyChat.Services.Abstractions;
using EasyChat.ViewModels.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace EasyChat.Views.Windows;

public partial class TextAssistResultWindowView : Window
{
    private readonly TextAssistResultWindowViewModel _viewModel;

    public TextAssistResultWindowView()
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = Global.Services?.GetRequiredService<TextAssistResultWindowViewModel>()
                     ?? throw new InvalidOperationException("Text assist result view model is unavailable.");
        DataContext = _viewModel;
        Opened += (_, _) =>
        {
            var handle = TryGetPlatformHandle()?.Handle;
            if (handle is { } value && value != IntPtr.Zero)
                Global.Services?.GetService<IFocusService>()?.SetWindowNoActivate(value);
        };
        Closed += (_, _) => _viewModel.Cancel();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public Task InitializeAsync(string text, TextAssistOperation operation) => _viewModel.InitializeAsync(text, operation);

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.CopyText)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(_viewModel.CopyText);
        CopyFeedback.Show(sender as Control);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextAssistResultWindowViewModel.IsCorrectionCorrect))
            Height = _viewModel.IsCorrectionCorrect ? 220 : 420;
    }

    private void OnOriginalPointerMoved(object? sender, PointerEventArgs e)
    {
        var annotationLayer = this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
        var hintBorder = this.FindControl<Border>("CorrectionHint");
        var hintText = this.FindControl<TextBlock>("CorrectionHintText");
        if (annotationLayer == null || hintBorder == null || hintText == null) return;
        var issue = annotationLayer.GetIssueAt(e.GetPosition(annotationLayer));
        if (issue == null) { hintBorder.IsVisible = false; return; }
        hintText.Text = $"{issue.Message}\n{issue.Suggestion}";
        var host = hintBorder.Parent as Visual ?? this;
        var pointer = e.GetPosition(host);
        hintBorder.RenderTransform = new TranslateTransform(
            Math.Clamp(pointer.X + 12, 0, Math.Max(0, host.Bounds.Width - hintBorder.Bounds.Width)),
            Math.Clamp(pointer.Y + 12, 0, Math.Max(0, host.Bounds.Height - hintBorder.Bounds.Height)));
        hintBorder.IsVisible = true;
    }

    private void OnOriginalPointerExited(object? sender, PointerEventArgs e)
    {
        var hintBorder = this.FindControl<Border>("CorrectionHint");
        if (hintBorder != null) hintBorder.IsVisible = false;
    }
}
