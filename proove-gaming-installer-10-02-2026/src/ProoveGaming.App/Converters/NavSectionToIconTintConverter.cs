using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Converters;

public sealed class NavSectionToIconTintConverter : IValueConverter
{
	private static readonly Color ActiveColor = Color.Parse("#6357FF");

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not NavSection activeSection)
		{
			return null;
		}

		if (!TryGetSection(parameter, out var section))
		{
			return null;
		}

		return activeSection == section ? ActiveColor : null;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();

	private static bool TryGetSection(object? parameter, out NavSection section)
	{
		section = default;
		return parameter is string name && Enum.TryParse(name, ignoreCase: true, out section);
	}
}
