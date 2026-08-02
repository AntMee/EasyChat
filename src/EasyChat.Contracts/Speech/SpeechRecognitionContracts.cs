using System.Text.Json.Serialization;

namespace EasyChat.Contracts.Speech;

public sealed record SpeechRecognitionCommand(
    string ModelPath,
    string Language,
    IReadOnlyList<int> ProcessIds);

public sealed record SpeechRecognitionModel(string Id);

public interface ISpeechRecognitionModelCatalog
{
    ValueTask<IReadOnlyList<SpeechRecognitionModel>> GetModelsAsync(
        CancellationToken cancellationToken = default);
}

public sealed record SpeechSubtitleLine(
    long Id,
    TimeSpan Timestamp,
    string OriginalText,
    string TranslatedText,
    string DisplayTranslatedText,
    bool IsTranslating,
    bool IsTemporary);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(SpeechSessionStartedEvent), "started")]
[JsonDerivedType(typeof(SpeechSubtitleChangedEvent), "subtitle_changed")]
[JsonDerivedType(typeof(SpeechFloatingSubtitleRemovedEvent), "floating_subtitle_removed")]
[JsonDerivedType(typeof(SpeechSessionErrorEvent), "error")]
[JsonDerivedType(typeof(SpeechSessionStoppedEvent), "stopped")]
public abstract record SpeechSessionEvent;

public sealed record SpeechSessionStartedEvent : SpeechSessionEvent;
public sealed record SpeechSubtitleChangedEvent(SpeechSubtitleLine Subtitle) : SpeechSessionEvent;
public sealed record SpeechFloatingSubtitleRemovedEvent(long SubtitleId) : SpeechSessionEvent;
public sealed record SpeechSessionErrorEvent(string Message) : SpeechSessionEvent;
public sealed record SpeechSessionStoppedEvent : SpeechSessionEvent;

public interface ISpeechRecognitionUseCases
{
    IAsyncEnumerable<SpeechSessionEvent> RecognizeAsync(
        SpeechRecognitionCommand command,
        CancellationToken cancellationToken = default);
}
