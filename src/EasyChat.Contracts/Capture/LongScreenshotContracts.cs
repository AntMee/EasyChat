using EasyChat.Contracts.Platform;

namespace EasyChat.Contracts.Capture;

public enum LongScreenshotAxis
{
    Vertical,
    Horizontal
}

public sealed record LongScreenshotPlacement(
    int Overlap,
    double Confidence,
    bool IsDuplicate = false,
    int Offset = 0,
    // -1 means no seam was supplied; the composer then uses the legacy
    // overlap boundary for callers that only provide an overlap.
    int SeamStart = -1,
    int SeamLength = 0);

public interface ILongScreenshotStitcher
{
    LongScreenshotPlacement Match(
        ImageFrame previous,
        ImageFrame current,
        LongScreenshotAxis axis);

    ImageFrame Compose(
        IReadOnlyList<ImageFrame> frames,
        IReadOnlyList<LongScreenshotPlacement> placements,
        LongScreenshotAxis axis);
}
