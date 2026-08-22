using System.Runtime.InteropServices;
using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;
using OpenCvSharp;

namespace EasyChat.Infrastructure.Windows.Capture;

/// <summary>
/// Windows matcher backed by OpenCV's normalized template matching. The
/// managed matcher supplies a conservative fallback when a frame has too
/// little texture for a reliable native match.
/// </summary>
public sealed class OpenCvLongScreenshotStitcher : ILongScreenshotStitcher
{
    public LongScreenshotPlacement Match(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis)
    {
        try
        {
            var scale = GetScale(previous, axis);
            using var previousGray = ToGray(previous, scale);
            using var currentGray = ToGray(current, scale);
            var previousDimension = GetDimension(previousGray, axis);
            var currentDimension = GetDimension(currentGray, axis);
            var (estimatedOverlap, estimateScore) = EstimateOverlap(previousGray, currentGray, axis);
            if (estimatedOverlap < 8 || estimateScore > 48d)
                return new LongScreenshotPlacement(0, Math.Clamp(1d - estimateScore / 128d, 0, 1));
            if (estimatedOverlap >= Math.Min(previousDimension, currentDimension) - 2 && estimateScore <= 4d)
            {
                var duplicateOverlap = ScaleOverlap(estimatedOverlap, scale, previous, current, axis);
                return new LongScreenshotPlacement(duplicateOverlap, 1d, IsDuplicate: true);
            }

            var expected = Math.Clamp(estimatedOverlap, 8, Math.Min(previousDimension, currentDimension) - 1);
            var templateDimension = Math.Clamp(expected, 8, Math.Min(previousDimension, currentDimension));
            var searchStart = Math.Max(0, previousDimension - Math.Min(previousDimension, templateDimension * 2));
            var searchLength = previousDimension - searchStart;
            if (searchLength < templateDimension)
                return CreatePlacement(estimatedOverlap, estimateScore, scale, previous, current, axis);

            using var search = axis == LongScreenshotAxis.Vertical
                ? new Mat(previousGray, new Rect(0, searchStart, previousGray.Width, searchLength))
                : new Mat(previousGray, new Rect(searchStart, 0, searchLength, previousGray.Height));
            using var template = axis == LongScreenshotAxis.Vertical
                ? new Mat(currentGray, new Rect(0, 0, currentGray.Width, templateDimension))
                : new Mat(currentGray, new Rect(0, 0, templateDimension, currentGray.Height));
            using var result = new Mat();
            Cv2.MatchTemplate(search, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var max, out _, out var maxLocation);
            if (max < 0.55)
                return CreatePlacement(estimatedOverlap, estimateScore, scale, previous, current, axis);

            var matchOffset = axis == LongScreenshotAxis.Vertical
                ? searchStart + maxLocation.Y
                : searchStart + maxLocation.X;
            var matchedScaledOverlap = GetDimension(previousGray, axis) - matchOffset;
            var matchedScore = Difference(previousGray, currentGray, axis, matchedScaledOverlap);
            // Ccoeff is intentionally tolerant of brightness changes, but that
            // makes a low-detail gradient look equally good at several shifts.
            // Retain the globally scored placement unless the template result
            // also agrees with the robust eight-band pixel comparison.
            if (matchedScore > estimateScore + 2d)
                return CreatePlacement(estimatedOverlap, estimateScore, scale, previous, current, axis);

            var overlap = (int)Math.Round(GetDimension(previous, axis) - matchOffset / scale);
            overlap = Math.Clamp(overlap, 1, Math.Min(GetDimension(previous, axis), GetDimension(current, axis)) - 1);
            var confidence = Math.Clamp(((1d - matchedScore / 48d) + max) / 2d, 0, 1);
            return CreatePlacement(overlap, confidence, previous, current, axis);
        }
        catch (OpenCVException)
        {
            return EstimateManaged(previous, current, axis);
        }
        catch (ArgumentException)
        {
            return EstimateManaged(previous, current, axis);
        }
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
        if (frames.Any(frame => frame.PixelFormat != ImagePixelFormat.Bgra32 ||
                                (axis == LongScreenshotAxis.Vertical
                                    ? frame.Width != first.Width
                                    : frame.Height != first.Height)))
        {
            throw new ArgumentException("Frames must use BGRA32 and share their perpendicular dimension.", nameof(frames));
        }

        var outputDimension = GetDimension(first, axis);
        for (var index = 1; index < frames.Count; index++)
        {
            var placement = GetPlacement(frames, placements, index, axis);
            if (placement.IsDuplicate)
                break;
            var overlap = Math.Clamp(placement.Overlap, 0, GetDimension(frames[index], axis) - 1);
            var start = placement.Offset > 0 ? placement.Offset : outputDimension - overlap;
            outputDimension = Math.Max(outputDimension, checked(start + GetDimension(frames[index], axis)));
        }

        return axis == LongScreenshotAxis.Vertical
            ? ComposeVertical(frames, placements, outputDimension)
            : ComposeHorizontal(frames, placements, outputDimension);
    }

