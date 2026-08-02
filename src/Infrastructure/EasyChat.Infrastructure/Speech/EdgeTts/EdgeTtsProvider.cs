using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Speech;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Speech.EdgeTts;

public sealed class EdgeTtsProvider : ITtsSynthesisProvider
{
    private readonly IEdgeTtsVoiceCatalog _catalog;
    private readonly IEdgeTtsTransport _transport;

    public EdgeTtsProvider(string assetsDirectory)
        : this(new EdgeTtsVoiceCatalog(assetsDirectory), new EdgeTtsTransport())
    {
    }

    internal EdgeTtsProvider(IEdgeTtsVoiceCatalog catalog, IEdgeTtsTransport transport)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string ProviderId => TtsProviderIds.EdgeTts;

    public async ValueTask<Result<IReadOnlyList<TtsVoice>>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<IReadOnlyList<TtsVoice>>.Success(
                await _catalog.GetVoicesAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<TtsVoice>>.Failure(
                new Error("tts.voices-failed", exception.Message));
        }
    }

    public async ValueTask<Result<IReadOnlyList<TtsLanguage>>> GetLanguagesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<IReadOnlyList<TtsLanguage>>.Success(
                await _catalog.GetLanguagesAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<TtsLanguage>>.Failure(
                new Error("tts.languages-failed", exception.Message));
        }
    }

    public async ValueTask<Result<AudioTrack>> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var audio = await _transport.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
            return audio.IsEmpty
                ? Result<AudioTrack>.Failure(new Error("tts.empty-audio", "Edge TTS returned no audio."))
                : Result<AudioTrack>.Success(new AudioTrack(audio, "audio/mpeg"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<AudioTrack>.Failure(new Error("tts.synthesis-failed", exception.Message));
        }
    }
}
