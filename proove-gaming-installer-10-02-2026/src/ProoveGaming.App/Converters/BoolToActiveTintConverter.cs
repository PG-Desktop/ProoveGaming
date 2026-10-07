using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ProoveGaming.App.Converters;

public sealed class BoolToActiveTintConverter : IValueConverter
{
	public static readonly BoolToActiveTintConverter Instance = new();

	private static readonly Color ActiveColor = Color.Parse("#6357FF");

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is true ? ActiveColor : (object?)null;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
