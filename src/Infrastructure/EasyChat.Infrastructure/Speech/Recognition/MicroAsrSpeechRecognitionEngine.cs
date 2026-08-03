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

        var events = new RecognitionEventBuffer();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var eventLifetime = new CancellationTokenSource();
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
            {
                if (item.Kind == SpeechRecognitionEventKind.Partial)
                    events.TryWritePartial(item);
                else
                    events.TryWriteReliable(item, eventLifetime.Token);
            }
            if (result.Kind == MicroAsrResultKind.Error)
                lifetime.Cancel();
        }

        recognizer!.ResultAvailable += OnResult;
        var pump = Task.Run(
            () => PumpAudioAsync(
                recognizer,
                options.Sources,
                events,
                lifetime.Token,
                eventLifetime.Token),
            CancellationToken.None);
        try
        {
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
            await foreach (var item in events.ReadAllAsync(CancellationToken.None)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            eventLifetime.Cancel();
            lifetime.Cancel();
            try
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
            finally
            {
                recognizer.ResultAvailable -= OnResult;
                try
                {
                    await recognizer.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
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
        RecognitionEventBuffer events,
        CancellationToken captureCancellationToken,
        CancellationToken eventCancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var pcm in _audioCapture.CaptureAsync(
                               sources,
                               PcmAudioFormat.SpeechRecognition,
                               captureCancellationToken).ConfigureAwait(false))
            {
                await recognizer.WriteAsync(pcm, captureCancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (captureCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                try
                {
                    await recognizer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                if (failure is not null)
                {
                    events.TryWriteReliable(new SpeechRecognitionEvent(
                        SpeechRecognitionEventKind.Error,
                        failure.Message), eventCancellationToken);
                }
            }
            finally
            {
                events.CompleteWithStopped();
            }
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

    private sealed class RecognitionEventBuffer
    {
        private const int Capacity = 32;
        private const int ProducerCapacity = Capacity - 1;
        private readonly object _sync = new();
        private readonly LinkedList<SpeechRecognitionEvent> _pending = [];
        private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false
            });
        private bool _completed;

        public bool TryWritePartial(SpeechRecognitionEvent item)
        {
            lock (_sync)
            {
                if (_completed)
                    return false;

                if (_pending.Last?.Value.Kind == SpeechRecognitionEventKind.Partial)
                {
                    _pending.Last.Value = item;
                    return true;
                }

                if (_pending.Count >= ProducerCapacity)
                {
                    var stalePartial = FindOldestPartial();
                    if (stalePartial is null)
                        return false;
                    _pending.Remove(stalePartial);
                }

                _pending.AddLast(item);
            }

            _signal.Writer.TryWrite(0);
            return true;
        }

        public bool TryWriteReliable(
            SpeechRecognitionEvent item,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(
                static state => ((RecognitionEventBuffer)state!).PulseWriters(),
                this);

            lock (_sync)
            {
                while (!_completed && _pending.Count >= ProducerCapacity)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    var stalePartial = FindOldestPartial();
                    if (stalePartial is not null)
                    {
                        _pending.Remove(stalePartial);
                        break;
                    }

                    Monitor.Wait(_sync);
                }

                if (_completed || cancellationToken.IsCancellationRequested)
                    return false;

                _pending.AddLast(item);
            }

            _signal.Writer.TryWrite(0);
            return true;
        }

        public void CompleteWithStopped()
        {
            lock (_sync)
            {
                if (_completed)
                    return;

                _completed = true;
                _pending.AddLast(new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped));
                Monitor.PulseAll(_sync);
            }

            _signal.Writer.TryWrite(0);
        }

        public async IAsyncEnumerable<SpeechRecognitionEvent> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                SpeechRecognitionEvent? item = null;
                bool completed;
                lock (_sync)
                {
                    if (_pending.First is not null)
                    {
                        item = _pending.First.Value;
                        _pending.RemoveFirst();
                        Monitor.PulseAll(_sync);
                    }
                    completed = _completed;
                }

                if (item is not null)
                {
                    yield return item;
                    continue;
                }
                if (completed)
                    yield break;

                await _signal.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private LinkedListNode<SpeechRecognitionEvent>? FindOldestPartial()
        {
            for (var node = _pending.First; node is not null; node = node.Next)
            {
                if (node.Value.Kind == SpeechRecognitionEventKind.Partial)
                    return node;
            }
            return null;
        }

        private void PulseWriters()
        {
            lock (_sync)
                Monitor.PulseAll(_sync);
        }
    }
}
