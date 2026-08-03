using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MicroASR;

public sealed class TextPostProcessor
{
    private static readonly Dictionary<string, long> SmallNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["oh"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3,
        ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, long> Scales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hundred"] = 100,
        ["thousand"] = 1_000,
        ["million"] = 1_000_000,
        ["billion"] = 1_000_000_000,
    };

    private static readonly string NumberWordPattern = string.Join('|',
        SmallNumbers.Keys.Concat(Scales.Keys).Concat(["and", "point", "minus", "negative"])
            .OrderByDescending(word => word.Length).Select(Regex.Escape));

    private static readonly Regex NumberSequence = new(
        $@"\b(?:{NumberWordPattern})(?:[\s-]+(?:{NumberWordPattern}))*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([,.;:!?%\)\]])", RegexOptions.Compiled);
    private static readonly Regex SpaceAfterOpeningPunctuation = new(@"([\(\[])\s+", RegexOptions.Compiled);
    private static readonly Regex RepeatedWhitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _explicitPunctuationRules;
    private readonly bool _normalizeEnglishNumbers;
    private readonly string _sentenceTerminator;

    public TextPostProcessor(string modelDirectory)
        : this(SpeechModelPackage.Load(modelDirectory))
    {
    }

    public TextPostProcessor(SpeechModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        _explicitPunctuationRules = package.PunctuationRulesPath is null
            ? Array.Empty<(Regex, string)>()
            : LoadPunctuationRules(package.PunctuationRulesPath);
        _normalizeEnglishNumbers = package.Locale.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
        _sentenceTerminator = package.Locale.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ||
                              package.Locale.StartsWith("ja-", StringComparison.OrdinalIgnoreCase)
            ? "。"
            : ".";
    }

    public string Process(string text, bool final)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = ApplyExplicitPunctuation(text);
        if (_normalizeEnglishNumbers)
            text = NumberSequence.Replace(text, ConvertNumberSequence);
        text = SpaceBeforePunctuation.Replace(text, "$1");
        text = SpaceAfterOpeningPunctuation.Replace(text, "$1");
        text = RepeatedWhitespace.Replace(text, " ").Trim();
        text = CapitalizeSentences(text);
        if (final && text.Length > 0 && char.IsLetterOrDigit(text[^1]))
            text += _sentenceTerminator;
        return text;
    }

    private string ApplyExplicitPunctuation(string text)
    {
        foreach ((Regex pattern, string replacement) in _explicitPunctuationRules)
            text = pattern.Replace(text, replacement);
        return text.Replace("\u2A1D", string.Empty, StringComparison.Ordinal);
    }

    private static string ConvertNumberSequence(Match match)
    {
        string[] words = match.Value.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        bool negative = words.Length > 0 &&
                        (words[0].Equals("minus", StringComparison.OrdinalIgnoreCase) ||
                         words[0].Equals("negative", StringComparison.OrdinalIgnoreCase));
        int start = negative ? 1 : 0;
        int point = Array.FindIndex(words, start, word => word.Equals("point", StringComparison.OrdinalIgnoreCase));
        int integerEnd = point >= 0 ? point : words.Length;
        long total = 0;
        long group = 0;
        bool consumed = false;

        for (int index = start; index < integerEnd; index++)
        {
            string word = words[index];
            if (word.Equals("and", StringComparison.OrdinalIgnoreCase))
                continue;
            if (SmallNumbers.TryGetValue(word, out long value))
            {
                group += value;
                consumed = true;
                continue;
            }
            if (!Scales.TryGetValue(word, out long scale))
                return match.Value;
            consumed = true;
            if (scale == 100)
            {
                group = Math.Max(1, group) * scale;
            }
            else
            {
                total += Math.Max(1, group) * scale;
                group = 0;
            }
        }

        if (!consumed)
            return match.Value;
        string result = (total + group).ToString(CultureInfo.InvariantCulture);
        if (point >= 0)
        {
            var fraction = new StringBuilder();
            for (int index = point + 1; index < words.Length; index++)
            {
                if (!SmallNumbers.TryGetValue(words[index], out long digit) || digit is < 0 or > 9)
                    return match.Value;
                fraction.Append((char)('0' + digit));
            }
            if (fraction.Length > 0)
                result += "." + fraction;
        }
        return negative ? "-" + result : result;
    }

    private static string CapitalizeSentences(string text)
    {
        var result = new StringBuilder(text.Length);
        bool capitalize = true;
        foreach (char character in text)
        {
            if (capitalize && char.IsLetter(character))
            {
                result.Append(char.ToUpperInvariant(character));
                capitalize = false;
            }
            else
            {
                result.Append(character);
            }

            if (character is '.' or '!' or '?' or '\n')
                capitalize = true;
            else if (!char.IsWhiteSpace(character) && !char.IsPunctuation(character))
                capitalize = false;
        }
        return result.ToString();
    }

    private static IReadOnlyList<(Regex Pattern, string Replacement)> LoadPunctuationRules(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<(Regex, string)>();

        var rules = new List<(string Spoken, string Replacement)>();
        bool inRules = false;
        foreach (string sourceLine in File.ReadLines(path))
        {
            string line = sourceLine.TrimEnd();
            if (line.StartsWith('['))
            {
                inRules = line.Equals("[Rules]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inRules || line.Length == 0 || line.StartsWith('#'))
                continue;

            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0].Length > 0)
                rules.Add((parts[0], parts[1].Replace("\\n", "\n").Replace("\\t", "\t")));
        }

        return rules.OrderByDescending(rule => rule.Spoken.Length)
            .Select(rule => (
                new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(rule.Spoken)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled),
                rule.Replacement))
            .ToArray();
    }
}
