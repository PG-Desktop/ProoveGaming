using System;
using System.Globalization;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.App;

public static class LocalizationManager
{
	private const string AssemblyName = "ProoveGaming.App";
	private static readonly string EnglishResourcePath = $"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.EnglishLanguageCode}.axaml";
	private static readonly string UkrainianResourcePath = $"avares://{AssemblyName}/{LocalizationConstants.StringsResourceSegment}{LocalizationConstants.UkrainianLanguageCode}.axaml";
	
	public static event Action? LanguageChanged;
	
	public static string CurrentLanguageCode { get; private set; } = LocalizationConstants.EnglishLanguageCode;

	public static void InitializeFromSystemCulture()
	{
#if DEBUG
		// Debug builds follow the same restore path as Release below (persisted preference,
		// else system culture) so language persistence is verifiable in dev builds — a
		// developer with `uk` persisted deliberately starts in Ukrainian. Uncomment to force
		// Ukrainian regardless of preference or culture when exercising the uk localisation.
		// Note SetLanguage PERSISTS the forced value to the shared registry preference that
		// the Installer, Uninstaller and Release builds read — re-comment and switch back via
		// Settings (or delete HKCU\Software\ProoveGaming\Settings\AppLanguage) when done:
		//SetLanguage(LocalizationConstants.UkrainianLanguageCode);
		//return;
#endif
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
		var normalized = LanguagePreferenceStore.NormalizeLanguage(languageCode);
		if (string.Equals(CurrentLanguageCode, normalized, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var app = Application.Current;
		if (app is not null)
		{
			var dictionaries = app.Resources.MergedDictionaries;
			for (var i = dictionaries.Count - 1; i >= 0; i--)
			{
				if (dictionaries[i] is ResourceInclude include &&
					 include.Source?.OriginalString.Contains(LocalizationConstants.StringsResourceSegment, StringComparison.OrdinalIgnoreCase) == true)
				{
					dictionaries.RemoveAt(i);
				}
			}

			var resourcePath = normalized == LocalizationConstants.UkrainianLanguageCode ? UkrainianResourcePath : EnglishResourcePath;
			dictionaries.Add(new ResourceInclude(baseUri: null) { Source = new Uri(resourcePath) });
		}

		CurrentLanguageCode = normalized;
		PersistLanguage(normalized);
		LanguageChanged?.Invoke();
	}

	private static bool TryGetPersistedLanguage(out string languageCode)
	   => LanguagePreferenceStore.TryGetLanguage(out languageCode);

	private static void PersistLanguage(string languageCode)
	   => LanguagePreferenceStore.SetLanguage(languageCode);

	public static string GetString(string key) => ResourceStrings.Get(key, key);
}
