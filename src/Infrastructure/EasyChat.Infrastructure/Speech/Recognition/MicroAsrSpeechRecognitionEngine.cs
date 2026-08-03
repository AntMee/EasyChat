using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Speech.Recognition;

public sealed class MicroAsrSpeechRecognitionEngine : ISpeechRecognitionEngine, IAsyncDisposable
{
    private readonly IPcmAudioCapture _audioCapture;
    private readonly IMicroAsrRecognizerFactory _recognizers;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _disposed;

    public MicroAsrSpeechRecognitionEngine(IPcmAudioCapture audioCapture)
        : this(
            audioCapture,
            new MicroAsrRecognizerFactory(),
            Path.Combine(AppContext.BaseDirectory, "Models"))
    {
    }

    internal MicroAsrSpeechRecognitionEngine(
        IPcmAudioCapture audioCapture,
        IMicroAsrRecognizerFactory recognizers,
        string modelsDirectory)
    {
        _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
        _recognizers = recognizers ?? throw new ArgumentNullException(nameof(recognizers));
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
        SpeechRecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        IMicroAsrRecognizer? recognizer = null;
        Exception? startFailure = null;
        try
        {
            recognizer = _recognizers.Create(ResolveModelDirectory(options.ModelPath));
        }
        catch (Exception exception)
        {
            startFailure = exception;
        }

        if (startFailure is not null)
        {
            _sessionGate.Release();
            yield return new SpeechRecognitionEvent(
                SpeechRecognitionEventKind.Error,
                startFailure.Message);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            yield break;
        }

        var events = Channel.CreateUnbounded<SpeechRecognitionEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnResult(MicroAsrResult result)
        {
            var item = result.Kind switch
            {
                MicroAsrResultKind.Partial => new SpeechRecognitionEvent(
                    SpeechRecognitionEventKind.Partial,
                    result.Text),
                MicroAsrResultKind.Final => new SpeechRecognitionEvent(
                    SpeechRecognitionEventKind.Final,
                    result.Text),
                MicroAsrResultKind.Error => new SpeechRecognitionEvent(
                    SpeechRecognitionEventKind.Error,
                    result.Exception?.Message ?? result.Text),
                _ => null
            };
            if (item is not null)
                events.Writer.TryWrite(item);
            if (result.Kind == MicroAsrResultKind.Error)
                lifetime.Cancel();
        }

        recognizer!.ResultAvailable += OnResult;
        var pump = PumpAudioAsync(recognizer, options.Sources, events.Writer, lifetime.Token);
        yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
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
            recognizer.ResultAvailable -= OnResult;
            await recognizer.DisposeAsync().ConfigureAwait(false);
            _sessionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _sessionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task PumpAudioAsync(
        IMicroAsrRecognizer recognizer,
        IReadOnlyList<AudioCaptureSourceToken> sources,
        ChannelWriter<SpeechRecognitionEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pcm in _audioCapture.CaptureAsync(
                               sources,
                               PcmAudioFormat.SpeechRecognition,
                               cancellationToken).ConfigureAwait(false))
            {
                await recognizer.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
            }

            await recognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            writer.TryWrite(new SpeechRecognitionEvent(
                SpeechRecognitionEventKind.Error,
                exception.Message));
        }
        finally
        {
            writer.TryWrite(new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped));
            writer.TryComplete();
        }
    }

    private string ResolveModelDirectory(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        var candidate = Path.GetFullPath(Path.Combine(_modelsDirectory, modelPath));
        var root = _modelsDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _modelsDirectory
            : _modelsDirectory + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(root, pathComparison))
            throw new ArgumentException("The speech model must be inside the model library.", nameof(modelPath));
        return candidate;
    }
}
