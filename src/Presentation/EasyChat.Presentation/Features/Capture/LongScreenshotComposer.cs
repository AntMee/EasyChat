using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Capture;
using Avalonia.Media.Imaging;
using EasyChat.Presentation.ImageTranslation;

namespace EasyChat.Presentation.Features.Capture;

internal enum LongScreenshotDirection
{
    Vertical,
    Horizontal
}

/// <summary>Combines viewport captures while removing the repeated scroll overlap.</summary>
internal static class LongScreenshotComposer
{
    internal const int MaximumHeight = 16_000;
    internal const int MaximumWidth = 16_000;
    internal const int MaximumImageBytes = 128 * 1024 * 1024;
    // Keep capturing until the target stops moving; this is only a guard for
    // pages that never expose a stable bottom edge.
    internal const int MaximumFrames = 96;
    // Poll frequently enough to retain intermediate viewports during a
    // continuous wheel or trackpad gesture.
    internal const int InitialSettleDelay = 120;
    internal const int SettleDelay = 30;
    // Require two equal captures before accepting a viewport. A sample taken
    // while the target is still moving otherwise creates seams and duplicates.
    internal const int MaximumSettleSamples = 8;
    internal const int StableViewportSamples = 2;

    internal static bool IsSameViewport(
        ImageFrame first,
        ImageFrame second,
        LongScreenshotDirection direction = LongScreenshotDirection.Vertical) =>
        AreEqual(first, second, direction);

    internal static ImageFrame ToImageFrame(Bitmap bitmap) =>
        AvaloniaImageFrames.ToImageFrame(bitmap);

    internal static Bitmap ToBitmap(ImageFrame frame) =>
        AvaloniaImageFrames.ToBitmap(frame);

