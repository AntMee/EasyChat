using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;

namespace EasyChat.Application.Speech;

public sealed class SpeechRecognitionUseCases : ISpeechRecognitionUseCases
{
    private readonly ISpeechRecognitionEngine _engine;
    private readonly IPlatformAccessUseCases _platformAccess;
    private readonly ISettingsUseCases _settings;
    private readonly ITranslationUseCases _translation;
    private readonly ITranslationLanguageCatalog _languages;
    private readonly ILogger<SpeechRecognitionUseCases> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SubtitleTranslationLane _subtitleAiTranslationLane = new();
    private readonly SubtitleTranslationLane _subtitleMachineTranslationLane = new();
    private readonly SubtitleFloatingLifecycleRegistry _subtitleFloatingLifecycle;
    private readonly SubtitleTimestampClock _subtitleTimestampClock;
    private long _nextSubtitleId;

    public SpeechRecognitionUseCases(
        ISpeechRecognitionEngine engine,
        IPlatformAccessUseCases platformAccess,
        ISettingsUseCases settings,
        ITranslationUseCases translation,
        ITranslationLanguageCatalog languages,
        ILogger<SpeechRecognitionUseCases> logger)
        : this(
            engine,
            platformAccess,
            settings,
            translation,
            languages,
            logger,
            TimeProvider.System)
    {
    }

    internal SpeechRecognitionUseCases(
        ISpeechRecognitionEngine engine,
        IPlatformAccessUseCases platformAccess,
        ISettingsUseCases settings,
        ITranslationUseCases translation,
        ITranslationLanguageCatalog languages,
        ILogger<SpeechRecognitionUseCases> logger,
        TimeProvider timeProvider)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _platformAccess = platformAccess ?? throw new ArgumentNullException(nameof(platformAccess));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _subtitleFloatingLifecycle = new SubtitleFloatingLifecycleRegistry(_timeProvider);
        _subtitleTimestampClock = new SubtitleTimestampClock(_timeProvider);
    }

    public async IAsyncEnumerable<SpeechSessionEvent> RecognizeAsync(
        SpeechRecognitionCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var access = await _platformAccess.EnsureAvailableAsync(
            PlatformCapability.SpeechRecognition,
            cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
        {
            yield return new SpeechSessionErrorEvent(access.Error.Message);
            yield return new SpeechSessionStoppedEvent();
            yield break;
        }

        foreach (var permission in RequiredPermissions(command.Sources))
        {
            var permissionAccess = await _platformAccess.EnsurePermissionAsync(
                permission,
                cancellationToken).ConfigureAwait(false);
            if (permissionAccess.IsFailure)
            {
                yield return new SpeechSessionErrorEvent(permissionAccess.Error.Message);
                yield return new SpeechSessionStoppedEvent();
                yield break;
            }
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateUnbounded<SpeechSessionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var pump = PumpAsync(command, events.Writer, lifetime.Token);
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private async Task PumpAsync(
        SpeechRecognitionCommand command,
        ChannelWriter<SpeechSessionEvent> writer,
        CancellationToken cancellationToken)
    {
        SpeechRecognitionSettings GetSettings() => _settings.Current.SpeechRecognition;
        void Publish(SpeechSessionEvent item) => writer.TryWrite(item);
        try
        {
            var coordinator = new SubtitleSessionCoordinator(
                GetSettings,
                _translation,
                _languages,
                _logger,
                _timeProvider,
                () => Interlocked.Increment(ref _nextSubtitleId),
                Publish,
                _subtitleAiTranslationLane,
                _subtitleMachineTranslationLane,
                _subtitleFloatingLifecycle,
                _subtitleTimestampClock);
            await coordinator.RunAsync(
                _engine.RecognizeAsync(
                    new SpeechRecognitionOptions(
                        command.ModelPath,
                        command.Language,
                        command.Sources.Select(source => source.Token).ToArray()),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Speech recognition workflow failed.");
            Publish(new SpeechSessionErrorEvent(exception.Message));
            Publish(new SpeechSessionStoppedEvent());
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static IEnumerable<PlatformPermission> RequiredPermissions(
        IReadOnlyList<AudioCaptureSourceReference> sources)
    {
        if (sources.Count == 0)
            return [PlatformPermission.SystemAudioCapture];

        return sources.Select(source => source.Kind == AudioCaptureSourceKind.Microphone
                ? PlatformPermission.Microphone
                : PlatformPermission.SystemAudioCapture)
            .Distinct();
    }
}
