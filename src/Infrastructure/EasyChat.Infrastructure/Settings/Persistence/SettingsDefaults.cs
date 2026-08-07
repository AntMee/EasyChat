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

    public static List<PromptEntrySettingsDto> CreateBuiltInPrompts() =>
    [
        new PromptEntrySettingsDto
        {
            Id = DefaultPromptId,
            Name = GetLocalizedName("\u4e13\u4e1a\u7ffb\u8bd1", "Professional translator"),
            Content = TranslationPromptDefaults.DefaultRole,
            IsDefault = true
        },
        new PromptEntrySettingsDto
        {
            Id = "builtin-technical-translator",
            Name = GetLocalizedName("\u6280\u672f\u7ffb\u8bd1", "Technical translator"),
            Content = TranslationPromptDefaults.TechnicalTranslatorRole,
            IsDefault = false
        },
        new PromptEntrySettingsDto
        {
            Id = "builtin-natural-localizer",
            Name = GetLocalizedName("\u81ea\u7136\u672c\u5730\u5316", "Natural localizer"),
            Content = TranslationPromptDefaults.NaturalLocalizerRole,
            IsDefault = false
        },
        new PromptEntrySettingsDto
        {
            Id = "builtin-literary-translator",
            Name = GetLocalizedName("\u6587\u5b66\u7ffb\u8bd1", "Literary translator"),
            Content = TranslationPromptDefaults.LiteraryTranslatorRole,
            IsDefault = false
        }
    ];

    private static string GetLocalizedName(string chineseName, string englishName) =>
        string.Equals(
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? chineseName
            : englishName;
}
