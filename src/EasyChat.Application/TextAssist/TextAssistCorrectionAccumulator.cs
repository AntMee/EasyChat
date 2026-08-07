using System.Text;
using EasyChat.Contracts.TextAssist;

namespace EasyChat.Application.TextAssist;

public sealed class TextAssistCorrectionAccumulator
{
    private readonly int _sourceLength;
    private readonly List<TextAssistIssueEvent> _issues = [];
    private readonly Dictionary<int, StringBuilder> _corrected = [];
    private readonly Dictionary<int, StringBuilder> _translations = [];
    private readonly Action<TextAssistIssueEvent>? _onInvalidIssue;
    private bool _started;
    private bool _completed;
    private string _language = string.Empty;

    public TextAssistCorrectionAccumulator(
        int sourceLength,
        Action<TextAssistIssueEvent>? onInvalidIssue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        _sourceLength = sourceLength;
        _onInvalidIssue = onInvalidIssue;
    }

    public IReadOnlyList<TextAssistIssueEvent> Issues => _issues;
    public string CorrectedText => GetCorrectedText(1);
    public IReadOnlyDictionary<int, string> CorrectedVariants => _corrected
        .ToDictionary(item => item.Key, item => item.Value.ToString());
    public IReadOnlyList<string> CorrectedTexts => _corrected
        .OrderBy(item => item.Key)
        .Select(item => item.Value.ToString())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToArray();
    public IReadOnlyDictionary<int, string> CorrectedTranslations => _translations
        .ToDictionary(item => item.Key, item => item.Value.ToString());
    public string Language => _language;

    public void Apply(TextAssistEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item)
        {
            case TextAssistStartedEvent started
                when string.Equals(started.Mode, "correction", StringComparison.OrdinalIgnoreCase):
                _started = true;
                _language = started.SourceLanguage;
                break;
            case TextAssistIssueEvent issue:
                _started = true;
                if (issue.Start >= 0 && issue.Length > 0 && issue.Start <= _sourceLength
                    && issue.Length <= _sourceLength - issue.Start)
                {
                    var isDuplicate = _issues.Any(existing =>
                        TextAssistCorrectionIssueRules.HasSameIdentity(existing, issue)
                        || (TextAssistCorrectionIssueRules.DescribesSameCorrection(existing, issue)
                            && TextAssistCorrectionIssueRules.RangesAreAdjacentOrOverlapping(existing, issue)));
                    if (!isDuplicate)
                        _issues.Add(issue);
                }
                else
                {
                    _onInvalidIssue?.Invoke(issue);
                }
                break;
            case TextAssistCorrectedDeltaEvent delta:
                _started = true;
                Append(_corrected, delta.Variant, delta.Text, delta.IsStreamingPartial);
                break;
            case TextAssistCorrectionTranslationDeltaEvent translation:
                _started = true;
                Append(_translations, translation.Variant, translation.Text, translation.IsStreamingPartial);
                break;
            case TextAssistCompletedEvent:
                _started = true;
                _completed = true;
                break;
        }
    }

    public void EnsureComplete()
    {
        if (!_started)
            throw new InvalidOperationException("Correction stream did not start.");
        if (!_completed)
            throw new InvalidOperationException("Correction stream did not complete.");
    }

    public void CompleteImplicitly()
    {
        if (_started)
            _completed = true;
    }

    private static void Append(
        Dictionary<int, StringBuilder> values,
        int variant,
        string text,
        bool isStreamingPartial = false)
    {
        variant = variant <= 0 ? 1 : Math.Min(3, variant);
        if (string.IsNullOrEmpty(text))
            return;
        if (!values.TryGetValue(variant, out var builder))
        {
            values[variant] = new StringBuilder(text);
            return;
        }

        if (isStreamingPartial)
        {
            builder.Append(text);
            return;
        }

        var current = builder.ToString();
        if (string.Equals(current, text, StringComparison.Ordinal)
            || current.StartsWith(text, StringComparison.Ordinal))
        {
            return;
        }
        if (text.StartsWith(current, StringComparison.Ordinal))
        {
            builder.Clear();
            builder.Append(text);
            return;
        }

        builder.Append(text.AsSpan(FindSuffixPrefixOverlap(current, text)));
    }

    private static int FindSuffixPrefixOverlap(string current, string next)
    {
        for (var length = Math.Min(current.Length, next.Length); length > 0; length--)
        {
            if (current.AsSpan(current.Length - length).SequenceEqual(next.AsSpan(0, length)))
                return length;
        }
        return 0;
    }

    private string GetCorrectedText(int variant) =>
        _corrected.TryGetValue(variant, out var builder) ? builder.ToString() : string.Empty;

}
