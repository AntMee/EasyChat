using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Input;

internal delegate IntPtr WindowsPointerHookCallback(int code, IntPtr message, IntPtr data);
internal delegate void WindowsWinEventCallback(
    IntPtr hook,
    uint eventType,
    IntPtr hwnd,
    int objectId,
    int childId,
    uint eventThread,
    uint eventTime);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint(int x, int y)
{
    public readonly int X = x;
    public readonly int Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeWindowRect
{
    public readonly int Left;
    public readonly int Top;
    public readonly int Right;
    public readonly int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePointerEvent
{
    public readonly NativePoint Point;
    public readonly uint MouseData;
    public readonly uint Flags;
    public readonly uint Time;
    public readonly IntPtr ExtraInfo;

    public NativePointerEvent(NativePoint point) => Point = point;
}

internal interface IWindowsPointerHookBackend
{
    uint DoubleClickTime { get; }
    int LastError { get; }
    IntPtr Install(WindowsPointerHookCallback callback);
    bool Uninstall(IntPtr hook);
    IntPtr InstallMoveSize(WindowsWinEventCallback callback);
    bool UninstallMoveSize(IntPtr hook);
    NativePointerEvent ReadEvent(IntPtr data);
    IntPtr WindowFromPoint(NativePoint point);
    IntPtr RootWindow(IntPtr window);
    bool TryGetWindowRect(IntPtr window, out NativeWindowRect rect);
    bool IsWindow(IntPtr window);
    IntPtr CallNext(IntPtr hook, int code, IntPtr message, IntPtr data);
}

[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsPointerHookBackend : IWindowsPointerHookBackend
{
    private const int LowLevelMouseHook = 14;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint AncestorRoot = 2;

    public uint DoubleClickTime => GetDoubleClickTime();
    public int LastError => Marshal.GetLastWin32Error();

    public IntPtr Install(WindowsPointerHookCallback callback)
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        var hook = SetWindowsHookEx(LowLevelMouseHook, callback, moduleHandle, 0);
        return hook != IntPtr.Zero
            ? hook
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the global mouse hook.");
    }

    public bool Uninstall(IntPtr hook) => UnhookWindowsHookEx(hook);

    public IntPtr InstallMoveSize(WindowsWinEventCallback callback) =>
        SetWinEventHook(
            EventSystemMoveSizeStart,
            EventSystemMoveSizeEnd,
            IntPtr.Zero,
            callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

    public bool UninstallMoveSize(IntPtr hook) => UnhookWinEvent(hook);

    public NativePointerEvent ReadEvent(IntPtr data) =>
        Marshal.PtrToStructure<NativePointerEvent>(data);

    public IntPtr WindowFromPoint(NativePoint point) => WindowFromPointNative(point);

    public IntPtr RootWindow(IntPtr window) => GetAncestor(window, AncestorRoot);

    public bool TryGetWindowRect(IntPtr window, out NativeWindowRect rect) =>
        GetWindowRect(window, out rect);

    public bool IsWindow(IntPtr window) => IsWindowNative(window);

    public IntPtr CallNext(IntPtr hook, int code, IntPtr message, IntPtr data) =>
        CallNextHookEx(hook, code, message, data);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        WindowsPointerHookCallback procedure,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WindowsWinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointNative(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeWindowRect rect);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
