namespace EasyChat.Contracts.Platform;

public sealed record SpeechRecognitionOptions(
    string ModelPath,
    string Language,
    IReadOnlyList<AudioCaptureSourceToken> Sources);

public enum SpeechRecognitionEventKind
{
    Started,
    Partial,
    Final,
    Error,
    Stopped
}

public sealed record SpeechRecognitionEvent(SpeechRecognitionEventKind Kind, string? Text = null);

public interface ISpeechRecognitionEngine
{
    IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
        SpeechRecognitionOptions options,
        CancellationToken cancellationToken = default);
}
