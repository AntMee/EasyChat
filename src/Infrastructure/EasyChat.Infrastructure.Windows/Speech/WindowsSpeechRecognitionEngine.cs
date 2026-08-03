using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Speech;

[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechRecognitionEngine : ISpeechRecognitionEngine, IDisposable
{
    private readonly IWindowsAsrBackend _backend;
    private readonly IWindowsAsrWorker _worker;
    private readonly string _modelsDirectory;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private WindowsAsrCallback? _callback;
    private bool _disposed;

    public WindowsSpeechRecognitionEngine()
        : this(
            new NativeWindowsAsrBackend(),
            new WindowsAsrWorker(),
            Path.Combine(AppContext.BaseDirectory, "Lib"))
    {
    }

    internal WindowsSpeechRecognitionEngine(
        IWindowsAsrBackend backend,
        IWindowsAsrWorker worker,
        string modelsDirectory)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
        SpeechRecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var events = Channel.CreateUnbounded<SpeechRecognitionEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _callback = (type, text) => Publish(events.Writer, type, text);

        Exception? startFailure = null;
        try
        {
            await _worker.InvokeAsync(
                () => Start(options),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            startFailure = exception;
        }

        if (startFailure is not null)
        {
            try
            {
                await _worker.InvokeAsync(_backend.Cleanup, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            _sessionGate.Release();
            yield return new SpeechRecognitionEvent(
                SpeechRecognitionEventKind.Error,
                startFailure.Message);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
            yield break;
        }

        yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            try
            {
                await _worker.InvokeAsync(_backend.Cleanup, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _callback = null;
                _sessionGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _worker.Dispose();
        _sessionGate.Dispose();
    }

    private void Start(SpeechRecognitionOptions options)
    {
        var modelPath = Path.Combine(_modelsDirectory, options.ModelPath);
        if (!_backend.Initialize(modelPath))
            throw new InvalidOperationException("ASR initialization failed.");
        _backend.SetCallback(_callback!);
        var processIds = ResolveProcessIds(options.Sources);
        _backend.StartLoopbackCapture(processIds);
        _backend.StartRecognition();
    }

    private static int[] ResolveProcessIds(IReadOnlyList<AudioCaptureSourceToken> sources)
    {
        if (sources.Count == 0 || sources.Contains(WindowsAudioCaptureSourceCatalog.SystemOutputToken))
            return [0];

        var processIds = new int[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            if (!WindowsAudioCaptureSourceCatalog.TryGetProcessId(sources[index], out processIds[index]))
            {
                throw new ArgumentException(
                    $"Audio source '{sources[index].Value}' is not supported by the Windows adapter.",
                    nameof(sources));
            }
        }

        return processIds;
    }

    private static void Publish(ChannelWriter<SpeechRecognitionEvent> writer, int type, string text)
    {
        var item = type switch
        {
            0 => new SpeechRecognitionEvent(SpeechRecognitionEventKind.Final, text),
            1 => new SpeechRecognitionEvent(SpeechRecognitionEventKind.Partial, text),
            2 => new SpeechRecognitionEvent(SpeechRecognitionEventKind.Error, text),
            3 => new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped),
            _ => null
        };
        if (item is null)
            return;
        writer.TryWrite(item);
        if (type == 3)
            writer.TryComplete();
    }
}
