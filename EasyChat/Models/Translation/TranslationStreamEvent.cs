using System.Text.Json.Serialization;

namespace EasyChat.Models.Translation;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(TranslationStartedEvent), "start")]
[JsonDerivedType(typeof(TranslationSourceDetectedEvent), "source_detected")]
[JsonDerivedType(typeof(TranslationDeltaEvent), "translation_delta")]
[JsonDerivedType(typeof(TranslationCompletedEvent), "done")]
public abstract record TranslationStreamEvent;

public sealed record TranslationStartedEvent(
    string Mode,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage) : TranslationStreamEvent;
public sealed record TranslationSourceDetectedEvent(string Language) : TranslationStreamEvent;
public sealed record TranslationDeltaEvent(string Text) : TranslationStreamEvent;
public sealed record TranslationCompletedEvent : TranslationStreamEvent;