    private static LongScreenshotPlacement CreatePlacement(
        int scaledOverlap,
        double score,
        double scale,
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis) => CreatePlacement(
        ScaleOverlap(scaledOverlap, scale, previous, current, axis),
        Math.Clamp(1d - score / 48d, 0, 1),
        previous,
        current,
        axis);

    private static LongScreenshotPlacement CreatePlacement(
        int overlap,
        double confidence,
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis)
    {
        var (seamStart, seamLength) = FindSeam(previous, current, axis, overlap);
        return new LongScreenshotPlacement(
            overlap,
            confidence,
            SeamStart: seamStart,
            SeamLength: seamLength);
    }

    private static int ScaleOverlap(
        int scaledOverlap,
        double scale,
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis)
    {
        var maximum = Math.Min(GetDimension(previous, axis), GetDimension(current, axis)) - 1;
        return Math.Clamp((int)Math.Round(scaledOverlap / scale), 0, Math.Max(0, maximum));
    }

    private static LongScreenshotPlacement EstimateManaged(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis)
    {
        var maximum = Math.Min(GetDimension(previous, axis), GetDimension(current, axis)) - 1;
        if (maximum < 8)
            return new LongScreenshotPlacement(0, 0);
        var preferred = Math.Clamp(GetDimension(previous, axis) * 3 / 4, 1, maximum);
        var coarseStep = Math.Max(2, maximum / 96);
        var bestOverlap = 0;
        var bestScore = double.MaxValue;
        for (var overlap = 1; overlap <= maximum; overlap += coarseStep)
            ConsiderManaged(previous, current, axis, overlap, preferred, ref bestOverlap, ref bestScore);
        for (var overlap = Math.Max(1, bestOverlap - coarseStep);
             overlap <= Math.Min(maximum, bestOverlap + coarseStep);
             overlap++)
        {
            ConsiderManaged(previous, current, axis, overlap, preferred, ref bestOverlap, ref bestScore);
        }
        if (bestScore > 48d)
            return new LongScreenshotPlacement(0, Math.Clamp(1d - bestScore / 128d, 0, 1));
        if (bestOverlap >= maximum - 1 && bestScore <= 4d)
            return new LongScreenshotPlacement(bestOverlap, 1d, IsDuplicate: true);
        return CreatePlacement(
            bestOverlap,
            Math.Clamp(1d - bestScore / 48d, 0, 1),
            previous,
            current,
            axis);
    }

    private static (int Overlap, double Score) EstimateOverlap(
        Mat previous,
        Mat current,
        LongScreenshotAxis axis)
    {
        var maximum = Math.Min(GetDimension(previous, axis), GetDimension(current, axis)) - 1;
        if (maximum < 8)
            return (0, double.MaxValue);
        var preferred = Math.Clamp(GetDimension(previous, axis) * 3 / 4, 1, maximum);
        var step = Math.Max(2, maximum / 96);
        var bestOverlap = 0;
        var bestScore = double.MaxValue;
        var (templateOverlap, templateResponse) = EstimateTemplateOverlap(previous, current, axis);
        if (templateResponse >= 0.55d && templateOverlap > 0 && templateOverlap <= maximum)
            ConsiderOpenCv(previous, current, axis, templateOverlap, preferred, ref bestOverlap, ref bestScore);
        var (phaseOverlap, phaseResponse) = EstimatePhaseOverlap(previous, current, axis);
        if (phaseResponse >= 0.1d && phaseOverlap > 0 && phaseOverlap <= maximum)
            ConsiderOpenCv(previous, current, axis, phaseOverlap, preferred, ref bestOverlap, ref bestScore);
        // Cover every residue for small viewports. For larger images two
        // staggered passes avoid the expensive all-residue scan; the template
        // candidate above provides the fine registration in that case. A
        // single stepped pass can otherwise miss a narrow exact minimum (for
        // example overlap 70 with step 2 starting at 1).
        var residuePasses = maximum <= 512 ? step : 2;
        for (var pass = 0; pass < residuePasses; pass++)
        {
            var residue = 1 + (pass * (maximum <= 512 ? 1 : Math.Max(1, step / 2)));
            if (residue > step)
                break;
            for (var overlap = residue; overlap <= maximum; overlap += step)
                ConsiderOpenCv(previous, current, axis, overlap, preferred, ref bestOverlap, ref bestScore);
        }
        for (var overlap = Math.Max(1, bestOverlap - step);
             overlap <= Math.Min(maximum, bestOverlap + step);
             overlap++)
        {
            ConsiderOpenCv(previous, current, axis, overlap, preferred, ref bestOverlap, ref bestScore);
        }
        return (bestOverlap, bestScore);
    }

