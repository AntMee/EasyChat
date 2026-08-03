using System.Collections.Concurrent;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using Microsoft.Extensions.Logging;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace EasyChat.Infrastructure.Windows.Audio;

[SupportedOSPlatform("windows")]
public sealed class WindowsSoundFlowAudioPlaybackQueue : IAudioPlaybackQueue, IDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentQueue<AudioTrack> _queue = new();
    private readonly ILogger<WindowsSoundFlowAudioPlaybackQueue> _logger;
    private AudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private CancellationTokenSource? _currentPlayback;
    private Task? _runner;
    private bool _disposed;

    public WindowsSoundFlowAudioPlaybackQueue(
        ILogger<WindowsSoundFlowAudioPlaybackQueue> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask EnqueueAsync(
        AudioTrack track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue(track);
            if (_runner is null || _runner.IsCompleted)
                _runner = Task.Run(RunAsync);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            while (_queue.TryDequeue(out _))
            {
            }
            _currentPlayback?.Cancel();
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Task? runner;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_queue.TryDequeue(out _))
            {
            }
            _currentPlayback?.Cancel();
            runner = _runner;
        }

        try
        {
            runner?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _device?.Stop();
        _device?.Dispose();
        _engine?.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            AudioTrack track;
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed || !_queue.TryDequeue(out track!))
                {
                    _runner = null;
                    return;
                }
                _currentPlayback?.Dispose();
                _currentPlayback = new CancellationTokenSource();
                token = _currentPlayback.Token;
            }

            try
            {
                await PlayAsync(track, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error playing queued audio.");
            }
        }
    }

    private async Task PlayAsync(AudioTrack track, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (_engine is null || _device is null)
            return;
        using var stream = new MemoryStream(track.Content.ToArray(), writable: false);
        using var provider = new StreamDataProvider(_engine, _device.Format, stream);
        using var player = new SoundPlayer(_engine, _device.Format, provider);
        _device.MasterMixer.AddComponent(player);
        try
        {
            player.Play();
            while (player.State == PlaybackState.Playing)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            player.Stop();
            _device.MasterMixer.RemoveComponent(player);
        }
    }

    private void EnsureInitialized()
    {
        if (_engine is not null && _device is not null)
            return;
        try
        {
            _engine = new MiniAudioEngine();
            _engine.UpdateAudioDevicesInfo();
            var defaultDevice = _engine.PlaybackDevices.FirstOrDefault(device => device.IsDefault);
            _device = _engine.InitializePlaybackDevice(defaultDevice, AudioFormat.DvdHq);
            _device.Start();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Exception during SoundFlow initialization.");
            _device?.Dispose();
            _engine?.Dispose();
            _device = null;
            _engine = null;
        }
    }
}
