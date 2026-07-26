using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Media;
using EasyChat.Controls;
using EasyChat.ViewModels.Pages;

namespace EasyChat.Views.Pages;

public partial class TextAssistCorrectionView : UserControl
{
    public TextAssistCorrectionView() => AvaloniaXamlLoader.Load(this);

    private void OnOriginalPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not TextAssistCorrectionViewModel)
        {
            return;
        }

        var annotationLayer = AnnotationLayer ?? this.FindControl<CorrectionAnnotationLayer>("AnnotationLayer");
        var textBox = AnnotatedTextBox ?? this.FindControl<TextBox>("AnnotatedTextBox");
        var hintBorder = CorrectionHint ?? this.FindControl<Border>("CorrectionHint");
        var hintText = CorrectionHintText ?? this.FindControl<TextBlock>("CorrectionHintText");
        if (annotationLayer == null || textBox == null) return;

        var issue = annotationLayer.GetIssueAt(e.GetPosition(annotationLayer));
        var hint = issue == null ? null : $"{issue.Message}\n{issue.Suggestion}";
        if (hintText != null) hintText.Text = hint;
        if (hintBorder == null) return;

        if (issue == null)
        {
            hintBorder.IsVisible = false;
            return;
        }

        var host = hintBorder.Parent as Visual ?? this;
        var pointer = e.GetPosition(host);
        var x = Math.Clamp(pointer.X + 14, 0, Math.Max(0, host.Bounds.Width - hintBorder.Bounds.Width));
        var y = Math.Clamp(pointer.Y + 14, 0, Math.Max(0, host.Bounds.Height - hintBorder.Bounds.Height));
        hintBorder.RenderTransform = new TranslateTransform(x, y);
        hintBorder.IsVisible = true;
    }

    private void OnOriginalPointerExited(object? sender, PointerEventArgs e)
    {
        var textBox = AnnotatedTextBox ?? this.FindControl<TextBox>("AnnotatedTextBox");
        var hintBorder = CorrectionHint ?? this.FindControl<Border>("CorrectionHint");
        if (hintBorder != null) hintBorder.IsVisible = false;
    }

    private async void OnCopyCorrectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CorrectionVariant variant }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(variant.Text);
    }
}
