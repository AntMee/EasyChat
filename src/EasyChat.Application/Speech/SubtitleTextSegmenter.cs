using System.Text.RegularExpressions;

namespace EasyChat.Application.Speech;

internal sealed partial class SubtitleTextSegmenter
{
    private static readonly char[] Terminators = ['.', '?', '!', '。', '？', '！'];

    public IReadOnlyList<string> SplitSentences(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : SentenceBoundary().Split(text)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .ToArray();

    public int CountSentences(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(Terminators.Contains);

    public IReadOnlyList<string> SplitIntoParagraphs(string text, int maximumSentences)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var sentences = SplitSentences(text);
        if (sentences.Count == 0)
            return [text];
        var size = Math.Max(1, maximumSentences);
        return sentences
            .Chunk(size)
            .Select(chunk => string.Join(" ", chunk))
            .ToArray();
    }

    [GeneratedRegex(@"(?<=[.?!。？！])")]
    private static partial Regex SentenceBoundary();
}
