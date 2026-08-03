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
        foreach (var issue in Issues)
        {
            if (issue.Start < 0 || issue.Length <= 0 || issue.Start >= Text.Length) continue;
            var end = Math.Min(Text.Length, issue.Start + issue.Length);
            Rect? line = null;
            for (var i = issue.Start; i < end; i++)
            {
                var character = layout[i];
                if (character.Width <= 0) continue;

                if (line is { } current && Math.Abs(current.Y - character.Y) < 0.5)
                {
                    line = current.Union(character);
                    continue;
                }

                if (line is { } completed) DrawUnderline(context, completed);
                line = character;
            }
            if (line is { } completedLine) DrawUnderline(context, completedLine);
        }
    }

    private static void DrawUnderline(DrawingContext context, Rect line)
    {
        context.FillRectangle(Brushes.IndianRed,
            new Rect(line.X, line.Bottom - 2, Math.Max(2, line.Width), 2));
    }

    public TextAssistIssueViewModel? GetIssueAt(Point point)
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
        if (Text.Length == 0) return result;

        var width = Math.Max(20, Bounds.Width - Padding.Left - Padding.Right);
        var lineHeight = LineHeight > 0 ? LineHeight : Math.Max(18, FontSize * 1.45);
        var layout = new TextLayout(
            Text,
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

        for (var i = 0; i < Text.Length; i++)
        {
            var start = layout.HitTestTextPosition(i);
            if (Text[i] is '\r' or '\n')
            {
                result.Add(new Rect(start.X + Padding.Left, start.Y + Padding.Top, 0, start.Height));
                continue;
            }

            var next = layout.HitTestTextPosition(i + 1);
            var characterWidth = Math.Abs(start.Y - next.Y) < 0.5
                ? Math.Max(1, next.X - start.X)
                : Math.Max(1, width - start.X);
            result.Add(new Rect(
                start.X + Padding.Left,
                start.Y + Padding.Top,
                characterWidth,
                Math.Max(1, start.Height)));
        }
        return result;
    }
}
