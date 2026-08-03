namespace EasyChat.Contracts.Platform;

public sealed record AudioTrack(ReadOnlyMemory<byte> Content, string MediaType);

public interface IAudioPlaybackQueue
{
    ValueTask EnqueueAsync(AudioTrack track, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
