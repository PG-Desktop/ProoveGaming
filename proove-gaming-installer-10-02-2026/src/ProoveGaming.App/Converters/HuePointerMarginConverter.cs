using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class HuePointerMarginConverter : IMultiValueConverter
{
	public static readonly HuePointerMarginConverter Instance = new();

	private const double PointerSize = 24.0;

	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values.Count < 2)
			return new Thickness(0);

		if (values[0] is not double normalized || values[1] is not double width || width <= 0)
			return new Thickness(0);

		normalized = Math.Clamp(normalized, 0, 1);
		var left = normalized * width - PointerSize / 2.0;
		left = Math.Clamp(left, 0, Math.Max(0, width - PointerSize));

		return new Thickness(left, 0, 0, 0);
	}
}
