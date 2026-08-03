using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Speech;

public sealed class TtsUseCases : ITtsUseCases
{
    private readonly IReadOnlyList<ITtsSynthesisProvider> _providers;
    private readonly ISettingsUseCases _settings;
    private readonly ITtsOutputWriter _outputWriter;
    private readonly IAudioPlaybackQueue _playbackQueue;

    public TtsUseCases(
        IEnumerable<ITtsSynthesisProvider> providers,
        ISettingsUseCases settings,
        ITtsOutputWriter outputWriter,
        IAudioPlaybackQueue playbackQueue)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        if (_providers.Count == 0)
            throw new InvalidOperationException("No TTS provider is registered.");
        if (_providers.Select(provider => provider.ProviderId).Distinct(StringComparer.Ordinal).Count()
            != _providers.Count)
        {
            throw new InvalidOperationException("TTS provider IDs must be unique.");
        }

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        _playbackQueue = playbackQueue ?? throw new ArgumentNullException(nameof(playbackQueue));
    }

    public IReadOnlyList<TtsProviderDescriptor> GetProviders() =>
        _providers.Select(provider => new TtsProviderDescriptor(provider.ProviderId)).ToArray();

    public ValueTask<Result<IReadOnlyList<TtsVoice>>> GetVoicesAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default) =>
        ResolveProvider(providerId).GetVoicesAsync(cancellationToken);

    public ValueTask<Result<IReadOnlyList<TtsLanguage>>> GetLanguagesAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default) =>
        ResolveProvider(providerId).GetLanguagesAsync(cancellationToken);

    public async ValueTask<Result<string?>> ResolvePreferredVoiceAsync(
        string languageId,
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        var provider = ResolveProvider(providerId);
        var preferences = _settings.Current.Tts.ProviderVoicePreferences;
        if (preferences.TryGetValue(provider.ProviderId, out var voices)
            && voices.TryGetValue(languageId, out var configuredVoice)
            && !string.IsNullOrWhiteSpace(configuredVoice))
        {
            return Result<string?>.Success(configuredVoice);
        }

        var available = await provider.GetVoicesAsync(cancellationToken).ConfigureAwait(false);
        if (available.IsFailure)
            return Result<string?>.Failure(available.Error);

        var language = languageId.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        var voice = available.Value.FirstOrDefault(candidate =>
                        candidate.LanguageId.StartsWith(language, StringComparison.OrdinalIgnoreCase))
                    ?? available.Value.FirstOrDefault(candidate =>
                        candidate.Id.Contains("en", StringComparison.OrdinalIgnoreCase));
        return Result<string?>.Success(voice?.Id);
    }

    public ValueTask<Result<AudioTrack>> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        return ResolveProvider(request.ProviderId).SynthesizeAsync(request, cancellationToken);
    }

    public async ValueTask<Result> SynthesizeToFileAsync(
        TtsSynthesisRequest request,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var synthesized = await SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        return synthesized.IsFailure
            ? Result.Failure(synthesized.Error)
            : await _outputWriter.WriteAsync(outputPath, synthesized.Value, cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<Result> EnqueueAsync(
        TtsSynthesisRequest request,
        bool interruptCurrent = false,
        CancellationToken cancellationToken = default)
    {
        var synthesized = await SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (synthesized.IsFailure)
            return Result.Failure(synthesized.Error);

        if (interruptCurrent)
            await _playbackQueue.StopAsync(cancellationToken).ConfigureAwait(false);
        await _playbackQueue.EnqueueAsync(synthesized.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private ITtsSynthesisProvider ResolveProvider(string? requestedId)
    {
        var providerId = string.IsNullOrWhiteSpace(requestedId)
            ? _settings.Current.Tts.Provider
            : requestedId;
        return _providers.FirstOrDefault(provider => provider.ProviderId == providerId)
               ?? _providers.FirstOrDefault(provider => provider.ProviderId == TtsProviderIds.EdgeTts)
               ?? _providers[0];
    }

    private static void Validate(TtsSynthesisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VoiceId);
    }
}
