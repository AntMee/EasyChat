using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
internal sealed class WindowsInputMethodContextRestorer
{
    private const uint WmInputLangChange = 0x0051;
    private readonly IWindowsInputMethodContextBackend _backend;

    public WindowsInputMethodContextRestorer()
        : this(new WindowsInputMethodContextBackend())
    {
    }

    internal WindowsInputMethodContextRestorer(IWindowsInputMethodContextBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public bool TryRestoreForegroundWindow()
    {
        var foreground = _backend.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        var threadId = _backend.GetWindowThreadProcessId(foreground, out var processId);
        if (threadId == 0 || processId != _backend.GetCurrentProcessId())
            return false;

        var keyboardLayout = _backend.GetKeyboardLayout(threadId);
        if (keyboardLayout == IntPtr.Zero)
            return false;

        // The UI host refreshes its process-wide input-method owner from this message.
        // This must be synchronous: WM_DESTROY clears the closing window's IMM state
        // immediately after raising its close notification.
        _backend.SendMessage(
            foreground,
            WmInputLangChange,
            IntPtr.Zero,
            keyboardLayout);
        return true;
    }
}

internal interface IWindowsInputMethodContextBackend
{
    IntPtr GetForegroundWindow();

    uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    uint GetCurrentProcessId();

    IntPtr GetKeyboardLayout(uint threadId);

    void SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsInputMethodContextBackend : IWindowsInputMethodContextBackend
{
    public IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    public uint GetWindowThreadProcessId(IntPtr window, out uint processId) =>
        GetWindowThreadProcessIdNative(window, out processId);

    public uint GetCurrentProcessId() => GetCurrentProcessIdNative();

    public IntPtr GetKeyboardLayout(uint threadId) => GetKeyboardLayoutNative(threadId);

    public void SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        SendMessageNative(window, message, wParam, lParam);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
    private static extern uint GetCurrentProcessIdNative();

    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
    private static extern IntPtr GetKeyboardLayoutNative(uint threadId);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageNative(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
