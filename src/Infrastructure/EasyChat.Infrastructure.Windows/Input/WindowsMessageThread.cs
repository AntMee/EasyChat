using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Input;

internal interface IWindowsMessageThread : IDisposable
{
    void Invoke(Action action);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMessageThread : IWindowsMessageThread
{
    private const uint CommandMessage = 0x8000 + 0x45;
    private const uint QuitMessage = 0x0012;

    private readonly object _lifecycle = new();
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private uint _nativeThreadId;
    private int _managedThreadId;
    private Exception? _startupFailure;
    private bool _disposed;

    public WindowsMessageThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "EasyChat Windows pointer hook"
        };
        _thread.Start();
        _ready.Wait();
        if (_startupFailure is not null)
            throw new InvalidOperationException("Unable to start the Windows pointer message thread.", _startupFailure);
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Environment.CurrentManagedThreadId == _managedThreadId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action();
            return;
        }

        Command command;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            command = new Command(action);
            _commands.Enqueue(command);
            if (!PostThreadMessage(_nativeThreadId, CommandMessage, UIntPtr.Zero, IntPtr.Zero))
                command.Fail(new Win32Exception(Marshal.GetLastWin32Error(), "Unable to signal the Windows message thread."));
        }

        command.Completion.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        bool waitForExit;
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            _disposed = true;
            waitForExit = Environment.CurrentManagedThreadId != _managedThreadId;
            PostThreadMessage(_nativeThreadId, QuitMessage, UIntPtr.Zero, IntPtr.Zero);
        }

        if (waitForExit)
            _thread.Join();
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _managedThreadId = Environment.CurrentManagedThreadId;
            _nativeThreadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _ready.Set();

            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                if (message.Message == CommandMessage)
                {
                    DrainCommands();
                    continue;
                }

                TranslateMessage(in message);
                DispatchMessage(in message);
            }

            if (result < 0)
                FailPending(new Win32Exception(Marshal.GetLastWin32Error(), "The Windows message loop failed."));
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _ready.Set();
            FailPending(exception);
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(WindowsMessageThread)));
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
            command.Execute();
    }

    private void FailPending(Exception exception)
    {
        while (_commands.TryDequeue(out var command))
            command.Fail(exception);
    }

    private sealed class Command(Action action)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public void Execute()
        {
            if (_completion.Task.IsCompleted)
                return;

            try
            {
                action();
                _completion.TrySetResult();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage
    {
        public readonly IntPtr Window;
        public readonly uint Message;
        public readonly UIntPtr WParam;
        public readonly IntPtr LParam;
        public readonly uint Time;
        public readonly NativePoint Point;
        public readonly uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
