namespace EasyChat.Contracts.Platform;

public enum PointerAction
{
    PrimaryPressed,
    PrimaryReleased,
    PrimaryDoubleClick,
    WindowMoveStarted
}

public sealed record GlobalPointerEvent(
    PointerAction Action,
    PhysicalScreenPoint Position,
    DateTimeOffset Timestamp);

public interface IPointerMonitorRegistration : IDisposable;

public interface IGlobalPointerMonitor
{
    IPointerMonitorRegistration Start(Action<GlobalPointerEvent> callback);
}

public interface IPointerPosition
{
    PhysicalScreenPoint GetCurrent();
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
