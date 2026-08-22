using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Capture;

/// <summary>
/// Platform-neutral scrolling screenshot matcher. Matching is performed on
/// sampled pixels, while composition always keeps the original BGRA pixels.
/// </summary>
public sealed class ManagedLongScreenshotStitcher : ILongScreenshotStitcher
{
    private const int BandCount = 8;
    private const double MaximumDifference = 48d;

    public LongScreenshotPlacement Match(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.PixelFormat != ImagePixelFormat.Bgra32 ||
            current.PixelFormat != ImagePixelFormat.Bgra32)
        {
            throw new NotSupportedException("Long screenshot stitching requires BGRA32 frames.");
        }

        var fixedDimension = axis == LongScreenshotAxis.Vertical
            ? previous.Width
            : previous.Height;
        var currentFixedDimension = axis == LongScreenshotAxis.Vertical
            ? current.Width
            : current.Height;
        if (fixedDimension != currentFixedDimension)
            throw new ArgumentException("Frames must have the same perpendicular dimension.");

        var previousDimension = GetDimension(previous, axis);
        var currentDimension = GetDimension(current, axis);
        var maximum = Math.Min(previousDimension, currentDimension) - 1;
        if (maximum < 8)
            return new LongScreenshotPlacement(0, 0);

        var preferred = Math.Clamp((previousDimension * 3) / 4, 1, maximum);
        var coarseStep = Math.Max(2, maximum / 96);
        var bestOverlap = 0;
        var bestScore = double.MaxValue;
        var residuePasses = maximum <= 512 ? coarseStep : 2;
        for (var pass = 0; pass < residuePasses; pass++)
        {
            var residue = 1 + (pass * (maximum <= 512 ? 1 : Math.Max(1, coarseStep / 2)));
            if (residue > coarseStep)
                break;
            for (var overlap = residue; overlap <= maximum; overlap += coarseStep)
                Consider(overlap, ref bestOverlap, ref bestScore, previous, current, preferred, axis);
        }

        var refineStart = Math.Max(1, bestOverlap - coarseStep);
        var refineEnd = Math.Min(maximum, bestOverlap + coarseStep);
        for (var overlap = refineStart; overlap <= refineEnd; overlap++)
            Consider(overlap, ref bestOverlap, ref bestScore, previous, current, preferred, axis);

        if (bestScore > MaximumDifference)
            return new LongScreenshotPlacement(0, Math.Clamp(1d - bestScore / 128d, 0, 1));

        var confidence = Math.Clamp(1d - bestScore / MaximumDifference, 0, 1);
        var duplicate = bestOverlap >= Math.Min(previousDimension, currentDimension) - 2
                        && bestScore <= 4d;
        if (duplicate)
            return new LongScreenshotPlacement(bestOverlap, confidence, IsDuplicate: true);