    private static (int Overlap, double Response) EstimateTemplateOverlap(
        Mat previous,
        Mat current,
        LongScreenshotAxis axis)
    {
        var previousDimension = GetDimension(previous, axis);
        var currentDimension = GetDimension(current, axis);
        var maximum = Math.Min(previousDimension, currentDimension) - 1;
        if (maximum < 8)
            return (0, 0);

        var candidates = new[]
        {
            Math.Min(maximum, 16),
            Math.Min(maximum, 32),
            Math.Min(maximum, 64),
            Math.Min(maximum, Math.Max(8, currentDimension / 2))
        }.Where(value => value >= 8).Distinct().ToArray();
        var searchStart = Math.Max(0, previousDimension - Math.Min(previousDimension, currentDimension * 2));
        var searchLength = previousDimension - searchStart;
        var bestOverlap = 0;
        var bestResponse = 0d;

        foreach (var templateDimension in candidates)
        {
            if (searchLength < templateDimension)
                continue;
            using var search = axis == LongScreenshotAxis.Vertical
                ? new Mat(previous, new Rect(0, searchStart, previous.Width, searchLength))
                : new Mat(previous, new Rect(searchStart, 0, searchLength, previous.Height));
            using var template = axis == LongScreenshotAxis.Vertical
                ? new Mat(current, new Rect(0, 0, current.Width, templateDimension))
                : new Mat(current, new Rect(0, 0, templateDimension, current.Height));
            using var result = new Mat();
            Cv2.MatchTemplate(search, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var response, out _, out var location);
            if (response <= bestResponse)
                continue;
            var matchOffset = axis == LongScreenshotAxis.Vertical
                ? searchStart + location.Y
                : searchStart + location.X;
            var overlap = previousDimension - matchOffset;
            if (overlap > 0 && overlap <= maximum)
            {
                bestOverlap = overlap;
                bestResponse = response;
            }
        }

        return (bestOverlap, bestResponse);
    }

    private static (int Overlap, double Response) EstimatePhaseOverlap(
        Mat previous,
        Mat current,
        LongScreenshotAxis axis)
    {
        if (previous.Size() != current.Size())
            return (0, 0);
        try
        {
            using var previousFloat = new Mat();
            using var currentFloat = new Mat();
            previous.ConvertTo(previousFloat, MatType.CV_32FC1);
            current.ConvertTo(currentFloat, MatType.CV_32FC1);
            using var window = new Mat();
            Cv2.CreateHanningWindow(window, previous.Size(), MatType.CV_32FC1);
            var shift = Cv2.PhaseCorrelate(previousFloat, currentFloat, window, out var response);
            var movement = Math.Abs(axis == LongScreenshotAxis.Vertical ? shift.Y : shift.X);
            var dimension = GetDimension(previous, axis);
            if (movement < 1d || movement >= dimension)
                return (0, response);
            return ((int)Math.Round(dimension - movement), response);
        }
        catch (OpenCVException)
        {
            return (0, 0);
        }
    }

    private static void ConsiderOpenCv(
        Mat previous,
        Mat current,
        LongScreenshotAxis axis,
        int overlap,
        int preferred,
        ref int bestOverlap,
        ref double bestScore)
    {
        var score = Difference(previous, current, axis, overlap) + Math.Abs(overlap - preferred) * 0.04d;
        if (score < bestScore)
        {
            bestScore = score;
            bestOverlap = overlap;
        }
    }

