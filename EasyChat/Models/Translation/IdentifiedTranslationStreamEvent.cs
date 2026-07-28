using System.Text.Json.Serialization;

namespace EasyChat.Models.Translation;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(IdentifiedTranslationStartedEvent), "start")]
[JsonDerivedType(typeof(IdentifiedTranslationDeltaEvent), "translation_delta")]
[JsonDerivedType(typeof(IdentifiedTranslationCompletedEvent), "done")]
public abstract record IdentifiedTranslationStreamEvent;

public sealed record IdentifiedTranslationStartedEvent(
    string Mode,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("target_language")] string TargetLanguage)
    : IdentifiedTranslationStreamEvent;

public sealed record IdentifiedTranslationDeltaEvent(string Id, string Text)
    : IdentifiedTranslationStreamEvent;

public sealed record IdentifiedTranslationCompletedEvent : IdentifiedTranslationStreamEvent;