    internal static Bitmap ToPreviewBitmap(
        ImageFrame frame,
        int maximumWidth = 480,
        int maximumHeight = 560)
    {
        var scale = Math.Min(
            1d,
            Math.Min((double)maximumWidth / frame.Width, (double)maximumHeight / frame.Height));
        if (scale >= 0.999d)
            return ToBitmap(frame);

        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var source = frame.Pixels.Span;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(frame.Height - 1, (int)(y / scale));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(frame.Width - 1, (int)(x / scale));
                source.Slice(sourceY * frame.Stride + sourceX * 4, 4)
                    .CopyTo(pixels.AsSpan(y * stride + x * 4, 4));
            }
        }

        return ToBitmap(new ImageFrame(width, height, stride, frame.DpiX, frame.DpiY, pixels));
    }

    /// <summary>
    /// Returns a compact copy of the composed image tail for global re-
    /// registration. Matching against more than the immediately preceding
    /// viewport prevents a one-pixel local error from becoming permanent
    /// drift after many scrolls.
    /// </summary>
    internal static ImageFrame TakeTail(
        ImageFrame frame,
        LongScreenshotDirection direction,
        int maximumDimension)
    {
        var dimension = GetDimension(frame, direction);
        var length = Math.Clamp(maximumDimension, 1, dimension);
        if (length == dimension)
            return frame;

        if (direction == LongScreenshotDirection.Vertical)
        {
            var stride = checked(frame.Width * 4);
            var pixels = new byte[checked(stride * length)];
            CopyRows(frame, pixels, stride, 0, frame.Height - length, length);
            return new ImageFrame(frame.Width, length, stride, frame.DpiX, frame.DpiY, pixels);
        }

        var compactStride = checked(length * 4);
        var compactPixels = new byte[checked(compactStride * frame.Height)];
        CopyColumns(frame, compactPixels, compactStride, 0, frame.Width - length, length);
        return new ImageFrame(length, frame.Height, compactStride, frame.DpiX, frame.DpiY, compactPixels);
    }

    internal static ImageFrame Compose(
        IReadOnlyList<ImageFrame> frames,
        LongScreenshotDirection direction = LongScreenshotDirection.Vertical,
        IReadOnlyList<int>? cachedOverlaps = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("At least one capture is required.", nameof(frames));

        var first = frames[0];
        if (frames.Any(frame => frame.PixelFormat != ImagePixelFormat.Bgra32) ||
            frames.Any(frame => direction == LongScreenshotDirection.Vertical
                ? frame.Width != first.Width
                : frame.Height != first.Height))
        {
            var axis = direction == LongScreenshotDirection.Vertical ? "width" : "height";
            throw new ArgumentException(
                $"Long screenshot captures must have the same {axis} and pixel format.",
                nameof(frames));
        }

        var fixedDimension = direction == LongScreenshotDirection.Vertical ? first.Width : first.Height;
        var maximumOutputDimension = Math.Min(
            direction == LongScreenshotDirection.Vertical ? MaximumHeight : MaximumWidth,
            MaximumImageBytes / checked(fixedDimension * 4));
        var firstDimension = direction == LongScreenshotDirection.Vertical ? first.Height : first.Width;
        if (maximumOutputDimension < firstDimension)
            throw new ArgumentException("The selected screenshot is too large.", nameof(frames));

        var outputDimension = firstDimension;
        for (var index = 1; index < frames.Count && outputDimension < maximumOutputDimension; index++)
        {
            if (AreEqual(frames[index - 1], frames[index], direction))
                break;
            var overlap = cachedOverlaps is not null && index - 1 < cachedOverlaps.Count
                ? cachedOverlaps[index - 1]
                : FindOverlap(frames[index - 1], frames[index], direction);
            outputDimension = checked(Math.Min(
                maximumOutputDimension,
                outputDimension + GetDimension(frames[index], direction) - overlap));
        }

        return direction == LongScreenshotDirection.Vertical
            ? ComposeVertically(frames, first, outputDimension, cachedOverlaps)
            : ComposeHorizontally(frames, first, outputDimension, cachedOverlaps);
    }

    internal static LongScreenshotAccumulator CreateAccumulator(
        ImageFrame first,
        LongScreenshotDirection direction,
        ILongScreenshotStitcher? stitcher = null) => new(first, direction, stitcher);

    internal static int EstimateAdvance(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotDirection direction = LongScreenshotDirection.Vertical)
    {
        var perpendicularMatches = direction == LongScreenshotDirection.Vertical
            ? previous.Width == current.Width
            : previous.Height == current.Height;
        if (!perpendicularMatches)
            return GetDimension(current, direction) / 2;
        return AreEqual(previous, current, direction)
            ? 0
            : GetDimension(current, direction) - FindOverlap(previous, current, direction);
    }

    private static ImageFrame ComposeVertically(
        IReadOnlyList<ImageFrame> frames,
        ImageFrame first,
        int outputHeight,
        IReadOnlyList<int>? cachedOverlaps)
    {
        var stride = checked(first.Width * 4);
        var pixels = new byte[checked(stride * outputHeight)];
        var outputY = first.Height;
        CopyRows(first, pixels, stride, 0, 0, first.Height);

        for (var index = 1; index < frames.Count && outputY < outputHeight; index++)
        {
            var previous = frames[index - 1];
            var current = frames[index];
            if (AreEqual(previous, current, LongScreenshotDirection.Vertical))
                break;

            var overlap = cachedOverlaps is not null && index - 1 < cachedOverlaps.Count
                ? cachedOverlaps[index - 1]
                : FindOverlap(previous, current, LongScreenshotDirection.Vertical);
            var rows = Math.Min(current.Height - overlap, outputHeight - outputY);
            if (rows <= 0)
                break;
            CopyRows(current, pixels, stride, outputY, overlap, rows);
            outputY += rows;
        }

        if (outputY != outputHeight)
            Array.Resize(ref pixels, checked(stride * outputY));
        return new ImageFrame(first.Width, outputY, stride, first.DpiX, first.DpiY, pixels);
    }

    private static ImageFrame ComposeHorizontally(
        IReadOnlyList<ImageFrame> frames,
        ImageFrame first,
        int outputWidth,
        IReadOnlyList<int>? cachedOverlaps)
    {
        var stride = checked(outputWidth * 4);
        var pixels = new byte[checked(stride * first.Height)];
        var outputX = first.Width;
        CopyColumns(first, pixels, stride, 0, 0, first.Width);

        for (var index = 1; index < frames.Count && outputX < outputWidth; index++)
        {
            var previous = frames[index - 1];
            var current = frames[index];
            if (AreEqual(previous, current, LongScreenshotDirection.Horizontal))
                break;

            var overlap = cachedOverlaps is not null && index - 1 < cachedOverlaps.Count
                ? cachedOverlaps[index - 1]
                : FindOverlap(previous, current, LongScreenshotDirection.Horizontal);
            var columns = Math.Min(current.Width - overlap, outputWidth - outputX);
            if (columns <= 0)
                break;
            CopyColumns(current, pixels, stride, outputX, overlap, columns);
            outputX += columns;
        }

        if (outputX != outputWidth)
        {
            var compactStride = checked(outputX * 4);
            var compactPixels = new byte[checked(compactStride * first.Height)];
            for (var row = 0; row < first.Height; row++)
                pixels.AsSpan(row * stride, compactStride)
                    .CopyTo(compactPixels.AsSpan(row * compactStride, compactStride));
            pixels = compactPixels;
            stride = compactStride;
        }

        return new ImageFrame(outputX, first.Height, stride, first.DpiX, first.DpiY, pixels);
    }

    internal static int FindOverlap(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotDirection direction)
    {
        var maximum = Math.Min(GetDimension(previous, direction), GetDimension(current, direction)) - 1;
        if (maximum < 8)
            return 0;

        // Search the complete valid range. Manual scrolling can advance by more
        // than 80% of a viewport, in which case forcing a minimum overlap causes
        // the next frame to be duplicated or truncated.
        var previousDimension = GetDimension(previous, direction);
        var minimum = 1;
        var preferredOverlap = Math.Clamp((previousDimension * 3) / 4, minimum, maximum);
        var bestOverlap = preferredOverlap;
        var bestScore = double.MaxValue;
        var coarseStep = Math.Max(2, maximum / 96);
        var residuePasses = maximum <= 512 ? coarseStep : 2;
        for (var pass = 0; pass < residuePasses; pass++)
        {
            var residue = minimum + (pass * (maximum <= 512 ? 1 : Math.Max(1, coarseStep / 2)));
            if (residue > coarseStep)
                break;
            for (var overlap = residue; overlap <= maximum; overlap += coarseStep)
                Score(overlap, ref bestOverlap, ref bestScore, previous, current, preferredOverlap, direction);
        }

        var refineStart = Math.Max(minimum, bestOverlap - coarseStep);
        var refineEnd = Math.Min(maximum, bestOverlap + coarseStep);
        for (var overlap = refineStart; overlap <= refineEnd; overlap++)
            Score(overlap, ref bestOverlap, ref bestScore, previous, current, preferredOverlap, direction);

        // A poor match means the scroll skipped the previous viewport entirely
        // (or the page is too dynamic to correlate). Appending the full frame is
        // safer than inventing an overlap that duplicates content.
        return bestScore <= 36d ? bestOverlap : 0;
    }

    private static void Score(
        int overlap,
        ref int bestOverlap,
        ref double bestScore,
        ImageFrame previous,
        ImageFrame current,
        int preferredOverlap,
        LongScreenshotDirection direction)
    {
        var pixelScore = Difference(previous, current, overlap, direction);
        var distance = Math.Abs(overlap - preferredOverlap);
        var score = pixelScore + (distance * 0.08d);
        if (score < bestScore)
        {
            bestScore = score;
            bestOverlap = overlap;
        }
    }

    private static double Difference(
        ImageFrame previous,
        ImageFrame current,
        int overlap,
        LongScreenshotDirection direction)
    {
        var sampleCount = Math.Min(48, overlap);
        var overlapStep = Math.Max(1, overlap / sampleCount);
        var perpendicularDimension = direction == LongScreenshotDirection.Vertical
            ? previous.Width
            : previous.Height;
        var perpendicularStep = Math.Max(1, perpendicularDimension / 96);
        // Score independent strips and discard the worst and best strip. A
        // moving caret, cursor, video frame or other local animation must not
        // move the seam for the whole screenshot.
        const int bandCount = 8;
        var bandDifferences = new double[bandCount];
        var bandEdgeDifferences = new double[bandCount];
        var bandPerpendicularEdgeDifferences = new double[bandCount];
        var bandSamples = new int[bandCount];
        for (var overlapOffset = 0; overlapOffset < overlap; overlapOffset += overlapStep)
        {
            for (var perpendicularOffset = 0;
                 perpendicularOffset < perpendicularDimension;
                 perpendicularOffset += perpendicularStep)
            {
                var band = Math.Min(
                    bandCount - 1,
                    perpendicularOffset * bandCount / Math.Max(1, perpendicularDimension));
                var previousOffset = direction == LongScreenshotDirection.Vertical
                    ? ((previous.Height - overlap + overlapOffset) * previous.Stride) + perpendicularOffset * 4
                    : perpendicularOffset * previous.Stride + (previous.Width - overlap + overlapOffset) * 4;
                var currentOffset = direction == LongScreenshotDirection.Vertical
                    ? overlapOffset * current.Stride + perpendicularOffset * 4
                    : perpendicularOffset * current.Stride + overlapOffset * 4;
                var previousPixels = previous.Pixels.Span;
                var currentPixels = current.Pixels.Span;
                bandDifferences[band] += Math.Abs(previousPixels[previousOffset] - currentPixels[currentOffset]);
                bandDifferences[band] += Math.Abs(previousPixels[previousOffset + 1] - currentPixels[currentOffset + 1]);
                bandDifferences[band] += Math.Abs(previousPixels[previousOffset + 2] - currentPixels[currentOffset + 2]);
                if (overlapOffset + 1 < overlap)
                {
                    var previousNextOffset = direction == LongScreenshotDirection.Vertical
                        ? previousOffset + previous.Stride
                        : previousOffset + 4;
                    var currentNextOffset = direction == LongScreenshotDirection.Vertical
                        ? currentOffset + current.Stride
                        : currentOffset + 4;
                    var previousEdge = Luma(previousPixels, previousOffset) - Luma(previousPixels, previousNextOffset);
                    var currentEdge = Luma(currentPixels, currentOffset) - Luma(currentPixels, currentNextOffset);
                    bandEdgeDifferences[band] += Math.Abs(previousEdge - currentEdge);
                }
                if (perpendicularOffset + perpendicularStep < perpendicularDimension)
                {
                    var previousNextOffset = direction == LongScreenshotDirection.Vertical
                        ? previousOffset + perpendicularStep * 4
                        : previousOffset + perpendicularStep * previous.Stride;
                    var currentNextOffset = direction == LongScreenshotDirection.Vertical
                        ? currentOffset + perpendicularStep * 4
                        : currentOffset + perpendicularStep * current.Stride;
                    var previousEdge = Luma(previousPixels, previousOffset) - Luma(previousPixels, previousNextOffset);
                    var currentEdge = Luma(currentPixels, currentOffset) - Luma(currentPixels, currentNextOffset);
                    bandPerpendicularEdgeDifferences[band] += Math.Abs(previousEdge - currentEdge);
                }
                bandSamples[band] += 3;
            }
        }
        var scores = new double[bandCount];
        var scoreCount = 0;
        for (var band = 0; band < bandCount; band++)
        {
            if (bandSamples[band] == 0)
                continue;
            var colorScore = bandDifferences[band] / bandSamples[band];
            var edgeScore = bandEdgeDifferences[band] / Math.Max(1, bandSamples[band] / 3);
            var perpendicularEdgeScore = bandPerpendicularEdgeDifferences[band]
                / Math.Max(1, bandSamples[band] / 3);
            scores[scoreCount++] = colorScore * 0.75d
                                   + edgeScore * 0.20d
                                   + perpendicularEdgeScore * 0.05d;
        }
        if (scoreCount == 0)
            return double.MaxValue;
        Array.Sort(scores, 0, scoreCount);
        var first = scoreCount >= 5 ? 1 : 0;
        var last = scoreCount >= 5 ? scoreCount - 1 : scoreCount;
        var total = 0d;
        for (var index = first; index < last; index++)
            total += scores[index];
        return total / Math.Max(1, last - first);
    }

    private static double Luma(ReadOnlySpan<byte> pixels, int offset) =>
        (pixels[offset] * 0.114d) +
        (pixels[offset + 1] * 0.587d) +
        (pixels[offset + 2] * 0.299d);

    private static bool AreEqual(
        ImageFrame first,
        ImageFrame second,
        LongScreenshotDirection direction) =>
        first.Width == second.Width && first.Height == second.Height &&
        Difference(first, second, GetDimension(first, direction), direction) <= 1d;

    private static int GetDimension(ImageFrame frame, LongScreenshotDirection direction) =>
        direction == LongScreenshotDirection.Vertical ? frame.Height : frame.Width;

    private static void CopyRows(
        ImageFrame source,
        byte[] destination,
        int destinationStride,
        int destinationY,
        int sourceY,
        int rows)
    {
        var rowBytes = checked(source.Width * 4);
        for (var row = 0; row < rows; row++)
            source.Pixels.Span.Slice((sourceY + row) * source.Stride, rowBytes)
                .CopyTo(destination.AsSpan((destinationY + row) * destinationStride, rowBytes));
    }

    private static void CopyColumns(
        ImageFrame source,
        byte[] destination,
        int destinationStride,
        int destinationX,
        int sourceX,
        int columns)
    {
        var bytes = checked(columns * 4);
        for (var row = 0; row < source.Height; row++)
            source.Pixels.Span.Slice(row * source.Stride + sourceX * 4, bytes)
                .CopyTo(destination.AsSpan(row * destinationStride + destinationX * 4, bytes));
    }
}

