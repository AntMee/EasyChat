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
    private const double NominalFontHeightRatio = 0.72;
    private const double MinimumFittedFontScale = 0.70;
    private const double MaximumPreferredHeightRatio = 1.0;
    private const double MinimumTextContrastRatio = 2.2;
    private const double MinimumForegroundSeparation = 4;
    private readonly IImageBackgroundCleaner _backgroundCleaner;

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
            overlay => AnalyzeStyle(source, overlay.Region));
        var backgroundFrame = await _backgroundCleaner.RemoveTextAsync(
            source,
            renderable.Select(overlay => overlay.Region).ToArray(),
            options.EraseMode,
            cancellationToken).ConfigureAwait(false);

        using var background = AvaloniaImageFrames.ToBitmap(backgroundFrame);
        using var output = new RenderTargetBitmap(background.PixelSize, background.Dpi);
        var pixelToDip = PixelToDipScale(background.PixelSize, background.Size);
        using (var context = output.CreateDrawingContext())
        {
            context.DrawImage(background, new Rect(background.Size));
            foreach (var overlay in renderable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var geometry = GetGeometry(overlay.Region);
                var boxWidth = Math.Max(1, geometry.BoxWidth * pixelToDip.X);
                var boxHeight = Math.Max(1, geometry.BoxHeight * pixelToDip.Y);
                var style = styles[overlay];
                var foreground = EnsureLegibleForeground(
                    style.Foreground,
                    backgroundFrame,
                    geometry.Bounds);
                var brush = new SolidColorBrush(foreground);
                var layout = CreateLayout(
                    overlay.Translation,
                    boxWidth,
                    boxHeight,
                    CalculatePreferredFontSize(new Rect(0, 0, boxWidth, boxHeight), overlay.Region.Angle),
                    brush);
                var center = new Point(
                    geometry.Center.X * pixelToDip.X,
                    geometry.Center.Y * pixelToDip.Y);
                var matrix = Matrix.CreateRotation(overlay.Region.Angle * Math.PI / 180d)
                             * Matrix.CreateTranslation(center.X, center.Y);
                using (context.PushTransform(matrix))
                {
                    var y = -layout.Height / 2;
                    foreach (var line in layout.Lines)
                    {
                        var x = -boxWidth / 2;
                        context.DrawText(Format(line.Text, line.FontSize, brush), new Point(x, y));
                        y += line.Height;
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
        return Math.Max(MinimumFontSize, originalBounds.Height * NominalFontHeightRatio);
    }

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
        double preferredFontSize,
        IBrush brush)
    {
        var initialFontSize = Math.Max(MinimumFontSize, preferredFontSize);
        // Preserve the original visual scale first. We only shrink enough to make a
        // normal translation fit; an unusually long translation may extend beyond
        // the OCR box rather than becoming unreadably small.
        var minimumFontSize = Math.Min(
            initialFontSize,
            Math.Max(MinimumReadableFontSize, initialFontSize * MinimumFittedFontScale));
        var fontSize = initialFontSize;

        while (true)
        {
            var lines = WrapText(text, width, fontSize, brush);
            var totalWidth = lines.Count == 0 ? 0 : lines.Max(line => line.Width);
            var totalHeight = lines.Sum(line => line.Height);
            if (totalHeight <= height * MaximumPreferredHeightRatio || fontSize <= minimumFontSize)
                return new TextLayout(lines, totalWidth, totalHeight);

            fontSize = Math.Max(minimumFontSize, fontSize * 0.94);
        }
    }

    private static IReadOnlyList<TextLine> WrapText(
        string text,
        double maxWidth,
        double fontSize,
        IBrush brush)
    {
        var lines = new List<TextLine>();
        foreach (var sourceLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            if (sourceLine.Length == 0)
            {
                lines.Add(Measure(string.Empty, fontSize, brush));
                continue;
            }

            var current = new StringBuilder();
            foreach (var character in sourceLine)
            {
                current.Append(character);
                if (current.Length == 1 || Measure(current.ToString(), fontSize, brush).Width <= maxWidth)
                    continue;

                var split = LastWrapOpportunity(current);
                if (split <= 0)
                    split = current.Length - 1;
                var completed = current.ToString(0, split).TrimEnd();
                if (completed.Length > 0)
                    lines.Add(Measure(completed, fontSize, brush));

                var remaining = current.ToString(split, current.Length - split).TrimStart();
                current.Clear();
                current.Append(remaining);
            }

            if (current.Length > 0)
                lines.Add(Measure(current.ToString(), fontSize, brush));
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
        IBrush brush) =>
        CreateTextLine(text, fontSize, brush);

    private static FormattedText Format(string text, double fontSize, IBrush brush) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            fontSize,
            brush);

    private static TextLine CreateTextLine(string text, double fontSize, IBrush brush)
    {
        var formatted = Format(text, fontSize, brush);
        return new TextLine(text, fontSize, formatted.Width, formatted.Height);
    }

    private static TextStyle AnalyzeStyle(ImageFrame frame, OcrTextRegion region)
    {
        var bounds = GetGeometry(region).Bounds;
        var left = Math.Max(0, (int)Math.Floor(bounds.Left));
        var top = Math.Max(0, (int)Math.Floor(bounds.Top));
        var right = Math.Min(frame.Width, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(bounds.Bottom));
        var samples = new List<Color>();
        var pixels = frame.Pixels.Span;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = y * frame.Stride + x * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                samples.Add(Color.FromRgb(red, green, blue));
            }
        }

        if (samples.Count == 0)
            return TextStyle.Default;

        var surroundingBackground = SampleSurroundingBackgroundColor(frame, bounds);
        return new TextStyle(SelectForegroundColor(surroundingBackground, samples));
    }

    internal static Color SelectForegroundColor(Color surroundingBackground, IReadOnlyList<Color> regionPixels)
    {
        ArgumentNullException.ThrowIfNull(regionPixels);
        if (regionPixels.Count == 0)
            return BestContrastingColor(surroundingBackground);

        // OCR boxes are mostly background. Separate the local pixels into a
        // background cluster and a text-colour cluster, anchored by the colour
        // sampled outside the OCR box. This preserves subtle UI text colours.
        var backgroundCenter = surroundingBackground;
        var foregroundCenter = regionPixels
            .OrderByDescending(color => ColorDistance(color, surroundingBackground))
            .First();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var backgroundCluster = new List<Color>();
            var foregroundCluster = new List<Color>();
            foreach (var color in regionPixels)
            {
                if (ColorDistance(color, backgroundCenter) <= ColorDistance(color, foregroundCenter))
                    backgroundCluster.Add(color);
                else
                    foregroundCluster.Add(color);
            }

            if (backgroundCluster.Count == 0 || foregroundCluster.Count == 0)
                break;

            backgroundCenter = AverageColors(backgroundCluster);
            foregroundCenter = AverageColors(foregroundCluster);
        }

        var selected = ColorDistance(foregroundCenter, surroundingBackground)
                       >= ColorDistance(backgroundCenter, surroundingBackground)
            ? foregroundCenter
            : backgroundCenter;
        return ColorDistance(selected, surroundingBackground) < MinimumForegroundSeparation
            ? BestContrastingColor(surroundingBackground)
            : selected;
    }

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

    private static Color SampleSurroundingBackgroundColor(ImageFrame frame, Rect bounds)
    {
        var padding = Math.Clamp(
            (int)Math.Round(Math.Min(bounds.Width, bounds.Height) * 0.4),
            2,
            16);
        var outerLeft = Math.Max(0, (int)Math.Floor(bounds.Left) - padding);
        var outerTop = Math.Max(0, (int)Math.Floor(bounds.Top) - padding);
        var outerRight = Math.Min(frame.Width, (int)Math.Ceiling(bounds.Right) + padding);
        var outerBottom = Math.Min(frame.Height, (int)Math.Ceiling(bounds.Bottom) + padding);
        var innerLeft = Math.Max(0, (int)Math.Floor(bounds.Left));
        var innerTop = Math.Max(0, (int)Math.Floor(bounds.Top));
        var innerRight = Math.Min(frame.Width, (int)Math.Ceiling(bounds.Right));
        var innerBottom = Math.Min(frame.Height, (int)Math.Ceiling(bounds.Bottom));
        var samples = new List<Color>();
        var pixels = frame.Pixels.Span;
        for (var y = outerTop; y < outerBottom; y++)
        {
            for (var x = outerLeft; x < outerRight; x++)
            {
                if (x >= innerLeft && x < innerRight && y >= innerTop && y < innerBottom)
                    continue;

                var offset = y * frame.Stride + x * 4;
                samples.Add(Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]));
            }
        }

        return samples.Count == 0
            ? SampleBackgroundColor(frame, bounds)
            : AverageColors(samples);
    }

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

    private static Color AverageColors(IEnumerable<Color> colors)
    {
        var values = colors.ToArray();
        return values.Length == 0
            ? Colors.Gray
            : Color.FromRgb(
                (byte)Math.Clamp(Math.Round(values.Average(value => value.R)), 0, 255),
                (byte)Math.Clamp(Math.Round(values.Average(value => value.G)), 0, 255),
                (byte)Math.Clamp(Math.Round(values.Average(value => value.B)), 0, 255));
    }

    private static double ColorDistance(Color left, Color right)
    {
        var red = left.R - right.R;
        var green = left.G - right.G;
        var blue = left.B - right.B;
        return Math.Sqrt(red * red + green * green + blue * blue);
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

    private sealed record TextLayout(
        IReadOnlyList<TextLine> Lines,
        double Width,
        double Height);

    private sealed record TextLine(string Text, double FontSize, double Width, double Height);

    private sealed record TextStyle(Color Foreground)
    {
        public static TextStyle Default { get; } = new(Colors.Black);
    }

}
