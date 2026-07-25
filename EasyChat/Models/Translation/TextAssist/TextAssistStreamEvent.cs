using System;
using System.Collections.Generic;
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

public sealed record TextAssistCorrectedDeltaEvent(string Text) : TextAssistStreamEvent;

public sealed record TextAssistCompletedEvent : TextAssistStreamEvent;

public sealed class TextAssistCorrectionAccumulator
{
    private readonly int _sourceLength;
    private readonly List<TextAssistIssueEvent> _issues = [];
    private readonly StringBuilder _corrected = new();
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
    public string CorrectedText => _corrected.ToString();
    public string Language => _language;

    public void Apply(TextAssistStreamEvent item)
    {
        switch (item)
        {
            case TextAssistStartedEvent started when started.Mode == "correction":
                _started = true;
                _language = started.SourceLanguage;
                break;
            case TextAssistIssueEvent issue:
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
                _corrected.Append(delta.Text);
                break;
            case TextAssistCompletedEvent:
                _completed = true;
                break;
        }
    }

    public void EnsureComplete()
    {
        if (!_started) throw new System.InvalidOperationException("Correction stream did not start.");
        if (!_completed) throw new System.InvalidOperationException("Correction stream did not complete.");
    }
}
