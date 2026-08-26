using EasyChat.Infrastructure.Windows.Input;

namespace EasyChat.Infrastructure.Windows.Tests.Input;

[TestClass]
public sealed class WindowsInputMethodContextRestorerTests
{
    [TestMethod]
    public void TryRestoreForegroundWindow_SendsCurrentLayoutToOwnedForegroundWindow()
    {
        var backend = new FakeBackend
        {
            ForegroundWindow = new IntPtr(123),
            ForegroundThreadId = 456,
            ForegroundProcessId = 789,
            CurrentProcessId = 789,
            KeyboardLayout = new IntPtr(0x08040804)
        };
        var restorer = new WindowsInputMethodContextRestorer(backend);

        var restored = restorer.TryRestoreForegroundWindow();

        Assert.IsTrue(restored);
        Assert.AreEqual(backend.ForegroundWindow, backend.MessageWindow);
        Assert.AreEqual(0x0051u, backend.Message);
        Assert.AreEqual(IntPtr.Zero, backend.MessageWParam);
        Assert.AreEqual(backend.KeyboardLayout, backend.MessageLParam);
    }

    [TestMethod]
    public void TryRestoreForegroundWindow_DoesNotMessageAnotherProcess()
    {
        var backend = new FakeBackend
        {
            ForegroundWindow = new IntPtr(123),
            ForegroundThreadId = 456,
            ForegroundProcessId = 789,
            CurrentProcessId = 999,
            KeyboardLayout = new IntPtr(0x08040804)
        };
        var restorer = new WindowsInputMethodContextRestorer(backend);

        var restored = restorer.TryRestoreForegroundWindow();

        Assert.IsFalse(restored);
        Assert.AreEqual(0, backend.SendMessageCount);
    }

    [TestMethod]
    [DataRow(0, 456, 789, 789, 0x08040804)]
    [DataRow(123, 0, 789, 789, 0x08040804)]
    [DataRow(123, 456, 789, 789, 0)]
    public void TryRestoreForegroundWindow_DoesNotMessageIncompleteNativeState(
        int foregroundWindow,
        int foregroundThreadId,
        int foregroundProcessId,
        int currentProcessId,
        int keyboardLayout)
    {
        var backend = new FakeBackend
        {
            ForegroundWindow = new IntPtr(foregroundWindow),
            ForegroundThreadId = (uint)foregroundThreadId,
            ForegroundProcessId = (uint)foregroundProcessId,
            CurrentProcessId = (uint)currentProcessId,
            KeyboardLayout = new IntPtr(keyboardLayout)
        };
        var restorer = new WindowsInputMethodContextRestorer(backend);

        var restored = restorer.TryRestoreForegroundWindow();

        Assert.IsFalse(restored);
        Assert.AreEqual(0, backend.SendMessageCount);
    }

    private sealed class FakeBackend : IWindowsInputMethodContextBackend
    {
        public IntPtr ForegroundWindow { get; init; }
        public uint ForegroundThreadId { get; init; }
        public uint ForegroundProcessId { get; init; }
        public uint CurrentProcessId { get; init; }
        public IntPtr KeyboardLayout { get; init; }
        public int SendMessageCount { get; private set; }
        public IntPtr MessageWindow { get; private set; }
        public uint Message { get; private set; }
        public IntPtr MessageWParam { get; private set; }
        public IntPtr MessageLParam { get; private set; }

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public uint GetWindowThreadProcessId(IntPtr window, out uint processId)
        {
            processId = ForegroundProcessId;
            return ForegroundThreadId;
        }

        public uint GetCurrentProcessId() => CurrentProcessId;

        public IntPtr GetKeyboardLayout(uint threadId) => KeyboardLayout;

        public void SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            SendMessageCount++;
            MessageWindow = window;
            Message = message;
            MessageWParam = wParam;
            MessageLParam = lParam;
        }
    }
}
