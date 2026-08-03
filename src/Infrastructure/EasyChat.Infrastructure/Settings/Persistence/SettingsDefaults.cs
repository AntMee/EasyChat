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

    public static PromptEntrySettingsDto CreateDefaultPrompt() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Default",
        Content = TranslationPromptDefaults.DefaultContent,
        IsDefault = true
    };

    private static string GetLocalizedName(string chineseName, string englishName) =>
        string.Equals(
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? chineseName
            : englishName;
}
