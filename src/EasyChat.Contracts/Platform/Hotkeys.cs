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
