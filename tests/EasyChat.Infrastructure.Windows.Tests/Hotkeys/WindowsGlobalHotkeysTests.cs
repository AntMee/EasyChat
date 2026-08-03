using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Hotkeys;
using GlobalHotKeys.Native.Types;

namespace EasyChat.Infrastructure.Windows.Tests.Hotkeys;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeysTests
{
    [TestMethod]
    public async Task Registration_PreservesWindowsMappingAndCancelsCallbackLifetime()
    {
        var backend = new FakeBackend();
        using var hotkeys = new WindowsGlobalHotkeys(backend);
        CancellationToken callbackToken = default;
        var result = await hotkeys.RegisterAsync(
            new ShortcutGesture(
                "Oem3",
                ShortcutModifiers.Control | ShortcutModifiers.Meta),
            token =>
            {
                callbackToken = token;
                return ValueTask.CompletedTask;
            });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(VirtualKeyCode.VK_OEM_3, backend.Binding.Key);
        Assert.AreEqual(Modifiers.Control | Modifiers.Win, backend.Binding.Modifiers);
        backend.Callback!();
        Assert.IsFalse(callbackToken.IsCancellationRequested);

        result.Value.Dispose();
        Assert.IsTrue(callbackToken.IsCancellationRequested);
        Assert.AreEqual(1, backend.Registration.DisposeCount);
    }

    private sealed class FakeBackend : IWindowsHotkeyBackend
    {
        public WindowsHotkeyBinding Binding { get; private set; }
        public Action? Callback { get; private set; }
        public FakeDisposable Registration { get; } = new();

        public WindowsHotkeyBackendRegistration Register(
            WindowsHotkeyBinding binding,
            Action callback)
        {
            Binding = binding;
            Callback = callback;
            return new WindowsHotkeyBackendRegistration(true, Registration);
        }

        public WindowsHotkeyBackendRegistration Probe(WindowsHotkeyBinding binding) =>
            new(true, new FakeDisposable());

        public void Dispose()
        {
        }
    }

    private sealed class FakeDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
