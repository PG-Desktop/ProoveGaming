using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class PercentageWidthConverter : IMultiValueConverter
{
	public static readonly PercentageWidthConverter Instance = new();

	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values.Count >= 2 && values[0] is double pct && values[1] is double totalWidth && totalWidth > 0)
			return pct / 100.0 * totalWidth;
		return 0.0;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
