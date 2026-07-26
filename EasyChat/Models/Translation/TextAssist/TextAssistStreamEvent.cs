using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using EasyChat.Lang;

namespace EasyChat.Models.Translation.TextAssist;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(TextAssistStartedEvent), "start")]
[JsonDerivedType(typeof(TextAssistSourceDetectedEvent), "source_detected")]
[JsonDerivedType(typeof(TextAssistTranslationDeltaEvent), "translation_delta")]
[JsonDerivedType(typeof(TextAssistIssueEvent), "issue")]
[JsonDerivedType(typeof(TextAssistCorrectedDeltaEvent), "corrected_delta")]
[JsonDerivedType(typeof(TextAssistCorrectionTranslationDeltaEvent), "correction_translation_delta")]
[JsonDerivedType(typeof(TextAssistCompletedEvent), "done")]
public abstract record TextAssistStreamEvent;

public sealed record TextAssistStartedEvent : TextAssistStreamEvent
{
    public TextAssistStartedEvent(string mode, string sourceLanguage, string? targetLanguage)
        : this(mode, sourceLanguage, targetLanguage, null)
    {
    }

    [JsonConstructor]
    public TextAssistStartedEvent(string mode, string? sourceLanguage, string? targetLanguage, string? language)
    {
        Mode = mode;
        SourceLanguage = sourceLanguage ?? language ?? string.Empty;
        TargetLanguage = targetLanguage;
        Language = language;
    }

    public string Mode { get; }
    [JsonPropertyName("sourceLanguage")]
    public string SourceLanguage { get; }
    public string? TargetLanguage { get; }
    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; }
}

public sealed record TextAssistSourceDetectedEvent(string Language) : TextAssistStreamEvent;

public sealed record TextAssistTranslationDeltaEvent(string Text) : TextAssistStreamEvent;

public sealed record TextAssistIssueEvent(
    int Start,
    int Length,
    string Category,
    string Message,
    string Suggestion) : TextAssistStreamEvent
{
    public string DisplayCategory => Category.ToLowerInvariant() switch
    {
        "grammar" => Resources.TextAssistCategoryGrammar,
        "spelling" => Resources.TextAssistCategorySpelling,
        "word_choice" => Resources.TextAssistCategoryWordChoice,
        "style" => Resources.TextAssistCategoryStyle,
        _ => Category
    };
}

public sealed record TextAssistCorrectedDeltaEvent(string Text, int Variant = 1) : TextAssistStreamEvent;
public sealed record TextAssistCorrectionTranslationDeltaEvent(string Text, int Variant = 1) : TextAssistStreamEvent;

public sealed record TextAssistCompletedEvent : TextAssistStreamEvent;

public sealed class TextAssistCorrectionAccumulator
{
    private readonly int _sourceLength;
    private readonly List<TextAssistIssueEvent> _issues = [];
    private readonly Dictionary<int, StringBuilder> _corrected = new();
    private readonly Dictionary<int, StringBuilder> _translations = new();
    private readonly Action<TextAssistIssueEvent>? _onInvalidIssue;
    private bool _started;
    private bool _completed;
    private string _language = string.Empty;

    public TextAssistCorrectionAccumulator(int sourceLength, Action<TextAssistIssueEvent>? onInvalidIssue = null)
    {
        _sourceLength = sourceLength;
        _onInvalidIssue = onInvalidIssue;
    }

    public IReadOnlyList<TextAssistIssueEvent> Issues => _issues;
    public string CorrectedText => GetCorrectedText(1);
    public IReadOnlyDictionary<int, string> CorrectedVariants => _corrected
        .ToDictionary(x => x.Key, x => x.Value.ToString());
    public IReadOnlyList<string> CorrectedTexts => _corrected
        .OrderBy(x => x.Key)
        .Select(x => x.Value.ToString())
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToArray();
    public IReadOnlyDictionary<int, string> CorrectedTranslations => _translations
        .ToDictionary(x => x.Key, x => x.Value.ToString());
    public string Language => _language;

    public void Apply(TextAssistStreamEvent item)
    {
        switch (item)
        {
            case TextAssistStartedEvent started when string.Equals(started.Mode, "correction", StringComparison.OrdinalIgnoreCase):
                _started = true;
                _language = started.SourceLanguage;
                break;
            case TextAssistIssueEvent issue:
                // Models occasionally omit the start event when there are only
                // diagnostics (for example, already-correct text). The issue
                // itself still proves that a correction stream was produced.
                _started = true;
                if (issue.Start >= 0 && issue.Length >= 0 && issue.Start <= _sourceLength &&
                    issue.Length <= _sourceLength - issue.Start)
                {
                    _issues.Add(issue);
                }
                else
                {
                    _onInvalidIssue?.Invoke(issue);
                }
                break;
            case TextAssistCorrectedDeltaEvent delta:
                // Some models omit the start event despite returning a valid corrected stream.
                // A corrected delta is still sufficient to establish the correction session.
                _started = true;
                var variant = delta.Variant <= 0 ? 1 : Math.Min(3, delta.Variant);
                if (!_corrected.TryGetValue(variant, out var builder))
                    _corrected[variant] = builder = new StringBuilder();
                builder.Append(delta.Text);
                break;
            case TextAssistCorrectionTranslationDeltaEvent translation:
                _started = true;
                var translationVariant = translation.Variant <= 0 ? 1 : Math.Min(3, translation.Variant);
                if (!_translations.TryGetValue(translationVariant, out var translationBuilder))
                    _translations[translationVariant] = translationBuilder = new StringBuilder();
                translationBuilder.Append(translation.Text);
                break;
            case TextAssistCompletedEvent:
                // Treat a terminal event as a valid, empty correction response.
                _started = true;
                _completed = true;
                break;
        }
    }

    public void EnsureComplete()
    {
        if (!_started) throw new System.InvalidOperationException("Correction stream did not start.");
        if (!_completed) throw new System.InvalidOperationException("Correction stream did not complete.");
    }

    public void CompleteImplicitly()
    {
        if (_started) _completed = true;
    }

    private string GetCorrectedText(int variant) =>
        _corrected.TryGetValue(variant, out var builder) ? builder.ToString() : string.Empty;
}
