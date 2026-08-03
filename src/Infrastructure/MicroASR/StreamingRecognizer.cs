using System.Threading.Channels;

namespace MicroASR;

public enum RecognitionEventType
{
    Partial,
    Final,
    Error,
}

public sealed record RecognitionEvent(
    RecognitionEventType Type,
    string Text,
    float Confidence = 0,
    IReadOnlyList<RecognitionWord>? Words = null,
    Exception? Exception = null);

public sealed record StreamingRecognizerOptions
{
    public RnntDecoderOptions Decoder { get; init; } =
        RnntDecoderOptions.ForMode(RnntRecognitionMode.Accuracy);

    public int PreRollFrames { get; init; } = 75;
    public int EndSilenceFrames { get; init; } = 50;
    public int MinimumSpeechFrames { get; init; } = 8;
    public int LowLatencySpeechFrames { get; init; } = 100;
    public int AudioQueueCapacity { get; init; } = 32;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Decoder);
        Decoder.Validate();
        if (PreRollFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(PreRollFrames));
        if (EndSilenceFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(EndSilenceFrames));
        if (MinimumSpeechFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSpeechFrames));
        if (LowLatencySpeechFrames < MinimumSpeechFrames)
            throw new ArgumentOutOfRangeException(nameof(LowLatencySpeechFrames));
        if (AudioQueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(AudioQueueCapacity));
    }
}

public sealed class StreamingRecognizer : IAsyncDisposable
{
    private const int FeatureWidth = StreamingFbankExtractor.FeatureSize;

    private readonly RnntRecognizer _recognizer;
    private readonly NeuralVoiceActivityDetector _vad;
    private readonly TextPostProcessor _postProcessor;
    private readonly StreamingRecognizerOptions _options;
    private readonly float _voiceThreshold;
    private readonly StreamingFbankExtractor _features = new();
    private readonly Channel<byte[]> _audio;
    private readonly Task _worker;
    private int _stopping;
    private bool _disposed;

    public StreamingRecognizer(string modelDirectory)
        : this(modelDirectory, new StreamingRecognizerOptions())
    {
    }

    public StreamingRecognizer(string modelDirectory, RnntRecognitionMode mode)
        : this(modelDirectory, new StreamingRecognizerOptions
        {
            Decoder = RnntDecoderOptions.ForMode(mode),
        })
    {
    }

    public StreamingRecognizer(string modelDirectory, StreamingRecognizerOptions options)
        : this(SpeechModelPackage.Load(modelDirectory), options)
    {
    }

