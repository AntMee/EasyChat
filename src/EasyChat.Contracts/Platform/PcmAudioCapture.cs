namespace EasyChat.Contracts.Platform;

public readonly record struct PcmAudioFormat(
    int SampleRateHz,
    int ChannelCount,
    int BitsPerSample)
{
    public static PcmAudioFormat SpeechRecognition { get; } = new(16_000, 1, 16);
}

/// <summary>
/// Platform audio input for speech recognition. Implementations must return PCM chunks in the
/// requested format and interpret source tokens only inside their owning platform adapter.
/// </summary>
public interface IPcmAudioCapture
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        IReadOnlyList<AudioCaptureSourceToken> sources,
        PcmAudioFormat format,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capture capability for keeping an input device open before recognition starts.
/// Prepared captures must discard audio until a consumer calls <see cref="IPcmAudioCapture.CaptureAsync"/>.
/// </summary>
public interface IPreparablePcmAudioCapture
{
    ValueTask PrepareCaptureAsync(
        IReadOnlyList<AudioCaptureSourceToken> sources,
        PcmAudioFormat format,
        CancellationToken cancellationToken = default);

    ValueTask ReleasePreparedCaptureAsync(
        IReadOnlyList<AudioCaptureSourceToken> sources,
        PcmAudioFormat format,
        CancellationToken cancellationToken = default);
}
