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
    private readonly ConcurrentQueue<QueuedPlayback> _queue = new();
    private readonly ILogger<WindowsSoundFlowAudioPlaybackQueue> _logger;
    private AudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private CancellationTokenSource? _currentPlayback;
    private Task? _runner;
    private bool _disposed;
    private AudioPlaybackTarget _deviceTarget;

    public WindowsSoundFlowAudioPlaybackQueue(
        ILogger<WindowsSoundFlowAudioPlaybackQueue> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask EnqueueAsync(
        AudioTrack track,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(track, AudioPlaybackTarget.Default, cancellationToken);

    public ValueTask EnqueueAsync(
        AudioTrack track,
        AudioPlaybackTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue(new QueuedPlayback(track, target));
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
            QueuedPlayback queued;
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed || !_queue.TryDequeue(out queued))
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
                await PlayAsync(queued.Track, queued.Target, token).ConfigureAwait(false);
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

    private async Task PlayAsync(
        AudioTrack track,
        AudioPlaybackTarget target,
        CancellationToken cancellationToken)
    {
        EnsureInitialized(target);
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

    private void EnsureInitialized(AudioPlaybackTarget target)
    {
        if (_engine is not null && _device is not null && _deviceTarget == target)
            return;

        _engine ??= new MiniAudioEngine();
        _engine.UpdateAudioDevicesInfo();
        var devices = _engine.PlaybackDevices;
        var selected = target == AudioPlaybackTarget.VirtualCable
            ? devices.FirstOrDefault(device =>
                WindowsAudioPlaybackDeviceCatalog.IsVirtualCableName(device.Name))
            : devices.FirstOrDefault(device => device.IsDefault);
        var hasSelected = target == AudioPlaybackTarget.VirtualCable
            ? devices.Any(device => WindowsAudioPlaybackDeviceCatalog.IsVirtualCableName(device.Name))
            : devices.Any(device => device.IsDefault);
        if (!hasSelected)
        {
            throw new InvalidOperationException(target == AudioPlaybackTarget.VirtualCable
                ? "VB-Audio Cable playback device 'CABLE Input' is not available."
                : "No default audio playback device is available.");
        }

        _device?.Stop();
        _device?.Dispose();
        _device = null;
        try
        {
            _device = _engine.InitializePlaybackDevice(selected, AudioFormat.DvdHq);
            _device.Start();
            _deviceTarget = target;
        }
        catch
        {
            _device?.Dispose();
            _device = null;
            throw;
        }
    }

    private readonly record struct QueuedPlayback(AudioTrack Track, AudioPlaybackTarget Target);
}