    public StreamingRecognizer(SpeechModelPackage package, StreamingRecognizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _recognizer = new RnntRecognizer(package, options.Decoder);
        _vad = new NeuralVoiceActivityDetector(package);
        _postProcessor = new TextPostProcessor(package);
        _voiceThreshold = package.VadThreshold;
        _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.AudioQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ProcessAudioAsync);
    }

    public event Action<RecognitionEvent>? ResultAvailable;

    public Task Completion => _worker;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotRunning();
        ValidatePcm(pcm16.Length);
        if (pcm16.IsEmpty)
            return;
        await _audio.Writer.WriteAsync(pcm16.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool TryWrite(ReadOnlySpan<byte> pcm16)
    {
        ThrowIfNotRunning();
        ValidatePcm(pcm16.Length);
        return pcm16.IsEmpty || _audio.Writer.TryWrite(pcm16.ToArray());
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
            _audio.Writer.TryComplete();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessAudioAsync()
    {
        var recent = new FeatureRingBuffer(_options.PreRollFrames);
        bool speaking = false;
        int silentFrames = 0;
        int speechFrames = 0;
        var partialState = new PartialPublicationState();

        void ProcessFeature(float[] feature)
        {
            float voiceProbability = _vad.AcceptFeature(feature);
            recent.Add(feature);

            if (!speaking)
            {
                if (voiceProbability < _voiceThreshold)
                    return;

                speaking = true;
                silentFrames = 0;
                speechFrames = 1;
                partialState.Reset();
                _recognizer.Reset();
                recent.ForEach(buffered =>
                {
                    if (_recognizer.AcceptFeature(buffered))
                        PublishPartial(partialState);
                });
                return;
            }

            if (voiceProbability >= _voiceThreshold)
            {
                silentFrames = 0;
                speechFrames++;
                if (speechFrames == _options.LowLatencySpeechFrames)
                    _recognizer.UseStandardChunking();
            }
            else
            {
                silentFrames++;
            }

            if (_recognizer.AcceptFeature(feature))
                PublishPartial(partialState);

            if (silentFrames < _options.EndSilenceFrames)
                return;

            PublishFinal(speechFrames >= _options.MinimumSpeechFrames);
            speaking = false;
            partialState.Reset();
            recent.KeepLast(silentFrames);
        }

        try
        {
            await foreach (byte[] pcm in _audio.Reader.ReadAllAsync().ConfigureAwait(false))
                _features.AcceptPcm(pcm, ProcessFeature);

            if (speaking)
                PublishFinal(speechFrames >= _options.MinimumSpeechFrames);
        }
        catch (Exception exception)
        {
            Publish(new RecognitionEvent(
                RecognitionEventType.Error,
                string.Empty,
                Exception: exception));
        }
    }

    private void PublishPartial(PartialPublicationState state)
    {
        RnntResult result = _recognizer.CurrentResult;
        string text = _postProcessor.Process(result.Text, final: false);
        if (text.Length == 0 || string.Equals(text, state.Published, StringComparison.Ordinal))
            return;

        bool extendsPublished = state.Published.Length == 0 ||
                                text.StartsWith(state.Published, StringComparison.OrdinalIgnoreCase);
        if (!extendsPublished)
        {
            bool confirmsCandidate = state.RevisionCandidate.Length > 0 &&
                                     text.StartsWith(state.RevisionCandidate, StringComparison.OrdinalIgnoreCase);
            if (!confirmsCandidate)
            {
                state.RevisionCandidate = text;
                state.RevisionConfirmations = 1;
                return;
            }

            state.RevisionConfirmations++;
            if (state.RevisionConfirmations < 2)
                return;
        }

        state.Published = text;
        state.RevisionCandidate = string.Empty;
        state.RevisionConfirmations = 0;
        Publish(new RecognitionEvent(
            RecognitionEventType.Partial,
            text,
            result.Confidence,
            result.Words));
    }

    private void PublishFinal(bool acceptResult)
    {
        _recognizer.Flush();
        RnntResult result = _recognizer.CurrentResult;
        string text = acceptResult ? _postProcessor.Process(result.Text, final: true) : string.Empty;
        if (text.Length > 0)
        {
            Publish(new RecognitionEvent(
                RecognitionEventType.Final,
                text,
                result.Confidence,
                result.Words));
        }
        _recognizer.Reset();
    }

    private void Publish(RecognitionEvent result)
    {
        foreach (Action<RecognitionEvent> handler in
                 ResultAvailable?.GetInvocationList().Cast<Action<RecognitionEvent>>() ?? [])
        {
            try
            {
                handler(result);
            }
            catch
            {
                // Consumer callbacks must not terminate the inference worker.
            }
        }
    }

    private static void ValidatePcm(int byteLength)
    {
        if ((byteLength & 1) != 0)
            throw new ArgumentException("PCM16 data must contain complete 16-bit samples.", "pcm16");
    }

    private void ThrowIfNotRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _stopping) != 0)
            throw new InvalidOperationException("The recognizer has already been completed.");
    }

    private sealed class PartialPublicationState
    {
        public string Published { get; set; } = string.Empty;
        public string RevisionCandidate { get; set; } = string.Empty;
        public int RevisionConfirmations { get; set; }

        public void Reset()
        {
            Published = string.Empty;
            RevisionCandidate = string.Empty;
            RevisionConfirmations = 0;
        }
    }

    private sealed class FeatureRingBuffer
    {
        private readonly float[][] _frames;
        private int _start;
        private int _count;

        public FeatureRingBuffer(int capacity)
        {
            _frames = new float[capacity][];
            for (int index = 0; index < capacity; index++)
                _frames[index] = new float[FeatureWidth];
        }

        public void Add(float[] feature)
        {
            int destination = (_start + _count) % _frames.Length;
            if (_count == _frames.Length)
            {
                destination = _start;
                _start = (_start + 1) % _frames.Length;
            }
            else
            {
                _count++;
            }
            feature.CopyTo(_frames[destination], 0);
        }

        public void ForEach(Action<float[]> action)
        {
            for (int index = 0; index < _count; index++)
                action(_frames[(_start + index) % _frames.Length]);
        }

        public void KeepLast(int count)
        {
            count = Math.Clamp(count, 0, _count);
            _start = (_start + _count - count) % _frames.Length;
            _count = count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await CompleteAsync().ConfigureAwait(false);
        _disposed = true;
        _vad.Dispose();
        _recognizer.Dispose();
    }
}

