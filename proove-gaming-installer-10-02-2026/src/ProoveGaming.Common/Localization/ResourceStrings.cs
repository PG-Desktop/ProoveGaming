using Avalonia;
using Avalonia.Styling;

namespace ProoveGaming.Common.Localization;

/// <summary>Reads localized strings out of the merged application resource dictionaries.
/// Shared controls use this because they cannot reference a host's LocalizationManager.</summary>
public static class ResourceStrings
{
	/// <summary>Resolves <paramref name="key"/> from the application resources, falling back to
	/// <paramref name="fallback"/> when the key is missing or no Application is running (design
	/// time, headless tests) — so an incomplete translation never paints a raw key into the UI.</summary>
	public static string Get(string key, string fallback)
	{
		if (Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true)
		{
			return value as string ?? fallback;
		}

		return fallback;
	}
}
