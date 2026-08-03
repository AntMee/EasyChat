using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalPointerMonitor : IGlobalPointerMonitor, IDisposable
{
    private const int LeftButtonDown = 0x0201;
    private const int LeftButtonUp = 0x0202;

    private readonly object _lifecycle = new();
    private readonly object _callbacksSync = new();
    private readonly Dictionary<long, Action<GlobalPointerEvent>> _callbacks = [];
    private readonly WindowsPointerHookCallback _procedure;
    private readonly IWindowsMessageThread _messageThread;
    private readonly IWindowsPointerHookBackend _backend;
    private readonly ILogger<WindowsGlobalPointerMonitor> _logger;
    private IntPtr _hook;
    private long _nextRegistrationId;
    private long _lastClickTick;
    private NativePoint _lastClickPosition;
    private bool _hasLastClick;
    private bool _disposed;

    public WindowsGlobalPointerMonitor(ILogger<WindowsGlobalPointerMonitor> logger)
        : this(logger, new WindowsMessageThread(), new NativeWindowsPointerHookBackend())
    {
    }

    internal WindowsGlobalPointerMonitor(
        ILogger<WindowsGlobalPointerMonitor> logger,
        IWindowsMessageThread messageThread,
        IWindowsPointerHookBackend backend)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messageThread = messageThread ?? throw new ArgumentNullException(nameof(messageThread));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _procedure = HandleHook;
    }

    public IPointerMonitorRegistration Start(Action<GlobalPointerEvent> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        long id;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextRegistrationId;
            lock (_callbacksSync)
                _callbacks.Add(id, callback);
        }

        try
        {
            _messageThread.Invoke(ReconcileHook);
            return new Registration(this, id);
        }
        catch
        {
            lock (_lifecycle)
            {
                lock (_callbacksSync)
                    _callbacks.Remove(id);
            }

            TryReconcileHook();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
                return;

            _disposed = true;
            lock (_callbacksSync)
                _callbacks.Clear();
        }

        try
        {
            _messageThread.Invoke(ReconcileHook);
        }
        finally
        {
            _messageThread.Dispose();
        }
    }

    private void RemoveRegistration(long id)
    {
        lock (_lifecycle)
        {
            lock (_callbacksSync)
            {
                if (!_callbacks.Remove(id))
                    return;
            }
        }

        TryReconcileHook();
    }

    private void ReconcileHook()
    {
        bool shouldBeInstalled;
        lock (_lifecycle)
        {
            lock (_callbacksSync)
                shouldBeInstalled = !_disposed && _callbacks.Count > 0;
        }

        if (shouldBeInstalled && _hook == IntPtr.Zero)
        {
            _hook = _backend.Install(_procedure);
            _logger.LogInformation("Windows global pointer monitor started.");
        }
        else if (!shouldBeInstalled && _hook != IntPtr.Zero)
        {
            if (!_backend.Uninstall(_hook))
                _logger.LogWarning("Unable to remove the Windows global mouse hook: {Error}", _backend.LastError);
            else
                _logger.LogInformation("Windows global pointer monitor stopped.");
            _hook = IntPtr.Zero;
        }
    }

    private void TryReconcileHook()
    {
        try
        {
            _messageThread.Invoke(ReconcileHook);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private IntPtr HandleHook(int code, IntPtr message, IntPtr data)
    {
        var hook = _hook;
        if (code >= 0 && (message == LeftButtonDown || message == LeftButtonUp))
        {
            var nativeEvent = _backend.ReadEvent(data);
            if (message == LeftButtonDown)
            {
                var now = Environment.TickCount64;
                if (_hasLastClick
                    && now - _lastClickTick <= _backend.DoubleClickTime
                    && Math.Abs(nativeEvent.Point.X - _lastClickPosition.X) < 5
                    && Math.Abs(nativeEvent.Point.Y - _lastClickPosition.Y) < 5)
                {
                    Publish(PointerAction.PrimaryDoubleClick, nativeEvent.Point);
                }

                _hasLastClick = true;
                _lastClickTick = now;
                _lastClickPosition = nativeEvent.Point;
                Publish(PointerAction.PrimaryPressed, nativeEvent.Point);
            }
            else
            {
                Publish(PointerAction.PrimaryReleased, nativeEvent.Point);
            }
        }

        return _backend.CallNext(hook, code, message, data);
    }

    private void Publish(PointerAction action, NativePoint point)
    {
        Action<GlobalPointerEvent>[] callbacks;
        lock (_callbacksSync)
            callbacks = _callbacks.Values.ToArray();

        var pointerEvent = new GlobalPointerEvent(
            action,
            new PhysicalScreenPoint(point.X, point.Y),
            DateTimeOffset.UtcNow);
        foreach (var callback in callbacks)
        {
            try
            {
                callback(pointerEvent);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unhandled global pointer callback failure.");
            }
        }
    }

    private sealed class Registration(WindowsGlobalPointerMonitor owner, long id)
        : IPointerMonitorRegistration
    {
        private WindowsGlobalPointerMonitor? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveRegistration(id);
    }

}
