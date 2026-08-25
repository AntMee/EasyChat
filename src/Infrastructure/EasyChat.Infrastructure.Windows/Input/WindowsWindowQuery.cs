using System.Runtime.InteropServices;

namespace EasyChat.Infrastructure.Windows.Input;

internal static class WindowsWindowQuery
{
    private const int GwlExStyle = -20;
    private const long ExLayered = 0x00080000;
    private const long ExToolWindow = 0x00000080;
    private const long ExNoActivate = 0x08000000;

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    public static IntPtr GetMouseCaptureWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return IntPtr.Zero;

        var threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref info) ? info.CaptureWindow : IntPtr.Zero;
    }

    public static bool IsLikelyOverlayWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
            return false;

        var virtualWidth = GetSystemMetrics(78);
        var virtualHeight = GetSystemMetrics(79);
        if (virtualWidth <= 0 || virtualHeight <= 0)
            return false;

        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width < virtualWidth * 0.75 || height < virtualHeight * 0.75)
            return false;

        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        return (style & ExLayered) != 0
            || (style & ExToolWindow) != 0
            || (style & ExNoActivate) != 0;
    }

    public static IntPtr GetFocusedWindow() => GetFocusedWindow(GetForegroundWindow());

    public static IntPtr GetFocusedWindow(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero)
            return IntPtr.Zero;

        var threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref info) && info.FocusedWindow != IntPtr.Zero
            ? info.FocusedWindow
            : foreground;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo threadInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusedWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public NativeRectangle CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
