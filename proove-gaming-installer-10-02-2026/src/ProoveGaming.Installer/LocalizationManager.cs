using System.Globalization;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.Installer;

public static class LocalizationManager
{
	private const string AssemblyName = "ProoveGaming.Installer";
	private static readonly string EnglishResourcePath =
		   $"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.EnglishLanguageCode}.axaml";
	private static readonly string UkrainianResourcePath =
		$"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.UkrainianLanguageCode}.axaml";

	public static string CurrentLanguageCode { get; private set; } = LocalizationConstants.EnglishLanguageCode;

	public static void InitializeFromSystemCulture()
	{
		if (TryGetPersistedLanguage(out var persistedLanguageCode))
		{
			SetLanguage(persistedLanguageCode);
			return;
		}

		var languageCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(LocalizationConstants.UkrainianLanguageCode, StringComparison.OrdinalIgnoreCase)
			 ? LocalizationConstants.UkrainianLanguageCode
			 : LocalizationConstants.EnglishLanguageCode;

		SetLanguage(languageCode);
	}

	public static void SetLanguage(string languageCode)
	{
		var normalizedCode = LanguagePreferenceStore.NormalizeLanguage(languageCode);
		var resourcePath = normalizedCode == LocalizationConstants.UkrainianLanguageCode ? UkrainianResourcePath : EnglishResourcePath;

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

	public static void PersistCurrentLanguage()
		=> PersistLanguage(CurrentLanguageCode);

	private static bool TryGetPersistedLanguage(out string languageCode)
		=> LanguagePreferenceStore.TryGetLanguage(out languageCode);

	private static void PersistLanguage(string languageCode)
		=> LanguagePreferenceStore.SetLanguage(languageCode);

	public static string GetString(string key)
	{
		if (Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true)
		{
			return value as string ?? key;
		}

		return key;
	}
}
