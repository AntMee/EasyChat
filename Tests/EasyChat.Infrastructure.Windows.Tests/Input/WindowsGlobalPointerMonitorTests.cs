using System.Runtime.Versioning;
using EasyChat.Infrastructure.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Infrastructure.Windows.Tests.Input;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalPointerMonitorTests
{
    [TestMethod]
    public void Registrations_InstallAndRemoveOneHookOnTheMessageThread()
    {
        var messageThread = new FakeMessageThread();
        var backend = new FakeBackend(messageThread);
        var monitor = new WindowsGlobalPointerMonitor(
            NullLogger<WindowsGlobalPointerMonitor>.Instance,
            messageThread,
            backend);

        var first = monitor.Start(_ => { });
        var second = monitor.Start(_ => { });

        Assert.AreEqual(1, backend.InstallCount);
        first.Dispose();
        Assert.AreEqual(0, backend.UninstallCount);

        second.Dispose();
        Assert.AreEqual(1, backend.UninstallCount);

        using var third = monitor.Start(_ => { });
        Assert.AreEqual(2, backend.InstallCount);
        monitor.Dispose();

        Assert.AreEqual(2, backend.UninstallCount);
        Assert.IsTrue(messageThread.IsDisposed);
        Assert.IsTrue(backend.AllCallsWereDispatched);
    }

    private sealed class FakeMessageThread : IWindowsMessageThread
    {
        public bool IsExecuting { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Invoke(Action action)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            IsExecuting = true;
            try
            {
                action();
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeBackend(FakeMessageThread messageThread) : IWindowsPointerHookBackend
    {
        public uint DoubleClickTime => 500;
        public int LastError => 0;
        public int InstallCount { get; private set; }
        public int UninstallCount { get; private set; }
        public bool AllCallsWereDispatched { get; private set; } = true;

        public IntPtr Install(WindowsPointerHookCallback callback)
        {
            RecordDispatchState();
            return new IntPtr(++InstallCount);
        }

        public bool Uninstall(IntPtr hook)
        {
            RecordDispatchState();
            UninstallCount++;
            return true;
        }

        public NativePointerEvent ReadEvent(IntPtr data) => new(new NativePoint(0, 0));

        public IntPtr CallNext(IntPtr hook, int code, IntPtr message, IntPtr data) => IntPtr.Zero;

        private void RecordDispatchState() =>
            AllCallsWereDispatched &= messageThread.IsExecuting;
    }
}
