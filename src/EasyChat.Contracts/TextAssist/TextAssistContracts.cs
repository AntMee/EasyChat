using System.Text.Json.Serialization;
using EasyChat.Contracts.Translation;

namespace EasyChat.Contracts.TextAssist;

public enum TextAssistOperation
{
    Translation,
    Correction,
    Polish,
    Summary,
    Explanation
}

public sealed record TextAssistProfile(
    TranslationLanguage Source,
    TranslationLanguage Target,
    string Provider,
    string? AiModelId,
    string? MachineProvider,
    bool UsesGlobalConfiguration = false,
    string? PromptId = null,
    bool DetailedExplanation = false);

public sealed record TextAssistRequest(
    string Text,
    TextAssistOperation Operation,
    TextAssistProfile? Profile = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(TextAssistStartedEvent), "start")]
[JsonDerivedType(typeof(TextAssistSourceDetectedEvent), "source_detected")]
[JsonDerivedType(typeof(TextAssistTranslationDeltaEvent), "translation_delta")]
[JsonDerivedType(typeof(TextAssistTranslationAnnotationEvent), "annotation")]
[JsonDerivedType(typeof(TextAssistPolishExplanationEvent), "polish_explanation")]
[JsonDerivedType(typeof(TextAssistIssueEvent), "issue")]
[JsonDerivedType(typeof(TextAssistCorrectedDeltaEvent), "corrected_delta")]
[JsonDerivedType(typeof(TextAssistCorrectionTranslationDeltaEvent), "correction_translation_delta")]
[JsonDerivedType(typeof(TextAssistCompletedEvent), "done")]
public abstract record TextAssistEvent;

public sealed record TextAssistStartedEvent : TextAssistEvent
{
    public TextAssistStartedEvent(string mode, string sourceLanguage, string? targetLanguage)
        : this(mode, sourceLanguage, targetLanguage, null)
    {
    }

    [JsonConstructor]
    public TextAssistStartedEvent(
        string mode,
        string? sourceLanguage,
        string? targetLanguage,
        string? language)
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

public sealed record TextAssistSourceDetectedEvent(string Language) : TextAssistEvent;
public sealed record TextAssistTranslationDeltaEvent(string Text) : TextAssistEvent;

public sealed record TextAssistTranslationAnnotationEvent(
    string Term,
    string Category,
    string Meaning,
    string? Note = null,
    string[]? RelatedTerms = null) : TextAssistEvent
{
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasRelatedTerms => RelatedTerms is { Length: > 0 };
}

public sealed record TextAssistPolishExplanationEvent(
    string Category,
    string Original,
    string Revised,
    string Explanation) : TextAssistEvent
{
    public bool HasOriginal => !string.IsNullOrWhiteSpace(Original);
    public bool HasRevised => !string.IsNullOrWhiteSpace(Revised);
}

public sealed record TextAssistIssueEvent(
    int Start,
    int Length,
    string Category,
    string Message,
    string Suggestion,
    string? Original = null) : TextAssistEvent;

public static class TextAssistIssueRangeResolver
{
    public static TextAssistIssueEvent Normalize(string sourceText, TextAssistIssueEvent issue)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(issue);
        if (string.IsNullOrEmpty(issue.Original))
            return issue;

        var original = issue.Original;
        var requestedStart = Math.Clamp(issue.Start, 0, sourceText.Length);
        if (MatchesAt(sourceText, original, requestedStart))
            return issue with { Start = requestedStart, Length = original.Length };

        var resolvedStart = FindNearestOccurrence(sourceText, original, requestedStart);
        return resolvedStart < 0
            ? issue
            : issue with { Start = resolvedStart, Length = original.Length };
    }

    private static bool MatchesAt(string sourceText, string original, int start) =>
        start <= sourceText.Length - original.Length
        && sourceText.AsSpan(start, original.Length).SequenceEqual(original.AsSpan());

    private static int FindNearestOccurrence(string sourceText, string original, int requestedStart)
    {
        var bestStart = -1;
        var bestDistance = int.MaxValue;
        var searchStart = 0;
        while (searchStart <= sourceText.Length - original.Length)
        {
            var candidate = sourceText.IndexOf(original, searchStart, StringComparison.Ordinal);
            if (candidate < 0) break;
            var distance = Math.Abs(candidate - requestedStart);
            if (distance < bestDistance)
            {
                bestStart = candidate;
                bestDistance = distance;
            }
            searchStart = candidate + 1;
        }
        return bestStart;
    }
}

public static class TextAssistCorrectionIssueRules
{
    public static bool HasSameIdentity(TextAssistIssueEvent first, TextAssistIssueEvent second) =>
        first.Start == second.Start
        && first.Length == second.Length
        && DescribesSameCorrection(first, second);

    public static bool DescribesSameCorrection(TextAssistIssueEvent first, TextAssistIssueEvent second) =>
        string.Equals(first.Category?.Trim(), second.Category?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Message?.Trim(), second.Message?.Trim(), StringComparison.Ordinal)
        && string.Equals(first.Suggestion?.Trim(), second.Suggestion?.Trim(), StringComparison.Ordinal);

    public static bool RangesAreAdjacentOrOverlapping(TextAssistIssueEvent first, TextAssistIssueEvent second)
    {
        var firstEnd = (long)first.Start + first.Length;
        var secondEnd = (long)second.Start + second.Length;
        return first.Start <= secondEnd + 1 && second.Start <= firstEnd + 1;
    }
}

public sealed record TextAssistCorrectedDeltaEvent(string Text, int Variant = 1) : TextAssistEvent
{
    [JsonIgnore]
    public bool IsStreamingPartial { get; init; }
}

public sealed record TextAssistCorrectionTranslationDeltaEvent(string Text, int Variant = 1) : TextAssistEvent
{
    [JsonIgnore]
    public bool IsStreamingPartial { get; init; }
}
public sealed record TextAssistCompletedEvent : TextAssistEvent;

public interface ITextAssistUseCases
{
    TextAssistProfile ResolveProfile(TextAssistOperation operation);

    IAsyncEnumerable<TextAssistEvent> StreamAsync(
        TextAssistRequest request,
        CancellationToken cancellationToken = default);
}
