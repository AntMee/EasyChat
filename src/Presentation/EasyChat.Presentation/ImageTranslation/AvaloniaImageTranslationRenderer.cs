using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.ImageTranslation;

public sealed class AvaloniaImageTranslationRenderer : IImageTranslationRenderer
{
    private const double MinimumFontSize = 1;
    private const double MinimumReadableFontSize = 8;
    private const double DesiredInkHeightRatio = 0.90;
    private const double MinimumMeasuredInkRatio = 0.60;
    private const double FontMeasurementSize = 100;
    private const int MaximumFontMeasurementCharacters = 128;
    private const double MinimumFittedFontScale = 0.70;
    private const double MaximumPreferredHeightRatio = 1.0;
    private const double MinimumTextContrastRatio = 2.2;
    private readonly IImageBackgroundCleaner _backgroundCleaner;
    private readonly ImageTextStyleAnalyzer _styleAnalyzer = new();

    public AvaloniaImageTranslationRenderer(IImageBackgroundCleaner backgroundCleaner)
    {
        _backgroundCleaner = backgroundCleaner
                             ?? throw new ArgumentNullException(nameof(backgroundCleaner));
    }

    public async Task<ImageTranslationRenderResult> RenderAsync(
        ImageFrame source,
        IReadOnlyList<ImageTranslationOverlay> overlays,
        ImageTranslationRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var renderable = overlays
            .Where(overlay => overlay.Region.Polygon.Count >= 3
                              && !string.IsNullOrWhiteSpace(overlay.Translation))
            .ToArray();
        if (renderable.Length == 0)
            return new ImageTranslationRenderResult(source, [], 0);

        // Analyse the original pixels before background removal so colour and contrast
        // are not contaminated by the inpainted result.
        var styles = renderable.ToDictionary(
            overlay => overlay,
            overlay => _styleAnalyzer.Analyze(
                source,
                overlay.Region,
                overlay.EraseRegions));
        var backgroundFrame = await _backgroundCleaner.RemoveTextAsync(
            source,
            renderable
                .SelectMany(overlay => overlay.EraseRegions ?? [overlay.Region])
                .ToArray(),
            options.EraseMode,
            cancellationToken).ConfigureAwait(false);

        using var background = AvaloniaImageFrames.ToBitmap(backgroundFrame);
        using var output = new RenderTargetBitmap(background.PixelSize, background.Dpi);
        var pixelToDip = PixelToDipScale(background.PixelSize, background.Size);
        var alignments = ImageTextAlignmentAnalyzer.Analyze(renderable);
        using (var context = output.CreateDrawingContext())
        {
            context.DrawImage(background, new Rect(background.Size));
            foreach (var overlay in renderable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometry = GetGeometry(overlay.Region);
                var orientedScale = GetOrientedScale(overlay.Region.Angle, pixelToDip);
                var boxWidth = Math.Max(1, geometry.BoxWidth * orientedScale.X);
                var boxHeight = Math.Max(1, geometry.BoxHeight * orientedScale.Y);
                var style = styles[overlay];
                var foreground = EnsureLegibleForeground(
                    style.Foreground,
                    backgroundFrame,
                    geometry.Bounds);
                var brush = new SolidColorBrush(foreground);
                var typeface = CreateTypeface(style.FontWeight);
                var layout = CreateLayout(
                    overlay.Translation,
                    boxWidth,
                    boxHeight,
                    CalculateSourceLineHeight(overlay, pixelToDip),
                    brush,
                    typeface);
                var center = new Point(
                    geometry.Center.X * pixelToDip.X,
                    geometry.Center.Y * pixelToDip.Y);
                var matrix = Matrix.CreateRotation(overlay.Region.Angle * Math.PI / 180d)
                             * Matrix.CreateTranslation(center.X, center.Y);
                using (context.PushTransform(matrix))
                {
                    var verticalOffset = CalculateVerticalOrigin(layout.InkBounds);
                    foreach (var line in layout.Lines)
                    {
                        var x = CalculateLineOriginX(
                            alignments[overlay],
                            boxWidth,
                            line.InkBounds);
                        context.DrawText(
                            line.Formatted,
                            new Point(x, verticalOffset + line.OffsetY));
                    }
                }
            }
        }

        return new ImageTranslationRenderResult(
            AvaloniaImageFrames.ToImageFrame(output),
            [],
            renderable.Length);
    }