        var (seamStart, seamLength) = FindSeam(previous, current, axis, bestOverlap);
        return new LongScreenshotPlacement(
            bestOverlap,
            confidence,
            SeamStart: seamStart,
            SeamLength: seamLength);
    }

    public ImageFrame Compose(
        IReadOnlyList<ImageFrame> frames,
        IReadOnlyList<LongScreenshotPlacement> placements,
        LongScreenshotAxis axis)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(placements);
        if (frames.Count == 0)
            throw new ArgumentException("At least one capture is required.", nameof(frames));

        var first = frames[0];
        var fixedDimension = axis == LongScreenshotAxis.Vertical ? first.Width : first.Height;
        if (frames.Any(frame => frame.PixelFormat != ImagePixelFormat.Bgra32 ||
                                (axis == LongScreenshotAxis.Vertical
                                    ? frame.Width != fixedDimension
                                    : frame.Height != fixedDimension)))
        {
            throw new ArgumentException("Frames must have the same perpendicular dimension.", nameof(frames));
        }

        var outputDimension = GetDimension(first, axis);
        for (var index = 1; index < frames.Count; index++)
        {
            var placement = index - 1 < placements.Count
                ? placements[index - 1]
                : Match(frames[index - 1], frames[index], axis);
            if (placement.IsDuplicate)
                break;
            var frameStart = placement.Offset > 0
                ? placement.Offset
                : outputDimension - Math.Clamp(placement.Overlap, 0, GetDimension(frames[index], axis) - 1);
            outputDimension = checked(Math.Max(
                outputDimension,
                frameStart + GetDimension(frames[index], axis)));
        }

        return axis == LongScreenshotAxis.Vertical
            ? ComposeVertical(frames, placements, outputDimension)
            : ComposeHorizontal(frames, placements, outputDimension);
    }

    private static void Consider(
        int overlap,
        ref int bestOverlap,
        ref double bestScore,
        ImageFrame previous,
        ImageFrame current,
        int preferred,
        LongScreenshotAxis axis)
    {
        var score = Difference(previous, current, overlap, axis) +
                    Math.Abs(overlap - preferred) * 0.04d;
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
        LongScreenshotAxis axis)
    {
        var dimension = GetDimension(previous, axis);
        var perpendicular = axis == LongScreenshotAxis.Vertical ? previous.Width : previous.Height;
        var dimensionStep = Math.Max(1, overlap / 48);
        var perpendicularStep = Math.Max(1, perpendicular / 128);
        var bands = new double[BandCount];
        var samples = new int[BandCount];
        var previousPixels = previous.Pixels.Span;
        var currentPixels = current.Pixels.Span;

        for (var axisOffset = 0; axisOffset < overlap; axisOffset += dimensionStep)
        {
            var sourceOffset = dimension - overlap + axisOffset;
            for (var perpendicularOffset = 0;
                 perpendicularOffset < perpendicular;
                 perpendicularOffset += perpendicularStep)
            {
                var band = Math.Min(
                    BandCount - 1,
                    perpendicularOffset * BandCount / Math.Max(1, perpendicular));
                var previousOffset = PixelOffset(previous, sourceOffset, perpendicularOffset, axis);
                var currentOffset = PixelOffset(current, axisOffset, perpendicularOffset, axis);
                var colour = Math.Abs(previousPixels[previousOffset] - currentPixels[currentOffset]) +
                             Math.Abs(previousPixels[previousOffset + 1] - currentPixels[currentOffset + 1]) +
                             Math.Abs(previousPixels[previousOffset + 2] - currentPixels[currentOffset + 2]);
                bands[band] += colour / 3d;
                samples[band]++;
            }
        }

        var score = new double[BandCount];
        var count = 0;
        for (var index = 0; index < BandCount; index++)
        {
            if (samples[index] == 0)
                continue;
            score[count++] = bands[index] / samples[index];
        }
        if (count == 0)
            return double.MaxValue;
        Array.Sort(score, 0, count);
        var first = count >= 5 ? 1 : 0;
        var last = count >= 5 ? count - 1 : count;
        var total = 0d;
        for (var index = first; index < last; index++)
            total += score[index];
        return total / Math.Max(1, last - first);
    }

    private static (int Start, int Length) FindSeam(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis,
        int overlap)
    {
        if (overlap < 4)
            return (-1, 0);

        var dimension = GetDimension(previous, axis);
        var perpendicular = axis == LongScreenshotAxis.Vertical ? previous.Width : previous.Height;
        var perpendicularStep = Math.Max(1, perpendicular / 256);
        var previousPixels = previous.Pixels.Span;
        var currentPixels = current.Pixels.Span;
        var bestSeam = overlap / 2;
        var bestScore = double.MaxValue;
        for (var seam = 1; seam < overlap; seam++)
        {
            var bandTotals = new double[BandCount];
            var bandSamples = new int[BandCount];
            for (var perpendicularOffset = 0;
                 perpendicularOffset < perpendicular;
                 perpendicularOffset += perpendicularStep)
            {
                var band = Math.Min(
                    BandCount - 1,
                    perpendicularOffset * BandCount / Math.Max(1, perpendicular));
                var previousOffset = PixelOffset(
                    previous,
                    dimension - overlap + seam,
                    perpendicularOffset,
                    axis);
                var currentOffset = PixelOffset(current, seam, perpendicularOffset, axis);
                bandTotals[band] += (Math.Abs(previousPixels[previousOffset] - currentPixels[currentOffset]) +
                                     Math.Abs(previousPixels[previousOffset + 1] - currentPixels[currentOffset + 1]) +
                                     Math.Abs(previousPixels[previousOffset + 2] - currentPixels[currentOffset + 2])) / 3d;
                bandSamples[band]++;
            }

            var scores = new double[BandCount];
            var count = 0;
            for (var band = 0; band < BandCount; band++)
            {
                if (bandSamples[band] > 0)
                    scores[count++] = bandTotals[band] / bandSamples[band];
            }
            if (count == 0)
                continue;
            Array.Sort(scores, 0, count);
            var first = count >= 5 ? 1 : 0;
            var last = count >= 5 ? count - 1 : count;
            var score = 0d;
            for (var index = first; index < last; index++)
                score += scores[index];
            score /= Math.Max(1, last - first);
            // Prefer the middle of a uniformly matching overlap. This keeps a
            // dynamic edge from forcing a seam directly against a boundary.
            score += Math.Abs(seam - overlap / 2d) * 0.02d;
            if (score < bestScore)
            {
                bestScore = score;
                bestSeam = seam;
            }
        }

        var feather = Math.Min(6, Math.Min(bestSeam, overlap - bestSeam));
        return feather <= 0 ? (-1, 0) : (bestSeam - feather, feather * 2);
    }

    private static ImageFrame ComposeVertical(
        IReadOnlyList<ImageFrame> frames,
        IReadOnlyList<LongScreenshotPlacement> placements,
        int outputHeight)
    {
        var first = frames[0];
        var stride = checked(first.Width * 4);
        var pixels = new byte[checked(stride * outputHeight)];
        CopyRows(first, pixels, stride, 0, 0, first.Height);
        var outputY = first.Height;
        for (var index = 1; index < frames.Count && outputY < outputHeight; index++)
        {
            var placement = index - 1 < placements.Count
                ? placements[index - 1]
                : new LongScreenshotPlacement(0, 0);
            if (placement.IsDuplicate)
                break;
            var overlap = Math.Clamp(placement.Overlap, 0, frames[index].Height - 1);
            var frameStart = placement.Offset > 0 ? placement.Offset : outputY - overlap;
            if (frameStart < 0 || frameStart >= outputHeight)
                break;
            CompositeRows(
                frames[index],
                pixels,
                stride,
                frameStart,
                outputHeight,
                outputY,
                overlap,
                placement.SeamStart,
                placement.SeamLength);
            outputY = Math.Max(outputY, Math.Min(outputHeight, frameStart + frames[index].Height));
        }
        if (outputY != outputHeight)
            Array.Resize(ref pixels, checked(stride * outputY));
        return new ImageFrame(first.Width, outputY, stride, first.DpiX, first.DpiY, pixels);
    }

    private static ImageFrame ComposeHorizontal(
        IReadOnlyList<ImageFrame> frames,
        IReadOnlyList<LongScreenshotPlacement> placements,
        int outputWidth)
    {
        var first = frames[0];
        var stride = checked(outputWidth * 4);
        var pixels = new byte[checked(stride * first.Height)];
        CopyColumns(first, pixels, stride, 0, 0, first.Width);
        var outputX = first.Width;
        for (var index = 1; index < frames.Count && outputX < outputWidth; index++)
        {
            var placement = index - 1 < placements.Count
                ? placements[index - 1]
                : new LongScreenshotPlacement(0, 0);
            if (placement.IsDuplicate)
                break;
            var overlap = Math.Clamp(placement.Overlap, 0, frames[index].Width - 1);
            var frameStart = placement.Offset > 0 ? placement.Offset : outputX - overlap;
            if (frameStart < 0 || frameStart >= outputWidth)
                break;
            CompositeColumns(
                frames[index],
                pixels,
                stride,
                frameStart,
                outputWidth,
                outputX,
                overlap,
                placement.SeamStart,
                placement.SeamLength);
            outputX = Math.Max(outputX, Math.Min(outputWidth, frameStart + frames[index].Width));
        }
        if (outputX != outputWidth)
        {
            var compactStride = checked(outputX * 4);
            var compact = new byte[checked(compactStride * first.Height)];
            for (var row = 0; row < first.Height; row++)
                pixels.AsSpan(row * stride, compactStride)
                    .CopyTo(compact.AsSpan(row * compactStride, compactStride));
            pixels = compact;
            stride = compactStride;
        }
        return new ImageFrame(outputX, first.Height, stride, first.DpiX, first.DpiY, pixels);
    }

    private static void CompositeRows(
        ImageFrame source,
        byte[] destination,
        int destinationStride,
        int frameStart,
        int outputHeight,
        int existingHeight,
        int overlap,
        int seamStart,
        int seamLength)
    {
        var rowBytes = checked(source.Width * 4);
        var transitionStart = seamStart >= 0
            ? Math.Clamp(seamStart, 0, overlap)
            : overlap;
        var transitionLength = seamStart >= 0
            ? Math.Clamp(seamLength, 0, overlap - transitionStart)
            : 0;
        var transitionEnd = transitionStart + transitionLength;
        for (var row = 0; row < source.Height; row++)
        {
            var destinationY = frameStart + row;
            if (destinationY < 0 || destinationY >= outputHeight)
                break;
            var sourceSpan = source.Pixels.Span.Slice(row * source.Stride, rowBytes);
            if (row < transitionStart && destinationY < existingHeight)
                continue;
            var destinationSpan = destination.AsSpan(destinationY * destinationStride, rowBytes);
            if (row < transitionEnd && destinationY < existingHeight && transitionLength > 0)
            {
                var weight = (row - transitionStart + 1d) / (transitionLength + 1d);
                for (var offset = 0; offset < rowBytes; offset++)
                    destinationSpan[offset] = (byte)Math.Round(
                        destinationSpan[offset] * (1d - weight) + sourceSpan[offset] * weight);
                continue;
            }
            sourceSpan.CopyTo(destinationSpan);
        }
    }

    private static void CompositeColumns(
        ImageFrame source,
        byte[] destination,
        int destinationStride,
        int frameStart,
        int outputWidth,
        int existingWidth,
        int overlap,
        int seamStart,
        int seamLength)
    {
        var transitionStart = seamStart >= 0
            ? Math.Clamp(seamStart, 0, overlap)
            : overlap;
        var transitionLength = seamStart >= 0
            ? Math.Clamp(seamLength, 0, overlap - transitionStart)
            : 0;
        var transitionEnd = transitionStart + transitionLength;
        for (var row = 0; row < source.Height; row++)
        {
            for (var column = 0; column < source.Width; column++)
            {
                var destinationX = frameStart + column;
                if (destinationX < 0 || destinationX >= outputWidth)
                    break;
                if (column < transitionStart && destinationX < existingWidth)
                    continue;
                var sourceOffset = row * source.Stride + column * 4;
                var destinationOffset = row * destinationStride + destinationX * 4;
                if (column < transitionEnd && destinationX < existingWidth && transitionLength > 0)
                {
                    var weight = (column - transitionStart + 1d) / (transitionLength + 1d);
                    for (var channel = 0; channel < 4; channel++)
                        destination[destinationOffset + channel] = (byte)Math.Round(
                            destination[destinationOffset + channel] * (1d - weight) +
                            source.Pixels.Span[sourceOffset + channel] * weight);
                    continue;
                }
                for (var channel = 0; channel < 4; channel++)
                    destination[destinationOffset + channel] = source.Pixels.Span[sourceOffset + channel];
            }
        }
    }

    private static int PixelOffset(
        ImageFrame frame,
        int axisOffset,
        int perpendicularOffset,
        LongScreenshotAxis axis) => axis == LongScreenshotAxis.Vertical
        ? axisOffset * frame.Stride + perpendicularOffset * 4
        : perpendicularOffset * frame.Stride + axisOffset * 4;

    private static int GetDimension(ImageFrame frame, LongScreenshotAxis axis) =>
        axis == LongScreenshotAxis.Vertical ? frame.Height : frame.Width;

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
