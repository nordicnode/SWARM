using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Swarm.Helpers;

/// <summary>
/// Converts null or empty string to Visibility.Collapsed, otherwise Visible.
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return Visibility.Collapsed;
        
        if (value is string str && string.IsNullOrWhiteSpace(str))
            return Visibility.Collapsed;
        
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to Visibility.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // If parameter is "Inverse", invert the logic
            if (parameter is string paramStr && paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
                boolValue = !boolValue;
            
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            var result = visibility == Visibility.Visible;
            
            if (parameter is string paramStr && paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
                result = !result;
            
            return result;
        }
        
        return false;
    }
}
