using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyChat.Infrastructure.Windows.Speech;

[SupportedOSPlatform("windows")]
internal sealed class WindowsMicrophoneAudioCaptureSession : IWindowsPcmCaptureSession
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private readonly string _deviceId;
    private readonly object _conversionLock = new();
    private readonly PcmAudioFormat _targetFormat;
    private readonly byte[] _convertedBuffer = new byte[8192];
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _inputBuffer;
    private IWaveProvider? _converter;
    private bool _stopping;

    public WindowsMicrophoneAudioCaptureSession(
        string deviceId,
        PcmAudioFormat targetFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _deviceId = deviceId;
        _targetFormat = targetFormat;
    }

    public event Action<ReadOnlyMemory<byte>>? DataAvailable;
    public event Action<Exception>? Failed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_capture is not null)
            return Task.CompletedTask;

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(_deviceId);
        var capture = new WasapiCapture(device);
        try
        {
            var inputBuffer = new BufferedWaveProvider(NormalizeWaveFormat(capture.WaveFormat))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            ISampleProvider samples = inputBuffer.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
                samples = new DownmixToMonoSampleProvider(samples);
            if (samples.WaveFormat.SampleRate != _targetFormat.SampleRateHz)
                samples = new WdlResamplingSampleProvider(samples, _targetFormat.SampleRateHz);

            _device = device;
            _inputBuffer = inputBuffer;
            _converter = new SampleToWaveProvider16(samples);
            _capture = capture;
            _stopping = false;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            return Task.CompletedTask;
        }
        catch
        {
            capture.Dispose();
            device.Dispose();
            _capture = null;
            _device = null;
            _inputBuffer = null;
            _converter = null;
            throw;
        }
    }

    public Task StopAsync()
    {
        if (_capture is null)
            return Task.CompletedTask;
        _stopping = true;
        _capture.StopRecording();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
        _device?.Dispose();
        _device = null;
        _inputBuffer = null;
        _converter = null;
        return ValueTask.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_conversionLock)
        {
            if (_inputBuffer is null || _converter is null)
                return;
            _inputBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            int read;
            while ((read = _converter.Read(_convertedBuffer, 0, _convertedBuffer.Length)) > 0)
                DataAvailable?.Invoke(_convertedBuffer.AsMemory(0, read).ToArray());
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (_stopping)
            return;
        Failed?.Invoke(args.Exception ?? new InvalidOperationException(
            "Windows microphone capture stopped unexpectedly."));
    }

    private static WaveFormat NormalizeWaveFormat(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible)
            return format;
        if (extensible.SubFormat == FloatSubFormat)
            return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
        if (extensible.SubFormat == PcmSubFormat)
            return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        throw new NotSupportedException($"Unsupported microphone format: {format}.");
    }

    private sealed class DownmixToMonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _sourceBuffer = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var requested = count * channels;
            if (_sourceBuffer.Length < requested)
                _sourceBuffer = new float[requested];

            var samplesRead = source.Read(_sourceBuffer, 0, requested);
            var framesRead = samplesRead / channels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                    sum += _sourceBuffer[(frame * channels) + channel];
                buffer[offset + frame] = sum / channels;
            }
            return framesRead;
        }
    }
}
