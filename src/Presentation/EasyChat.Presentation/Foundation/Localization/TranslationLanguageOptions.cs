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

    private static LanguageSettings ToSettings(TranslationLanguage language)
    {
        var localized = language.NativeName ?? language.EnglishName;
        return new LanguageSettings(
            language.Id,
            localized,
            language.EnglishName,
            language.Icon ?? "unknown.png",
            localized,
            LanguageDisplayNames.ForUi(localized, language.EnglishName),
            language.ProviderCodes ?? new Dictionary<string, string>());
    }
}
