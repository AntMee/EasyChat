using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;

namespace EasyChat.Presentation.ImageTranslation;

internal enum ImageTextAlignment
{
    Left,
    Center,
    Right
}

internal static class ImageTextAlignmentAnalyzer
{
    private const double MaximumAngleDifference = 8;
    private const double MaximumLineHeightRatio = 1.6;
    private const double MinimumHorizontalOverlapRatio = 0.5;
    private const double MinimumVerticalGapRatio = -0.25;
    private const double MaximumVerticalGapRatio = 1.75;
    private const double MaximumAnchorSpreadRatio = 0.25;
    private const double MinimumWinningMarginRatio = 0.10;
    private const int MinimumBodyTextLength = 24;

    public static IReadOnlyDictionary<ImageTranslationOverlay, ImageTextAlignment> Analyze(
        IReadOnlyList<ImageTranslationOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);

        var alignments = new Dictionary<ImageTranslationOverlay, ImageTextAlignment>();
        var standalone = new List<ImageTranslationOverlay>();
        foreach (var overlay in overlays)
        {
            if (overlay.EraseRegions is { Count: > 1 } sourceRegions)
                alignments[overlay] = InferAlignment(sourceRegions);
            else
                standalone.Add(overlay);
        }

        foreach (var group in GroupAdjacentRegions(standalone))
        {
            var alignment = InferAlignment(
                group.Select(overlay => overlay.Region).ToArray());
            foreach (var overlay in group)
                alignments[overlay] = alignment;
        }

