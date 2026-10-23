using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WgRdp.Views;

public sealed class LevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "INFO" => Brush.Parse("#5BA3E6"),
            "WARN" => Brush.Parse("#E6B450"),
            "ERROR" => Brush.Parse("#E06C75"),
            _ => Brush.Parse("#8B9BB0"),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
