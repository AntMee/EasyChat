using System;
using System.Threading.Tasks;

namespace EasyChat.Services.Abstractions;

public interface IPlatformService
{
    IntPtr GetForegroundWindowHandle();
    IntPtr GetFocusedWindowHandle();
    void SetForegroundWindow(IntPtr hWnd);
    void SetFocus(IntPtr hWnd);
    void PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    void SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    Task SendTextAsync(string text, int delayMs = 10);
    Task SendKeyCombinationAsync(string combination);
    Task PasteTextAsync(string text);
    Task SendTextMessageAsync(IntPtr hWnd, string text, int delayMs = 10);
    Task<bool> EnsureFocused(IntPtr hWnd);
    bool TrySelectAllText();
    Task<string?> GetSelectedTextAsync(
        int? x = null,
        int? y = null,
        bool copyOnly = false,
        IntPtr? expectedForegroundWindow = null,
        IntPtr? expectedFocusedWindow = null);
    Task<string?> GetSelectedTextDirectAsync(
        IntPtr? expectedForegroundWindow = null,
        IntPtr? expectedFocusedWindow = null);
    string? LastSelectedTextCaptureMethod { get; }
    (int X, int Y) GetCursorPosition();
}
