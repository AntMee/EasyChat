namespace MicroASR;

public enum RnntRecognitionMode
{
    Accuracy,
    Balanced,
    Performance,
}

public sealed record RnntDecoderOptions
{
    public bool EnableAdaptiveBeam { get; init; }
    public int MaximumBeamWidth { get; init; } = 7;
    public int MediumBeamWidth { get; init; } = 7;
    public int MinimumBeamWidth { get; init; } = 7;
    public int AdaptiveBeamWarmupFrames { get; init; } = 8;
    public double MediumBeamScoreGap { get; init; } = 3.0;
    public double MinimumBeamScoreGap { get; init; } = 6.0;
    public int TopTokensPerExpansion { get; init; } = 2;
    public int MaximumSymbolsPerFrame { get; init; } = 4;

    public static RnntDecoderOptions ForMode(RnntRecognitionMode mode) => mode switch
    {
        RnntRecognitionMode.Accuracy => new RnntDecoderOptions(),
        RnntRecognitionMode.Balanced => new RnntDecoderOptions
        {
            EnableAdaptiveBeam = true,
            MediumBeamWidth = 6,
            MinimumBeamWidth = 5,
            AdaptiveBeamWarmupFrames = 12,
            MediumBeamScoreGap = 4.0,
            MinimumBeamScoreGap = 8.0,
        },
        RnntRecognitionMode.Performance => new RnntDecoderOptions
        {
            EnableAdaptiveBeam = true,
            MediumBeamWidth = 5,
            MinimumBeamWidth = 3,
            AdaptiveBeamWarmupFrames = 8,
            MediumBeamScoreGap = 3.0,
            MinimumBeamScoreGap = 6.0,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal void Validate()
    {
        if (MaximumBeamWidth is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaximumBeamWidth), "Beam width must be between 1 and 32.");
        if (MinimumBeamWidth < 1 || MediumBeamWidth < MinimumBeamWidth ||
            MaximumBeamWidth < MediumBeamWidth)
        {
            throw new ArgumentException(
                "Beam widths must satisfy 1 <= minimum <= medium <= maximum.");
        }
        if (AdaptiveBeamWarmupFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveBeamWarmupFrames));
        if (!double.IsFinite(MediumBeamScoreGap) || MediumBeamScoreGap < 0)
            throw new ArgumentOutOfRangeException(nameof(MediumBeamScoreGap));
        if (!double.IsFinite(MinimumBeamScoreGap) || MinimumBeamScoreGap < MediumBeamScoreGap)
        {
            throw new ArgumentException(
                "The minimum-beam score gap must be finite and at least the medium-beam score gap.");
        }
        if (TopTokensPerExpansion is < 1 or > 2)
            throw new ArgumentOutOfRangeException(
                nameof(TopTokensPerExpansion), "This decoder supports one or two token candidates per expansion.");
        if (MaximumSymbolsPerFrame is < 1 or > 8)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumSymbolsPerFrame), "Maximum symbols per frame must be between 1 and 8.");
    }
}

