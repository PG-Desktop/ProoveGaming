using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProoveGaming.Common.Localization;

public sealed class LanguageOption
{
	public string Code { get; init; } = string.Empty;
	public string DisplayName { get; init; } = string.Empty;
	public string FlagIconUri { get; init; } = string.Empty;

	public Bitmap? FlagImage => LoadBitmap(FlagIconUri);

	private static Bitmap? LoadBitmap(string uri)
	{
		if (string.IsNullOrEmpty(uri)) return null;
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
}
