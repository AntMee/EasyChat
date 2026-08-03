using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Speech;

namespace EasyChat.Infrastructure.Windows.Tests.Speech;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechRecognitionEngineTests
{
    [TestMethod]
    public async Task RecognitionOwnsWorkerCallbackProcessNormalizationAndCleanup()
    {
        var backend = new FakeBackend();
        var worker = new FakeWorker();
        using var engine = new WindowsSpeechRecognitionEngine(backend, worker, @"C:\models");
        var events = new List<SpeechRecognitionEvent>();

        await foreach (var item in engine.RecognizeAsync(
                           new SpeechRecognitionOptions("en", "en", [])))
        {
            events.Add(item);
        }

        Assert.IsTrue(backend.ModelPath.EndsWith("models\\en", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { 0 }, backend.ProcessIds);
        CollectionAssert.AreEqual(
            new[]
            {
                SpeechRecognitionEventKind.Started,
                SpeechRecognitionEventKind.Partial,
                SpeechRecognitionEventKind.Final,
                SpeechRecognitionEventKind.Stopped
            },
            events.Select(item => item.Kind).ToArray());
        Assert.AreEqual(1, backend.CleanupCount);
        Assert.IsTrue(worker.AllCallsDispatched);
    }

    [TestMethod]
    public async Task ApplicationSourceTokens_AreDecodedOnlyByTheWindowsAdapter()
    {
        var backend = new FakeBackend();
        using var engine = new WindowsSpeechRecognitionEngine(
            backend,
            new FakeWorker(),
            @"C:\models");

        await foreach (var _ in engine.RecognizeAsync(new SpeechRecognitionOptions(
                           "en",
                           "en",
                           [WindowsAudioCaptureSourceCatalog.FromProcessId(42)])))
        {
        }

        CollectionAssert.AreEqual(new[] { 42 }, backend.ProcessIds);
    }

    private sealed class FakeWorker : IWindowsAsrWorker
    {
        public bool IsExecuting { get; private set; }
        public bool AllCallsDispatched { get; private set; } = true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            IsExecuting = true;
            try
            {
                action();
            }
            finally
            {
                IsExecuting = false;
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeBackend : IWindowsAsrBackend
    {
        private WindowsAsrCallback? _callback;
        public string ModelPath { get; private set; } = string.Empty;
        public int[] ProcessIds { get; private set; } = [];
        public int CleanupCount { get; private set; }

        public bool Initialize(string modelPath)
        {
            ModelPath = modelPath;
            return true;
        }

        public void SetCallback(WindowsAsrCallback callback) => _callback = callback;
        public void StartLoopbackCapture(int[] processIds) => ProcessIds = processIds;

        public void StartRecognition()
        {
            _callback!(1, "partial");
            _callback(0, "final");
            _callback(3, string.Empty);
        }

        public void Cleanup() => CleanupCount++;
    }
}
