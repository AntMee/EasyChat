using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Lang;
using Material.Icons;

namespace EasyChat.Presentation.Foundation.Formatting;

public sealed class BoolToColorConverter : IMultiValueConverter
{
    public static readonly BoolToColorConverter Instance = new();
    private static readonly SolidColorBrush Green = new(Color.Parse("#4CAF50"));
    private static readonly SolidColorBrush Gray = new(Color.Parse("#9E9E9E"));

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values[0] is true ? Green : Gray;
}

public sealed class BoolToGridLengthConverter : IValueConverter
{
    public static readonly BoolToGridLengthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
            return new GridLength(0);

        return double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
               && size > 0
            ? new GridLength(size)
            : new GridLength(1, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class BoolToIconConverters
{
    public static readonly IValueConverter Animation = new BoolToIconConverter(MaterialIconKind.Pause, MaterialIconKind.Play);
    public static readonly IValueConverter WindowLock = new BoolToIconConverter(MaterialIconKind.Unlocked, MaterialIconKind.Lock);
    public static readonly IValueConverter Visibility = new BoolToIconConverter(MaterialIconKind.EyeClosed, MaterialIconKind.Eye);
    public static readonly IValueConverter Simple = new BoolToIconConverter(MaterialIconKind.Close, MaterialIconKind.Ticket);
}

public sealed class BoolToIconConverter(
    MaterialIconKind trueIcon = MaterialIconKind.Help,
    MaterialIconKind falseIcon = MaterialIconKind.Help) : IValueConverter
{
    public static readonly BoolToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
            return null;

        var on = trueIcon;
        var off = falseIcon;
        if (parameter is string pair)
        {
            var icons = pair.Split(';', 2);
            if (icons.Length == 2)
            {
                Enum.TryParse(icons[0], true, out on);
                Enum.TryParse(icons[1], true, out off);
            }
        }

        return state ? on : off;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool state)
            return null;

        var off = "False";
        var on = "True";
        if (parameter is string pair)
        {
            var keys = pair.Split('|', 2);
            if (keys.Length == 2)
            {
                off = Resources.ResourceManager.GetString(keys[0], culture) ?? keys[0];
                on = Resources.ResourceManager.GetString(keys[1], culture) ?? keys[1];
            }
        }

        return state ? on : off;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        string text when Color.TryParse(text, out var color) => new SolidColorBrush(color),
        Color color => new SolidColorBrush(color),
        _ => Brushes.Transparent
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color.ToString() : "#00000000";
}

public sealed class ColorToHexConverter : IValueConverter
{
    public static readonly ColorToHexConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        string text when Color.TryParse(text, out var color) => color,
        Color color => color.ToString(),
        _ => Colors.Transparent
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Color color => color.ToString(),
        string text when Color.TryParse(text, out var color) => color,
        string => Colors.Transparent,
        _ => "#00000000"
    };
}

public sealed class EnumToDescriptionConverter : IValueConverter
{
    public static readonly EnumToDescriptionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ClosingBehavior behavior)
        {
            return behavior switch
            {
                ClosingBehavior.Ask => Resources.Option_Ask,
                ClosingBehavior.ExitApp => Resources.Option_Exit,
                ClosingBehavior.MinimizeToTray => Resources.Option_Minimize,
                _ => behavior.ToString()
            };
        }

        if (value is not Enum enumValue)
            return value?.ToString();

        var field = value.GetType().GetField(enumValue.ToString());
        return field?.GetCustomAttributes(typeof(DescriptionAttribute), false)
                   .OfType<DescriptionAttribute>()
                   .FirstOrDefault()?.Description
               ?? enumValue.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public static readonly EqualityMultiConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 2 && Equals(values[0], values[1]);
}

public static class LangNameToIndexIntConverters
{
    public static readonly IValueConverter Lang = new LangNameToIndexIntConverter();
}

public sealed class LangNameToIndexIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as string == "Simplified Chinese" ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 1 ? "Simplified Chinese" : "English";
}

public static class MathConverters
{
    public static readonly IValueConverter Multiply =
        new FuncValueConverter<double, object, double>((value, parameter) => parameter switch
        {
            double factor => value * factor,
            string text when double.TryParse(text, out var factor) => value * factor,
            _ => value
        });
}

public sealed class StringToFontFamilyConverter : IValueConverter
{
    public static readonly StringToFontFamilyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name && !string.IsNullOrEmpty(name) ? new FontFamily(name) : FontFamily.Default;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FontFamily family ? family.Name : string.Empty;
}
