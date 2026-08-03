#pragma warning disable CS0618

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace EasyChat.Infrastructure.Windows.Speech;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessAudioCaptureSession(
    int processId,
    PcmAudioFormat format) : IWindowsPcmCaptureSession
{
    private static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private readonly EventWaitHandle _audioReady = new(false, EventResetMode.AutoReset);
    private readonly CancellationTokenSource _stopping = new();
    private IAudioClient? _audioClient;
    private IAudioCaptureClientNative? _captureClient;
    private Task? _captureTask;
    private bool _stopped;
    private bool _disposed;

    public event Action<ReadOnlyMemory<byte>>? DataAvailable;
    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioClient is not null)
            return;

        var audioClient = await ProcessLoopbackAudioClientActivator.ActivateAsync(
            processId,
            cancellationToken).ConfigureAwait(false);
        object? captureObject = null;
        try
        {
            var waveFormat = new WaveFormat(
                format.SampleRateHz,
                format.BitsPerSample,
                format.ChannelCount);
            var sessionId = Guid.Empty;
            var initializeResult = audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality,
                0,
                0,
                waveFormat,
                ref sessionId);
            Marshal.ThrowExceptionForHR(initializeResult);
            audioClient.SetEventHandle(_audioReady.SafeWaitHandle.DangerousGetHandle());

            var serviceResult = audioClient.GetService(CaptureClientId, out captureObject);
            Marshal.ThrowExceptionForHR(serviceResult);
            var captureClient = (IAudioCaptureClientNative)captureObject;

            _audioClient = audioClient;
            _captureClient = captureClient;
            _stopped = false;
            audioClient.Start();
            _captureTask = Task.Run(() => CaptureLoop(captureClient, _stopping.Token));
        }
        catch
        {
            ReleaseComObject(captureObject);
            ReleaseComObject(audioClient);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_audioClient is null || _stopped)
            return;

        _stopped = true;
        _stopping.Cancel();
        _audioReady.Set();
        if (_captureTask is not null)
        {
            try
            {
                await _captureTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }
        _audioClient.Stop();
        _captureTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        ReleaseComObject(_captureClient);
        ReleaseComObject(_audioClient);
        _captureClient = null;
        _audioClient = null;
        _audioReady.Dispose();
        _stopping.Dispose();
    }

    private void CaptureLoop(
        IAudioCaptureClientNative captureClient,
        CancellationToken cancellationToken)
    {
        var waits = new[] { _audioReady, cancellationToken.WaitHandle };
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits) != 0)
                    break;
                ReadAvailablePackets(captureClient);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Failed?.Invoke(exception);
        }
    }

    private void ReadAvailablePackets(IAudioCaptureClientNative captureClient)
    {
        ThrowForHResult(captureClient.GetNextPacketSize(out var packetFrames));
        while (packetFrames > 0)
        {
            IntPtr buffer = IntPtr.Zero;
            var frames = 0;
            try
            {
                ThrowForHResult(captureClient.GetBuffer(
                    out buffer,
                    out frames,
                    out var flags,
                    out _,
                    out _));
                var bytes = checked(frames * format.ChannelCount * (format.BitsPerSample / 8));
                var pcm = new byte[bytes];
                if ((flags & AudioClientBufferFlags.Silent) == 0 && bytes > 0)
                    Marshal.Copy(buffer, pcm, 0, bytes);
                if (pcm.Length > 0)
                    DataAvailable?.Invoke(pcm);
            }
            finally
            {
                if (frames > 0)
                    ThrowForHResult(captureClient.ReleaseBuffer(frames));
            }
            ThrowForHResult(captureClient.GetNextPacketSize(out packetFrames));
        }
    }

    private static void ThrowForHResult(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientNative
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr dataBuffer,
            out int framesToRead,
            out AudioClientBufferFlags bufferFlags,
            out long devicePosition,
            out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int framesRead);

        [PreserveSig]
        int GetNextPacketSize(out int framesInNextPacket);
    }
}

[SupportedOSPlatform("windows")]
internal static class ProcessLoopbackAudioClientActivator
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    public static async Task<IAudioClient> ActivateAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        cancellationToken.ThrowIfCancellationRequested();

        var activation = new AudioClientActivationParameters
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
            {
                TargetProcessId = (uint)processId,
                Mode = ProcessLoopbackMode.IncludeTargetProcessTree
            }
        };
        var activationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParameters>());
        var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(activation, activationPointer, false);
            var variant = new PropVariant
            {
                vt = (short)VarEnum.VT_BLOB,
                blobVal = new Blob
                {
                    Length = Marshal.SizeOf<AudioClientActivationParameters>(),
                    Data = activationPointer
                }
            };
            Marshal.StructureToPtr(variant, variantPointer, false);

            var completion = new AudioClientActivationCompletionHandler();
            ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice,
                AudioClientId,
                variantPointer,
                completion,
                out var operation);
            var audioClient = await completion.Completion.ConfigureAwait(false);
            GC.KeepAlive(operation);
            if (cancellationToken.IsCancellationRequested)
            {
                if (Marshal.IsComObject(audioClient))
                    Marshal.FinalReleaseComObject(audioClient);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return audioClient;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPointer);
            Marshal.FreeHGlobal(activationPointer);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [In] IntPtr activationParameters,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParameters
    {
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParameters
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode Mode;
    }

    private enum AudioClientActivationType
    {
        Default,
        ProcessLoopback
    }

    private enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree,
        ExcludeTargetProcessTree
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioClientActivationCompletionHandler :
        IActivateAudioInterfaceCompletionHandler,
        IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Completion => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var result, out var activated);
                Marshal.ThrowExceptionForHR(result);
                _completion.TrySetResult((IAudioClient)activated);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
