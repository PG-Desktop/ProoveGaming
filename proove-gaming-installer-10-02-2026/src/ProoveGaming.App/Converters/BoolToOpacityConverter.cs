using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public sealed class BoolToOpacityConverter : IValueConverter
{
	public double TrueValue { get; set; } = 1d;
	public double FalseValue { get; set; } = 0.3d;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is true ? TrueValue : FalseValue;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
