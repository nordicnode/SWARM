using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Swarm.Avalonia.Converters;

/// <summary>
/// Converts a file kind string (e.g., "Folder", "File") into a PathGeometry for PathIcon.
/// </summary>
public class IconConverter : IValueConverter
{
    private static readonly Geometry FolderIcon = StreamGeometry.Parse("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    private static readonly Geometry FileIcon = StreamGeometry.Parse("M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string kind)
        {
            return kind == "Folder" ? FolderIcon : FileIcon;
        }
        return FileIcon;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("ConvertBack is not supported for one-way converters");
    }
}
