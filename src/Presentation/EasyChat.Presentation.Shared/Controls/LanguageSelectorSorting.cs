using System.Collections;
using System.Globalization;
using System.Reflection;

namespace EasyChat.Presentation.Shared.Controls;

public enum LanguageSortMode
{
    Popularity = 0,
    Original = 1,
    Alphabetical = 2
}

internal static class LanguageSelectorSorting
{
    private const int UnknownLanguageRank = int.MaxValue;

    // Covers every id in BuiltInTranslationLanguageCatalog. Region-specific
    // provider ids fall back to their base language in GetPopularityRank.
    private static readonly string[] PopularityOrder =
    [
        "auto",
        "zh-Hans", "zh-Hant", "yue", "wyw",
        "en", "es", "hi", "ar", "fr", "pt", "pt-BR", "bn", "ru", "de",
        "ja", "ko", "vi", "tr", "it", "id", "fa", "ur", "nl", "pl", "uk",
        "th", "ms", "he", "ta", "te", "mr", "sw", "ro", "cs", "el", "hu",
        "sv", "da", "fi", "no", "bg", "sk", "sr", "sr-Latn", "sr-Cyrl", "hr", "ca",
        "et", "sl", "lt", "lv", "af", "sq", "am", "az", "be", "bs", "cy", "eo",
        "eu", "ga", "gl", "gu", "hy", "is", "ka", "kk", "km", "kn", "ky", "lo",
        "mk", "ml", "mn", "mt", "my", "ne", "pa", "so", "tg", "tl", "uz", "ku",
        "mi", "oc", "la", "lb", "rm", "qu", "ug", "bh", "mai", "ang", "bho", "mah",
        "sck", "new", "gom", "sa", "bgc", "abq", "ady", "kbd", "ava", "dar", "inh",
        "che", "lbe", "lez", "tab"
    ];

    private static readonly IReadOnlyDictionary<string, int> PopularityRanks =
        BuildPopularityRanks();

    public static IEnumerable<object?> Sort(IEnumerable? source, LanguageSortMode mode)
    {
        var entries = (source ?? Array.Empty<object>())
            .Cast<object?>()
            .Select((item, index) => new SortEntry(item, index))
            .ToArray();

        return mode switch
        {
            LanguageSortMode.Original => entries
                .OrderBy(entry => entry.Index)
                .Select(entry => entry.Item),
            LanguageSortMode.Alphabetical => entries
                .OrderBy(entry => GetDisplayName(entry.Item), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Item),
            _ => entries
                .OrderBy(entry => GetPopularityRank(entry.Item))
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Item)
        };
    }

    private static IReadOnlyDictionary<string, int> BuildPopularityRanks()
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < PopularityOrder.Length; index++)
            ranks[PopularityOrder[index]] = index * 10;

        // Common aliases used by translation and speech catalogs.
        ranks["zh"] = ranks["zh-Hans"];
        ranks["zh-CN"] = ranks["zh-Hans"];
        ranks["zh-TW"] = ranks["zh-Hant"];
        ranks["cht"] = ranks["zh-Hant"];
        return ranks;
    }

    private static int GetPopularityRank(object? item)
    {
        var id = GetLanguageId(item);
        if (string.IsNullOrWhiteSpace(id))
            return UnknownLanguageRank;

        var normalizedId = id.Trim().Replace('_', '-').ToLowerInvariant();
        if (PopularityRanks.TryGetValue(normalizedId, out var rank))
            return rank;

        var separator = normalizedId.IndexOf('-');
        return separator > 0 && PopularityRanks.TryGetValue(normalizedId[..separator], out rank)
            ? rank
            : UnknownLanguageRank;
    }

    private static string GetDisplayName(object? item)
    {
        if (item is null)
            return string.Empty;

        var converted = LanguageDisplayNameConverter.Instance.Convert(
            item,
            typeof(string),
            parameter: null,
            culture: CultureInfo.CurrentUICulture);
        return converted?.ToString() ?? GetLanguageId(item) ?? string.Empty;
    }

    private static string? GetLanguageId(object? item, int depth = 0)
    {
        if (item is null)
            return null;

        if (item is string text)
            return text;

        var type = item.GetType();
        foreach (var propertyName in new[] { "Id", "LanguageId", "Locale", "Code" })
        {
            if (type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(item) is string id
                && !string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        if (depth >= 1)
            return null;

        var languageProperty = type.GetProperty("Language", BindingFlags.Instance | BindingFlags.Public);
        return GetLanguageId(languageProperty?.GetValue(item), depth + 1);
    }

    private readonly record struct SortEntry(object? Item, int Index);
}
