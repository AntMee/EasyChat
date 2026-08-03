using System.Runtime.InteropServices;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows.Input;

public sealed class WindowsTextSelection : ITextSelection
{
    private const uint EmGetSel = 0x00B0;
    private const uint EmSetSel = 0x00B1;

    public ValueTask<Result<TextSelectionRange>> SelectAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var focusedWindow = WindowsWindowQuery.GetFocusedWindow();
        if (focusedWindow == IntPtr.Zero)
        {
            return ValueTask.FromResult(Result<TextSelectionRange>.Success(
                new TextSelectionRange(false, 0, 0)));
        }

        SendMessage(focusedWindow, EmSetSel, IntPtr.Zero, new IntPtr(-1));
        var packedSelection = unchecked((uint)SendMessage(
            focusedWindow,
            EmGetSel,
            IntPtr.Zero,
            IntPtr.Zero).ToInt64());
        return ValueTask.FromResult(Result<TextSelectionRange>.Success(
            new TextSelectionRange(
                true,
                (int)(packedSelection & 0xFFFF),
                (int)(packedSelection >> 16))));
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
