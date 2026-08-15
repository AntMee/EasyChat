using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shortcuts;
using EasyChat.Presentation.Features.Speech;

namespace EasyChat.Presentation.Features.Shortcuts;

public sealed class SpeechInterpretationShortcutAction(
    SpeechInterpretationHotkeyController controller) : IHoldShortcutAction
{
    public string ActionType => "RealtimeInterpretation";
    public bool PreventConcurrentExecution => false;

    public ValueTask ExecuteAsync(
        ShortcutParameterSettings? parameter,
        CancellationToken cancellationToken = default) =>
        controller.PressAsync(cancellationToken);

    public ValueTask ExecutePressedAsync(
        ShortcutParameterSettings? parameter,
        CancellationToken cancellationToken = default) =>
        controller.PressAsync(cancellationToken);

    public ValueTask ExecuteReleasedAsync(
        ShortcutParameterSettings? parameter,
        CancellationToken cancellationToken = default) =>
        controller.ReleaseAsync(cancellationToken);
}
