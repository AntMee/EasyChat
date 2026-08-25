using System.Text.Json.Serialization;
using EasyChat.Contracts.Translation;

namespace EasyChat.Contracts.SelectionTranslation;

[JsonConverter(typeof(JsonStringEnumConverter<SelectionTranslationMode>))]
public enum SelectionTranslationMode
{
    Sentence,
    Word
}

public enum SelectionTranslationSource
{
    Ai,
    Machine
}

public enum SelectionTranslationConfigurationScope
{
    Selection,
    Global
}

public sealed record SelectionTranslationRequest(
    string Text,
    TranslationLanguage Source,
    TranslationLanguage Target,
    TranslationLanguage? AnnotationLanguage = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(SelectionTranslationStartedEvent), "start")]
[JsonDerivedType(typeof(SelectionTranslationSourceDetectedEvent), "source_detected")]
[JsonDerivedType(typeof(SelectionTranslationDeltaEvent), "translation_delta")]
[JsonDerivedType(typeof(SelectionTranslationWordHeaderEvent), "word_header")]
[JsonDerivedType(typeof(SelectionTranslationDefinitionEvent), "definition")]
[JsonDerivedType(typeof(SelectionTranslationFormEvent), "form")]
[JsonDerivedType(typeof(SelectionTranslationTipsEvent), "tips")]
[JsonDerivedType(typeof(SelectionTranslationExampleEvent), "example")]
[JsonDerivedType(typeof(SelectionTranslationWordEvent), "word")]
[JsonDerivedType(typeof(SelectionTranslationCompletedEvent), "done")]
public abstract record SelectionTranslationEvent;

public sealed record SelectionTranslationStartedEvent(SelectionTranslationMode Mode)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationSourceDetectedEvent(string Language)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationDeltaEvent(string Text)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationWordHeaderEvent(string Word, string? Phonetic)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationDefinitionEvent(string? Pos, string Meaning)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationFormEvent(string Label, string Word)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationTipsEvent(string Text)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationExampleEvent(string Origin, string Translation)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationWordEvent(
    string Word,
    string Meaning,
    string? Phonetic = null,
    [property: JsonPropertyName("part_of_speech")] string? PartOfSpeech = null,
    IReadOnlyList<string>? Forms = null,
    IReadOnlyList<string>? Meanings = null)
    : SelectionTranslationEvent;

public sealed record SelectionTranslationCompletedEvent : SelectionTranslationEvent;

public abstract record SelectionTranslationResult(
    SelectionTranslationSource Source,
    string? DetectedSourceLanguage);

public sealed record SelectionWordResult(
    SelectionTranslationSource Source,
    string? DetectedSourceLanguage,
    string Word,
    string Phonetic,
    IReadOnlyList<SelectionWordDefinition> Definitions,
    string Tips,
    IReadOnlyList<SelectionWordExample> Examples,
    IReadOnlyList<SelectionWordForm> Forms)
    : SelectionTranslationResult(Source, DetectedSourceLanguage);

public sealed record SelectionWordDefinition(string Pos, string Meaning);
public sealed record SelectionWordExample(string Origin, string Translation);
public sealed record SelectionWordForm(string Label, string Word);

public sealed record SelectionSentenceResult(
    SelectionTranslationSource Source,
    string? DetectedSourceLanguage,
    string Origin,
    string Translation,
    IReadOnlyList<SelectionWord> Words)
    : SelectionTranslationResult(Source, DetectedSourceLanguage);

public sealed record SelectionWord(
    string Word,
    string Meaning,
    string? Phonetic = null,
    string? PartOfSpeech = null,
    IReadOnlyList<string>? Forms = null,
    IReadOnlyList<string>? Meanings = null);

public interface ISelectionTranslationUseCases
{
    Task<SelectionTranslationResult> TranslateAsync(
        SelectionTranslationRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SelectionTranslationEvent> StreamAsync(
        SelectionTranslationRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SelectionTranslationEvent> StreamAsync(
        SelectionTranslationRequest request,
        SelectionTranslationConfigurationScope configurationScope,
        CancellationToken cancellationToken = default) =>
        StreamAsync(request, cancellationToken);

    IAsyncEnumerable<SelectionTranslationEvent> StreamSentenceAsync(
        SelectionTranslationRequest request,
        SelectionTranslationConfigurationScope configurationScope,
        CancellationToken cancellationToken = default) =>
        StreamAsync(request, configurationScope, cancellationToken);

    IAsyncEnumerable<SelectionTranslationEvent> StreamDictionaryAsync(
        SelectionTranslationRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SelectionTranslationEvent> StreamDictionaryAsync(
        SelectionTranslationRequest request,
        SelectionTranslationConfigurationScope configurationScope,
        CancellationToken cancellationToken = default) =>
        StreamDictionaryAsync(request, cancellationToken);
}
