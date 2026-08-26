using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Presentation.Foundation.Localization;

public sealed class TranslationLanguageOptions(ITranslationLanguageCatalog catalog)
{
    public IReadOnlyList<LanguageSettings> All { get; } = catalog.All
        .Select(ToSettings)
        .ToArray();

    public LanguageSettings Get(string id) =>
        All.FirstOrDefault(language => language.Id == id)
        ?? throw new KeyNotFoundException($"Unknown translation language '{id}'.");

    public string NormalizeId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var language = All.FirstOrDefault(candidate =>
                           string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                       ?? All.FirstOrDefault(candidate => candidate.ProviderCodes.Values.Any(code =>
                           string.Equals(code, id, StringComparison.OrdinalIgnoreCase)));
        return language?.Id ?? id;
    }

    private static LanguageSettings ToSettings(TranslationLanguage language)
    {
        var localized = language.NativeName ?? language.EnglishName;
        return new LanguageSettings(
            language.Id,
            localized,
            language.EnglishName,
            language.Icon ?? "unknown.png",
            localized,
            language.NativeName is { Length: > 0 } && language.NativeName != language.EnglishName
                ? $"{language.NativeName} ({language.EnglishName})"
                : language.EnglishName,
            language.ProviderCodes ?? new Dictionary<string, string>());
    }
}
