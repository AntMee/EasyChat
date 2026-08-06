using System.Globalization;
using System.Reflection;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Lang;
using Material.Icons;

namespace EasyChat.Presentation.Foundation.Formatting;

public static class AiModelTypeConverters
{
    public static readonly IValueConverter ToIcon = new AiModelTypeToIconConverter();
    public static readonly IValueConverter IsOpenAi = new AiModelTypeMatchConverter(AiModelType.OpenAi);
    public static readonly IValueConverter IsGemini = new AiModelTypeMatchConverter(AiModelType.Gemini);
    public static readonly IValueConverter IsClaude = new AiModelTypeMatchConverter(AiModelType.Claude);
}

public sealed class AiModelTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiModelType modelType
            ? AssetIconLoader.Load(modelType switch
            {
                AiModelType.OpenAi => "openai.png",
                AiModelType.Gemini => "gemini.png",
                AiModelType.Claude => "claude.png",
                AiModelType.DeepSeek => "deepseek.png",
                AiModelType.Custom => "custom.png",
                _ => null
            })
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AiModelTypeMatchConverter(AiModelType expected) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AiModelType actual && actual == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class EngineConverters
{
    public static readonly IValueConverter ToIcon = new EngineNameToIconConverter();
    public static readonly IValueConverter HasIcon = new EngineHasIconConverter();
    public static readonly IValueConverter HasNoIcon = new EngineHasNoIconConverter();
}

public sealed class EngineNameToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AssetIconLoader.Load(AssetIconLoader.ResolveEngineFile(value as string));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EngineHasIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        AssetIconLoader.Exists(AssetIconLoader.ResolveEngineFile(value as string));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EngineHasNoIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !AssetIconLoader.Exists(AssetIconLoader.ResolveEngineFile(value as string));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class EngineTypeToBoolConverters
{
    public static readonly IValueConverter AiModel = new EngineTypeToBoolConverter("AiModel");
    public static readonly IValueConverter MachineTrans = new EngineTypeToBoolConverter("MachineTrans");
}

public sealed class EngineTypeToBoolConverter(string expected) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string actual && actual == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? expected : BindingOperations.DoNothing;
}

public static class LanguageFlagConverters
{
    public static readonly IValueConverter ToIcon = new LanguageFlagToIconConverter();
    public static readonly IValueConverter HasIcon = new LanguageFlagHasIconConverter();
    public static readonly IValueConverter HasNoIcon = new LanguageFlagHasNoIconConverter();
}

public static class LanguageSettingsConverters
{
    public static readonly IValueConverter ToDisplayName = new LanguageSettingsDisplayNameConverter();
}

/// <summary>
/// Resolves a language item's display text for the current UI culture: picks
/// <c>ChineseName</c>/<c>EnglishName</c> (Chinese UI shows Chinese names, any
/// other UI shows English names) and falls back to <c>DisplayName</c> for item
/// types that do not expose the two names separately. Works with any language
/// item type via reflection, so it can be used inside generic controls.
/// </summary>
public sealed class LanguageDisplayNameConverter : IValueConverter
{
    public static readonly LanguageDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return null;

        var type = value.GetType();
        var chineseName = GetStringProperty(type, value, "ChineseName");
        var englishName = GetStringProperty(type, value, "EnglishName");
        if (chineseName is not null || englishName is not null)
            return LanguageDisplayNames.ForUi(chineseName, englishName ?? string.Empty);

        return GetStringProperty(type, value, "DisplayName");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string? GetStringProperty(Type type, object instance, string name) =>
        type.GetProperty(name)?.GetValue(instance) as string;
}

public static class SpeechRecognitionModelConverters
{
    public static readonly IValueConverter ToDisplayName = new SpeechRecognitionModelDisplayNameConverter();
}

public sealed class SpeechRecognitionModelDisplayNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SpeechRecognitionModel model
            ? LanguageDisplayNames.ForUi(model.ChineseName, model.EnglishName)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LanguageSettingsDisplayNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LanguageSettings language
            ? LanguageDisplayNames.ForUi(language.ChineseName, language.EnglishName)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LanguageFlagToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return LanguageFlagAssetLoader.Load(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LanguageFlagHasIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LanguageFlagAssetLoader.Exists(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LanguageFlagHasNoIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !LanguageFlagAssetLoader.Exists(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TranslationEngineStringToIconKindConverter : IValueConverter
{
    public static readonly TranslationEngineStringToIconKindConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string provider)
        {
            if (provider.Equals("AiModel", StringComparison.OrdinalIgnoreCase)
                || provider.Equals(Resources.AIEngine, StringComparison.OrdinalIgnoreCase))
                return MaterialIconKind.Robot;
            if (provider.Equals("MachineTrans", StringComparison.OrdinalIgnoreCase)
                || provider.Equals(Resources.MachineTranslation, StringComparison.OrdinalIgnoreCase))
                return MaterialIconKind.Translate;
        }

        return MaterialIconKind.HelpCircleOutline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal static class AssetIconLoader
{
    public static string? ResolveEngineFile(string? engine) => engine?.ToLowerInvariant() switch
    {
        "baidu" => "Baidu.png",
        "tencent" => "Tencent.png",
        "google" => "Google.png",
        "deepl" => "DeepL.png",
        "bing" => "Bing.png",
        "youdao" => "Youdao.png",
        _ when engine?.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) == true => "openai.png",
        _ when engine?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true => "gemini.png",
        _ when engine?.Contains("Claude", StringComparison.OrdinalIgnoreCase) == true => "claude.png",
        _ => null
    };

    public static Bitmap? Load(string? file)
    {
        if (file is null)
            return null;

        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://EasyChat.Desktop/Assets/Images/Engine/{file}"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(string? file)
    {
        if (file is null)
            return false;

        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://EasyChat.Desktop/Assets/Images/Engine/{file}"));
            return stream.CanRead;
        }
        catch
        {
            return false;
        }
    }
}

internal static class LanguageFlagAssetLoader
{
    private const string AssetRoot = "avares://EasyChat.Desktop/Assets/Images/Flags/mini/";

    public static Bitmap? Load(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;

        try
        {
            using var stream = AssetLoader.Open(new Uri($"{AssetRoot}{file}"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public static bool Exists(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return false;

        try
        {
            return AssetLoader.Exists(new Uri($"{AssetRoot}{file}"));
        }
        catch
        {
            return false;
        }
    }
}