    public static Vector PixelToDipScale(PixelSize pixelSize, Size dipSize) =>
        new(
            dipSize.Width / Math.Max(1, pixelSize.Width),
            dipSize.Height / Math.Max(1, pixelSize.Height));

    public static double CalculatePreferredFontSize(Rect originalBounds, double angle)
    {
        // GetGeometry already normalizes the OCR polygon into a long and short edge.
        // The short edge is text height for both horizontal and rotated text.
        return CalculatePreferredFontSize(
            originalBounds.Height,
            targetInkHeight: FontMeasurementSize,
            measurementFontSize: FontMeasurementSize);
    }

    internal static double CalculatePreferredFontSize(
        Rect originalBounds,
        double angle,
        int sourceLineCount)
    {
        var lineCount = Math.Max(1, sourceLineCount);
        return CalculatePreferredFontSize(
            new Rect(
                originalBounds.X,
                originalBounds.Y,
                originalBounds.Width,
                originalBounds.Height / lineCount),
            angle);
    }

    internal static double CalculatePreferredFontSize(
        double sourceLineHeight,
        double targetInkHeight,
        double measurementFontSize)
    {
        var measuredRatio = measurementFontSize > 0 && targetInkHeight > 0
            ? targetInkHeight / measurementFontSize
            : 1;
        measuredRatio = Math.Max(MinimumMeasuredInkRatio, measuredRatio);
        return Math.Max(
            MinimumFontSize,
            sourceLineHeight * DesiredInkHeightRatio / measuredRatio);
    }

    private static int CountSourceLines(string text) =>
        Math.Max(1, text.Replace("\r", string.Empty, StringComparison.Ordinal).Count(character => character == '\n') + 1);

    public static bool IsLayoutWithinBox(
        double layoutWidth,
        double layoutHeight,
        double boxWidth,
        double boxHeight) =>
        layoutWidth <= boxWidth && layoutHeight <= boxHeight;

    private static TextLayout CreateLayout(
        string text,
        double width,
        double height,
        double sourceLineHeight,
        IBrush brush,
        Typeface typeface)
    {
        var measurement = Measure(
            NormalizeForMeasurement(text),
            FontMeasurementSize,
            brush,
            typeface);
        var preferredFontSize = CalculatePreferredFontSize(
            sourceLineHeight,
            measurement.InkBounds.Height,
            FontMeasurementSize);
        var initialFontSize = Math.Max(MinimumFontSize, preferredFontSize);
        // Preserve the original visual scale first. We only shrink enough to make a
        // normal translation fit; an unusually long translation may extend beyond
        // the OCR box rather than becoming unreadably small.
        var minimumFontSize = CalculateMinimumFittedFontSize(initialFontSize);
        var fontSize = initialFontSize;

        while (true)
        {
            var lines = WrapText(text, width, fontSize, brush, typeface);
            var layout = CreateTextLayout(lines);
            if (layout.InkBounds.Height <= height * MaximumPreferredHeightRatio
                || fontSize <= minimumFontSize)
            {
                return layout;
            }

            fontSize = Math.Max(minimumFontSize, fontSize * 0.94);
        }
    }

    private static string NormalizeForMeasurement(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= MaximumFontMeasurementCharacters)
            return normalized;

