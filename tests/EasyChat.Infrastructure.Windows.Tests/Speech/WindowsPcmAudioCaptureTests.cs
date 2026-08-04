using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Speech;

namespace EasyChat.Infrastructure.Windows.Tests.Speech;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsPcmAudioCaptureTests
{
    [TestMethod]
    public async Task ProcessTokensAreDecodedAndMultipleSourcesAreMixed()
    {
        var factory = new FakeSessionFactory();
        var capture = new WindowsPcmAudioCapture(factory);
        var sources = new[]
        {
            WindowsAudioCaptureSourceCatalog.FromProcessId(42),
            WindowsAudioCaptureSourceCatalog.FromProcessId(84)
        };
        ReadOnlyMemory<byte> mixed = default;

        await foreach (var frame in capture.CaptureAsync(
                           sources,
                           PcmAudioFormat.SpeechRecognition))
        {
            mixed = frame;
            break;
        }

        CollectionAssert.AreEqual(new[] { 42, 84 }, factory.ProcessIds.ToArray());
        Assert.AreEqual(3000, BitConverter.ToInt16(mixed.Span[..2]));
        Assert.IsTrue(factory.Sessions.All(session => session.Stopped && session.Disposed));
    }

    [TestMethod]
    public async Task EmptySelectionUsesTheSystemOutputSession()
    {
        var factory = new FakeSessionFactory();
        var capture = new WindowsPcmAudioCapture(factory);

        await foreach (var _ in capture.CaptureAsync([], PcmAudioFormat.SpeechRecognition))
            break;

        Assert.AreEqual(1, factory.SystemOutputCount);
        Assert.IsEmpty(factory.ProcessIds);
    }

    [TestMethod]
    public async Task SilentSessionProducesFixedFramesAndLaterAudioIsPreserved()
    {
        var factory = new FakeSessionFactory(emitSystemAudioOnStart: false);
        var capture = new WindowsPcmAudioCapture(factory);
        await using var frames = capture.CaptureAsync(
                [],
                PcmAudioFormat.SpeechRecognition)
            .GetAsyncEnumerator();

        Assert.IsTrue(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(640, frames.Current.Length);
        Assert.IsTrue(frames.Current.Span.SequenceEqual(new byte[640]));

        Assert.IsTrue(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(640, frames.Current.Length);
        Assert.IsTrue(frames.Current.Span.SequenceEqual(new byte[640]));

        factory.Sessions.Single().Emit(1234);

        Assert.IsTrue(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(640, frames.Current.Length);
        Assert.AreEqual(1234, BitConverter.ToInt16(frames.Current.Span[..2]));
    }

    [TestMethod]
    public async Task ProducerContinuesWhileTheConsumerIsPausedAndKeepsRecentSilence()
    {
        var factory = new FakeSessionFactory();
        var capture = new WindowsPcmAudioCapture(factory);
        await using var frames = capture.CaptureAsync(
                [],
                PcmAudioFormat.SpeechRecognition)
            .GetAsyncEnumerator();

        Assert.IsTrue(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(1000, BitConverter.ToInt16(frames.Current.Span[..2]));

        await Task.Delay(TimeSpan.FromSeconds(1));

        var buffered = await ReadBufferedSilenceAsync()
            .WaitAsync(TimeSpan.FromMilliseconds(400));
        Assert.HasCount(35, buffered);
        Assert.IsTrue(buffered.All(frame => frame.Length == 640));
        Assert.IsTrue(buffered.All(frame => frame.Span.SequenceEqual(new byte[640])));

        async Task<List<ReadOnlyMemory<byte>>> ReadBufferedSilenceAsync()
        {
            var result = new List<ReadOnlyMemory<byte>>();
            while (result.Count < 35 && await frames.MoveNextAsync())
                result.Add(frames.Current);
            return result;
        }
    }

    [TestMethod]
    public async Task CaptureFailureIsPropagatedAfterAllSessionsAreCleanedUp()
    {
        var factory = new FakeSessionFactory();
        var capture = new WindowsPcmAudioCapture(factory);
        var sources = new[]
        {
            WindowsAudioCaptureSourceCatalog.FromProcessId(42),
            WindowsAudioCaptureSourceCatalog.FromProcessId(84)
        };
        await using var frames = capture.CaptureAsync(
                sources,
                PcmAudioFormat.SpeechRecognition)
            .GetAsyncEnumerator();

        Assert.IsTrue(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        factory.Sessions[1].Fail(new InvalidOperationException("capture failed"));

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            while (await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)))
            {
            }
        });

        Assert.AreEqual("Windows audio capture failed.", failure.Message);
        Assert.AreEqual("capture failed", failure.InnerException?.Message);
        Assert.IsTrue(factory.Sessions.All(session => session.Stopped && session.Disposed));
    }

    private sealed class FakeSessionFactory : IWindowsPcmCaptureSessionFactory
    {
        private readonly bool _emitSystemAudioOnStart;

        public FakeSessionFactory(bool emitSystemAudioOnStart = true)
        {
            _emitSystemAudioOnStart = emitSystemAudioOnStart;
        }

        public List<int> ProcessIds { get; } = [];
        public List<FakeSession> Sessions { get; } = [];
        public int SystemOutputCount { get; private set; }

        public IWindowsPcmCaptureSession CreateSystemOutput(PcmAudioFormat format)
        {
            SystemOutputCount++;
            return Add(new FakeSession(1000, _emitSystemAudioOnStart));
        }

        public IWindowsPcmCaptureSession CreateProcess(int processId, PcmAudioFormat format)
        {
            ProcessIds.Add(processId);
            return Add(new FakeSession((short)(processId == 42 ? 1000 : 2000)));
        }

        private FakeSession Add(FakeSession session)
        {
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession(short sample, bool emitAudioOnStart = true) : IWindowsPcmCaptureSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataAvailable;
        public event Action<Exception>? Failed;
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (emitAudioOnStart)
                Emit(sample);
            return Task.CompletedTask;
        }

        public void Emit(short value)
        {
            var frame = new byte[640];
            for (var offset = 0; offset < frame.Length; offset += 2)
            {
                frame[offset] = (byte)value;
                frame[offset + 1] = (byte)(value >> 8);
            }
            DataAvailable?.Invoke(frame);
        }

        public void Fail(Exception exception) => Failed?.Invoke(exception);

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