internal sealed class LongScreenshotAccumulator
{
    private readonly List<ImageFrame> _frames = [];
    private readonly List<int> _overlaps = [];
    private readonly List<LongScreenshotPlacement> _placements = [];
    private readonly LongScreenshotDirection _direction;
    private readonly ILongScreenshotStitcher? _stitcher;
    private ImageFrame _current;

    internal LongScreenshotAccumulator(
        ImageFrame first,
        LongScreenshotDirection direction,
        ILongScreenshotStitcher? stitcher = null)
    {
        _direction = direction;
        _stitcher = stitcher;
        _frames.Add(first);
        _current = first;
    }

    internal IReadOnlyList<ImageFrame> Frames => _frames;
    internal ImageFrame Current => _current;

    internal bool Append(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (LongScreenshotComposer.IsSameViewport(_frames[^1], frame, _direction))
            return false;

        if (_stitcher is not null)
        {
            var axis = _direction == LongScreenshotDirection.Vertical
                ? LongScreenshotAxis.Vertical
                : LongScreenshotAxis.Horizontal;
            var previousDimension = axis == LongScreenshotAxis.Vertical
                ? _frames[^1].Height
                : _frames[^1].Width;
            var composedDimension = axis == LongScreenshotAxis.Vertical
                ? _current.Height
                : _current.Width;
            var referenceDimension = Math.Min(
                composedDimension,
                checked(Math.Max(previousDimension, previousDimension * 2)));
            var referenceStart = composedDimension - referenceDimension;
            var reference = LongScreenshotComposer.TakeTail(
                _current,
                _direction,
                referenceDimension);
            var match = _stitcher.Match(reference, frame, axis);
            if (match.IsDuplicate)
                return false;

            var previousOffset = _placements.Count == 0
                ? 0
                : _placements[^1].Offset;
            var currentDimension = axis == LongScreenshotAxis.Vertical ? frame.Height : frame.Width;
            var matchedOverlap = Math.Clamp(
                match.Overlap,
                0,
                Math.Min(referenceDimension, currentDimension) - 1);
            var offset = checked(referenceStart + referenceDimension - matchedOverlap);
            // A manually scrolled viewport must move forward. A bad local match
            // can otherwise move the composition backwards and duplicate content.
            if (_placements.Count > 0 && offset <= previousOffset)
                offset = previousOffset + 1;
            var fixedDimension = axis == LongScreenshotAxis.Vertical ? _frames[0].Width : _frames[0].Height;
            var maximumDimension = Math.Min(
                axis == LongScreenshotAxis.Vertical
                    ? LongScreenshotComposer.MaximumHeight
                    : LongScreenshotComposer.MaximumWidth,
                LongScreenshotComposer.MaximumImageBytes / checked(fixedDimension * 4));
            if (offset + currentDimension > maximumDimension)
                return false;
            match = match with { Offset = offset };
            _frames.Add(frame);
            _placements.Add(match);
            _current = _stitcher.Compose(_frames, _placements, axis);
            return true;
        }

        var overlap = LongScreenshotComposer.FindOverlap(_frames[^1], frame, _direction);
        _frames.Add(frame);
        _overlaps.Add(overlap);
        _current = LongScreenshotComposer.Compose(_frames, _direction, _overlaps);
        return true;
    }

    internal void RemoveLast()
    {
        if (_frames.Count <= 1)
            return;
        _frames.RemoveAt(_frames.Count - 1);
        if (_stitcher is not null)
        {
            _placements.RemoveAt(_placements.Count - 1);
            var axis = _direction == LongScreenshotDirection.Vertical
                ? LongScreenshotAxis.Vertical
                : LongScreenshotAxis.Horizontal;
            _current = _stitcher.Compose(_frames, _placements, axis);
        }
        else
        {
            _overlaps.RemoveAt(_overlaps.Count - 1);
            _current = LongScreenshotComposer.Compose(_frames, _direction, _overlaps);
        }
    }
}
