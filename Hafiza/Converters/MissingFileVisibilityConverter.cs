using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Hafiza.Models;

namespace Hafiza.Converters;

public sealed class MissingFileVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ClipboardEntry { Kind: "image" } entry &&
        (string.IsNullOrWhiteSpace(entry.ImagePath) || !File.Exists(entry.ImagePath))
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
