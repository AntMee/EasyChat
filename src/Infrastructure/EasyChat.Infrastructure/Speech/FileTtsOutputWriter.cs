using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Speech;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Speech;

public sealed class FileTtsOutputWriter : ITtsOutputWriter
{
    public async ValueTask<Result> WriteAsync(
        string path,
        AudioTrack track,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(track);
        try
        {
            await File.WriteAllBytesAsync(path, track.Content.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("tts.output-failed", exception.Message));
        }
    }
}
