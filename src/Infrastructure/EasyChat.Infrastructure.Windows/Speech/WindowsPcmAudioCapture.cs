using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Speech;

internal interface IWindowsPcmCaptureSession : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? DataAvailable;
    event Action<Exception>? Failed;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

internal interface IWindowsPcmCaptureSessionFactory
{
    IWindowsPcmCaptureSession CreateSystemOutput(PcmAudioFormat format);
    IWindowsPcmCaptureSession CreateProcess(int processId, PcmAudioFormat format);
}

internal sealed class WindowsPcmCaptureSessionFactory : IWindowsPcmCaptureSessionFactory
{
    public IWindowsPcmCaptureSession CreateSystemOutput(PcmAudioFormat format) =>
        new WindowsSystemAudioCaptureSession(format);

    public IWindowsPcmCaptureSession CreateProcess(int processId, PcmAudioFormat format) =>
        new WindowsProcessAudioCaptureSession(processId, format);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPcmAudioCapture : IPcmAudioCapture
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    private readonly IWindowsPcmCaptureSessionFactory _sessions;

    public WindowsPcmAudioCapture()
        : this(new WindowsPcmCaptureSessionFactory())
    {
    }

    internal WindowsPcmAudioCapture(IWindowsPcmCaptureSessionFactory sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        IReadOnlyList<AudioCaptureSourceToken> sources,
        PcmAudioFormat format,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ValidateFormat(format);

        var captures = CreateSessions(sources, format);
        var buffers = captures.Select(_ => new PcmByteBuffer(format.SampleRateHz * 4)).ToArray();
        var failures = new Queue<Exception>();
        var sync = new object();
        var dataHandlers = new Action<ReadOnlyMemory<byte>>[captures.Count];
        var failureHandlers = new Action<Exception>[captures.Count];

        for (var index = 0; index < captures.Count; index++)
        {
            var captureIndex = index;
            dataHandlers[index] = pcm => buffers[captureIndex].Write(pcm.Span);
            failureHandlers[index] = exception =>
            {
                lock (sync)
                    failures.Enqueue(exception);
            };
            captures[index].DataAvailable += dataHandlers[index];
            captures[index].Failed += failureHandlers[index];
        }

        try
        {
            foreach (var capture in captures)
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(FrameDuration);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Exception? failure = null;
                lock (sync)
                {
                    if (failures.Count > 0)
                        failure = failures.Dequeue();
                }
                if (failure is not null)
                    throw new InvalidOperationException("Windows audio capture failed.", failure);

                var mixed = MixFrame(buffers, format);
                if (!mixed.IsEmpty)
                    yield return mixed;
            }
        }
        finally
        {
            for (var index = captures.Count - 1; index >= 0; index--)
            {
                captures[index].DataAvailable -= dataHandlers[index];
                captures[index].Failed -= failureHandlers[index];
                try
                {
                    await captures[index].StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    await captures[index].DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private IReadOnlyList<IWindowsPcmCaptureSession> CreateSessions(
        IReadOnlyList<AudioCaptureSourceToken> sources,
        PcmAudioFormat format)
    {
        if (sources.Count == 0 || sources.Contains(WindowsAudioCaptureSourceCatalog.SystemOutputToken))
            return [_sessions.CreateSystemOutput(format)];

        var result = new List<IWindowsPcmCaptureSession>(sources.Count);
        foreach (var source in sources.Distinct())
        {
            if (!WindowsAudioCaptureSourceCatalog.TryGetProcessId(source, out var processId))
            {
                throw new ArgumentException(
                    $"Audio source '{source.Value}' is not supported by the Windows adapter.",
                    nameof(sources));
            }
            result.Add(_sessions.CreateProcess(processId, format));
        }
        return result;
    }

    private static ReadOnlyMemory<byte> MixFrame(
        IReadOnlyList<PcmByteBuffer> sources,
        PcmAudioFormat format)
    {
        var bytesPerFrame = format.SampleRateHz * format.ChannelCount *
                            (format.BitsPerSample / 8) * (int)FrameDuration.TotalMilliseconds / 1000;
        var output = new byte[bytesPerFrame];
        var sourceFrame = new byte[bytesPerFrame];
        var sums = new int[bytesPerFrame / 2];
        var hasAudio = false;

        foreach (var source in sources)
        {
            Array.Clear(sourceFrame);
            var read = source.Read(sourceFrame);
            if (read == 0)
                continue;
            hasAudio = true;
            for (var offset = 0; offset + 1 < read; offset += 2)
                sums[offset / 2] += BitConverter.ToInt16(sourceFrame, offset);
        }

        if (!hasAudio)
            return ReadOnlyMemory<byte>.Empty;

        for (var sample = 0; sample < sums.Length; sample++)
        {
            var value = (short)Math.Clamp(sums[sample], short.MinValue, short.MaxValue);
            output[sample * 2] = (byte)value;
            output[(sample * 2) + 1] = (byte)(value >> 8);
        }
        return output;
    }

    private static void ValidateFormat(PcmAudioFormat format)
    {
        if (format != PcmAudioFormat.SpeechRecognition)
        {
            throw new NotSupportedException(
                "Windows speech capture currently supports 16 kHz mono PCM16 only.");
        }
    }

    internal sealed class PcmByteBuffer(int capacity)
    {
        private readonly object _sync = new();
        private readonly Queue<byte[]> _chunks = new();
        private readonly int _capacity = capacity;
        private int _headOffset;
        private int _length;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;
            lock (_sync)
            {
                var chunk = data.ToArray();
                _chunks.Enqueue(chunk);
                _length += chunk.Length;
                while (_length > _capacity && _chunks.Count > 0)
                {
                    var removed = _chunks.Dequeue();
                    _length -= removed.Length - _headOffset;
                    _headOffset = 0;
                }
            }
        }

        public int Read(Span<byte> destination)
        {
            lock (_sync)
            {
                var written = 0;
                while (written < destination.Length && _chunks.TryPeek(out var chunk))
                {
                    var available = chunk.Length - _headOffset;
                    var count = Math.Min(available, destination.Length - written);
                    chunk.AsSpan(_headOffset, count).CopyTo(destination[written..]);
                    written += count;
                    _headOffset += count;
                    _length -= count;
                    if (_headOffset == chunk.Length)
                    {
                        _chunks.Dequeue();
                        _headOffset = 0;
                    }
                }
                return written;
            }
        }
    }
}
