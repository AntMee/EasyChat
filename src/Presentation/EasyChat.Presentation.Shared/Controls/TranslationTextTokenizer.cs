using System.Text.RegularExpressions;
namespace EasyChat.Presentation.Shared.Controls;

public static partial class TranslationTextTokenizer
{
    public static IReadOnlyList<TextToken> Tokenize(string text, string? languageId)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var primary = languageId?.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        var regex = primary is "zh" or "ja" ? CharacterTokenizer() : WordTokenizer();
        return regex.Matches(text).Select(match => new TextToken(
            match.Value,
            char.IsLetterOrDigit(match.Value[0]),
            match.Index,
            match.Length)).ToArray();
    }

    [GeneratedRegex(@"([a-zA-Z0-9]+)|(\s+)|(.)")]
    private static partial Regex CharacterTokenizer();

    [GeneratedRegex(@"(\w+)|(\s+)|([^\w\s]+)")]
    private static partial Regex WordTokenizer();
}
