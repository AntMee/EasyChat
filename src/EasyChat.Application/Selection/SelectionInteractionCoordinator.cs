using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Selection;
using EasyChat.Contracts.Settings;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging;

namespace EasyChat.Application.Selection;

public sealed class SelectionInteractionCoordinator : ISelectionInteractionUseCases
{
    private const int DragThreshold = 5;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DragCaptureDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DoubleClickCaptureDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DoubleClickGuard = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsUseCases _settings;
    private readonly IPlatformAccessUseCases _platformAccess;
    private readonly IGlobalPointerMonitor _pointerMonitor;
    private readonly IWindowFocus _windowFocus;
    private readonly IClipboardSnapshots _clipboardSnapshots;
    private readonly ISelectedTextUseCases _selectedText;
    private readonly IRunningProcessCatalog _runningProcesses;
    private readonly ISelectionDelay _delay;
    private readonly ILogger<SelectionInteractionCoordinator> _logger;
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly object _tasksSync = new();
    private readonly object _processCacheSync = new();
    private readonly Dictionary<ExternalTargetToken, string?> _processIdentifierCache = [];
    private readonly HashSet<Task> _pendingTasks = [];

    private CancellationTokenSource? _lifetime;
    private IPointerMonitorRegistration? _registration;
    private ISelectionInteractionSink? _sink;
    private PhysicalScreenPoint? _downPoint;
    private ExternalTargetToken _foregroundAtMouseDown;
    private ExternalTargetToken _focusedAtMouseDown;
    private ExternalTargetToken _pointerTargetAtMouseDown;
    private ExternalTargetToken _capturedTargetAtMouseDown;
    private uint _clipboardSequenceAtMouseDown;
    private DateTimeOffset _lastDoubleClickTime;
    private PhysicalScreenPoint _lastDoubleClickPoint;
    private DateTimeOffset _lastBlockedGestureTime;
    private PhysicalScreenPoint _lastBlockedGesturePoint;
    private long _generation;
    private bool _disposed;
    private Task? _disposeTask;

    public SelectionInteractionCoordinator(
        ISettingsUseCases settings,
        IPlatformAccessUseCases platformAccess,
        IGlobalPointerMonitor pointerMonitor,
        IWindowFocus windowFocus,
        IClipboardSnapshots clipboardSnapshots,
        ISelectedTextUseCases selectedText,
        IRunningProcessCatalog runningProcesses,
        ILogger<SelectionInteractionCoordinator> logger)
        : this(settings, platformAccess, pointerMonitor, windowFocus, clipboardSnapshots, selectedText,
            runningProcesses, new SystemSelectionDelay(), logger)
    {
    }

