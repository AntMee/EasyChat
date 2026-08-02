using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using GlobalHotKeys;
using GlobalHotKeys.Native.Types;

namespace EasyChat.Infrastructure.Windows.Hotkeys;

[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeys : IGlobalHotkeys, IDisposable
{
    private readonly IWindowsHotkeyBackend _backend;
    private int _disposed;

    public WindowsGlobalHotkeys()
        : this(new GlobalHotkeysBackend())
    {
    }

    internal WindowsGlobalHotkeys(IWindowsHotkeyBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public ValueTask<Result<IHotkeyRegistration>> RegisterAsync(
        ShortcutGesture gesture,
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        if (!WindowsHotkeyMapper.TryMap(gesture, out var binding))
        {
            return ValueTask.FromResult(Result<IHotkeyRegistration>.Failure(new Error(
                "hotkey.gesture-invalid",
                $"The hotkey key '{gesture.Key}' is not supported on Windows.")));
        }

        var lifetime = new CancellationTokenSource();
        var callbackToken = lifetime.Token;

        try
        {
            var nativeRegistration = _backend.Register(
                binding,
                () => callback(callbackToken).GetAwaiter().GetResult());

            if (!nativeRegistration.IsSuccessful)
            {
                nativeRegistration.Lifetime.Dispose();
                lifetime.Dispose();
                return ValueTask.FromResult(Result<IHotkeyRegistration>.Failure(new Error(
                    "hotkey.registration-conflict",
                    "Windows rejected the hotkey registration.")));
            }

            return ValueTask.FromResult(Result<IHotkeyRegistration>.Success(
                new WindowsHotkeyRegistration(nativeRegistration.Lifetime, lifetime)));
        }
        catch (Exception exception)
        {
            lifetime.Dispose();
            return ValueTask.FromResult(Result<IHotkeyRegistration>.Failure(
                new Error("hotkey.registration-failed", exception.Message)));
        }
    }

    public ValueTask<Result> ProbeAsync(
        ShortcutGesture gesture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        cancellationToken.ThrowIfCancellationRequested();

        if (!WindowsHotkeyMapper.TryMap(gesture, out var binding))
        {
            return ValueTask.FromResult(Result.Failure(new Error(
                "hotkey.gesture-invalid",
                $"The hotkey key '{gesture.Key}' is not supported on Windows.")));
        }

        try
        {
            var registration = _backend.Probe(binding);
            try
            {
                return ValueTask.FromResult(registration.IsSuccessful
                    ? Result.Success()
                    : Result.Failure(new Error(
                        "hotkey.registration-conflict",
                        "Windows rejected the hotkey availability probe.")));
            }
            finally
            {
                registration.Lifetime.Dispose();
            }
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Result.Failure(
                new Error("hotkey.probe-failed", exception.Message)));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _backend.Dispose();
    }

    private sealed class WindowsHotkeyRegistration : IHotkeyRegistration
    {
        private IDisposable? _nativeRegistration;
        private CancellationTokenSource? _lifetime;

        public WindowsHotkeyRegistration(
            IDisposable nativeRegistration,
            CancellationTokenSource lifetime)
        {
            _nativeRegistration = nativeRegistration;
            _lifetime = lifetime;
        }

        public void Dispose()
        {
            var lifetime = Interlocked.Exchange(ref _lifetime, null);
            if (lifetime is not null)
            {
                lifetime.Cancel();
                lifetime.Dispose();
            }

            Interlocked.Exchange(ref _nativeRegistration, null)?.Dispose();
        }
    }
}

internal readonly record struct WindowsHotkeyBinding(VirtualKeyCode Key, Modifiers Modifiers);

internal static class WindowsHotkeyMapper
{
    private const ShortcutModifiers AllModifiers =
        ShortcutModifiers.Alt |
        ShortcutModifiers.Control |
        ShortcutModifiers.Shift |
        ShortcutModifiers.Meta;

    private static readonly IReadOnlyDictionary<string, VirtualKeyCode> NamedKeys =
        new Dictionary<string, VirtualKeyCode>(StringComparer.OrdinalIgnoreCase)
        {
            ["KeyZ"] = (VirtualKeyCode)0,
            ["Escape"] = VirtualKeyCode.VK_ESCAPE,
            ["Back"] = VirtualKeyCode.VK_BACK,
            ["Tab"] = VirtualKeyCode.VK_TAB,
            ["Enter"] = VirtualKeyCode.VK_RETURN,
            ["Return"] = VirtualKeyCode.VK_RETURN,
            ["Space"] = VirtualKeyCode.VK_SPACE,
            ["PageUp"] = VirtualKeyCode.VK_PRIOR,
            ["PageDown"] = VirtualKeyCode.VK_NEXT,
            ["End"] = VirtualKeyCode.VK_END,
            ["Home"] = VirtualKeyCode.VK_HOME,
            ["Left"] = VirtualKeyCode.VK_LEFT,
            ["Up"] = VirtualKeyCode.VK_UP,
            ["Right"] = VirtualKeyCode.VK_RIGHT,
            ["Down"] = VirtualKeyCode.VK_DOWN,
            ["Insert"] = VirtualKeyCode.VK_INSERT,
            ["Delete"] = VirtualKeyCode.VK_DELETE,
            ["LWin"] = VirtualKeyCode.VK_LWIN,
            ["RWin"] = VirtualKeyCode.VK_RWIN,
            ["Apps"] = VirtualKeyCode.VK_APPS,
            ["Oem1"] = VirtualKeyCode.VK_OEM_1,
            ["OemSemicolon"] = VirtualKeyCode.VK_OEM_1,
            ["OemPlus"] = VirtualKeyCode.VK_OEM_PLUS,
            ["OemComma"] = VirtualKeyCode.VK_OEM_COMMA,
            ["OemMinus"] = VirtualKeyCode.VK_OEM_MINUS,
            ["OemPeriod"] = VirtualKeyCode.VK_OEM_PERIOD,
            ["Oem2"] = VirtualKeyCode.VK_OEM_2,
            ["OemQuestion"] = VirtualKeyCode.VK_OEM_2,
            ["Oem3"] = VirtualKeyCode.VK_OEM_3,
            ["OemTilde"] = VirtualKeyCode.VK_OEM_3,
            ["Oem4"] = VirtualKeyCode.VK_OEM_4,
            ["OemOpenBrackets"] = VirtualKeyCode.VK_OEM_4,
            ["Oem5"] = VirtualKeyCode.VK_OEM_5,
            ["OemPipe"] = VirtualKeyCode.VK_OEM_5,
            ["Oem6"] = VirtualKeyCode.VK_OEM_6,
            ["OemCloseBrackets"] = VirtualKeyCode.VK_OEM_6,
            ["Oem7"] = VirtualKeyCode.VK_OEM_7,
            ["OemQuotes"] = VirtualKeyCode.VK_OEM_7
        };

    public static bool TryMap(ShortcutGesture gesture, out WindowsHotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(gesture.Key) || (gesture.Modifiers & ~AllModifiers) != 0)
            return false;

        if (!TryMapKey(gesture.Key.Trim(), out var key))
            return false;

        var modifiers = (Modifiers)0;
        if (gesture.Modifiers.HasFlag(ShortcutModifiers.Alt))
            modifiers |= Modifiers.Alt;
        if (gesture.Modifiers.HasFlag(ShortcutModifiers.Control))
            modifiers |= Modifiers.Control;
        if (gesture.Modifiers.HasFlag(ShortcutModifiers.Shift))
            modifiers |= Modifiers.Shift;
        if (gesture.Modifiers.HasFlag(ShortcutModifiers.Meta))
            modifiers |= Modifiers.Win;

        binding = new WindowsHotkeyBinding(key, modifiers);
        return true;
    }

    private static bool TryMapKey(string keyName, out VirtualKeyCode key)
    {
        if (NamedKeys.TryGetValue(keyName, out key))
            return true;

        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0]))
            return Enum.TryParse($"KEY_{char.ToUpperInvariant(keyName[0])}", out key);

        if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0]))
            return Enum.TryParse($"KEY_{keyName[0]}", out key);

        if (keyName.Length == 2 &&
            keyName[0] is 'D' or 'd' &&
            char.IsAsciiDigit(keyName[1]))
        {
            return Enum.TryParse($"KEY_{keyName[1]}", out key);
        }

        if (keyName.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(keyName.AsSpan(6), out var numPad) &&
            numPad is >= 0 and <= 9)
        {
            return Enum.TryParse($"VK_NUMPAD{numPad}", out key);
        }

        if (keyName.StartsWith('F') &&
            int.TryParse(keyName.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return Enum.TryParse($"VK_F{functionKey}", out key);
        }

        key = default;
        return false;
    }
}

