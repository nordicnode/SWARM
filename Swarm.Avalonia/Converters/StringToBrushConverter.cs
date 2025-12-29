using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Swarm.Avalonia.Converters;

/// <summary>
/// Converts a hex color string to a SolidColorBrush.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public static StringToBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorHex && !string.IsNullOrEmpty(colorHex))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(colorHex));
            }
            catch
            {
                // Fallback to green if parsing fails
                return new SolidColorBrush(Color.Parse("#34d399"));
            }
        }
        return new SolidColorBrush(Color.Parse("#34d399"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
