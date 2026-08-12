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
    private readonly WindowsWinEventCallback _moveSizeProcedure;
    private readonly IWindowsMessageThread _messageThread;
    private readonly IWindowsPointerHookBackend _backend;
    private readonly ILogger<WindowsGlobalPointerMonitor> _logger;
    private IntPtr _hook;
    private IntPtr _moveSizeHook;
    private long _nextRegistrationId;
    private long _lastClickTick;
    private NativePoint _lastClickPosition;
    private bool _hasLastClick;
    private bool _disposed;
    private IntPtr _gestureWindow;
    private NativeWindowRect _gestureRect;
    private bool _hasGestureWindow;
    private bool _hasGestureRect;
    private bool _windowMoveActive;

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
        _moveSizeProcedure = HandleMoveSizeEvent;
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
            _moveSizeHook = _backend.InstallMoveSize(_moveSizeProcedure);
            if (_moveSizeHook == IntPtr.Zero)
            {
                _backend.Uninstall(_hook);
                _hook = IntPtr.Zero;
                throw new InvalidOperationException("Unable to install the Windows move/size event hook.");
            }
            _logger.LogInformation("Windows global pointer monitor started.");
        }
        else if (!shouldBeInstalled && _hook != IntPtr.Zero)
        {
            if (_moveSizeHook != IntPtr.Zero)
                _backend.UninstallMoveSize(_moveSizeHook);
            _moveSizeHook = IntPtr.Zero;
            if (!_backend.Uninstall(_hook))
                _logger.LogWarning("Unable to remove the Windows global mouse hook: {Error}", _backend.LastError);
            else
                _logger.LogInformation("Windows global pointer monitor stopped.");
            _hook = IntPtr.Zero;
            ClearGestureWindow();
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
                BeginGestureWindow(nativeEvent.Point);
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
                var wasWindowMove = _windowMoveActive || HasGestureWindowMoved();
                ClearGestureWindow();
                if (wasWindowMove)
                {
                    Publish(PointerAction.WindowMoveStarted, nativeEvent.Point);
                    return _backend.CallNext(hook, code, message, data);
                }
                Publish(PointerAction.PrimaryReleased, nativeEvent.Point);
            }
        }

        return _backend.CallNext(hook, code, message, data);
    }

    private void HandleMoveSizeEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (eventType != 0x000A || objectId != 0 || childId != 0 || hwnd == IntPtr.Zero)
            return;

        var root = _backend.RootWindow(hwnd);
        if (root == _gestureWindow && root != IntPtr.Zero)
            _windowMoveActive = true;
    }

    private void BeginGestureWindow(NativePoint point)
    {
        _gestureWindow = _backend.RootWindow(_backend.WindowFromPoint(point));
        _hasGestureWindow = _gestureWindow != IntPtr.Zero;
        _windowMoveActive = false;
        _gestureRect = default;
        _hasGestureRect = _hasGestureWindow
            && _backend.TryGetWindowRect(_gestureWindow, out _gestureRect);
    }

    private bool HasGestureWindowMoved()
    {
        if (!_hasGestureWindow)
            return false;
        if (!_backend.IsWindow(_gestureWindow))
            return true;
        if (!_hasGestureRect)
            return true;
        if (!_backend.TryGetWindowRect(_gestureWindow, out var current))
            return true;
        return current.Left != _gestureRect.Left
            || current.Top != _gestureRect.Top
            || current.Right != _gestureRect.Right
            || current.Bottom != _gestureRect.Bottom;
    }

    private void ClearGestureWindow()
    {
        _gestureWindow = IntPtr.Zero;
        _gestureRect = default;
        _hasGestureWindow = false;
        _hasGestureRect = false;
        _windowMoveActive = false;
    }

    private void Publish(PointerAction action, NativePoint point)
    {
        Action<GlobalPointerEvent>[] callbacks;
        lock (_callbacksSync)
            callbacks = _callbacks.Values.ToArray();

        var pointerEvent = new GlobalPointerEvent(
            action,
            new PhysicalScreenPoint(point.X, point.Y),
            DateTimeOffset.UtcNow,
            WindowsTargetTokens.FromHandle(WindowsWindowQuery.GetForegroundWindowHandle()),
            WindowsClipboardBackend.GetCurrentChangeToken(),
            WindowsTargetTokens.FromHandle(_backend.RootWindow(_backend.WindowFromPoint(point))),
            WindowsTargetTokens.FromHandle(WindowsWindowQuery.GetMouseCaptureWindow()),
            WindowsWindowQuery.IsLikelyOverlayWindow(_backend.RootWindow(_backend.WindowFromPoint(point))));
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