        var sample = new StringBuilder(MaximumFontMeasurementCharacters);
        foreach (var rune in normalized.EnumerateRunes().Take(MaximumFontMeasurementCharacters))
            sample.Append(rune);
        return sample.ToString();
    }

    private static TextLayout CreateTextLayout(IReadOnlyList<TextLine> measuredLines)
    {
        if (measuredLines.Count == 0)
            return new TextLayout([], new Rect());

        var lines = new List<TextLine>(measuredLines.Count);
        var offsetY = 0d;
        var inkLeft = double.PositiveInfinity;
        var inkTop = double.PositiveInfinity;
        var inkRight = double.NegativeInfinity;
        var inkBottom = double.NegativeInfinity;
        foreach (var measured in measuredLines)
        {
            var line = measured with { OffsetY = offsetY };
            lines.Add(line);
            inkLeft = Math.Min(inkLeft, line.InkBounds.Left);
            inkTop = Math.Min(inkTop, offsetY + line.InkBounds.Top);
            inkRight = Math.Max(inkRight, line.InkBounds.Right);
            inkBottom = Math.Max(inkBottom, offsetY + line.InkBounds.Bottom);
            offsetY += line.Height;
        }

        return new TextLayout(
            lines,
            new Rect(
                inkLeft,
                inkTop,
                Math.Max(0, inkRight - inkLeft),
                Math.Max(0, inkBottom - inkTop)));
    }

    private static IReadOnlyList<TextLine> WrapText(
        string text,
        double maxWidth,
        double fontSize,
        IBrush brush,
        Typeface typeface)
    {
        var lines = new List<TextLine>();
        foreach (var sourceLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (sourceLine.Length == 0)
            {
                lines.Add(Measure(string.Empty, fontSize, brush, typeface));
                continue;
            }

            var current = new StringBuilder();
            foreach (var character in sourceLine)
            {
                current.Append(character);
                if (current.Length == 1
                    || MeasureWidth(current.ToString(), fontSize, brush, typeface) <= maxWidth)
                    continue;

                var split = LastWrapOpportunity(current);
                if (split <= 0)
                    split = current.Length - 1;
                var completed = current.ToString(0, split).TrimEnd();
                if (completed.Length > 0)
                    lines.Add(Measure(completed, fontSize, brush, typeface));

                var remaining = current.ToString(split, current.Length - split).TrimStart();
                current.Clear();
                current.Append(remaining);
            }

            if (current.Length > 0)
                lines.Add(Measure(current.ToString(), fontSize, brush, typeface));
        }

        return lines;
    }

    private static int LastWrapOpportunity(StringBuilder value)
    {
        for (var index = value.Length - 2; index >= 0; index--)
        {
            if (char.IsWhiteSpace(value[index]))
                return index + 1;
        }

        return -1;
    }

    private static TextLine Measure(
        string text,
        double fontSize,
        IBrush brush,
        Typeface typeface) =>
        CreateTextLine(text, fontSize, brush, typeface);

    private static double MeasureWidth(
        string text,
        double fontSize,
        IBrush brush,
        Typeface typeface) =>
        Format(text, fontSize, brush, typeface).Width;

    private static FormattedText Format(
        string text,
        double fontSize,
        IBrush brush,
        Typeface typeface) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush);

    internal static Typeface CreateTypeface(FontWeight fontWeight) =>
        new("Microsoft YaHei UI", FontStyle.Normal, fontWeight);

    private static TextLine CreateTextLine(
        string text,
        double fontSize,
        IBrush brush,
        Typeface typeface)
    {
        var formatted = Format(text, fontSize, brush, typeface);
        return new TextLine(formatted, GetInkBounds(formatted), 0);
    }

    private static Rect GetInkBounds(FormattedText formatted)
    {
        var geometry = formatted.BuildGeometry(default);
        if (geometry is not null && IsUsableInkBounds(geometry.Bounds))
            return geometry.Bounds;

        var extent = Math.Max(0, formatted.Extent);
        var top = formatted.Height + formatted.OverhangAfter - extent;
        return new Rect(0, top, Math.Max(0, formatted.Width), extent);
    }

    private static bool IsUsableInkBounds(Rect bounds) =>
        double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width > 0
        && bounds.Height > 0;

    internal static double CalculateLineOriginX(
        ImageTextAlignment alignment,
        double boxWidth,
        Rect inkBounds) =>
        alignment switch
        {
            ImageTextAlignment.Left => -boxWidth / 2 - inkBounds.Left,
            ImageTextAlignment.Right => boxWidth / 2 - inkBounds.Right,
            _ => -(inkBounds.Left + inkBounds.Right) / 2
        };

    internal static double CalculateVerticalOrigin(Rect inkBounds) =>
        -(inkBounds.Top + inkBounds.Bottom) / 2;

    internal static double CalculateMinimumFittedFontSize(double initialFontSize) =>
        Math.Min(
            initialFontSize,
            Math.Max(MinimumReadableFontSize, initialFontSize * MinimumFittedFontScale));

    private static double CalculateSourceLineHeight(
        ImageTranslationOverlay overlay,
        Vector pixelToDip)
    {
        var regions = overlay.EraseRegions is { Count: > 0 }
            ? overlay.EraseRegions
            : [overlay.Region];
        var heights = regions
            .Select(region =>
            {
                var geometry = GetGeometry(region);
                var scale = GetOrientedScale(region.Angle, pixelToDip).Y;
                return geometry.BoxHeight * scale / CountSourceLines(region.Text);
            })
            .OrderBy(height => height)
            .ToArray();
        if (heights.Length == 0)
            return MinimumFontSize;

        var middle = heights.Length / 2;
        return heights.Length % 2 == 0
            ? (heights[middle - 1] + heights[middle]) / 2
            : heights[middle];
    }

    private static Vector GetOrientedScale(double angle, Vector pixelToDip)
    {
        var radians = angle * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new Vector(
            Math.Sqrt(
                cosine * cosine * pixelToDip.X * pixelToDip.X
                + sine * sine * pixelToDip.Y * pixelToDip.Y),
            Math.Sqrt(
                sine * sine * pixelToDip.X * pixelToDip.X
                + cosine * cosine * pixelToDip.Y * pixelToDip.Y));
    }

    internal static Color SelectForegroundColor(Color surroundingBackground, IReadOnlyList<Color> regionPixels)
        => ImageTextStyleAnalyzer.SelectForegroundColor(surroundingBackground, regionPixels);

    private static Color EnsureLegibleForeground(Color candidate, ImageFrame background, Rect bounds) =>
        SelectContrastingForeground(candidate, SampleBackgroundColor(background, bounds));

    internal static Color SelectContrastingForeground(Color candidate, Color background)
    {
        if (ContrastRatio(candidate, background) >= MinimumTextContrastRatio)
            return candidate;

        return BestContrastingColor(background);
    }

    private static Color BestContrastingColor(Color background) =>
        ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background)
            ? Colors.Black
            : Colors.White;

    private static Color SampleBackgroundColor(ImageFrame frame, Rect bounds)
    {
        var left = Math.Max(0, (int)Math.Floor(bounds.Left));
        var top = Math.Max(0, (int)Math.Floor(bounds.Top));
        var right = Math.Min(frame.Width, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(bounds.Bottom));
        if (right <= left || bottom <= top)
            return Colors.Gray;

        double red = 0;
        double green = 0;
        double blue = 0;
        var count = 0;
        var pixels = frame.Pixels.Span;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = y * frame.Stride + x * 4;
                blue += pixels[offset];
                green += pixels[offset + 1];
                red += pixels[offset + 2];
                count++;
            }
        }

        return count == 0
            ? Colors.Gray
            : Color.FromRgb(
                (byte)Math.Clamp(Math.Round(red / count), 0, 255),
                (byte)Math.Clamp(Math.Round(green / count), 0, 255),
                (byte)Math.Clamp(Math.Round(blue / count), 0, 255));
    }

    private static double ContrastRatio(Color left, Color right)
    {
        var leftLuminance = RelativeLuminance(left);
        var rightLuminance = RelativeLuminance(right);
        return (Math.Max(leftLuminance, rightLuminance) + 0.05)
               / (Math.Min(leftLuminance, rightLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R)
        + 0.7152 * Linearize(color.G)
        + 0.0722 * Linearize(color.B);

    private static double Linearize(byte component)
    {
        var value = component / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static RegionGeometry GetGeometry(OcrTextRegion region)
    {
        var left = region.Polygon.Min(point => point.X);
        var top = region.Polygon.Min(point => point.Y);
        var right = region.Polygon.Max(point => point.X);
        var bottom = region.Polygon.Max(point => point.Y);
        var center = new Point(
            region.Polygon.Average(point => point.X),
            region.Polygon.Average(point => point.Y));
        var edges = new List<double>(region.Polygon.Count);
        for (var index = 0; index < region.Polygon.Count; index++)
        {
            var start = region.Polygon[index];
            var end = region.Polygon[(index + 1) % region.Polygon.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 0.01)
                edges.Add(length);
        }

        var bounds = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        return edges.Count == 0
            ? new RegionGeometry(bounds, center, bounds.Width, bounds.Height)
            : new RegionGeometry(bounds, center, edges.Max(), edges.Min());
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private sealed record RegionGeometry(Rect Bounds, Point Center, double BoxWidth, double BoxHeight);

    private sealed record TextLayout(IReadOnlyList<TextLine> Lines, Rect InkBounds);

    private sealed record TextLine(FormattedText Formatted, Rect InkBounds, double OffsetY)
    {
        public double Width => Formatted.Width;
        public double Height => Formatted.Height;
    }

}
