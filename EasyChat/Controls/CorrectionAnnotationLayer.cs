using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EasyChat.Models.Translation.TextAssist;

namespace EasyChat.Controls;

public sealed class CorrectionAnnotationLayer : Control
{
    private INotifyCollectionChanged? _observedIssues;
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<IEnumerable<TextAssistIssueEvent>?> IssuesProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, IEnumerable<TextAssistIssueEvent>?>(nameof(Issues));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, double>(nameof(FontSize), 16);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<CorrectionAnnotationLayer, Thickness>(nameof(Padding), new Thickness(0));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable<TextAssistIssueEvent>? Issues
    {
        get => GetValue(IssuesProperty);
        set => SetValue(IssuesProperty, value);
    }

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Thickness Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

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
        foreach (var issue in Issues)
        {
            if (issue.Start < 0 || issue.Length <= 0 || issue.Start >= Text.Length) continue;
            var end = Math.Min(Text.Length, issue.Start + issue.Length);
            var start = layout[Math.Min(issue.Start, layout.Count - 1)];
            var finish = layout[Math.Max(issue.Start, end - 1)];
            var brush = Brushes.IndianRed;
            if (Math.Abs(start.Y - finish.Y) < 0.5)
            {
                context.FillRectangle(brush, new Rect(start.X, start.Bottom - 2, Math.Max(2, finish.Right - start.X), 2));
            }
            else
            {
                context.FillRectangle(brush, new Rect(start.X, start.Bottom - 2, Math.Max(2, Bounds.Width - start.X - 8), 2));
                context.FillRectangle(brush, new Rect(0, finish.Bottom - 2, Math.Max(2, finish.Right), 2));
            }
        }
    }

    public TextAssistIssueEvent? GetIssueAt(Point point)
    {
        var layout = BuildLayout();
        var index = -1;
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i].Contains(point)) { index = i; break; }
        }
        // TextBox glyphs and the annotation layer can differ by a fractional
        // baseline/padding offset. Fall back to the nearest character on the
        // same line so hovering the underline itself still resolves the issue.
        if (index < 0)
        {
            var nearestDistance = double.MaxValue;
            for (var i = 0; i < layout.Count; i++)
            {
                var rect = layout[i];
                if (point.Y < rect.Top - 4 || point.Y > rect.Bottom + 4) continue;
                var distance = point.X < rect.Left ? rect.Left - point.X :
                    point.X > rect.Right ? point.X - rect.Right : 0;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    index = i;
                }
            }
            if (nearestDistance > 18) index = -1;
        }
        if (index < 0 || Issues == null) return null;
        foreach (var issue in Issues)
            if (index >= issue.Start && index < issue.Start + issue.Length) return issue;
        return null;
    }

    private List<Rect> BuildLayout()
    {
        var result = new List<Rect>(Text.Length);
        var width = Math.Max(20, Bounds.Width - Padding.Left - Padding.Right);
        var x = Padding.Left;
        var y = Padding.Top;
        var lineHeight = Math.Max(18, FontSize * 1.45);
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        for (var i = 0; i < Text.Length; i++)
        {
            var value = Text[i].ToString();
            if (value == "\r") { result.Add(new Rect(x, y, 0, lineHeight)); continue; }
            if (value == "\n")
            {
                result.Add(new Rect(x, y, 0, lineHeight));
                x = Padding.Left;
                y += lineHeight;
                continue;
            }
            var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, FontSize, Brushes.Transparent);
            var charWidth = Math.Max(1, formatted.Width);
            if (x + charWidth > width + Padding.Left && x > Padding.Left)
            {
                x = Padding.Left;
                y += lineHeight;
            }
            result.Add(new Rect(x, y, charWidth, lineHeight));
            x += charWidth;
        }
        return result;
    }
}
