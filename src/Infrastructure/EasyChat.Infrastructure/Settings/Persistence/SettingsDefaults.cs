using EasyChat.Contracts.Translation;

namespace EasyChat.Infrastructure.Settings.Persistence;

internal static class SettingsDefaults
{
    public static LanguageSettingsDto CreateSourceLanguage()
    {
        const string chineseName = "\u81ea\u52a8\u68c0\u6d4b";
        const string englishName = "Auto Detect";
        var localizedName = GetLocalizedName(chineseName, englishName);
        return new LanguageSettingsDto
        {
            Id = "auto",
            ChineseName = chineseName,
            EnglishName = englishName,
            Icon = "auto.png",
            LocalizedName = localizedName,
            DisplayName = localizedName,
            ProviderCodes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Baidu"] = "auto",
                ["Tencent"] = "auto",
                ["Google"] = "auto"
            }
        };
    }

    public static LanguageSettingsDto CreateTargetLanguage()
    {
        const string chineseName = "\u7b80\u4f53\u4e2d\u6587";
        const string englishName = "Simplified Chinese";
        var localizedName = GetLocalizedName(chineseName, englishName);
        return new LanguageSettingsDto
        {
            Id = "zh-Hans",
            ChineseName = chineseName,
            EnglishName = englishName,
            Icon = "cn.png",
            LocalizedName = localizedName,
            DisplayName = localizedName,
            ProviderCodes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Baidu"] = "zh",
                ["Tencent"] = "zh",
                ["Google"] = "zh-CN",
                ["DeepL"] = "ZH"
            }
        };
    }

    public const string DefaultPromptId = "builtin-professional-translator";
    public const int BuiltInPromptCatalogVersion = 2;

    public static List<PromptEntrySettingsDto> CreateBuiltInPrompts() =>
    [
        CreateEnglishProfessionalTranslator(),
        CreateEnglishTechnicalTranslator(),
        CreateEnglishNaturalLocalizer(),
        CreateEnglishLiteraryTranslator(),
        CreateChineseProfessionalTranslator(),
        CreateChineseTechnicalTranslator(),
        CreateChineseNaturalLocalizer(),
        CreateChineseLiteraryTranslator()
    ];

    public static List<PromptEntrySettingsDto> CreateBuiltInPromptsAddedAfter(int catalogVersion) =>
        catalogVersion < 2
            ? [
                CreateChineseProfessionalTranslator(),
                CreateChineseTechnicalTranslator(),
                CreateChineseNaturalLocalizer(),
                CreateChineseLiteraryTranslator()
            ]
            : [];

    public static bool UpgradeLegacyBuiltInPrompt(PromptEntrySettingsDto prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return prompt.Id switch
        {
            DefaultPromptId => UpgradeLegacyBuiltInPrompt(
                prompt,
                "You are a professional translator. Produce accurate, fluent translations that preserve meaning, tone, terminology, formatting, and intent.",
                TranslationPromptDefaults.DefaultRole,
                "Professional translator (English)",
                "Professional translator",
                "\u4e13\u4e1a\u7ffb\u8bd1"),
            "builtin-technical-translator" => UpgradeLegacyBuiltInPrompt(
                prompt,
                "You are a technical translator experienced with software documentation, APIs, engineering, and product interfaces. Preserve established terminology, commands, identifiers, and code.",
                TranslationPromptDefaults.TechnicalTranslatorRole,
                "Technical translator (English)",
                "Technical translator",
                "\u6280\u672f\u7ffb\u8bd1"),
            "builtin-natural-localizer" => UpgradeLegacyBuiltInPrompt(
                prompt,
                "You are a native-level localization specialist. Adapt wording naturally for the target audience while retaining intent, register, and important cultural context.",
                TranslationPromptDefaults.NaturalLocalizerRole,
                "Natural localizer (English)",
                "Natural localizer",
                "\u81ea\u7136\u672c\u5730\u5316"),
            "builtin-literary-translator" => UpgradeLegacyBuiltInPrompt(
                prompt,
                "You are a literary translator. Preserve voice, rhythm, imagery, and emotional nuance while keeping the translation natural in the target language.",
                TranslationPromptDefaults.LiteraryTranslatorRole,
                "Literary translator (English)",
                "Literary translator",
                "\u6587\u5b66\u7ffb\u8bd1"),
            _ => false
        };
    }

    private static PromptEntrySettingsDto CreateEnglishProfessionalTranslator() => new()
    {
        Id = DefaultPromptId,
        Name = "Professional translator (English)",
        Content = TranslationPromptDefaults.DefaultRole,
        IsDefault = true
    };

    private static PromptEntrySettingsDto CreateEnglishTechnicalTranslator() => new()
    {
        Id = "builtin-technical-translator",
        Name = "Technical translator (English)",
        Content = TranslationPromptDefaults.TechnicalTranslatorRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateEnglishNaturalLocalizer() => new()
    {
        Id = "builtin-natural-localizer",
        Name = "Natural localizer (English)",
        Content = TranslationPromptDefaults.NaturalLocalizerRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateEnglishLiteraryTranslator() => new()
    {
        Id = "builtin-literary-translator",
        Name = "Literary translator (English)",
        Content = TranslationPromptDefaults.LiteraryTranslatorRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateChineseProfessionalTranslator() => new()
    {
        Id = "builtin-professional-translator-zh",
        Name = "\u4e13\u4e1a\u7ffb\u8bd1\uff08\u4e2d\u6587\uff09",
        Content = TranslationPromptDefaults.ChineseProfessionalTranslatorRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateChineseTechnicalTranslator() => new()
    {
        Id = "builtin-technical-translator-zh",
        Name = "\u6280\u672f\u7ffb\u8bd1\uff08\u4e2d\u6587\uff09",
        Content = TranslationPromptDefaults.ChineseTechnicalTranslatorRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateChineseNaturalLocalizer() => new()
    {
        Id = "builtin-natural-localizer-zh",
        Name = "\u81ea\u7136\u672c\u5730\u5316\uff08\u4e2d\u6587\uff09",
        Content = TranslationPromptDefaults.ChineseNaturalLocalizerRole,
        IsDefault = false
    };

    private static PromptEntrySettingsDto CreateChineseLiteraryTranslator() => new()
    {
        Id = "builtin-literary-translator-zh",
        Name = "\u6587\u5b66\u7ffb\u8bd1\uff08\u4e2d\u6587\uff09",
        Content = TranslationPromptDefaults.ChineseLiteraryTranslatorRole,
        IsDefault = false
    };

    private static bool UpgradeLegacyBuiltInPrompt(
        PromptEntrySettingsDto prompt,
        string legacyContent,
        string currentContent,
        string currentName,
        string legacyEnglishName,
        string legacyChineseName)
    {
        if (!string.Equals(prompt.Content, legacyContent, StringComparison.Ordinal))
            return false;

        prompt.Content = currentContent;
        if (string.Equals(prompt.Name, legacyEnglishName, StringComparison.Ordinal)
            || string.Equals(prompt.Name, legacyChineseName, StringComparison.Ordinal))
        {
            prompt.Name = currentName;
        }
        return true;
    }

    private static string GetLocalizedName(string chineseName, string englishName) =>
        string.Equals(
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? chineseName
            : englishName;
}
