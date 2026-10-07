using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ProoveGaming.App.Converters;

public class PlanePointerMarginConverter : IMultiValueConverter
{
	public static readonly PlanePointerMarginConverter Instance = new();

	private const double PointerSize = 24.0;

	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values.Count < 4)
			return new Thickness(0);

		if (values[0] is not double saturation || values[1] is not double value || values[2] is not double width || values[3] is not double height || width <= 0 || height <= 0)
			return new Thickness(0);

		saturation = Math.Clamp(saturation, 0, 1);
		value = Math.Clamp(value, 0, 1);

		var left = saturation * width - PointerSize / 2.0;
		var top = (1.0 - value) * height - PointerSize / 2.0;

		left = Math.Clamp(left, 0, Math.Max(0, width - PointerSize));
		top = Math.Clamp(top, 0, Math.Max(0, height - PointerSize));

		return new Thickness(left, top, 0, 0);
	}
}