        return alignments;
    }

    internal static ImageTextAlignment InferAlignment(IReadOnlyList<OcrTextRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
            return ImageTextAlignment.Center;
        if (regions.Count == 1)
            return IsBodyText(regions[0].Text)
                ? ImageTextAlignment.Left
                : ImageTextAlignment.Center;

        var angle = CircularMeanAngle(regions.Select(region => region.Angle));
        var projections = regions
            .Select(region => Project(region, angle))
            .ToArray();
        var lineHeight = Median(projections.Select(projection => projection.LineHeight));
        var scores = new[]
        {
            new AlignmentScore(
                ImageTextAlignment.Left,
                StandardDeviation(projections.Select(projection => projection.Left))),
            new AlignmentScore(
                ImageTextAlignment.Center,
                StandardDeviation(projections.Select(projection => projection.Center))),
            new AlignmentScore(
                ImageTextAlignment.Right,
                StandardDeviation(projections.Select(projection => projection.Right)))
        };
        Array.Sort(scores, static (left, right) =>
        {
            var comparison = left.Spread.CompareTo(right.Spread);
            return comparison != 0
                ? comparison
                : left.Alignment.CompareTo(right.Alignment);
        });

        var maximumSpread = Math.Max(1, lineHeight) * MaximumAnchorSpreadRatio;
        var minimumMargin = Math.Max(1, lineHeight) * MinimumWinningMarginRatio;
        return scores[0].Spread <= maximumSpread
               && scores[1].Spread - scores[0].Spread >= minimumMargin
            ? scores[0].Alignment
            : ImageTextAlignment.Left;
    }

    internal static double CircularMeanAngle(IEnumerable<double> angles)
    {
        var values = angles.ToArray();
        if (values.Length == 0)
            return 0;

        var sine = 0d;
        var cosine = 0d;
        foreach (var angle in values)
        {
            var radians = angle * Math.PI / 180d;
            sine += Math.Sin(radians);
            cosine += Math.Cos(radians);
        }

        if (Math.Abs(sine) < 0.000001 && Math.Abs(cosine) < 0.000001)
            return NormalizeAngle(values[0]);

        return NormalizeAngle(Math.Atan2(sine, cosine) * 180d / Math.PI);
    }

    private static IReadOnlyList<IReadOnlyList<ImageTranslationOverlay>> GroupAdjacentRegions(
        IReadOnlyList<ImageTranslationOverlay> overlays)
    {
        if (overlays.Count == 0)
            return [];

        var parents = Enumerable.Range(0, overlays.Count).ToArray();
        for (var left = 0; left < overlays.Count; left++)
        {
            for (var right = left + 1; right < overlays.Count; right++)
            {
                if (CanShareParagraph(overlays[left].Region, overlays[right].Region))
                    Union(parents, left, right);
            }
        }

        return overlays
            .Select((overlay, index) => new { Overlay = overlay, Root = Find(parents, index) })
            .GroupBy(item => item.Root)
            .Select(group => (IReadOnlyList<ImageTranslationOverlay>)group
                .Select(item => item.Overlay)
                .ToArray())
            .ToArray();
    }

    private static bool CanShareParagraph(OcrTextRegion first, OcrTextRegion second)
    {
        if (Math.Abs(NormalizeAngle(first.Angle - second.Angle)) > MaximumAngleDifference)
            return false;

        var angle = CircularMeanAngle([first.Angle, second.Angle]);
        var firstProjection = Project(first, angle);
        var secondProjection = Project(second, angle);
        var upper = firstProjection.Top <= secondProjection.Top
            ? firstProjection
            : secondProjection;
        var lower = firstProjection.Top <= secondProjection.Top
            ? secondProjection
            : firstProjection;
        var lineHeight = Math.Max(1, Math.Min(upper.LineHeight, lower.LineHeight));
        var heightRatio = Math.Max(upper.LineHeight, lower.LineHeight)
                          / Math.Max(1, Math.Min(upper.LineHeight, lower.LineHeight));
        if (heightRatio > MaximumLineHeightRatio)
            return false;

        var verticalGap = lower.Top - upper.Bottom;
        if (verticalGap < lineHeight * MinimumVerticalGapRatio
            || verticalGap > lineHeight * MaximumVerticalGapRatio)
        {
            return false;
        }

        var horizontalOverlap = Math.Min(upper.Right, lower.Right)
                                - Math.Max(upper.Left, lower.Left);
        var minimumWidth = Math.Min(upper.Width, lower.Width);
        return horizontalOverlap / Math.Max(1, minimumWidth)
               >= MinimumHorizontalOverlapRatio;
    }

    private static ProjectedRegion Project(OcrTextRegion region, double angle)
    {
        var radians = angle * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var point in region.Polygon)
        {
            var horizontal = point.X * cosine + point.Y * sine;
            var vertical = -point.X * sine + point.Y * cosine;
            left = Math.Min(left, horizontal);
            top = Math.Min(top, vertical);
            right = Math.Max(right, horizontal);
            bottom = Math.Max(bottom, vertical);
        }

        return new ProjectedRegion(
            left,
            top,
            right,
            bottom,
            CountSourceLines(region.Text));
    }

    private static bool IsBodyText(string text)
    {
        var normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Contains('\n')
               || normalized.EnumerateRunes().Take(MinimumBodyTextLength).Count()
               >= MinimumBodyTextLength;
    }

    private static int CountSourceLines(string text) =>
        Math.Max(
            1,
            text.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Count(character => character == '\n') + 1);

    private static double StandardDeviation(IEnumerable<double> values)
    {
        var items = values.ToArray();
        if (items.Length <= 1)
            return 0;

        var average = items.Average();
        return Math.Sqrt(items.Average(value => Math.Pow(value - average, 2)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var items = values.OrderBy(value => value).ToArray();
        if (items.Length == 0)
            return 1;

        var middle = items.Length / 2;
        return items.Length % 2 == 0
            ? (items[middle - 1] + items[middle]) / 2
            : items[middle];
    }

    private static int Find(IList<int> parents, int index)
    {
        while (parents[index] != index)
        {
            parents[index] = parents[parents[index]];
            index = parents[index];
        }

        return index;
    }

    private static void Union(IList<int> parents, int left, int right)
    {
        var leftRoot = Find(parents, left);
        var rightRoot = Find(parents, right);
        if (leftRoot != rightRoot)
            parents[rightRoot] = leftRoot;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private readonly record struct AlignmentScore(ImageTextAlignment Alignment, double Spread);

    private readonly record struct ProjectedRegion(
        double Left,
        double Top,
        double Right,
        double Bottom,
        int LineCount)
    {
        public double Width => Math.Max(0, Right - Left);
        public double Height => Math.Max(0, Bottom - Top);
        public double LineHeight => Height / Math.Max(1, LineCount);
        public double Center => (Left + Right) / 2;
    }
}
