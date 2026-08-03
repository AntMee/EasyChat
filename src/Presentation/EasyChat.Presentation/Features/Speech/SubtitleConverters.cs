using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using EasyChat.Contracts.Settings;

namespace EasyChat.Presentation.Features.Speech;

public sealed class SubtitleContentConverter : IMultiValueConverter
{
    public static readonly SubtitleContentConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3)
            return string.Empty;

        var original = values[0] as string ?? string.Empty;
        var translated = values[1] as string ?? string.Empty;
        var source = ParseSource(values[2]);
        return source switch
        {
            SubtitleSource.Original => original,
            SubtitleSource.Translated => translated,
            _ => string.Empty
        };
    }

    private static SubtitleSource ParseSource(object? value) => value switch
    {
        SubtitleSource source => source,
        int source => (SubtitleSource)source,
        _ => SubtitleSource.None
    };
}

public sealed class SubtitleLoadingConverter : IMultiValueConverter
{
    public static readonly SubtitleLoadingConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 2
        && values[0] is true
        && (values[1] is SubtitleSource.Translated
            || values[1] is int source && source == (int)SubtitleSource.Translated);
}

public sealed class SubtitleVisibilityConverter : IMultiValueConverter
{
    public static readonly SubtitleVisibilityConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 3
        && !string.IsNullOrEmpty(SubtitleContentConverter.Instance
            .Convert(values, targetType, parameter, culture) as string);
}
