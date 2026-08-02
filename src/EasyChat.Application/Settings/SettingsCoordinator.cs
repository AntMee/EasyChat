using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Settings.Persistence;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Settings;

public sealed class SettingsCoordinator : ISettingsUseCases
{
    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsPersistenceGateway _persistence;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _saveDelay;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _initializer = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Dictionary<SettingsSection, PendingSave> _pending = [];
    private SettingsBundle? _current;
    private long _version;
    private int _disposeState;

    public SettingsCoordinator(
        ISettingsPersistenceGateway persistence,
        TimeProvider? timeProvider = null,
        TimeSpan? saveDelay = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _saveDelay = saveDelay ?? DefaultSaveDelay;
        if (_saveDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(saveDelay));
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    public bool IsInitialized
    {
        get { lock (_stateLock) return _current is not null; }
    }

    public SettingsBundle Current
    {
        get { lock (_stateLock) return _current ?? throw new InvalidOperationException("Settings are not initialized."); }
    }

    public async ValueTask<Result<SettingsBundle>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _initializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_stateLock)
            {
                if (_current is not null)
                    return Result<SettingsBundle>.Success(_current);
            }

            var read = await _persistence.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (read.IsFailure)
                return read;

            var original = read.Value;
            var migratedGeneral = original.General.NativeLanguage is null;
            var current = original with
            {
                General = migratedGeneral
                    ? original.General with { NativeLanguage = original.General.TargetLanguage }
                    : original.General,
                TextAssist = original.TextAssist with { FollowGlobal = false }
            };

            var machineWrite = await _persistence.WriteAsync(
                SettingsSection.MachineTranslation,
                current,
                cancellationToken).ConfigureAwait(false);
            if (machineWrite.IsFailure)
                return Result<SettingsBundle>.Failure(machineWrite.Error);

            if (migratedGeneral)
            {
                var generalWrite = await _persistence.WriteAsync(
                    SettingsSection.General,
                    current,
                    cancellationToken).ConfigureAwait(false);
                if (generalWrite.IsFailure)
                    return Result<SettingsBundle>.Failure(generalWrite.Error);
            }

            lock (_stateLock)
                _current = current;

            return Result<SettingsBundle>.Success(current);
        }
        finally
        {
            _initializer.Release();
        }
    }

    public Result Update(SettingsSection section, SettingsBundle settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();

        PendingSave pending;
        long version;
        lock (_stateLock)
        {
            if (_current is null)
                return Result.Failure(new Error("settings.not-initialized", "Settings are not initialized."));

            _current = settings;
            version = ++_version;
            if (_pending.Remove(section, out var previous))
            {
                previous.Cancellation.Cancel();
                DisposeCancellationAfterCompletion(previous);
            }

            var delayCancellation = new CancellationTokenSource();
            pending = new PendingSave(version, delayCancellation);
            _pending[section] = pending;
            pending.BackgroundSave = PersistAfterQuietPeriodAsync(
                section,
                version,
                delayCancellation.Token);
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section, settings));
        return Result.Success();
    }

    public async ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            return;

        await _initializer.WaitAsync().ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                foreach (var pending in _pending.Values)
                    pending.Cancellation.Dispose();
                _pending.Clear();
            }

            _initializer.Release();
            _initializer.Dispose();
            _writer.Dispose();
            Volatile.Write(ref _disposeState, 2);
        }
    }

    private async ValueTask<Result> FlushCoreAsync(CancellationToken cancellationToken)
    {
        SettingsSection[] sections;
        Task[] backgroundSaves;
        lock (_stateLock)
        {
            sections = _pending.Keys.ToArray();
            foreach (var pending in _pending.Values)
                pending.Cancellation.Cancel();
            backgroundSaves = _pending.Values
                .Select(pending => pending.BackgroundSave)
                .ToArray();
        }

        await Task.WhenAll(backgroundSaves).WaitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var section in sections)
        {
            long version;
            lock (_stateLock)
            {
                if (!_pending.TryGetValue(section, out var pending))
                    continue;
                version = pending.Version;
            }

            var write = await PersistAsync(section, version, cancellationToken).ConfigureAwait(false);
            if (write.IsFailure)
                return write;
        }

        return Result.Success();
    }

    private async Task PersistAfterQuietPeriodAsync(SettingsSection section, long version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_saveDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            await PersistAsync(section, version, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<Result> PersistAsync(SettingsSection section, long version, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SettingsBundle snapshot;
            lock (_stateLock)
            {
                if (!_pending.TryGetValue(section, out var pending) || pending.Version != version)
                    return Result.Success();
                snapshot = _current!;
            }

            var result = await _persistence.WriteAsync(section, snapshot, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(section, result.Error));
                return result;
            }

            lock (_stateLock)
            {
                if (_pending.TryGetValue(section, out var pending) && pending.Version == version)
                {
                    _pending.Remove(section);
                    pending.Cancellation.Dispose();
                }
            }
            return Result.Success();
        }
        finally
        {
            _writer.Release();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private static void DisposeCancellationAfterCompletion(PendingSave pending)
    {
        _ = pending.BackgroundSave.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            pending.Cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class PendingSave(long version, CancellationTokenSource cancellation)
    {
        public long Version { get; } = version;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task BackgroundSave { get; set; } = Task.CompletedTask;
    }
}
