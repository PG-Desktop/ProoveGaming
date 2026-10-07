using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class StringToUpperConverter : IValueConverter
{
    public static readonly StringToUpperConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s.ToUpperInvariant() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
