using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.Uninstaller;

internal static class LocalizationManager
{
	private const string AssemblyName = "ProoveGaming.Uninstaller";
	private static readonly string EnglishResourcePath =
		$"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.EnglishLanguageCode}.axaml";
	private static readonly string UkrainianResourcePath =
		$"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.UkrainianLanguageCode}.axaml";

	public static string CurrentLanguageCode { get; private set; } = LocalizationConstants.EnglishLanguageCode;

	public static void InitializeFromPersistedPreference()
	{
#if DEBUG
		if (TryGetDebugOverride(out var debugCode))
		{
			SetLanguage(debugCode);
			return;
		}
#endif

		if (LanguagePreferenceStore.TryGetLanguage(out var persistedCode))
		{
			SetLanguage(persistedCode);
			return;
		}

		SetLanguage(LocalizationConstants.EnglishLanguageCode);
	}

	public static void SetLanguage(string languageCode)
	{
		var normalizedCode = LanguagePreferenceStore.NormalizeLanguage(languageCode);
		var resourcePath = normalizedCode == LocalizationConstants.UkrainianLanguageCode
			? UkrainianResourcePath
			: EnglishResourcePath;

		var app = Application.Current;
		if (app is null)
		{
			return;
		}

		var merged = app.Resources.MergedDictionaries;
		for (var i = merged.Count - 1; i >= 0; i--)
		{
			if (merged[i] is ResourceInclude include &&
				include.Source?.OriginalString.Contains(LocalizationConstants.StringsResourceSegment, StringComparison.OrdinalIgnoreCase) == true)
			{
				merged.RemoveAt(i);
			}
		}

		merged.Add(new ResourceInclude(baseUri: null) { Source = new Uri(resourcePath) });
		CurrentLanguageCode = normalizedCode;
	}

	public static string GetString(string key)
	{
		if (Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true)
		{
			return value as string ?? key;
		}

		return key;
	}

#if DEBUG
	private static bool TryGetDebugOverride(out string languageCode)
	{
		languageCode = LocalizationConstants.EnglishLanguageCode;
		var value = Environment.GetEnvironmentVariable("PROOVE_UNINSTALLER_LANG");
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		languageCode = value;
		return true;
	}
#endif
}
