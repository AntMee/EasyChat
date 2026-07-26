using System;
using System.Globalization;
using Avalonia.Data.Converters;
using EasyChat.Constants;
using EasyChat.Lang;
using Material.Icons;

namespace EasyChat.Converters;

public class TranslationEngineStringToIconKindConverter : IValueConverter
{
    public static readonly TranslationEngineStringToIconKindConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            // ComboBox values are persisted provider ids, while the labels are localized.
            if (s.Equals(TextAssistConstants.AiProvider, StringComparison.OrdinalIgnoreCase)
                || s.Equals(Resources.AIEngine, StringComparison.OrdinalIgnoreCase))
                return MaterialIconKind.Robot;
            if (s.Equals(TextAssistConstants.MachineProvider, StringComparison.OrdinalIgnoreCase)
                || s.Equals(Resources.MachineTranslation, StringComparison.OrdinalIgnoreCase))
                return MaterialIconKind.Translate;
        }
        return MaterialIconKind.HelpCircleOutline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
