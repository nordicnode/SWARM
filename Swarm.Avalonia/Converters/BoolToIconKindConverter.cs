using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace Swarm.Avalonia.Converters;

/// <summary>
/// Converts a boolean (IsDirectory) to MaterialIconKind.
/// </summary>
public class BoolToIconKindConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isDirectory)
        {
            return isDirectory ? MaterialIconKind.Folder : MaterialIconKind.FileDocumentOutline;
        }
        return MaterialIconKind.FileDocumentOutline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("ConvertBack is not supported for one-way converters");
    }
}
