using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
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
    private readonly List<Segment> _segments = [];
    private readonly ImageFrame _first;
    private readonly LongScreenshotDirection _direction;
    private readonly ILongScreenshotStitcher? _stitcher;
    private readonly int _fixedDimension;
    private readonly double _dpiX;
    private readonly double _dpiY;
    private ImageFrame _last;
    private int _axisLength;
    private int _count = 1;
    private int _lastOffset;

    internal LongScreenshotAccumulator(
        ImageFrame first,
        LongScreenshotDirection direction,
        ILongScreenshotStitcher? stitcher = null)
    {
        _direction = direction;
        _stitcher = stitcher;
        _fixedDimension = direction == LongScreenshotDirection.Vertical ? first.Width : first.Height;
        _dpiX = first.DpiX;
        _dpiY = first.DpiY;
        _axisLength = direction == LongScreenshotDirection.Vertical ? first.Height : first.Width;
        AddInitialSegment(first);
        _first = CreateFrameFromSegment(_segments[0]);
        // The initial segment is already a compact, tightly-strided copy. Reuse
        // it for matching so the original capture can be collected immediately.
        _last = _first;
    }

    internal int Count => _count;
    internal ImageFrame First => _first;
    internal ImageFrame Last => _last;
    internal int Dimension => _axisLength;
    internal ImageFrame Current => Materialize();

    internal Bitmap CreateBitmap()
    {
        var width = _direction == LongScreenshotDirection.Vertical ? _fixedDimension : _axisLength;
        var height = _direction == LongScreenshotDirection.Vertical ? _axisLength : _fixedDimension;
        var rowBytes = checked(width * 4);
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(_dpiX, _dpiY),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        try
        {
            using var locked = bitmap.Lock();
            foreach (var segment in _segments)
            {
                if (_direction == LongScreenshotDirection.Vertical)
                {
                    if (locked.RowBytes == rowBytes)
                    {
                        Marshal.Copy(
                            segment.Pixels,
                            0,
                            locked.Address + segment.Start * locked.RowBytes,
                            checked(segment.Stride * segment.Length));
                    }
                    else
                    {
                        for (var row = 0; row < segment.Length; row++)
                            Marshal.Copy(
                                segment.Pixels,
                                row * segment.Stride,
                                locked.Address + (segment.Start + row) * locked.RowBytes,
                                rowBytes);
                    }
                }
            }

            if (_direction == LongScreenshotDirection.Horizontal)
            {
                var rowBuffer = ArrayPool<byte>.Shared.Rent(rowBytes);
                try
                {
                    for (var row = 0; row < height; row++)
                    {
                        var destination = rowBuffer.AsSpan(0, rowBytes);
                        destination.Clear();
                        foreach (var segment in _segments)
                            segment.Pixels.AsSpan(row * segment.Stride, segment.Stride)
                                .CopyTo(destination.Slice(segment.Start * 4, segment.Stride));
                        Marshal.Copy(
                            rowBuffer,
                            0,
                            locked.Address + row * locked.RowBytes,
                            rowBytes);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rowBuffer);
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal Bitmap CreatePreviewBitmap(int maximumWidth = 480, int maximumHeight = 560)
    {
        var width = _direction == LongScreenshotDirection.Vertical ? _fixedDimension : _axisLength;
        var height = _direction == LongScreenshotDirection.Vertical ? _axisLength : _fixedDimension;
        var scale = Math.Min(1d, Math.Min((double)maximumWidth / width, (double)maximumHeight / height));
        var previewWidth = Math.Max(1, (int)Math.Round(width * scale));
        var previewHeight = Math.Max(1, (int)Math.Round(height * scale));
        var stride = checked(previewWidth * 4);
        var pixelBytes = checked(stride * previewHeight);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            for (var y = 0; y < previewHeight; y++)
            {
                var sourceY = Math.Min(height - 1, (int)(y / scale));
                for (var x = 0; x < previewWidth; x++)
                {
                    var sourceX = Math.Min(width - 1, (int)(x / scale));
                    ReadPixel(sourceX, sourceY).CopyTo(pixels.AsSpan(y * stride + x * 4, 4));
                }
            }

            return LongScreenshotComposer.ToBitmap(
                new ImageFrame(
                    previewWidth,
                    previewHeight,
                    stride,
                    _dpiX,
                    _dpiY,
                    pixels.AsMemory(0, pixelBytes)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal bool Append(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (LongScreenshotComposer.IsSameViewport(_last, frame, _direction))
            return false;

        if (_stitcher is not null)
        {
            var axis = _direction == LongScreenshotDirection.Vertical
                ? LongScreenshotAxis.Vertical
                : LongScreenshotAxis.Horizontal;
            var previousDimension = axis == LongScreenshotAxis.Vertical ? _last.Height : _last.Width;
            var composedDimension = _axisLength;
            var referenceDimension = Math.Min(
                composedDimension,
                checked(Math.Max(previousDimension, previousDimension * 2)));
            var referenceStart = composedDimension - referenceDimension;
            var reference = GetTail(referenceDimension);
            var match = _stitcher.Match(reference, frame, axis);
            if (match.IsDuplicate)
                return false;

            var currentDimension = axis == LongScreenshotAxis.Vertical ? frame.Height : frame.Width;
            var matchedOverlap = Math.Clamp(
                match.Overlap,
                0,
                Math.Min(referenceDimension, currentDimension) - 1);
            var offset = checked(referenceStart + referenceDimension - matchedOverlap);
            // A manually scrolled viewport must move forward. A bad local match
            // can otherwise move the composition backwards and duplicate content.
            if (_count > 1 && offset <= _lastOffset)
                offset = _lastOffset + 1;
            var fixedDimension = axis == LongScreenshotAxis.Vertical ? _first.Width : _first.Height;
            var maximumDimension = Math.Min(
                axis == LongScreenshotAxis.Vertical
                    ? LongScreenshotComposer.MaximumHeight
                    : LongScreenshotComposer.MaximumWidth,
                LongScreenshotComposer.MaximumImageBytes / checked(fixedDimension * 4));
            if (offset + currentDimension > maximumDimension)
                return false;
            match = match with { Offset = offset };
            ApplyFrame(frame, offset, match.Overlap, match.SeamStart, match.SeamLength);
            _last = frame;
            _lastOffset = offset;
            _count++;
            return true;
        }

        var overlap = LongScreenshotComposer.FindOverlap(_last, frame, _direction);
        var frameStart = _axisLength - overlap;
        ApplyFrame(frame, frameStart, overlap, -1, 0);
        _last = frame;
        _count++;
        return true;
    }

    private void AddInitialSegment(ImageFrame frame)
    {
        if (_direction == LongScreenshotDirection.Vertical)
        {
            var stride = checked(frame.Width * 4);
            var pixels = new byte[checked(stride * frame.Height)];
            for (var row = 0; row < frame.Height; row++)
                frame.Pixels.Span.Slice(row * frame.Stride, stride)
                    .CopyTo(pixels.AsSpan(row * stride, stride));
            _segments.Add(new Segment(0, frame.Height, stride, pixels));
            return;
        }

        var compactStride = checked(frame.Width * 4);
        var compact = new byte[checked(compactStride * frame.Height)];
        for (var row = 0; row < frame.Height; row++)
            frame.Pixels.Span.Slice(row * frame.Stride, compactStride)
                .CopyTo(compact.AsSpan(row * compactStride, compactStride));
        _segments.Add(new Segment(0, frame.Width, compactStride, compact));
    }

    private void ApplyFrame(
        ImageFrame frame,
        int frameStart,
        int overlap,
        int seamStart,
        int seamLength)
    {
        var existingLength = _axisLength;
        var transitionStart = seamStart >= 0
            ? Math.Clamp(seamStart, 0, overlap)
            : overlap;
        var transitionLength = seamStart >= 0
            ? Math.Clamp(seamLength, 0, overlap - transitionStart)
            : 0;
        var transitionEnd = transitionStart + transitionLength;

        if (_direction == LongScreenshotDirection.Vertical)
        {
            for (var row = 0; row < frame.Height; row++)
            {
                var destination = frameStart + row;
                if (destination < 0 || destination >= existingLength)
                    continue;
                if (row < transitionStart)
                    continue;
                var source = frame.Pixels.Span.Slice(row * frame.Stride, frame.Width * 4);
                if (row < transitionEnd && transitionLength > 0)
                {
                    var weight = (row - transitionStart + 1d) / (transitionLength + 1d);
                    BlendRow(destination, source, weight);
                }
                else
                {
                    WriteRow(destination, source);
                }
            }

            var sourceStart = Math.Max(0, existingLength - frameStart);
            if (frameStart > existingLength)
            {
                AddZeroSegment(frameStart - existingLength);
                _axisLength = frameStart;
            }
            if (sourceStart < frame.Height)
                AddRows(frame, sourceStart, frame.Height - sourceStart);
            _axisLength = Math.Max(existingLength, checked(frameStart + frame.Height));
            return;
        }

        for (var column = 0; column < frame.Width; column++)
        {
            var destination = frameStart + column;
            if (destination < 0 || destination >= existingLength)
                continue;
            if (column < transitionStart)
                continue;
            if (column < transitionEnd && transitionLength > 0)
            {
                var weight = (column - transitionStart + 1d) / (transitionLength + 1d);
                BlendColumn(destination, frame, column, weight);
            }
            else
            {
                WriteColumn(destination, frame, column);
            }
        }

        var sourceColumn = Math.Max(0, existingLength - frameStart);
        if (frameStart > existingLength)
        {
            AddZeroSegment(frameStart - existingLength);
            _axisLength = frameStart;
        }
        if (sourceColumn < frame.Width)
            AddColumns(frame, sourceColumn, frame.Width - sourceColumn);
        _axisLength = Math.Max(existingLength, checked(frameStart + frame.Width));
    }

    private void AddRows(ImageFrame frame, int sourceRow, int rows)
    {
        var stride = checked(_fixedDimension * 4);
        var pixels = new byte[checked(stride * rows)];
        for (var row = 0; row < rows; row++)
            frame.Pixels.Span.Slice((sourceRow + row) * frame.Stride, stride)
                .CopyTo(pixels.AsSpan(row * stride, stride));
        _segments.Add(new Segment(_axisLength, rows, stride, pixels));
    }

    private void AddColumns(ImageFrame frame, int sourceColumn, int columns)
    {
        var stride = checked(columns * 4);
        var pixels = new byte[checked(stride * _fixedDimension)];
        for (var row = 0; row < _fixedDimension; row++)
            frame.Pixels.Span.Slice(row * frame.Stride + sourceColumn * 4, stride)
                .CopyTo(pixels.AsSpan(row * stride, stride));
        _segments.Add(new Segment(_axisLength, columns, stride, pixels));
    }

    private void AddZeroSegment(int length)
    {
        if (length <= 0)
            return;
        var stride = checked((_direction == LongScreenshotDirection.Vertical ? _fixedDimension : length) * 4);
        var rows = _direction == LongScreenshotDirection.Vertical ? length : _fixedDimension;
        _segments.Add(new Segment(_axisLength, length, stride, new byte[checked(stride * rows)]));
    }

    private void WriteRow(int destination, ReadOnlySpan<byte> source)
    {
        var segment = FindSegment(destination);
        var row = destination - segment.Start;
        source.CopyTo(segment.Pixels.AsSpan(row * segment.Stride, source.Length));
    }

    private void BlendRow(int destination, ReadOnlySpan<byte> source, double weight)
    {
        var segment = FindSegment(destination);
        var row = destination - segment.Start;
        var target = segment.Pixels.AsSpan(row * segment.Stride, source.Length);
        for (var index = 0; index < source.Length; index++)
            target[index] = (byte)Math.Round(target[index] * (1d - weight) + source[index] * weight);
    }

    private void WriteColumn(int destination, ImageFrame source, int sourceColumn)
    {
        var segment = FindSegment(destination);
        var column = destination - segment.Start;
        for (var row = 0; row < _fixedDimension; row++)
        {
            var sourceOffset = row * source.Stride + sourceColumn * 4;
            var targetOffset = row * segment.Stride + column * 4;
            source.Pixels.Span.Slice(sourceOffset, 4).CopyTo(segment.Pixels.AsSpan(targetOffset, 4));
        }
    }

    private void BlendColumn(int destination, ImageFrame source, int sourceColumn, double weight)
    {
        var segment = FindSegment(destination);
        var column = destination - segment.Start;
        for (var row = 0; row < _fixedDimension; row++)
        {
            var sourceOffset = row * source.Stride + sourceColumn * 4;
            var targetOffset = row * segment.Stride + column * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var sourceValue = source.Pixels.Span[sourceOffset + channel];
                var targetValue = segment.Pixels[targetOffset + channel];
                segment.Pixels[targetOffset + channel] =
                    (byte)Math.Round(targetValue * (1d - weight) + sourceValue * weight);
            }
        }
    }

    private Segment FindSegment(int coordinate)
    {
        var low = 0;
        var high = _segments.Count - 1;
        while (low <= high)
        {
            var index = low + ((high - low) >> 1);
            var segment = _segments[index];
            if (coordinate >= segment.Start && coordinate < segment.Start + segment.Length)
                return segment;
            if (coordinate < segment.Start)
                high = index - 1;
            else
                low = index + 1;
        }
        throw new InvalidOperationException("The composed screenshot segment is missing.");
    }

    private ImageFrame GetTail(int length)
    {
        length = Math.Clamp(length, 1, _axisLength);
        var start = _axisLength - length;
        var segment = FindSegment(start);
        if (start >= segment.Start && start + length <= segment.Start + segment.Length)
        {
            var local = start - segment.Start;
            if (_direction == LongScreenshotDirection.Vertical)
            {
                var stride = segment.Stride;
                return new ImageFrame(
                    _fixedDimension,
                    length,
                    stride,
                    _dpiX,
                    _dpiY,
                    segment.Pixels.AsMemory(local * stride));
            }

            if (local == 0 && length == segment.Length)
            {
                return new ImageFrame(
                    length,
                    _fixedDimension,
                    segment.Stride,
                    _dpiX,
                    _dpiY,
                    segment.Pixels);
            }
        }

        var strideBytes = checked((_direction == LongScreenshotDirection.Vertical ? _fixedDimension : length) * 4);
        var rows = _direction == LongScreenshotDirection.Vertical ? length : _fixedDimension;
        var pixels = new byte[checked(strideBytes * rows)];
        if (_direction == LongScreenshotDirection.Vertical)
        {
            for (var row = 0; row < length; row++)
                ReadRow(start + row).CopyTo(pixels.AsSpan(row * strideBytes, strideBytes));
        }
        else
        {
            for (var row = 0; row < _fixedDimension; row++)
                for (var column = 0; column < length; column++)
                    ReadPixel(start + column, row).CopyTo(pixels.AsSpan(row * strideBytes + column * 4, 4));
        }
        return new ImageFrame(
            _direction == LongScreenshotDirection.Vertical ? _fixedDimension : length,
            _direction == LongScreenshotDirection.Vertical ? length : _fixedDimension,
            strideBytes,
            _dpiX,
            _dpiY,
            pixels);
    }

    private ReadOnlySpan<byte> ReadRow(int coordinate)
    {
        var segment = FindSegment(coordinate);
        var row = coordinate - segment.Start;
        return segment.Pixels.AsSpan(row * segment.Stride, segment.Stride);
    }

    private ReadOnlySpan<byte> ReadPixel(int x, int y)
    {
        var coordinate = _direction == LongScreenshotDirection.Vertical ? y : x;
        var segment = FindSegment(coordinate);
        var local = coordinate - segment.Start;
        var offset = _direction == LongScreenshotDirection.Vertical
            ? local * segment.Stride + x * 4
            : y * segment.Stride + local * 4;
        return segment.Pixels.AsSpan(offset, 4);
    }

    private ImageFrame Materialize()
    {
        var width = _direction == LongScreenshotDirection.Vertical ? _fixedDimension : _axisLength;
        var height = _direction == LongScreenshotDirection.Vertical ? _axisLength : _fixedDimension;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        foreach (var segment in _segments)
        {
            if (_direction == LongScreenshotDirection.Vertical)
            {
                for (var row = 0; row < segment.Length; row++)
                    segment.Pixels.AsSpan(row * segment.Stride, stride)
                        .CopyTo(pixels.AsSpan((segment.Start + row) * stride, stride));
            }
            else
            {
                for (var row = 0; row < height; row++)
                    segment.Pixels.AsSpan(row * segment.Stride, segment.Stride)
                        .CopyTo(pixels.AsSpan(row * stride + segment.Start * 4, segment.Stride));
            }
        }
        return new ImageFrame(width, height, stride, _dpiX, _dpiY, pixels);
    }

    private ImageFrame CreateFrameFromSegment(Segment segment) =>
        _direction == LongScreenshotDirection.Vertical
            ? new ImageFrame(_fixedDimension, segment.Length, segment.Stride, _dpiX, _dpiY, segment.Pixels)
            : new ImageFrame(segment.Length, _fixedDimension, segment.Stride, _dpiX, _dpiY, segment.Pixels);

    private sealed record Segment(int Start, int Length, int Stride, byte[] Pixels);

}
