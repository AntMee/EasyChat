using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace EasyChat.Infrastructure.Windows.Speech;

internal interface IWindowsAsrWorker : IDisposable
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAsrWorker : IWindowsAsrWorker
{
    private readonly BlockingCollection<Command> _commands = new();
    private readonly Thread _thread;
    private bool _disposed;

    public WindowsAsrWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "EasyChat ASR worker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = new Command(action, cancellationToken);
        _commands.Add(command, cancellationToken);
        return command.Completion;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _commands.CompleteAdding();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            _thread.Join();
        _commands.Dispose();
    }

    private void Run()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
            command.Execute();
    }

    private sealed class Command(Action action, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

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
    }
}
