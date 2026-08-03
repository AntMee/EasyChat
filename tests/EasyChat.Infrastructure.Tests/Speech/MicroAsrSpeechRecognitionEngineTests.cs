using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Speech.Recognition;

namespace EasyChat.Infrastructure.Tests.Speech;

[TestClass]
public sealed class MicroAsrSpeechRecognitionEngineTests
{
    [TestMethod]
    public async Task RecognitionStreamsPcmAndMapsMicroAsrEvents()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), "easychat-microasr-tests", "models");
        var capture = new FakePcmCapture();
        var recognizer = new FakeRecognizer();
        var factory = new FakeRecognizerFactory(recognizer);
        await using var engine = new MicroAsrSpeechRecognitionEngine(
            capture,
            factory,
            modelsDirectory);
        var source = new AudioCaptureSourceToken("platform:opaque-source");
        var events = new List<SpeechRecognitionEvent>();

        await foreach (var item in engine.RecognizeAsync(
                           new SpeechRecognitionOptions("en-US", "en-US", [source])))
        {
            events.Add(item);
        }

        Assert.AreEqual(Path.Combine(modelsDirectory, "en-US"), factory.ModelDirectory);
        CollectionAssert.AreEqual(new[] { source }, capture.Sources.ToArray());
        Assert.AreEqual(PcmAudioFormat.SpeechRecognition, capture.Format);
        CollectionAssert.AreEqual(new byte[] { 1, 0, 2, 0 }, recognizer.WrittenPcm.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                SpeechRecognitionEventKind.Started,
                SpeechRecognitionEventKind.Partial,
                SpeechRecognitionEventKind.Final,
                SpeechRecognitionEventKind.Stopped
            },
            events.Select(item => item.Kind).ToArray());
        Assert.IsTrue(recognizer.Completed);
        Assert.IsTrue(recognizer.Disposed);
    }

    [TestMethod]
    public async Task ModelCreationFailureIsReportedAsErrorThenStopped()
    {
        var factory = new FakeRecognizerFactory(new InvalidDataException("bad model"));
        await using var engine = new MicroAsrSpeechRecognitionEngine(
            new FakePcmCapture(),
            factory,
            Path.Combine(Path.GetTempPath(), "easychat-microasr-tests", "models"));
        var events = new List<SpeechRecognitionEvent>();

        await foreach (var item in engine.RecognizeAsync(
                           new SpeechRecognitionOptions("invalid", "invalid", [])))
        {
            events.Add(item);
        }

        CollectionAssert.AreEqual(
            new[] { SpeechRecognitionEventKind.Error, SpeechRecognitionEventKind.Stopped },
            events.Select(item => item.Kind).ToArray());
        Assert.AreEqual("bad model", events[0].Text);
    }

    [TestMethod]
    public async Task ModelPathCannotEscapeTheModelLibrary()
    {
        var factory = new FakeRecognizerFactory(new FakeRecognizer());
        await using var engine = new MicroAsrSpeechRecognitionEngine(
            new FakePcmCapture(),
            factory,
            Path.Combine(Path.GetTempPath(), "easychat-microasr-tests", "models"));
        var events = new List<SpeechRecognitionEvent>();

        await foreach (var item in engine.RecognizeAsync(
                           new SpeechRecognitionOptions("..", "invalid", [])))
        {
            events.Add(item);
        }

        CollectionAssert.AreEqual(
            new[] { SpeechRecognitionEventKind.Error, SpeechRecognitionEventKind.Stopped },
            events.Select(item => item.Kind).ToArray());
        Assert.IsTrue(string.IsNullOrEmpty(factory.ModelDirectory));
    }

    private sealed class FakePcmCapture : IPcmAudioCapture
    {
        public IReadOnlyList<AudioCaptureSourceToken> Sources { get; private set; } = [];
        public PcmAudioFormat Format { get; private set; }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            IReadOnlyList<AudioCaptureSourceToken> sources,
            PcmAudioFormat format,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sources = sources;
            Format = format;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new byte[] { 1, 0, 2, 0 };
        }
    }

    private sealed class FakeRecognizerFactory : IMicroAsrRecognizerFactory
    {
        private readonly IMicroAsrRecognizer? _recognizer;
        private readonly Exception? _failure;

        public FakeRecognizerFactory(IMicroAsrRecognizer recognizer)
        {
            _recognizer = recognizer;
        }

        public FakeRecognizerFactory(Exception failure)
        {
            _failure = failure;
        }

        public string ModelDirectory { get; private set; } = string.Empty;

        public IMicroAsrRecognizer Create(string modelDirectory)
        {
            ModelDirectory = modelDirectory;
            if (_failure is not null)
                throw _failure;
            return _recognizer!;
        }
    }

    private sealed class FakeRecognizer : IMicroAsrRecognizer
    {
        public event Action<MicroAsrResult>? ResultAvailable;
        public List<byte> WrittenPcm { get; } = [];
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> pcm16,
            CancellationToken cancellationToken = default)
        {
            WrittenPcm.AddRange(pcm16.ToArray());
            ResultAvailable?.Invoke(new MicroAsrResult(MicroAsrResultKind.Partial, "partial"));
            ResultAvailable?.Invoke(new MicroAsrResult(MicroAsrResultKind.Final, "final"));
            return ValueTask.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
