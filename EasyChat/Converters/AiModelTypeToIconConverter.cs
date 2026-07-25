using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyChat.Models.Configuration;

namespace EasyChat.Converters;

/// <summary>
///     Static converters for AiModelType to use in XAML bindings.
/// </summary>
public static class AiModelTypeConverters
{
    public static readonly IValueConverter ToIcon = new AiModelTypeToIconConverter();
    public static readonly IValueConverter IsOpenAi = new AiModelTypeMatchConverter(AiModelType.OpenAi);
    public static readonly IValueConverter IsGemini = new AiModelTypeMatchConverter(AiModelType.Gemini);
    public static readonly IValueConverter IsClaude = new AiModelTypeMatchConverter(AiModelType.Claude);
}

/// <summary>
///     Converts AiModelType to corresponding icon Bitmap for Image Source binding.
/// </summary>
public class AiModelTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AiModelType modelType)
        {
            var iconPath = modelType switch
            {
                AiModelType.OpenAi => "avares://EasyChat/Assets/Images/Engine/openai.png",
                AiModelType.Gemini => "avares://EasyChat/Assets/Images/Engine/gemini.png",
                AiModelType.Claude => "avares://EasyChat/Assets/Images/Engine/claude.png",
                AiModelType.DeepSeek => "avares://EasyChat/Assets/Images/Engine/deepseek.png",
                AiModelType.Custom => "avares://EasyChat/Assets/Images/Engine/custom.png",
                _ => null
            };

            if (iconPath != null)
                try
                {
                    var uri = new Uri(iconPath);
                    using var stream = AssetLoader.Open(uri);
                    return new Bitmap(stream);
                }
                catch
                {
                    return null;
                }
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
///     Generic converter to check if the value matches a specific AiModelType.
/// </summary>
public class AiModelTypeMatchConverter : IValueConverter
{
    private readonly AiModelType _targetType;

    public AiModelTypeMatchConverter(AiModelType targetType)
    {
        _targetType = targetType;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is AiModelType modelType && modelType == _targetType;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
