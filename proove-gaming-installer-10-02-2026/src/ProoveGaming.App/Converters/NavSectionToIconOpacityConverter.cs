using System;
using System.Globalization;
using Avalonia.Data.Converters;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Converters;

public sealed class NavSectionToIconOpacityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not NavSection activeSection)
		{
			return 0.5;
		}

		if (!TryGetSection(parameter, out var section))
		{
			return 0.5;
		}

		return activeSection == section ? 1d : 0.5d;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();

	private static bool TryGetSection(object? parameter, out NavSection section)
	{
		section = default;
		return parameter is string name && Enum.TryParse(name, ignoreCase: true, out section);
	}
}
