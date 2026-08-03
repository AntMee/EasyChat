using System.Globalization;

namespace EasyChat.Presentation.Foundation.Localization;

public static class LanguageDisplayNames
{
    public static string ForUi(
        string? chineseName,
        string englishName,
        CultureInfo? uiCulture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(englishName);

        var culture = uiCulture ?? CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName == "zh" && !string.IsNullOrWhiteSpace(chineseName)
            ? chineseName
            : englishName;
    }
}
