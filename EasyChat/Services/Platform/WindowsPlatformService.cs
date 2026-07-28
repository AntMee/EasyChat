using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Shortcuts;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services.Platform;

public class WindowsPlatformService : IPlatformService
{
    private readonly ILogger<WindowsPlatformService> _logger;
    public string? LastSelectedTextCaptureMethod { get; private set; }
    public WindowsPlatformService(ILogger<WindowsPlatformService> logger)
    {
        _logger = logger;
    }

    public IntPtr GetForegroundWindowHandle()
    {
        return Win32.GetForegroundWindow();
    }

    public void SetForegroundWindow(IntPtr hWnd)
    {
        var targetThreadId = Win32.GetWindowThreadProcessId(hWnd, out _);
        var currentThreadId = Win32.GetCurrentThreadId();
        var attached = false;

        try 
        {
            if (currentThreadId != targetThreadId)
            {
                attached = Win32.AttachThreadInput(currentThreadId, targetThreadId, true);
                if (!attached)
                {
                    _logger.LogWarning($"AttachThreadInput failed using ids: {currentThreadId} -> {targetThreadId}");
                }
            }

            // Method 1: Standard SetForegroundWindow
            var result = Win32.SetForegroundWindow(hWnd);
            
            if (!result)
            {
                 // Method 2: BringWindowToTop
                 Win32.BringWindowToTop(hWnd);
                 result = Win32.SetForegroundWindow(hWnd);
            }

            if (!result)
            {
                // Method 3: SwitchToThisWindow (Deprecated but effective)
                Win32.SwitchToThisWindow(hWnd, true);
                result = Win32.SetForegroundWindow(hWnd);
            }

            if (!result)
            {
                // Method 4: The "Alt Key" Trick
                // Simulates a user action to bypass timeout restrictions
                Win32.keybd_event(Win32.VK_MENU, 0, 0, 0); // Alt Down
                Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, 0); // Alt Up
                
                result = Win32.SetForegroundWindow(hWnd);
            }

            if (!result)
            {
                _logger.LogWarning($"All attempts to SetForegroundWindow failed for hWnd: {hWnd}");
            }
            
            // Ensure focus is explicitly set
            Win32.SetFocus(hWnd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SetForegroundWindow");
        }
        finally
        {
            if (attached)
            {
                Win32.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    public void SetFocus(IntPtr hWnd)
    {
        Win32.SetFocus(hWnd);
    }

    public void PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32.PostMessage(hWnd, msg, wParam, lParam);
    }

    public void SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32.SendMessage(hWnd, msg, wParam, lParam);
    }

    public async Task SendTextAsync(string text, int delayMs = 10)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var c in text)
        {
            if (c == '\r') continue; 

            var inputs = new List<Win32.INPUT>();

            if (c == '\n')
            {
                 var down = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x0D, dwFlags = 0 } } };
                 var up = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x0D, dwFlags = Win32.KEYEVENTF_KEYUP } } };
                 inputs.Add(down);
                 inputs.Add(up);
            }
            else
            {
                var down = new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    u = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wScan = c,
                            dwFlags = Win32.KEYEVENTF_UNICODE
                        }
                    }
                };

                var up = new Win32.INPUT
                {
                    type = Win32.INPUT_KEYBOARD,
                    u = new Win32.InputUnion
                    {
                        ki = new Win32.KEYBDINPUT
                        {
                            wScan = c,
                            dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP
                        }
                    }
                };

                inputs.Add(down);
                inputs.Add(up);
            }

            Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Win32.INPUT)));
            
            await Task.Delay(delayMs); 
        }
        
        await Task.CompletedTask;
    }

    public Task SendKeyCombinationAsync(string combination)
    {
        var parsed = KeyCombinationParser.Parse(combination);
        if (parsed == null)
        {
            _logger.LogWarning("Cannot simulate invalid key combination: {Combination}", combination);
            return Task.CompletedTask;
        }

        var (modifiers, key) = parsed.Value;
        var inputs = new List<Win32.INPUT>();

        AddModifierInput(inputs, modifiers, KeyModifiers.Control, 0x11, keyUp: false);
        AddModifierInput(inputs, modifiers, KeyModifiers.Alt, 0x12, keyUp: false);
        AddModifierInput(inputs, modifiers, KeyModifiers.Shift, 0x10, keyUp: false);
        AddModifierInput(inputs, modifiers, KeyModifiers.Windows, 0x5B, keyUp: false);

        var virtualKey = MapVirtualKey(key);
        inputs.Add(CreateKeyboardInput(virtualKey, keyUp: false));
        inputs.Add(CreateKeyboardInput(virtualKey, keyUp: true));

        AddModifierInput(inputs, modifiers, KeyModifiers.Windows, 0x5B, keyUp: true);
        AddModifierInput(inputs, modifiers, KeyModifiers.Shift, 0x10, keyUp: true);
        AddModifierInput(inputs, modifiers, KeyModifiers.Alt, 0x12, keyUp: true);
        AddModifierInput(inputs, modifiers, KeyModifiers.Control, 0x11, keyUp: true);

        var sent = Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Win32.INPUT)));
        if (sent != (uint)inputs.Count)
            _logger.LogWarning("Only {SentCount} of {InputCount} simulated key events were sent for {Combination}",
                sent, inputs.Count, combination);

        return Task.CompletedTask;
    }

    private static void AddModifierInput(List<Win32.INPUT> inputs, KeyModifiers modifiers,
        KeyModifiers modifier, ushort virtualKey, bool keyUp)
    {
        if (modifiers.HasFlag(modifier))
            inputs.Add(CreateKeyboardInput(virtualKey, keyUp));
    }

    private static Win32.INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? Win32.KEYEVENTF_KEYUP : 0
                }
            }
        };
    }

    private static ushort MapVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return (ushort)key;
        if (key is >= Key.D0 and <= Key.D9) return (ushort)(0x30 + (int)key - (int)Key.D0);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return (ushort)(0x60 + (int)key - (int)Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24) return (ushort)key;

        return key switch
        {
            Key.Escape => 0x1B,
            Key.Tab => 0x09,
            Key.Space => 0x20,
            Key.Back => 0x08,
            Key.Enter => 0x0D,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,
            Key.Apps => 0x5D,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => (ushort)key
        };
    }

    [Obsolete("Obsolete")]
    public async Task PasteTextAsync(string text)
    {
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
                if (clipboard == null) return;

                await clipboard.SetTextAsync(text);
            });
            
            await Task.Delay(50);

            var inputs = new List<Win32.INPUT>();
            
            var ctrlDown = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x11, dwFlags = 0 } } };
            var vDown = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x56, dwFlags = 0 } } };
            var vUp = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x56, dwFlags = Win32.KEYEVENTF_KEYUP } } };
            var ctrlUp = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x11, dwFlags = Win32.KEYEVENTF_KEYUP } } };

            inputs.Add(ctrlDown);
            inputs.Add(vDown);
            inputs.Add(vUp);
            inputs.Add(ctrlUp);

            Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Win32.INPUT)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to paste text");
        }
    }
    
    public async Task<string?> GetSelectedTextAsync(int? x = null, int? y = null, bool copyOnly = false)
    {
        return await Task.Run(async () =>
        {
            LastSelectedTextCaptureMethod = null;

            if (!copyOnly)
            {
                // Traditional edit controls expose their selection through window
                // messages. This path does not touch the clipboard or inject input.
                var text = TryGetSelectedTextFromFocusedControl();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LastSelectedTextCaptureMethod = "EM_GETSEL/WM_GETTEXT";
                    _logger.LogInformation("Selected text captured using EM_GETSEL/WM_GETTEXT: {Length} chars", text.Length);
                    return text;
                }

                // Some controls implement the copy command but do not expose their
                // text through EM_GETSEL. Ask the focused control to copy directly
                // before falling back to synthesized Ctrl+C.
                text = await TryCopySelectedTextWithWindowMessageAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LastSelectedTextCaptureMethod = "WM_COPY";
                    _logger.LogInformation("Selected text captured using WM_COPY: {Length} chars", text.Length);
                    return text;
                }
            }

            const ushort vkC = 0x43;

            // Never synthesize Ctrl+C while the user is interacting with either
            // key. Releasing a real Ctrl key can turn the user's C press into a
            // literal character in the focused application.
            if (IsAnyModifierKeyDown() || IsKeyDown(vkC))
            {
                _logger.LogDebug("Skipping selection capture while a modifier or C is pressed by the user");
                return null;
            }

            try
            {
                // 1. Clear Clipboard (to detect new copy)
                // Use OpenClipboard/EmptyClipboard for reliability
                int retryCount = 0;
                bool cleared = false;
                while (retryCount < 5 && !cleared)
                {
                    if (Win32.OpenClipboard(IntPtr.Zero))
                    {
                        Win32.EmptyClipboard();
                        Win32.CloseClipboard();
                        cleared = true;
                    }
                    else
                    {
                        retryCount++;
                        await Task.Delay(10);
                    }
                }
                
                if (!cleared)
                {
                    _logger.LogWarning("Failed to clear clipboard, text extraction might be inaccurate");
                }
                
                // Never release or re-press physical modifier keys. Doing so can
                // activate menus or leave modifier state stuck in the target app.
                await Task.Delay(10);

                if (IsAnyModifierKeyDown() || IsKeyDown(vkC))
                {
                    _logger.LogDebug("Skipping selection capture while a modifier or C is pressed by the user");
                    return null;
                }
                
                // 3. Send Ctrl+C
                var inputs = new Win32.INPUT[4];
                
                // Ctrl Down
                inputs[0].type = Win32.INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = 0x11; // VK_CONTROL
                
                // C Down
                inputs[1].type = Win32.INPUT_KEYBOARD;
                inputs[1].u.ki.wVk = 0x43; // C
                
                // C Up
                inputs[2].type = Win32.INPUT_KEYBOARD;
                inputs[2].u.ki.wVk = 0x43;
                inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
                
                // Ctrl Up
                inputs[3].type = Win32.INPUT_KEYBOARD;
                inputs[3].u.ki.wVk = 0x11;
                inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
                
                Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Win32.INPUT)));
                
                // 4. Poll for text
                string? result = null;
                // Poll more frequently for faster response
                for (int i = 0; i < 20; i++) 
                {
                    await Task.Delay(10); // 10ms * 20 = 200ms max
                    if (IsClipboardTextAvailable())
                    {
                        result = GetClipboardTextWin32();
                        if (!string.IsNullOrEmpty(result)) break;
                    }
                }
                
                if (!string.IsNullOrEmpty(result))
                {
                    LastSelectedTextCaptureMethod = "Ctrl+C";
                    _logger.LogInformation("Selected text captured using Ctrl+C: {Length} chars", result.Length);
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get text via Clipboard");
                return null;
            }
        });
    }

    private string? TryGetSelectedTextFromFocusedControl()
    {
        var focusedWindow = GetFocusedWindow();
        if (focusedWindow == IntPtr.Zero)
        {
            return null;
        }

        Win32.SendMessage(focusedWindow, Constants.Windows.EM_GETSEL, out var selectionStart, out var selectionEnd);
        if (selectionEnd <= selectionStart)
        {
            return null;
        }

        var textLength = Win32.SendMessage(focusedWindow, Constants.Windows.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt64();
        if (textLength <= 0 || textLength > Constants.Windows.MaxWindowTextLength)
        {
            return null;
        }

        var buffer = new StringBuilder((int)textLength + 1);
        Win32.SendMessage(focusedWindow, Constants.Windows.WM_GETTEXT, buffer.Capacity, buffer);
        var windowText = buffer.ToString();
        if (selectionStart < 0 || selectionStart >= windowText.Length)
        {
            return null;
        }

        var selectedLength = Math.Min(selectionEnd, windowText.Length) - selectionStart;
        return selectedLength > 0 ? windowText.Substring(selectionStart, selectedLength) : null;
    }

    public bool TrySelectAllText()
    {
        var focusedWindow = GetFocusedWindow();
        if (focusedWindow == IntPtr.Zero)
        {
            return false;
        }

        Win32.SendMessage(focusedWindow, Constants.Windows.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
        var packedSelection = unchecked((uint)Win32.SendMessage(
            focusedWindow,
            Constants.Windows.EM_GETSEL,
            IntPtr.Zero,
            IntPtr.Zero).ToInt64());
        var selectionStart = (int)(packedSelection & 0xFFFF);
        var selectionEnd = (int)(packedSelection >> 16);

        // Some modern RichEdit controls (including newer Notepad versions)
        // support selection messages but do not expose a reliable text length
        // across processes. Verify the resulting selection directly instead.
        var selected = selectionStart == 0 && selectionEnd > selectionStart;
        if (selected)
        {
            _logger.LogDebug("Selected all text through the focused control's native edit messages.");
        }
        else
        {
            _logger.LogDebug("Focused control did not accept native select-all; selection is {Start}..{End}.",
                selectionStart, selectionEnd);
        }
        return selected;
    }

    private async Task<string?> TryCopySelectedTextWithWindowMessageAsync()
    {
        var focusedWindow = GetFocusedWindow();
        if (focusedWindow == IntPtr.Zero || !ClearClipboard())
        {
            return null;
        }

        Win32.SendMessage(focusedWindow, Constants.Windows.WM_COPY, IntPtr.Zero, IntPtr.Zero);
        // WM_COPY is synchronous for standard controls. Do not delay the
        // Ctrl+C fallback for applications that ignore this message.
        return await WaitForClipboardTextAsync(5);
    }

    private static IntPtr GetFocusedWindow()
    {
        var foregroundWindow = Win32.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var threadId = Win32.GetWindowThreadProcessId(foregroundWindow, out _);
        var threadInfo = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        return Win32.GetGUIThreadInfo(threadId, ref threadInfo) && threadInfo.hwndFocus != IntPtr.Zero
            ? threadInfo.hwndFocus
            : foregroundWindow;
    }

    private static bool ClearClipboard()
    {
        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            return Win32.EmptyClipboard();
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private async Task<string?> WaitForClipboardTextAsync(int attempts = 20)
    {
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(10);
            if (!IsClipboardTextAvailable())
            {
                continue;
            }

            var text = GetClipboardTextWin32();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return null;
    }

    private bool IsClipboardTextAvailable()
    {
        // CF_UNICODETEXT = 13
        return Win32.IsClipboardFormatAvailable(13);
    }

    private static bool IsKeyDown(ushort virtualKey)
    {
        return (Win32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool IsAnyModifierKeyDown()
    {
        return IsKeyDown(0x10) // Shift
               || IsKeyDown(0x11) // Ctrl
               || IsKeyDown(0x12) // Alt
               || IsKeyDown(0x5B) // Left Windows
               || IsKeyDown(0x5C); // Right Windows
    }
    
    private string? GetClipboardTextWin32()
    {
        if (!Win32.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr hData = Win32.GetClipboardData(13); // CF_UNICODETEXT
            if (hData != IntPtr.Zero)
            {
                IntPtr pData = Win32.GlobalLock(hData);
                if (pData != IntPtr.Zero)
                {
                    try
                    {
                        return Marshal.PtrToStringUni(pData);
                    }
                    finally
                    {
                        Win32.GlobalUnlock(hData);
                    }
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return null;
    }
    
    public async Task SendTextMessageAsync(IntPtr hWnd, string text, int delayMs = 10)
    {
        foreach (var c in text)
        {
            Win32.PostMessage(hWnd, Constants.Windows.WM_CHAR, c, IntPtr.Zero);
            await Task.Delay(delayMs);
        }
    }

    public async Task<bool> EnsureFocused(IntPtr hWnd)
    {
        for (int i = 0; i < 5; i++)
        {
            var foreground = Win32.GetForegroundWindow();
            if (foreground == hWnd) return true;

            SetForegroundWindow(hWnd);

            await Task.Delay(50);
        }

        return Win32.GetForegroundWindow() == hWnd;
    }

    public (int X, int Y) GetCursorPosition()
    {
        if (Win32.GetCursorPos(out var point))
        {
            return (point.X, point.Y);
        }
        return (0, 0);
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class Win32
    {
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, out int wParam, out int lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int SendMessage(IntPtr hWnd, uint msg, int wParam, StringBuilder lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        public const int INPUT_MOUSE = 0;
        public const int INPUT_KEYBOARD = 1;
        public const int INPUT_HARDWARE = 2;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll")]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);
        
        [DllImport("user32.dll")]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        public static extern bool CloseClipboard();
        
        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint uFormat);
        
        [DllImport("user32.dll")]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);
        
        [DllImport("kernel32.dll")]
        public static extern bool GlobalUnlock(IntPtr hMem);
        
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct FORMATETC
        {
            public short cfFormat;
            public IntPtr ptd;
            public int dwAspect;
            public int lindex;
            public int tymed;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        public const byte VK_MENU = 0x12;

        [StructLayout(LayoutKind.Sequential)]
        public struct STGMEDIUM
        {
            public int tymed;
            public IntPtr unionmember;
            public IntPtr pUnkForRelease;
        }
    }
}
