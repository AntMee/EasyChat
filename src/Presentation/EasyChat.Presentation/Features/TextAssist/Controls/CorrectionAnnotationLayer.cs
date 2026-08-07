using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using EasyChat.Presentation.Features.TextAssist;

namespace EasyChat.Presentation.Features.TextAssist.Controls;

public sealed class CorrectionAnnotationLayer : Control
{
    private INotifyCollectionChanged? _observedIssues;
    private TextLayout? _layout;
    private TextPresenter? _layoutPresenter;
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

    /// <summary>
    /// Uses the editable control's rendered text layout when one is available.
    /// </summary>
    public TextPresenter? LayoutPresenter
    {
        get => _layoutPresenter;
        set
        {
            if (ReferenceEquals(_layoutPresenter, value)) return;
            if (_layoutPresenter is not null) _layoutPresenter.LayoutUpdated -= OnLayoutPresenterLayoutUpdated;
            _layoutPresenter = value;
            if (_layoutPresenter is not null) _layoutPresenter.LayoutUpdated += OnLayoutPresenterLayoutUpdated;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IssuesProperty)
        {
            if (_observedIssues != null) _observedIssues.CollectionChanged -= OnIssuesChanged;
            _observedIssues = change.NewValue as INotifyCollectionChanged;
            if (_observedIssues != null) _observedIssues.CollectionChanged += OnIssuesChanged;
        }
        else
        {
            _layout = null;
        }
        InvalidateVisual();
    }

    private void OnIssuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void OnLayoutPresenterLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout = null;
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrEmpty(Text) || Issues == null || Bounds.Width <= 0) return;

        foreach (var (_, bounds) in GetIssueBounds()) DrawUnderline(context, bounds);
    }

    private static void DrawUnderline(DrawingContext context, Rect line)
    {
        context.FillRectangle(Brushes.IndianRed,
            new Rect(line.X, line.Bottom - 2, Math.Max(2, line.Width), 2));
    }

    public TextAssistIssueViewModel? GetIssueAt(Point point)
    {
        TextAssistIssueViewModel? match = null;
        foreach (var (issue, bounds) in GetIssueBounds())
        {
            if (!bounds.Contains(point)) continue;
            if (match is null || issue.Length < match.Length)
                match = issue;
        }
        return match;
    }

    private IEnumerable<(TextAssistIssueViewModel Issue, Rect Bounds)> GetIssueBounds()
    {
        if (Issues is null) yield break;

        var hasPresenterLayout = TryGetPresenterLayout(out var presenterLayout, out var presenterTransform);
        var layout = presenterLayout ?? BuildLayout();
        var sourceLength = Text.Length;
        var presenterOrigin = default(Point);
        if (hasPresenterLayout && _layoutPresenter is not null)
        {
            sourceLength = Math.Min(sourceLength, _layoutPresenter.Text?.Length ?? 0);
            presenterOrigin = GetPresenterLayoutOrigin(_layoutPresenter, layout);
        }

        foreach (var issue in Issues)
        {
            if (issue.Start < 0 || issue.Length <= 0 || issue.Start >= sourceLength) continue;
            var length = Math.Min(issue.Length, sourceLength - issue.Start);
            foreach (var bounds in layout.HitTestTextRange(issue.Start, length))
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                var layoutBounds = hasPresenterLayout
                    ? new Rect(
                        bounds.X + presenterOrigin.X,
                        bounds.Y + presenterOrigin.Y,
                        bounds.Width,
                        bounds.Height)
                    : bounds;
                var issueBounds = hasPresenterLayout
                    ? TransformBounds(presenterTransform, layoutBounds)
                    : new Rect(
                        bounds.X + Padding.Left,
                        bounds.Y + Padding.Top,
                        bounds.Width,
                        bounds.Height);
                if (issueBounds.Width <= 0 || issueBounds.Height <= 0) continue;
                yield return (issue, issueBounds);
            }
        }
    }

    private bool TryGetPresenterLayout(out TextLayout? layout, out Matrix transform)
    {
        layout = null;
        transform = default;
        if (_layoutPresenter is null) return false;

        var presenterTransform = _layoutPresenter.TransformToVisual(this);
        if (presenterTransform is null || _layoutPresenter.TextLayout is null) return false;

        layout = _layoutPresenter.TextLayout;
        transform = presenterTransform.Value;
        return true;
    }

    private static Point GetPresenterLayoutOrigin(TextPresenter presenter, TextLayout layout)
    {
        var verticalSpace = presenter.Bounds.Height - layout.Height;
        if (verticalSpace <= 0) return default;
        var y = presenter.VerticalAlignment switch
        {
            VerticalAlignment.Center => verticalSpace / 2,
            VerticalAlignment.Bottom => verticalSpace,
            _ => 0
        };
        return new Point(0, y);
    }

    private static Rect TransformBounds(Matrix transform, Rect bounds)
    {
        var topLeft = transform.Transform(new Point(bounds.X, bounds.Y));
        var topRight = transform.Transform(new Point(bounds.X + bounds.Width, bounds.Y));
        var bottomLeft = transform.Transform(new Point(bounds.X, bounds.Y + bounds.Height));
        var bottomRight = transform.Transform(new Point(bounds.X + bounds.Width, bounds.Y + bounds.Height));
        var left = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomLeft.X, bottomRight.X));
        var top = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomLeft.Y, bottomRight.Y));
        var right = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomLeft.X, bottomRight.X));
        var bottom = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomLeft.Y, bottomRight.Y));
        return new Rect(left, top, right - left, bottom - top);
    }

    private TextLayout BuildLayout()
    {
        if (_layout is not null) return _layout;
        var width = Math.Max(20, Bounds.Width - Padding.Left - Padding.Right);
        var lineHeight = LineHeight > 0 ? LineHeight : Math.Max(18, FontSize * 1.45);
        return _layout = new TextLayout(
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
