namespace EasyChat.Application.Speech;

internal sealed class SubtitleTimestampClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _displayOrigin;
    private readonly long _monotonicOrigin;

    public SubtitleTimestampClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _displayOrigin = _timeProvider.GetLocalNow().TimeOfDay;
        _monotonicOrigin = _timeProvider.GetTimestamp();
    }

    public TimeSpan GetTimestamp() =>
        _displayOrigin
        + _timeProvider.GetElapsedTime(_monotonicOrigin, _timeProvider.GetTimestamp());
}
