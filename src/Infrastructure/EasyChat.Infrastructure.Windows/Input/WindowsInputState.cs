using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsPointerPosition : IPointerPosition
{
    public PhysicalScreenPoint GetCurrent() => GetCursorPos(out var point)
        ? new PhysicalScreenPoint(point.X, point.Y)
        : new PhysicalScreenPoint(0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsKeyboardState : IKeyboardState
{
    public bool IsPressed(KeyboardKey key) => (GetAsyncKeyState(Map(key)) & 0x8000) != 0;

    private static int Map(KeyboardKey key) => key switch
    {
        KeyboardKey.Control => 0x11,
        KeyboardKey.Alt => 0x12,
        KeyboardKey.Shift => 0x10,
        KeyboardKey.LeftMeta => 0x5B,
        KeyboardKey.RightMeta => 0x5C,
        KeyboardKey.C => 0x43,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
