using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProoveGaming.App.Converters;

/// <summary>
/// Converts an <c>avares://</c> URI string into a <see cref="Bitmap"/> so it can
/// be bound to <c>Image.Source</c>. Used for raster device hero images that come
/// from the VM (e.g. <c>DeviceCardViewModel.ImageUri</c>).
/// </summary>
public sealed class UriToBitmapConverter : IValueConverter
{
	public static readonly UriToBitmapConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not string uri || string.IsNullOrWhiteSpace(uri))
			return null;

		try
		{
			using var stream = AssetLoader.Open(new Uri(uri));
			return new Bitmap(stream);
		}
		catch
		{
			return null;
		}
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
