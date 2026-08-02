namespace EasyChat.Contracts.Platform;

public enum PointerAction
{
    PrimaryPressed,
    PrimaryReleased,
    PrimaryDoubleClick
}

public sealed record GlobalPointerEvent(
    PointerAction Action,
    ScreenPoint Position,
    DateTimeOffset Timestamp);

public interface IPointerMonitorRegistration : IDisposable;

public interface IGlobalPointerMonitor
{
    IPointerMonitorRegistration Start(Action<GlobalPointerEvent> callback);
}

public interface IPointerPosition
{
    ScreenPoint GetCurrent();
}

public enum KeyboardKey
{
    Control,
    Alt,
    Shift,
    LeftMeta,
    RightMeta,
    C
}

public interface IKeyboardState
{
    bool IsPressed(KeyboardKey key);
}
