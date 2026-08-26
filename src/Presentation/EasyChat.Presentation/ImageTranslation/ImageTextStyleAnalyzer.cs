using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.ImageTranslation;

internal sealed class ImageTextStyleAnalyzer
{
    private const int MaximumAnalysisPixels = 262_144;
    private const int MinimumForegroundPixels = 24;
    private const int MinimumCenterlineSamples = 6;
    private const int MinimumComponentPixels = 2;
    private const double MinimumLineHeight = 12;
    private const double MinimumForegroundRatio = 0.10;
    private const double MinimumNormalizedBoldStrokeWidth = 0.115;
    private const double MinimumForegroundSeparationSquared = 16;
    private const double DiagonalDistance = 1.4142135623730951;
    private readonly ConditionalWeakTable<ImageFrame, FrameAnalysisCache> _frameCaches = new();

    public ImageTextStyle Analyze(
        ImageFrame frame,
        OcrTextRegion renderRegion,
        IReadOnlyList<OcrTextRegion>? sourceRegions = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(renderRegion);

        var cache = _frameCaches.GetValue(frame, static _ => new FrameAnalysisCache());
        var renderAnalysis = cache.GetOrAdd(
            renderRegion,
            () => AnalyzeRegion(frame, renderRegion));
        var regions = sourceRegions is { Count: > 0 }
            ? sourceRegions
            : [renderRegion];
        var weight = regions.All(region => cache.GetOrAdd(
                region,
                () => AnalyzeRegion(frame, region)).IsBold)
            ? FontWeight.Bold
            : FontWeight.Normal;
        return new ImageTextStyle(renderAnalysis.Foreground, weight);
    }

    internal static Color SelectForegroundColor(
        Color surroundingBackground,
        IReadOnlyList<Color> regionPixels)
    {
        ArgumentNullException.ThrowIfNull(regionPixels);
        if (regionPixels.Count == 0)
            return BestContrastingColor(surroundingBackground);

        var clusters = ClusterColors(surroundingBackground, regionPixels);
        return ColorDistanceSquared(clusters.Foreground, surroundingBackground)
               < MinimumForegroundSeparationSquared
            ? BestContrastingColor(surroundingBackground)
            : clusters.Foreground;
    }

