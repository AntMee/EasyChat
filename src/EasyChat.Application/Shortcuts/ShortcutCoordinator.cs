using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shortcuts;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Shortcuts;

public sealed class ShortcutCoordinator : IShortcutUseCases
{
    private readonly ISettingsUseCases _settings;
    private readonly IPlatformAccessUseCases _platformAccess;
    private readonly IGlobalHotkeys _hotkeys;
    private readonly IReadOnlyDictionary<string, ActionState> _actions;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly List<IHotkeyRegistration> _registrations = [];
    private int _started;
    private int _disposed;

    public ShortcutCoordinator(
        ISettingsUseCases settings,
        IPlatformAccessUseCases platformAccess,
        IGlobalHotkeys hotkeys,
        IEnumerable<IShortcutAction> actions)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _platformAccess = platformAccess ?? throw new ArgumentNullException(nameof(platformAccess));
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions.ToDictionary(
            action => action.ActionType,
            action => new ActionState(action),
            StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask<ShortcutRegistrationReport> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            _settings.SettingsChanged += OnSettingsChanged;

        return await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ShortcutRegistrationReport> ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeRegistrations();
            var entries = _settings.Current.Shortcut.Entries
                .Where(entry => entry.IsEnabled && !string.IsNullOrWhiteSpace(entry.KeyCombination))
                .ToArray();
            var issues = new List<ShortcutRegistrationIssue>();

            var access = await _platformAccess.EnsureAvailableAsync(
                PlatformCapability.GlobalHotkeys,
                cancellationToken).ConfigureAwait(false);
            if (access.IsFailure)
            {
                issues.Add(new ShortcutRegistrationIssue(
                    "GlobalHotkeys",
                    string.Empty,
                    access.Error));
                return new ShortcutRegistrationReport(entries.Length, 0, issues);
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_actions.TryGetValue(entry.ActionType, out var action))
                {
                    issues.Add(new ShortcutRegistrationIssue(
                        entry.ActionType,
                        entry.KeyCombination,
                        new Error(
                            "shortcut.action-unavailable",
                            $"No shortcut action is registered for '{entry.ActionType}'.")));
                    continue;
                }

                var gesture = ShortcutGestureParser.Parse(entry.KeyCombination);
                if (gesture.IsFailure)
                {
                    issues.Add(new ShortcutRegistrationIssue(
                        entry.ActionType,
                        entry.KeyCombination,
                        gesture.Error));
                    continue;
                }

                var parameter = entry.Parameter;
                var registration = await _hotkeys.RegisterAsync(
                    gesture.Value,
                    token => action.ExecuteAsync(parameter, token),
                    cancellationToken).ConfigureAwait(false);
                if (registration.IsFailure)
                {
                    issues.Add(new ShortcutRegistrationIssue(
                        entry.ActionType,
                        entry.KeyCombination,
                        registration.Error));
                    continue;
                }

                _registrations.Add(registration.Value);
            }

            return new ShortcutRegistrationReport(
                entries.Length,
                _registrations.Count,
                issues);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public async ValueTask<Result> ProbeAsync(
        string keyCombination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var access = await _platformAccess.EnsureAvailableAsync(
            PlatformCapability.GlobalHotkeys,
            cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
            return Result.Failure(access.Error);

        var gesture = ShortcutGestureParser.Parse(keyCombination);
        return gesture.IsFailure
            ? Result.Failure(gesture.Error)
            : await _hotkeys.ProbeAsync(gesture.Value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Interlocked.Exchange(ref _started, 0) != 0)
            _settings.SettingsChanged -= OnSettingsChanged;

        await _reloadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeRegistrations();
        }
        finally
        {
            _reloadLock.Release();
            _reloadLock.Dispose();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs eventArgs)
    {
        if (eventArgs.Section == SettingsSection.Shortcut)
            _ = ReloadAfterSettingsChangeAsync();
    }

    private async Task ReloadAfterSettingsChangeAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeRegistrations()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ActionState(IShortcutAction action)
    {
        private int _executing;

        public async ValueTask ExecuteAsync(
            ShortcutParameterSettings? parameter,
            CancellationToken cancellationToken)
        {
            if (action.PreventConcurrentExecution &&
                Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await action.ExecuteAsync(parameter, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (action.PreventConcurrentExecution)
                    Volatile.Write(ref _executing, 0);
            }
        }
    }
}
