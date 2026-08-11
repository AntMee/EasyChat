using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EasyChat.Presentation.Features.Shell;
using ShadUI;

namespace EasyChat.Presentation.Foundation.Formatting;

public static class HomeStatusKindToBrushConverter
{
    public static readonly IValueConverter Background = new StatusBrushConverter("SuccessColor10", "WarningColor10");
    public static readonly IValueConverter Border = new StatusBrushConverter("SuccessColor60", "WarningColor60");
    public static readonly IValueConverter Foreground = new StatusBrushConverter("SuccessColor", "WarningColor");
    public static readonly IValueConverter Dot = new StatusBrushConverter("SuccessColor", "WarningColor");

    private sealed class StatusBrushConverter(string successKey, string warningKey) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var key = value is HomeStatusKind.Warning ? warningKey : successKey;
            if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) == true)
            {
                return resource switch
                {
                    Color color => new SolidColorBrush(color),
                    IBrush brush => brush,
                    _ => Brushes.Transparent
                };
            }

            return Brushes.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
