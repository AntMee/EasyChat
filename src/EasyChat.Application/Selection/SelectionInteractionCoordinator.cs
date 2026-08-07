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
    private readonly ISelectionDelay _delay;
    private readonly ILogger<SelectionInteractionCoordinator> _logger;
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly object _tasksSync = new();
    private readonly HashSet<Task> _pendingTasks = [];

    private CancellationTokenSource? _lifetime;
    private IPointerMonitorRegistration? _registration;
    private ISelectionInteractionSink? _sink;
    private PhysicalScreenPoint? _downPoint;
    private ExternalTargetToken _foregroundAtMouseDown;
    private ExternalTargetToken _focusedAtMouseDown;
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
        ILogger<SelectionInteractionCoordinator> logger)
        : this(settings, platformAccess, pointerMonitor, windowFocus, clipboardSnapshots, selectedText,
            new SystemSelectionDelay(), logger)
    {
    }

    internal SelectionInteractionCoordinator(
        ISettingsUseCases settings,
        IPlatformAccessUseCases platformAccess,
        IGlobalPointerMonitor pointerMonitor,
        IWindowFocus windowFocus,
        IClipboardSnapshots clipboardSnapshots,
        ISelectedTextUseCases selectedText,
        ISelectionDelay delay,
        ILogger<SelectionInteractionCoordinator> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _platformAccess = platformAccess ?? throw new ArgumentNullException(nameof(platformAccess));
        _pointerMonitor = pointerMonitor ?? throw new ArgumentNullException(nameof(pointerMonitor));
        _windowFocus = windowFocus ?? throw new ArgumentNullException(nameof(windowFocus));
        _clipboardSnapshots = clipboardSnapshots ?? throw new ArgumentNullException(nameof(clipboardSnapshots));
        _selectedText = selectedText ?? throw new ArgumentNullException(nameof(selectedText));
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
            if (_settings.Current.SelectionTranslation.Enabled)
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
            if (_settings.Current.SelectionTranslation.Enabled)
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
                _registration?.Dispose();
                _registration = null;
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
        if (!config.Enabled)
            return;

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

        _foregroundAtMouseDown = await ReadTargetAsync(
            _windowFocus.GetForegroundTargetAsync(cancellationToken)).ConfigureAwait(false);
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

        var clipboardToken = await CaptureClipboardTokenAsync(cancellationToken).ConfigureAwait(false);
        var generation = Interlocked.Read(ref _generation);
        Track(CaptureAfterDelayAsync(
            SelectionGesture.Drag,
            pointerEvent.Position,
            _foregroundAtMouseDown,
            _focusedAtMouseDown,
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
        var generation = Interlocked.Read(ref _generation);
        Track(CaptureAfterDelayAsync(
            SelectionGesture.DoubleClick,
            pointerEvent.Position,
            _foregroundAtMouseDown,
            _focusedAtMouseDown,
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
