using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace EasyChat.Models.Translation.Selection;

public enum SelectionTranslationMode
{
    Sentence,
    Word
}

/// <summary>
/// A complete, independently parseable record emitted by a structured translation stream.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(SelectionTranslationStartedEvent), typeDiscriminator: "start")]
[JsonDerivedType(typeof(SelectionTranslationSourceDetectedEvent), typeDiscriminator: "source_detected")]
[JsonDerivedType(typeof(SelectionTranslationDeltaEvent), typeDiscriminator: "translation_delta")]
[JsonDerivedType(typeof(SelectionTranslationWordHeaderEvent), typeDiscriminator: "word_header")]
[JsonDerivedType(typeof(SelectionTranslationDefinitionEvent), typeDiscriminator: "definition")]
[JsonDerivedType(typeof(SelectionTranslationFormEvent), typeDiscriminator: "form")]
[JsonDerivedType(typeof(SelectionTranslationTipsEvent), typeDiscriminator: "tips")]
[JsonDerivedType(typeof(SelectionTranslationExampleEvent), typeDiscriminator: "example")]
[JsonDerivedType(typeof(SelectionTranslationKeywordEvent), typeDiscriminator: "keyword")]
[JsonDerivedType(typeof(SelectionTranslationCompletedEvent), typeDiscriminator: "done")]
public abstract record SelectionTranslationStreamEvent;

public sealed record SelectionTranslationStartedEvent(SelectionTranslationMode Mode) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationSourceDetectedEvent(string Language) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationDeltaEvent(string Text) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationWordHeaderEvent(string Word, string? Phonetic) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationDefinitionEvent(string? Pos, string Meaning) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationFormEvent(string Label, string Word) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationTipsEvent(string Text) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationExampleEvent(string Origin, string Translation) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationKeywordEvent(string Word, string Meaning) : SelectionTranslationStreamEvent;

public sealed record SelectionTranslationCompletedEvent : SelectionTranslationStreamEvent;

/// <summary>
/// Builds the existing final result model from the same events consumed by the UI.
/// This keeps streaming and non-streaming callers consistent.
/// </summary>
public sealed class SelectionTranslationResultAccumulator
{
    private readonly string _sourceText;
    private readonly TranslationSourceType _sourceType;
    private readonly StringBuilder _translation = new();
    private readonly List<WordDefinition> _definitions = [];
    private readonly List<WordForm> _forms = [];
    private readonly List<WordExample> _examples = [];
    private readonly List<SentenceKeyWord> _keywords = [];

    private SelectionTranslationMode? _mode;
    private string? _detectedSourceLanguage;
    private string? _word;
    private string? _phonetic;
    private string? _tips;
    private bool _completed;

    public SelectionTranslationResultAccumulator(string sourceText, TranslationSourceType sourceType = TranslationSourceType.Ai)
    {
        _sourceText = sourceText;
        _sourceType = sourceType;
    }

    public void Apply(SelectionTranslationStreamEvent translationEvent)
    {
        switch (translationEvent)
        {
            case SelectionTranslationStartedEvent started:
                _mode ??= started.Mode;
                if (_mode != started.Mode)
                {
                    throw new InvalidOperationException("Translation stream cannot change modes.");
                }
                break;
            case SelectionTranslationSourceDetectedEvent detected:
                _detectedSourceLanguage = detected.Language;
                break;
            case SelectionTranslationDeltaEvent delta:
                _translation.Append(delta.Text);
                break;
            case SelectionTranslationWordHeaderEvent header:
                _word = header.Word;
                _phonetic = header.Phonetic;
                break;
            case SelectionTranslationDefinitionEvent definition:
                _definitions.Add(new WordDefinition { Pos = definition.Pos ?? string.Empty, Meaning = definition.Meaning });
                break;
            case SelectionTranslationFormEvent form:
                _forms.Add(new WordForm { Label = form.Label, Word = form.Word });
                break;
            case SelectionTranslationTipsEvent tips:
                _tips = tips.Text;
                break;
            case SelectionTranslationExampleEvent example:
                _examples.Add(new WordExample { Origin = example.Origin, Translation = example.Translation });
                break;
            case SelectionTranslationKeywordEvent keyword:
                _keywords.Add(new SentenceKeyWord { Word = keyword.Word, Meaning = keyword.Meaning });
                break;
            case SelectionTranslationCompletedEvent:
                _completed = true;
                break;
        }
    }

    public SelectionTranslationResult Build()
    {
        if (_mode is null)
        {
            throw new InvalidOperationException("Translation stream did not specify a mode.");
        }

        if (!_completed)
        {
            throw new InvalidOperationException("Translation stream ended before its done event.");
        }

        return _mode == SelectionTranslationMode.Word
            ? new WordTranslationResult
            {
                SourceType = _sourceType,
                DetectedSourceLanguage = _detectedSourceLanguage,
                Word = _word ?? _sourceText,
                Phonetic = _phonetic ?? string.Empty,
                Definitions = _definitions,
                Forms = _forms,
                Tips = _tips ?? string.Empty,
                Examples = _examples
            }
            : new SentenceTranslationResult
            {
                SourceType = _sourceType,
                DetectedSourceLanguage = _detectedSourceLanguage,
                Origin = _sourceText,
                Translation = _translation.ToString(),
                KeyWords = _keywords
            };
    }
}

public static class SelectionTranslationStreamEventFactory
{
    public static IEnumerable<SelectionTranslationStreamEvent> FromResult(SelectionTranslationResult result)
    {
        yield return result switch
        {
            WordTranslationResult => new SelectionTranslationStartedEvent(SelectionTranslationMode.Word),
            SentenceTranslationResult => new SelectionTranslationStartedEvent(SelectionTranslationMode.Sentence),
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        if (!string.IsNullOrWhiteSpace(result.DetectedSourceLanguage))
        {
            yield return new SelectionTranslationSourceDetectedEvent(result.DetectedSourceLanguage);
        }

        if (result is WordTranslationResult word)
        {
            yield return new SelectionTranslationWordHeaderEvent(word.Word, word.Phonetic);
            foreach (var definition in word.Definitions)
                yield return new SelectionTranslationDefinitionEvent(definition.Pos, definition.Meaning);
            foreach (var form in word.Forms)
                yield return new SelectionTranslationFormEvent(form.Label, form.Word);
            if (!string.IsNullOrWhiteSpace(word.Tips))
                yield return new SelectionTranslationTipsEvent(word.Tips);
            foreach (var example in word.Examples)
                yield return new SelectionTranslationExampleEvent(example.Origin, example.Translation);
        }
        else if (result is SentenceTranslationResult sentence)
        {
            yield return new SelectionTranslationDeltaEvent(sentence.Translation);
            foreach (var keyword in sentence.KeyWords)
                yield return new SelectionTranslationKeywordEvent(keyword.Word, keyword.Meaning);
        }

        yield return new SelectionTranslationCompletedEvent();
    }
}
