namespace EasyChat.Contracts.Platform;

public enum AudioFeedbackCue
{
    RealtimeInterpretationStarted,
    RealtimeInterpretationReleased,
    RealtimeInterpretationCompleted
}

/// <summary>
/// Plays a short local UI cue. Implementations must not route cues through virtual audio devices.
/// </summary>
public interface IAudioFeedbackCuePlayer
{
    ValueTask PlayAsync(
        AudioFeedbackCue cue,
        CancellationToken cancellationToken = default);
}
