using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Input;

internal delegate IntPtr WindowsPointerHookCallback(int code, IntPtr message, IntPtr data);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint(int x, int y)
{
    public readonly int X = x;
    public readonly int Y = y;
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
    NativePointerEvent ReadEvent(IntPtr data);
    IntPtr CallNext(IntPtr hook, int code, IntPtr message, IntPtr data);
}

[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsPointerHookBackend : IWindowsPointerHookBackend
{
    private const int LowLevelMouseHook = 14;

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

    public NativePointerEvent ReadEvent(IntPtr data) =>
        Marshal.PtrToStructure<NativePointerEvent>(data);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