    private static double Difference(Mat previous, Mat current, LongScreenshotAxis axis, int overlap)
    {
        const int bands = 8;
        var scores = new double[bands];
        var perpendicular = axis == LongScreenshotAxis.Vertical ? previous.Width : previous.Height;
        for (var band = 0; band < bands; band++)
        {
            var first = band * perpendicular / bands;
            var last = (band + 1) * perpendicular / bands;
            var width = axis == LongScreenshotAxis.Vertical ? last - first : overlap;
            var height = axis == LongScreenshotAxis.Vertical ? overlap : last - first;
            using var previousPart = axis == LongScreenshotAxis.Vertical
                ? new Mat(previous, new Rect(first, previous.Height - overlap, width, height))
                : new Mat(previous, new Rect(previous.Width - overlap, first, width, height));
            using var currentPart = axis == LongScreenshotAxis.Vertical
                ? new Mat(current, new Rect(first, 0, width, height))
                : new Mat(current, new Rect(0, first, width, height));
            using var difference = new Mat();
            Cv2.Absdiff(previousPart, currentPart, difference);
            scores[band] = Cv2.Mean(difference).Val0;
        }
        Array.Sort(scores);
        var start = bands > 4 ? 1 : 0;
        var end = bands > 4 ? bands - 1 : bands;
        return scores[start..end].Average();
    }

    private static void ConsiderManaged(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis,
        int overlap,
        int preferred,
        ref int bestOverlap,
        ref double bestScore)
    {
        var score = Difference(previous, current, axis, overlap) + Math.Abs(overlap - preferred) * 0.04d;
        if (score < bestScore)
        {
            bestScore = score;
            bestOverlap = overlap;
        }
    }

    private static double Difference(ImageFrame previous, ImageFrame current, LongScreenshotAxis axis, int overlap)
    {
        const int bands = 8;
        var totals = new double[bands];
        var samples = new int[bands];
        var perpendicular = axis == LongScreenshotAxis.Vertical ? previous.Width : previous.Height;
        var axisStep = Math.Max(1, overlap / 48);
        var perpendicularStep = Math.Max(1, perpendicular / 128);
        var previousPixels = previous.Pixels.Span;
        var currentPixels = current.Pixels.Span;
        for (var offset = 0; offset < overlap; offset += axisStep)
        {
            for (var perpendicularOffset = 0; perpendicularOffset < perpendicular; perpendicularOffset += perpendicularStep)
            {
                var band = Math.Min(bands - 1, perpendicularOffset * bands / Math.Max(1, perpendicular));
                var previousOffset = PixelOffset(previous, GetDimension(previous, axis) - overlap + offset, perpendicularOffset, axis);
                var currentOffset = PixelOffset(current, offset, perpendicularOffset, axis);
                totals[band] += (Math.Abs(previousPixels[previousOffset] - currentPixels[currentOffset]) +
                                 Math.Abs(previousPixels[previousOffset + 1] - currentPixels[currentOffset + 1]) +
                                 Math.Abs(previousPixels[previousOffset + 2] - currentPixels[currentOffset + 2])) / 3d;
                samples[band]++;
            }
        }
        var scores = new List<double>(bands);
        for (var band = 0; band < bands; band++)
            if (samples[band] > 0)
                scores.Add(totals[band] / samples[band]);
        if (scores.Count == 0)
            return double.MaxValue;
        scores.Sort();
        var start = scores.Count >= 5 ? 1 : 0;
        var end = scores.Count >= 5 ? scores.Count - 1 : scores.Count;
        return scores.Skip(start).Take(end - start).Average();
    }