internal interface IWindowsHotkeyBackend : IDisposable
{
    WindowsHotkeyBackendRegistration Register(
        WindowsHotkeyBinding binding,
        Action callback);

    WindowsHotkeyBackendRegistration Probe(WindowsHotkeyBinding binding);
}

internal readonly record struct WindowsHotkeyBackendRegistration(
    bool IsSuccessful,
    IDisposable Lifetime);

[SupportedOSPlatform("windows")]
internal sealed class GlobalHotkeysBackend : IWindowsHotkeyBackend
{
    private readonly HotKeyManager _manager = new();

    public WindowsHotkeyBackendRegistration Register(
        WindowsHotkeyBinding binding,
        Action callback)
    {
        var registration = _manager.Register(binding.Key, binding.Modifiers);
        if (!registration.IsSuccessful)
            return new WindowsHotkeyBackendRegistration(false, registration);

        var subscription = _manager.HotKeyPressed
            .Where(pressed => pressed.Id == registration.Id)
            .Subscribe(_ => callback());

        return new WindowsHotkeyBackendRegistration(
            true,
            Disposable.Create(() =>
            {
                subscription.Dispose();
                registration.Dispose();
            }));
    }

    public WindowsHotkeyBackendRegistration Probe(WindowsHotkeyBinding binding)
    {
        var registration = _manager.Register(binding.Key, binding.Modifiers);
        return new WindowsHotkeyBackendRegistration(
            registration.IsSuccessful,
            registration);
    }

    public void Dispose() => _manager.Dispose();
}
