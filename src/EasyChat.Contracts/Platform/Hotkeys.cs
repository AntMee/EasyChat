using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8
}

public sealed record ShortcutGesture(string Key, ShortcutModifiers Modifiers = ShortcutModifiers.None);

public interface IHotkeyRegistration : IDisposable;

public interface IGlobalHotkeys
{
    ValueTask<Result<IHotkeyRegistration>> RegisterAsync(
        ShortcutGesture gesture,
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default);

    ValueTask<Result> ProbeAsync(
        ShortcutGesture gesture,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional extension for actions that need the lifetime of a key press.
/// Implementations must invoke <paramref name="pressed"/> once on key down and
/// <paramref name="released"/> once after the key is released.
/// </summary>
public interface IHoldGlobalHotkeys : IGlobalHotkeys
{
    ValueTask<Result<IHotkeyRegistration>> RegisterHoldAsync(
        ShortcutGesture gesture,
        Func<CancellationToken, ValueTask> pressed,
        Func<CancellationToken, ValueTask> released,
        CancellationToken cancellationToken = default);
}
