using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class PercentageThumbMarginConverter : IMultiValueConverter
{
	public static readonly PercentageThumbMarginConverter Instance = new();

	private const double ThumbWidth = 9.0;

	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values.Count >= 2 && values[0] is double pct && values[1] is double totalWidth && totalWidth > 0)
		{
			double left = pct / 100.0 * totalWidth - ThumbWidth / 2.0;
			left = Math.Max(0, Math.Min(left, totalWidth - ThumbWidth));
			return new Thickness(left, 0, 0, 0);
		}
		return new Thickness(0);
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
