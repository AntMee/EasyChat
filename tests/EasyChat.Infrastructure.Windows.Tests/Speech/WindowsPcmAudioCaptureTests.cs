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

    private sealed class FakeSessionFactory : IWindowsPcmCaptureSessionFactory
    {
        public List<int> ProcessIds { get; } = [];
        public List<FakeSession> Sessions { get; } = [];
        public int SystemOutputCount { get; private set; }

        public IWindowsPcmCaptureSession CreateSystemOutput(PcmAudioFormat format)
        {
            SystemOutputCount++;
            return Add(new FakeSession(1000));
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

    private sealed class FakeSession(short sample) : IWindowsPcmCaptureSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataAvailable;
        public event Action<Exception>? Failed
        {
            add { }
            remove { }
        }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var frame = new byte[640];
            for (var offset = 0; offset < frame.Length; offset += 2)
            {
                frame[offset] = (byte)sample;
                frame[offset + 1] = (byte)(sample >> 8);
            }
            DataAvailable?.Invoke(frame);
            return Task.CompletedTask;
        }

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
