using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Audio;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioFeedbackCuePlayer : IAudioFeedbackCuePlayer
{
    public ValueTask PlayAsync(
        AudioFeedbackCue cue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (cue)
        {
            case AudioFeedbackCue.RealtimeInterpretationStarted:
                Console.Beep(880, 75);
                break;
            case AudioFeedbackCue.RealtimeInterpretationReleased:
                Console.Beep(740, 75);
                break;
            case AudioFeedbackCue.RealtimeInterpretationCompleted:
                Console.Beep(660, 100);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cue), cue, null);
        }

        return ValueTask.CompletedTask;
    }
}