    internal static int CalculateSampleStep(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 1;

        var area = (long)width * height;
        return area <= MaximumAnalysisPixels
            ? 1
            : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(area / (double)MaximumAnalysisPixels)));
    }

    private static RegionAnalysis AnalyzeRegion(ImageFrame frame, OcrTextRegion region)
    {
        if (region.Polygon.Count < 3)
            return RegionAnalysis.Default;

        var bounds = GetBounds(region);
        var left = Math.Max(0, (int)Math.Floor(bounds.Left));
        var top = Math.Max(0, (int)Math.Floor(bounds.Top));
        var right = Math.Min(frame.Width, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(frame.Height, (int)Math.Ceiling(bounds.Bottom));
        if (right <= left || bottom <= top)
            return RegionAnalysis.Default;

        var step = CalculateSampleStep(right - left, bottom - top);
        var maskWidth = (right - left + step - 1) / step;
        var maskHeight = (bottom - top + step - 1) / step;
        var mask = new bool[checked(maskWidth * maskHeight)];
        var insidePolygon = new bool[mask.Length];
        var polygonPixelCount = 0;
        var regionPixels = new List<Color>(mask.Length);
        var sampledColors = new Color[mask.Length];
        var pixels = frame.Pixels.Span;

        for (var sampleY = 0; sampleY < maskHeight; sampleY++)
        {
            var y = Math.Min(bottom - 1, top + sampleY * step + step / 2);
            for (var sampleX = 0; sampleX < maskWidth; sampleX++)
            {
                var x = Math.Min(right - 1, left + sampleX * step + step / 2);
                if (!Contains(region.Polygon, x + 0.5, y + 0.5))
                    continue;

                var index = sampleY * maskWidth + sampleX;
                insidePolygon[index] = true;
                var color = ReadColor(pixels, frame.Stride, x, y);
                sampledColors[index] = color;
                regionPixels.Add(color);
                polygonPixelCount++;
            }
        }

        if (polygonPixelCount == 0)
            return RegionAnalysis.Default;

        var surroundingBackground = SampleSurroundingBackgroundColor(frame, bounds);
        var clusters = ClusterColors(surroundingBackground, regionPixels);
        var foreground = ColorDistanceSquared(clusters.Foreground, surroundingBackground)
                         < MinimumForegroundSeparationSquared
            ? BestContrastingColor(surroundingBackground)
            : clusters.Foreground;
        var lineHeight = GetShortEdge(region) / CountSourceLines(region.Text);
        if (lineHeight < MinimumLineHeight
            || ColorDistanceSquared(clusters.Foreground, clusters.Background)
            < MinimumForegroundSeparationSquared)
        {
            return new RegionAnalysis(foreground, false);
        }

        // Keep only pixels that are both closer to the inferred text colour and
        // visibly separated from the local background.
        var foregroundCount = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if (!insidePolygon[index])
                continue;

            var color = sampledColors[index];
            var backgroundDistance = ColorDistanceSquared(color, clusters.Background);
            mask[index] = ColorDistanceSquared(color, clusters.Foreground)
                          < backgroundDistance
                          && backgroundDistance >= MinimumForegroundSeparationSquared;
            if (mask[index])
                foregroundCount++;
        }

        if (foregroundCount < MinimumForegroundPixels
            || foregroundCount / (double)polygonPixelCount < MinimumForegroundRatio)
        {
            return new RegionAnalysis(foreground, false);
        }

        RemoveSmallComponents(mask, maskWidth, maskHeight);
        foregroundCount = mask.Count(value => value);
        if (foregroundCount < MinimumForegroundPixels
            || foregroundCount / (double)polygonPixelCount < MinimumForegroundRatio)
        {
            return new RegionAnalysis(foreground, false);
        }

        var distances = CalculateDistanceTransform(mask, maskWidth, maskHeight);
        // Local distance maxima approximate stroke centerlines. Their diameter is
        // stable across rotation and can be compared with the OCR line height.
        var centerlineDiameters = CollectCenterlineDiameters(
            mask,
            distances,
            maskWidth,
            maskHeight,
            step);
        if (centerlineDiameters.Count < MinimumCenterlineSamples)
            return new RegionAnalysis(foreground, false);

        centerlineDiameters.Sort();
        var medianStrokeWidth = centerlineDiameters.Count % 2 == 0
            ? (centerlineDiameters[centerlineDiameters.Count / 2 - 1]
               + centerlineDiameters[centerlineDiameters.Count / 2]) / 2
            : centerlineDiameters[centerlineDiameters.Count / 2];
        return new RegionAnalysis(
            foreground,
            medianStrokeWidth / lineHeight >= MinimumNormalizedBoldStrokeWidth);
    }

    private static ColorClusters ClusterColors(
        Color surroundingBackground,
        IReadOnlyList<Color> regionPixels)
    {
        // Anchor one cluster at the surrounding background and initialize the
        // other with the most visually distant pixel from inside the OCR region.
        var backgroundCenter = surroundingBackground;
        var foregroundCenter = regionPixels[0];
        var farthestDistance = -1d;
        foreach (var color in regionPixels)
        {
            var distance = ColorDistanceSquared(color, surroundingBackground);
            if (distance <= farthestDistance)
                continue;

            farthestDistance = distance;
            foregroundCenter = color;
        }

        for (var iteration = 0; iteration < 4; iteration++)
        {
            long backgroundRed = 0;
            long backgroundGreen = 0;
            long backgroundBlue = 0;
            long foregroundRed = 0;
            long foregroundGreen = 0;
            long foregroundBlue = 0;
            var backgroundCount = 0;
            var foregroundCount = 0;
            foreach (var color in regionPixels)
            {
                if (ColorDistanceSquared(color, backgroundCenter)
                    <= ColorDistanceSquared(color, foregroundCenter))
                {
                    backgroundRed += color.R;
                    backgroundGreen += color.G;
                    backgroundBlue += color.B;
                    backgroundCount++;
                }
                else
                {
                    foregroundRed += color.R;
                    foregroundGreen += color.G;
                    foregroundBlue += color.B;
                    foregroundCount++;
                }
            }

            if (backgroundCount == 0 || foregroundCount == 0)
                break;

            backgroundCenter = AverageColor(
                backgroundRed,
                backgroundGreen,
                backgroundBlue,
                backgroundCount);
            foregroundCenter = AverageColor(
                foregroundRed,
                foregroundGreen,
                foregroundBlue,
                foregroundCount);
        }

        return ColorDistanceSquared(foregroundCenter, surroundingBackground)
               >= ColorDistanceSquared(backgroundCenter, surroundingBackground)
            ? new ColorClusters(backgroundCenter, foregroundCenter)
            : new ColorClusters(foregroundCenter, backgroundCenter);
    }

    private static void RemoveSmallComponents(bool[] mask, int width, int height)
    {
        var visited = new bool[mask.Length];
        var queue = new Queue<int>();
        var component = new List<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
                continue;

            queue.Enqueue(start);
            visited[start] = true;
            component.Clear();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                var x = current % width;
                var y = current / width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        var nextX = x + dx;
                        var nextY = y + dy;
                        if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                            continue;

                        var next = nextY * width + nextX;
                        if (!mask[next] || visited[next])
                            continue;

                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            if (component.Count >= MinimumComponentPixels)
                continue;

            foreach (var index in component)
                mask[index] = false;
        }
    }

    private static double[] CalculateDistanceTransform(bool[] mask, int width, int height)
    {
        var distances = new double[mask.Length];
        for (var index = 0; index < mask.Length; index++)
            distances[index] = mask[index] ? double.PositiveInfinity : 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!mask[index])
                    continue;

                distances[index] = Math.Min(
                    distances[index],
                    Math.Min(
                        NeighborDistance(distances, width, height, x - 1, y, 1),
                        Math.Min(
                            NeighborDistance(distances, width, height, x, y - 1, 1),
                            Math.Min(
                                NeighborDistance(distances, width, height, x - 1, y - 1, DiagonalDistance),
                                NeighborDistance(distances, width, height, x + 1, y - 1, DiagonalDistance)))));
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var index = y * width + x;
                if (!mask[index])
                    continue;

                distances[index] = Math.Min(
                    distances[index],
                    Math.Min(
                        NeighborDistance(distances, width, height, x + 1, y, 1),
                        Math.Min(
                            NeighborDistance(distances, width, height, x, y + 1, 1),
                            Math.Min(
                                NeighborDistance(distances, width, height, x + 1, y + 1, DiagonalDistance),
                                NeighborDistance(distances, width, height, x - 1, y + 1, DiagonalDistance)))));
            }
        }

        return distances;
    }

    private static double NeighborDistance(
        IReadOnlyList<double> distances,
        int width,
        int height,
        int x,
        int y,
        double cost) =>
        x < 0 || x >= width || y < 0 || y >= height
            ? cost
            : distances[y * width + x] + cost;

    private static List<double> CollectCenterlineDiameters(
        IReadOnlyList<bool> mask,
        IReadOnlyList<double> distances,
        int width,
        int height,
        int sampleStep)
    {
        var values = new List<double>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!mask[index])
                    continue;

                var distance = distances[index];
                var isMaximum = true;
                for (var dy = -1; dy <= 1 && isMaximum; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        var neighborX = x + dx;
                        var neighborY = y + dy;
                        if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height)
                            continue;
                        if (distances[neighborY * width + neighborX] > distance)
                        {
                            isMaximum = false;
                            break;
                        }
                    }
                }

                if (isMaximum)
                    values.Add(distance * 2 * sampleStep);
            }
        }

        return values;
    }

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
        var step = CalculateSampleStep(outerRight - outerLeft, outerBottom - outerTop);
        for (var y = outerTop + step / 2; y < outerBottom; y += step)
        {
            for (var x = outerLeft + step / 2; x < outerRight; x += step)
            {
                if (x >= innerLeft && x < innerRight && y >= innerTop && y < innerBottom)
                    continue;

                samples.Add(ReadColor(pixels, frame.Stride, x, y));
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

        var step = CalculateSampleStep(right - left, bottom - top);
        var sampleWidth = (right - left + step - 1) / step;
        var sampleHeight = (bottom - top + step - 1) / step;
        var samples = new List<Color>(checked(sampleWidth * sampleHeight));
        var pixels = frame.Pixels.Span;
        for (var y = top + step / 2; y < bottom; y += step)
        {
            for (var x = left + step / 2; x < right; x += step)
                samples.Add(ReadColor(pixels, frame.Stride, x, y));
        }

        return AverageColors(samples);
    }

    private static Color ReadColor(ReadOnlySpan<byte> pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static bool Contains(IReadOnlyList<ImagePoint> polygon, double x, double y)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            var currentPoint = polygon[current];
            var previousPoint = polygon[previous];
            if ((currentPoint.Y > y) == (previousPoint.Y > y))
                continue;

            var intersectionX = (previousPoint.X - currentPoint.X)
                                * (y - currentPoint.Y)
                                / (previousPoint.Y - currentPoint.Y)
                                + currentPoint.X;
            if (x < intersectionX)
                inside = !inside;
        }

        return inside;
    }

    private static Rect GetBounds(OcrTextRegion region)
    {
        var left = region.Polygon.Min(point => point.X);
        var top = region.Polygon.Min(point => point.Y);
        var right = region.Polygon.Max(point => point.X);
        var bottom = region.Polygon.Max(point => point.Y);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double GetShortEdge(OcrTextRegion region)
    {
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

        return edges.Count == 0
            ? GetBounds(region).Height
            : edges.Min();
    }

    private static int CountSourceLines(string text) =>
        Math.Max(
            1,
            text.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Count(character => character == '\n') + 1);

    private static Color AverageColors(IEnumerable<Color> colors)
    {
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;
        foreach (var color in colors)
        {
            red += color.R;
            green += color.G;
            blue += color.B;
            count++;
        }

        return count == 0
            ? Colors.Gray
            : AverageColor(red, green, blue, count);
    }

    private static Color AverageColor(long red, long green, long blue, int count) =>
        Color.FromRgb(
            (byte)Math.Clamp(Math.Round(red / (double)count), 0, 255),
            (byte)Math.Clamp(Math.Round(green / (double)count), 0, 255),
            (byte)Math.Clamp(Math.Round(blue / (double)count), 0, 255));

    private static Color BestContrastingColor(Color background) =>
        RelativeLuminance(background) >= 0.5 ? Colors.Black : Colors.White;

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

    private static double ColorDistanceSquared(Color left, Color right)
    {
        var red = left.R - right.R;
        var green = left.G - right.G;
        var blue = left.B - right.B;
        return red * red + green * green + blue * blue;
    }

    private sealed class FrameAnalysisCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<OcrTextRegion, RegionAnalysis> _regions =
            new(RegionReferenceComparer.Instance);

        public RegionAnalysis GetOrAdd(OcrTextRegion region, Func<RegionAnalysis> factory)
        {
            lock (_gate)
            {
                if (_regions.TryGetValue(region, out var cached))
                    return cached;
            }

            var created = factory();
            lock (_gate)
            {
                if (_regions.TryGetValue(region, out var cached))
                    return cached;

                _regions.Add(region, created);
                return created;
            }
        }
    }

    private sealed class RegionReferenceComparer : IEqualityComparer<OcrTextRegion>
    {
        public static RegionReferenceComparer Instance { get; } = new();

        public bool Equals(OcrTextRegion? left, OcrTextRegion? right) =>
            ReferenceEquals(left, right);

        public int GetHashCode(OcrTextRegion value) => RuntimeHelpers.GetHashCode(value);
    }

    private readonly record struct RegionAnalysis(Color Foreground, bool IsBold)
    {
        public static RegionAnalysis Default { get; } = new(Colors.Black, false);
    }

    private readonly record struct ColorClusters(Color Background, Color Foreground);
}

internal sealed record ImageTextStyle(Color Foreground, FontWeight FontWeight)
{
    public static ImageTextStyle Default { get; } = new(Colors.Black, FontWeight.Normal);
}
