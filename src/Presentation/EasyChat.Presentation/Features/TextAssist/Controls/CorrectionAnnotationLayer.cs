using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using EasyChat.Presentation.Features.TextAssist;

namespace EasyChat.Presentation.Features.TextAssist.Controls;

public sealed class CorrectionAnnotationLayer : Control
{
    private INotifyCollectionChanged? _observedIssues;
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<IEnumerable<TextAssistIssueViewModel>?> IssuesProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, IEnumerable<TextAssistIssueViewModel>?>(nameof(Issues));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, double>(nameof(FontSize), 16);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, Thickness>(nameof(Padding), new Thickness(0));

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, double>(nameof(LineHeight), 0);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable<TextAssistIssueViewModel>? Issues
    {
        get => GetValue(IssuesProperty);
        set => SetValue(IssuesProperty, value);
    }

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Thickness Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public double LineHeight { get => GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IssuesProperty)
        {
            if (_observedIssues != null) _observedIssues.CollectionChanged -= OnIssuesChanged;
            _observedIssues = change.NewValue as INotifyCollectionChanged;
            if (_observedIssues != null) _observedIssues.CollectionChanged += OnIssuesChanged;
        }
        InvalidateVisual();
    }

    private void OnIssuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrEmpty(Text) || Issues == null || Bounds.Width <= 0) return;

        var layout = BuildLayout();
        foreach (var (_, bounds) in GetIssueBounds(layout)) DrawUnderline(context, bounds);
    }

    private static void DrawUnderline(DrawingContext context, Rect line)
    {
        context.FillRectangle(Brushes.IndianRed,
            new Rect(line.X, line.Bottom - 2, Math.Max(2, line.Width), 2));
    }

    public TextAssistIssueViewModel? GetIssueAt(Point point)
    {
        var layout = BuildLayout();
        TextAssistIssueViewModel? nearestIssue = null;
        var nearestDistance = double.MaxValue;
        foreach (var (issue, bounds) in GetIssueBounds(layout))
        {
            if (bounds.Contains(point)) return issue;
            if (point.Y < bounds.Top - 4 || point.Y > bounds.Bottom + 4) continue;
            var distance = point.X < bounds.Left ? bounds.Left - point.X :
                point.X > bounds.Right ? point.X - bounds.Right : 0;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestIssue = issue;
        }
        return nearestDistance <= 18 ? nearestIssue : null;
    }

    private IEnumerable<(TextAssistIssueViewModel Issue, Rect Bounds)> GetIssueBounds(TextLayout layout)
    {
        if (Issues is null) yield break;
        foreach (var issue in Issues)
        {
            if (issue.Start < 0 || issue.Length <= 0 || issue.Start >= Text.Length) continue;
            var length = Math.Min(issue.Length, Text.Length - issue.Start);
            foreach (var bounds in layout.HitTestTextRange(issue.Start, length))
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                yield return (issue, new Rect(
                    bounds.X + Padding.Left,
                    bounds.Y + Padding.Top,
                    bounds.Width,
                    bounds.Height));
            }
        }
    }

    private TextLayout BuildLayout()
    {
        var width = Math.Max(20, Bounds.Width - Padding.Left - Padding.Right);
        var lineHeight = LineHeight > 0 ? LineHeight : Math.Max(18, FontSize * 1.45);
        return new TextLayout(
            Text ?? string.Empty,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize,
            Brushes.Transparent,
            TextAlignment.Left,
            TextWrapping.Wrap,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            width,
            double.PositiveInfinity,
            lineHeight,
            0,
            int.MaxValue);
    }
}