    internal SelectionInteractionCoordinator(
        ISettingsUseCases settings,
        IPlatformAccessUseCases platformAccess,
        IGlobalPointerMonitor pointerMonitor,
        IWindowFocus windowFocus,
        IClipboardSnapshots clipboardSnapshots,
        ISelectedTextUseCases selectedText,
        IRunningProcessCatalog runningProcesses,
        ISelectionDelay delay,
        ILogger<SelectionInteractionCoordinator> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _platformAccess = platformAccess ?? throw new ArgumentNullException(nameof(platformAccess));
        _pointerMonitor = pointerMonitor ?? throw new ArgumentNullException(nameof(pointerMonitor));
        _windowFocus = windowFocus ?? throw new ArgumentNullException(nameof(windowFocus));
        _clipboardSnapshots = clipboardSnapshots ?? throw new ArgumentNullException(nameof(clipboardSnapshots));
        _selectedText = selectedText ?? throw new ArgumentNullException(nameof(selectedText));
        _runningProcesses = runningProcesses ?? throw new ArgumentNullException(nameof(runningProcesses));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start(ISelectionInteractionSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sink is not null)
                throw new InvalidOperationException("Selection interaction monitoring is already started.");

            _sink = sink;
            _lifetime = new CancellationTokenSource();
            _settings.SettingsChanged += OnSettingsChanged;
            Track(StartAfterInitialDelayAsync(_lifetime.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (_lifecycle)
        {
            if (_sink is null)
                return;

            _settings.SettingsChanged -= OnSettingsChanged;
            _registration?.Dispose();
            _registration = null;
            _sink = null;
            lifetime = _lifetime;
            _lifetime = null;
            Interlocked.Increment(ref _generation);
            _downPoint = null;
            _processIdentifierCache.Clear();
        }

        lifetime?.Cancel();
        lifetime?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Stop();
        await DrainPendingTasksAsync().ConfigureAwait(false);
        _eventGate.Dispose();
    }

    private async Task StartAfterInitialDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delay.WaitAsync(StartupDelay, cancellationToken).ConfigureAwait(false);
            await EnableMonitoringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to start selection interaction monitoring.");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.Section != SettingsSection.SelectionTranslation)
            return;

        if (args.Current.SelectionTranslation.Enabled)
        {
            Track(EnableMonitoringAsync(_lifetime?.Token ?? CancellationToken.None));
        }
        else
        {
            lock (_lifecycle)
            {
                Interlocked.Increment(ref _generation);
                _downPoint = null;
            }
        }
    }

    private async Task EnableMonitoringAsync(CancellationToken cancellationToken)
    {
        var access = await _platformAccess.EnsureAvailableAsync(
            PlatformCapability.GlobalPointerMonitoring,
            cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
        {
            _logger.LogWarning(
                "Selection monitoring is unavailable: {Message}",
                access.Error.Message);
            return;
        }

        ISelectionInteractionSink? sink;
        lock (_lifecycle)
        {
            if (_sink is null || _registration is not null || cancellationToken.IsCancellationRequested)
                return;
            _registration = _pointerMonitor.Start(pointerEvent => Queue(pointerEvent, cancellationToken));
            sink = _sink;
        }

        Track(NotifyMonitoringStartedAsync(sink, cancellationToken));
    }

    private async Task NotifyMonitoringStartedAsync(
        ISelectionInteractionSink sink,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.OnMonitoringStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Unable to prewarm selection presentation surfaces.");
        }
    }

    private void Queue(GlobalPointerEvent pointerEvent, CancellationToken cancellationToken) =>
        Track(ProcessQueuedAsync(pointerEvent, cancellationToken));

    private async Task ProcessQueuedAsync(
        GlobalPointerEvent pointerEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ProcessAsync(pointerEvent, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _eventGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Selection interaction processing failed.");
        }
    }

    private async Task ProcessAsync(
        GlobalPointerEvent pointerEvent,
        CancellationToken cancellationToken)
    {
        var config = _settings.Current.SelectionTranslation;
        if (!config.Enabled || !await IsTargetAllowedAsync(config, cancellationToken).ConfigureAwait(false))
        {
            if (pointerEvent.Action != PointerAction.PrimaryPressed)
                return;

            var sink = GetSink();
            var surface = await sink.InspectSurfaceAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
            if (!surface.IsPointerOverOwnedSurface)
                await sink.OnExternalPointerPressedAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (pointerEvent.Action)
        {
            case PointerAction.PrimaryPressed:
                await HandlePressedAsync(pointerEvent, config, cancellationToken).ConfigureAwait(false);
                break;
            case PointerAction.PrimaryReleased:
                await HandleReleasedAsync(pointerEvent, config, cancellationToken).ConfigureAwait(false);
                break;
            case PointerAction.PrimaryDoubleClick:
                await HandleDoubleClickAsync(pointerEvent, config, cancellationToken).ConfigureAwait(false);
                break;
            case PointerAction.WindowMoveStarted:
                Interlocked.Increment(ref _generation);
                _downPoint = null;
                break;
        }
    }

    private async Task HandlePressedAsync(
        GlobalPointerEvent pointerEvent,
        SelectionTranslationSettings config,
        CancellationToken cancellationToken)
    {
        if (IsBlockedGestureContinuation(pointerEvent))
        {
            _downPoint = null;
            return;
        }

        _foregroundAtMouseDown = pointerEvent.ForegroundTarget.IsEmpty
            ? await ReadTargetAsync(_windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false)
            : pointerEvent.ForegroundTarget;
        _pointerTargetAtMouseDown = pointerEvent.PointerTarget;
        _capturedTargetAtMouseDown = pointerEvent.CapturedTarget;
        _clipboardSequenceAtMouseDown = pointerEvent.ClipboardSequence;
        _focusedAtMouseDown = await ReadTargetAsync(
            _windowFocus.GetFocusedTargetAsync(cancellationToken)).ConfigureAwait(false);

        var sink = GetSink();
        var surface = await sink.InspectSurfaceAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
        if (surface.IsPointerOverOwnedSurface)
        {
            _downPoint = null;
            return;
        }

        await sink.OnExternalPointerPressedAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
        if (IsForeignPointerTarget(pointerEvent)
            || pointerEvent.PointerTargetIsOverlay
            || (!_pointerTargetAtMouseDown.IsEmpty
                && !pointerEvent.PointerTarget.IsEmpty
                && _pointerTargetAtMouseDown != pointerEvent.PointerTarget)
            || IsForeignCaptureTarget(pointerEvent)
            || (!_capturedTargetAtMouseDown.IsEmpty
                && !pointerEvent.CapturedTarget.IsEmpty
                && _capturedTargetAtMouseDown != pointerEvent.CapturedTarget))
        {
            BlockGesture(pointerEvent);
            Interlocked.Increment(ref _generation);
            _downPoint = null;
            return;
        }
        if (surface.BlocksSelectionCapture)
        {
            BlockGesture(pointerEvent);
            Interlocked.Increment(ref _generation);
            _downPoint = null;
            return;
        }

        if (!HandlesDrag(config.TriggerMode))
        {
            if (!IsGuardedDoubleClick(pointerEvent))
                Interlocked.Increment(ref _generation);
            _downPoint = null;
            return;
        }

        _downPoint = pointerEvent.Position;
        if (IsGuardedDoubleClick(pointerEvent))
            return;

        Interlocked.Increment(ref _generation);
    }

    private async Task HandleReleasedAsync(
        GlobalPointerEvent pointerEvent,
        SelectionTranslationSettings config,
        CancellationToken cancellationToken)
    {
        if (!HandlesDrag(config.TriggerMode) || _downPoint is not { } down)
            return;
        _downPoint = null;

        var sink = GetSink();
        var surface = await sink.InspectSurfaceAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
        if (surface.IsPointerOverOwnedSurface
            || surface.BlocksSelectionCapture
            || Distance(down, pointerEvent.Position) <= DragThreshold)
            return;

        if (HasExternalGestureContextChanged(pointerEvent))
            return;

        var clipboardToken = await CaptureClipboardTokenAsync(cancellationToken).ConfigureAwait(false);
        var foregroundAtRelease = pointerEvent.ForegroundTarget.IsEmpty
            ? await ReadTargetAsync(_windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false)
            : pointerEvent.ForegroundTarget;
        var generation = Interlocked.Read(ref _generation);
        Track(CaptureAfterDelayAsync(
            SelectionGesture.Drag,
            pointerEvent.Position,
            _foregroundAtMouseDown,
            _focusedAtMouseDown,
            foregroundAtRelease,
            clipboardToken,
            generation,
            DragCaptureDelay,
            config,
            cancellationToken));
    }

    private async Task HandleDoubleClickAsync(
        GlobalPointerEvent pointerEvent,
        SelectionTranslationSettings config,
        CancellationToken cancellationToken)
    {
        if (!HandlesDoubleClick(config.TriggerMode) || IsBlockedGestureContinuation(pointerEvent))
            return;

        var sink = GetSink();
        var surface = await sink.InspectSurfaceAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
        if (surface.IsPointerOverOwnedSurface)
            return;
        if (surface.BlocksSelectionCapture)
        {
            await sink.OnExternalPointerPressedAsync(pointerEvent.Position, cancellationToken).ConfigureAwait(false);
            BlockGesture(pointerEvent);
            Interlocked.Increment(ref _generation);
            return;
        }

        _lastDoubleClickTime = pointerEvent.Timestamp;
        _lastDoubleClickPoint = pointerEvent.Position;
        var clipboardToken = await CaptureClipboardTokenAsync(cancellationToken).ConfigureAwait(false);
        var foregroundAtDoubleClick = pointerEvent.ForegroundTarget.IsEmpty
            ? await ReadTargetAsync(_windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false)
            : pointerEvent.ForegroundTarget;
        if (IsForeignPointerTarget(pointerEvent))
            return;
        if (!_foregroundAtMouseDown.IsEmpty
            && !foregroundAtDoubleClick.IsEmpty
            && _foregroundAtMouseDown != foregroundAtDoubleClick)
            return;
        var generation = Interlocked.Read(ref _generation);
        Track(CaptureAfterDelayAsync(
            SelectionGesture.DoubleClick,
            pointerEvent.Position,
            _foregroundAtMouseDown,
            _focusedAtMouseDown,
            foregroundAtDoubleClick,
            clipboardToken,
            generation,
            DoubleClickCaptureDelay,
            config,
            cancellationToken));
    }

    private async Task CaptureAfterDelayAsync(
        SelectionGesture gesture,
        PhysicalScreenPoint point,
        ExternalTargetToken foreground,
        ExternalTargetToken focused,
        ExternalTargetToken foregroundAtTrigger,
        IClipboardChangeToken? clipboardToken,
        long generation,
        TimeSpan delay,
        SelectionTranslationSettings config,
        CancellationToken cancellationToken)
    {
        try
        {
            await _delay.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            if (generation != Interlocked.Read(ref _generation))
                return;

            if (!await IsForegroundUnchangedAsync(foregroundAtTrigger, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Skipping selection capture because another application changed focus during the gesture.");
                return;
            }

            if (clipboardToken is not null)
            {
                var unchanged = await _clipboardSnapshots.IsChangeTokenCurrentAsync(
                    clipboardToken,
                    cancellationToken).ConfigureAwait(false);
                if (unchanged.IsSuccess && !unchanged.Value)
                    return;
            }

            var captured = await _selectedText.CaptureAsync(
                new SelectedTextCaptureCommand(
                    SelectedTextCaptureMode.Automatic,
                    point,
                    foreground,
                    focused),
                cancellationToken).ConfigureAwait(false);
            if (captured.IsFailure || generation != Interlocked.Read(ref _generation))
                return;

            var toolbar = new SelectionToolbarOptions(
                config.TranslationEnabled,
                config.CorrectionEnabled,
                config.PolishEnabled,
                config.SummaryEnabled,
                config.ExplanationEnabled);
            if (!toolbar.HasAnyAction)
                return;

            await GetSink().OnSelectionCapturedAsync(
                new SelectionCapture(captured.Value, gesture, toolbar),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to capture text from the selection gesture.");
        }
    }

    private async ValueTask<bool> IsForegroundUnchangedAsync(
        ExternalTargetToken foregroundAtTrigger,
        CancellationToken cancellationToken)
    {
        if (foregroundAtTrigger.IsEmpty)
            return true;

        var current = await ReadTargetAsync(
            _windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false);
        return current.IsEmpty || current == foregroundAtTrigger;
    }

    private bool HasExternalGestureContextChanged(GlobalPointerEvent pointerEvent)
    {
        var foregroundChanged = !_foregroundAtMouseDown.IsEmpty
                                && !pointerEvent.ForegroundTarget.IsEmpty
                                && _foregroundAtMouseDown != pointerEvent.ForegroundTarget;
        var clipboardChanged = _clipboardSequenceAtMouseDown != 0
                               && pointerEvent.ClipboardSequence != 0
                               && _clipboardSequenceAtMouseDown != pointerEvent.ClipboardSequence;
        var pointerTargetChanged = !_pointerTargetAtMouseDown.IsEmpty
                                   && !pointerEvent.PointerTarget.IsEmpty
                                   && _pointerTargetAtMouseDown != pointerEvent.PointerTarget;
        var capturedTargetChanged = !_capturedTargetAtMouseDown.IsEmpty
                                    && !pointerEvent.CapturedTarget.IsEmpty
                                    && _capturedTargetAtMouseDown != pointerEvent.CapturedTarget;
        return foregroundChanged
               || clipboardChanged
               || pointerTargetChanged
               || capturedTargetChanged
               || pointerEvent.PointerTargetIsOverlay
               || IsForeignPointerTarget(pointerEvent)
               || IsForeignCaptureTarget(pointerEvent);
    }

    private bool IsForeignPointerTarget(GlobalPointerEvent pointerEvent) =>
        !pointerEvent.PointerTarget.IsEmpty
        && !pointerEvent.ForegroundTarget.IsEmpty
        && pointerEvent.PointerTarget != pointerEvent.ForegroundTarget;

    private bool IsForeignCaptureTarget(GlobalPointerEvent pointerEvent) =>
        !pointerEvent.CapturedTarget.IsEmpty
        && !pointerEvent.ForegroundTarget.IsEmpty
        && pointerEvent.CapturedTarget != pointerEvent.ForegroundTarget
        && pointerEvent.CapturedTarget != _focusedAtMouseDown;

    private async ValueTask<IClipboardChangeToken?> CaptureClipboardTokenAsync(
        CancellationToken cancellationToken)
    {
        var result = await _clipboardSnapshots.GetChangeTokenAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }

    private ISelectionInteractionSink GetSink()
    {
        lock (_lifecycle)
            return _sink ?? throw new InvalidOperationException("Selection interaction monitoring is not started.");
    }

    private bool IsGuardedDoubleClick(GlobalPointerEvent pointerEvent) =>
        pointerEvent.Timestamp - _lastDoubleClickTime < DoubleClickGuard
        && Distance(pointerEvent.Position, _lastDoubleClickPoint) < 40;

    private void BlockGesture(GlobalPointerEvent pointerEvent)
    {
        _lastBlockedGestureTime = pointerEvent.Timestamp;
        _lastBlockedGesturePoint = pointerEvent.Position;
    }

    private bool IsBlockedGestureContinuation(GlobalPointerEvent pointerEvent)
    {
        var elapsed = pointerEvent.Timestamp - _lastBlockedGestureTime;
        return elapsed >= TimeSpan.Zero
               && elapsed < DoubleClickGuard
               && Distance(pointerEvent.Position, _lastBlockedGesturePoint) < 40;
    }

    private static async ValueTask<ExternalTargetToken> ReadTargetAsync(
        ValueTask<Result<ExternalTargetToken>> pending)
    {
        var result = await pending.ConfigureAwait(false);
        return result.IsSuccess ? result.Value : ExternalTargetToken.None;
    }

    private async Task<bool> IsTargetAllowedAsync(
        SelectionTranslationSettings config,
        CancellationToken cancellationToken)
    {
        if (config.FilterMode == SelectionFilterMode.Disabled)
            return true;

        var foreground = await ReadTargetAsync(
            _windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false);
        if (foreground.IsEmpty)
            return true;

        var identifier = await ResolveProcessIdentifierAsync(foreground, cancellationToken).ConfigureAwait(false);
        return SelectionFilterPolicy.IsAllowed(config.FilterMode, config.SafeAppList, identifier);
    }

    private async Task<string?> ResolveProcessIdentifierAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken)
    {
        lock (_processCacheSync)
        {
            if (_processIdentifierCache.TryGetValue(target, out var cached))
                return cached;
        }

        var result = await _runningProcesses.ResolveProcessIdentifierAsync(target, cancellationToken)
            .ConfigureAwait(false);
        var identifier = result.IsSuccess ? result.Value : null;
        lock (_processCacheSync)
        {
            if (_processIdentifierCache.Count > 256)
                _processIdentifierCache.Clear();
            _processIdentifierCache[target] = identifier;
        }

        return identifier;
    }

    private static bool HandlesDrag(SelectionTriggerMode mode) =>
        mode is SelectionTriggerMode.DragSelection or SelectionTriggerMode.All;

    private static bool HandlesDoubleClick(SelectionTriggerMode mode) =>
        mode is SelectionTriggerMode.DoubleClick or SelectionTriggerMode.All;

    private static double Distance(PhysicalScreenPoint left, PhysicalScreenPoint right)
    {
        var x = right.X - left.X;
        var y = right.Y - left.Y;
        return Math.Sqrt((double)x * x + (double)y * y);
    }

    private void Track(Task task)
    {
        lock (_tasksSync)
            _pendingTasks.Add(task);

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_tasksSync)
                    _pendingTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainPendingTasksAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_tasksSync)
                pending = _pendingTasks.ToArray();
            if (pending.Length == 0)
                return;

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
}
