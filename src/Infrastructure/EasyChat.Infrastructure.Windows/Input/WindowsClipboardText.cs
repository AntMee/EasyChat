using System.Runtime.InteropServices;
using System.Text;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows.Input;

public sealed class WindowsClipboardText : IClipboardText
{
    private const uint UnicodeText = 13;
    private const uint MoveableMemory = 0x0002;

    public ValueTask<Result<string?>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!OpenClipboardWithRetry())
                return ValueTask.FromResult(Result<string?>.Failure(
                    new Error("clipboard.open-failed", "The Windows clipboard could not be opened.")));

            try
            {
                var handle = GetClipboardData(UnicodeText);
                if (handle == IntPtr.Zero)
                    return ValueTask.FromResult(Result<string?>.Success(null));

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                    return ValueTask.FromResult(Result<string?>.Success(null));

                try
                {
                    return ValueTask.FromResult(Result<string?>.Success(
                        Marshal.PtrToStringUni(pointer)));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Result<string?>.Failure(
                new Error("clipboard.read-failed", exception.Message)));
        }
    }

    public ValueTask<Result> WriteAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!OpenClipboardWithRetry())
                return ValueTask.FromResult(Result.Failure(
                    new Error("clipboard.open-failed", "The Windows clipboard could not be opened.")));

            IntPtr memory = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard())
                    throw new InvalidOperationException("The Windows clipboard could not be emptied.");

                var bytes = Encoding.Unicode.GetBytes(text + '\0');
                memory = GlobalAlloc(MoveableMemory, new UIntPtr((uint)bytes.Length));
                if (memory == IntPtr.Zero)
                    throw new OutOfMemoryException("Unable to allocate clipboard text memory.");

                var target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                    throw new OutOfMemoryException("Unable to lock clipboard text memory.");
                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to set Windows clipboard text.");

                memory = IntPtr.Zero;
                return ValueTask.FromResult(Result.Success());
            }
            finally
            {
                if (memory != IntPtr.Zero)
                    GlobalFree(memory);
                CloseClipboard();
            }
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Result.Failure(
                new Error("clipboard.write-failed", exception.Message)));
        }
    }

    private static bool OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(10);
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
