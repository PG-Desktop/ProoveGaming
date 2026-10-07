using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public sealed class BoolToActiveOpacityConverter : IValueConverter
{
	public static readonly BoolToActiveOpacityConverter Instance = new();

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is true ? 1d : 0.5d;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