    private static (int Start, int Length) FindSeam(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis,
        int overlap)
    {
        if (overlap < 4)
            return (-1, 0);

        const int bands = 8;
        var dimension = GetDimension(previous, axis);
        var perpendicular = axis == LongScreenshotAxis.Vertical ? previous.Width : previous.Height;
        var perpendicularStep = Math.Max(1, perpendicular / 256);
        var previousPixels = previous.Pixels.Span;
        var currentPixels = current.Pixels.Span;
        var bestSeam = overlap / 2;
        var bestScore = double.MaxValue;
        for (var seam = 1; seam < overlap; seam++)
        {
            var totals = new double[bands];
            var samples = new int[bands];
            for (var perpendicularOffset = 0;
                 perpendicularOffset < perpendicular;
                 perpendicularOffset += perpendicularStep)
            {
                var band = Math.Min(bands - 1, perpendicularOffset * bands / Math.Max(1, perpendicular));
                var previousOffset = PixelOffset(previous, dimension - overlap + seam, perpendicularOffset, axis);
                var currentOffset = PixelOffset(current, seam, perpendicularOffset, axis);
                totals[band] += (Math.Abs(previousPixels[previousOffset] - currentPixels[currentOffset]) +
                                 Math.Abs(previousPixels[previousOffset + 1] - currentPixels[currentOffset + 1]) +
                                 Math.Abs(previousPixels[previousOffset + 2] - currentPixels[currentOffset + 2])) / 3d;
                samples[band]++;
            }

            var scores = new double[bands];
            var count = 0;
            for (var band = 0; band < bands; band++)
            {
                if (samples[band] > 0)
                    scores[count++] = totals[band] / samples[band];
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
        for (var index = 1; index < frames.Count; index++)
        {
            var placement = GetPlacement(frames, placements, index, LongScreenshotAxis.Vertical);
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
        for (var index = 1; index < frames.Count; index++)
        {
            var placement = GetPlacement(frames, placements, index, LongScreenshotAxis.Horizontal);
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
        if (outputX == outputWidth)
            return new ImageFrame(outputWidth, first.Height, stride, first.DpiX, first.DpiY, pixels);
        var compactStride = checked(outputX * 4);
        var compact = new byte[checked(compactStride * first.Height)];
        for (var row = 0; row < first.Height; row++)
            pixels.AsSpan(row * stride, compactStride).CopyTo(compact.AsSpan(row * compactStride, compactStride));
        return new ImageFrame(outputX, first.Height, compactStride, first.DpiX, first.DpiY, compact);
    }

    private static LongScreenshotPlacement GetPlacement(
        IReadOnlyList<ImageFrame> frames,
        IReadOnlyList<LongScreenshotPlacement> placements,
        int index,
        LongScreenshotAxis axis) => index - 1 < placements.Count
        ? placements[index - 1]
        : EstimateManaged(frames[index - 1], frames[index], axis);

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
                for (var byteIndex = 0; byteIndex < rowBytes; byteIndex++)
                    destinationSpan[byteIndex] = (byte)Math.Round(
                        destinationSpan[byteIndex] * (1d - weight) + sourceSpan[byteIndex] * weight);
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

    private static int PixelOffset(ImageFrame frame, int axisOffset, int perpendicularOffset, LongScreenshotAxis axis) =>
        axis == LongScreenshotAxis.Vertical
            ? axisOffset * frame.Stride + perpendicularOffset * 4
            : perpendicularOffset * frame.Stride + axisOffset * 4;

    private static void CopyRows(ImageFrame source, byte[] destination, int stride, int destinationY, int sourceY, int rows)
    {
        var bytes = source.Width * 4;
        for (var row = 0; row < rows; row++)
            source.Pixels.Span.Slice((sourceY + row) * source.Stride, bytes)
                .CopyTo(destination.AsSpan((destinationY + row) * stride, bytes));
    }

    private static void CopyColumns(ImageFrame source, byte[] destination, int stride, int destinationX, int sourceX, int columns)
    {
        var bytes = columns * 4;
        for (var row = 0; row < source.Height; row++)
            source.Pixels.Span.Slice(row * source.Stride + sourceX * 4, bytes)
                .CopyTo(destination.AsSpan(row * stride + destinationX * 4, bytes));
    }

    private static Mat ToGray(ImageFrame frame, double scale)
    {
        if (!MemoryMarshal.TryGetArray(frame.Pixels, out var segment) || segment.Array is null)
            throw new InvalidOperationException("The image buffer is not array-backed.");
        using var bgra = Mat.FromPixelData(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4,
            segment.Array,
            frame.Stride);
        using var gray = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        if (scale >= 0.999)
            return gray.Clone();
        var scaled = new Mat();
        Cv2.Resize(
            gray,
            scaled,
            new OpenCvSharp.Size(
                Math.Max(1, (int)Math.Round(frame.Width * scale)),
                Math.Max(1, (int)Math.Round(frame.Height * scale))),
            0,
            0,
            InterpolationFlags.Area);
        return scaled;
    }

    private static double GetScale(ImageFrame frame, LongScreenshotAxis axis)
    {
        var perpendicular = axis == LongScreenshotAxis.Vertical ? frame.Width : frame.Height;
        var dimension = GetDimension(frame, axis);
        return Math.Min(1d, Math.Min(1024d / Math.Max(1, perpendicular), 4096d / Math.Max(1, dimension)));
    }

    private static int GetDimension(ImageFrame frame, LongScreenshotAxis axis) =>
        axis == LongScreenshotAxis.Vertical ? frame.Height : frame.Width;

    private static int GetDimension(Mat frame, LongScreenshotAxis axis) =>
        axis == LongScreenshotAxis.Vertical ? frame.Height : frame.Width;
}
