using Microsoft.Win32;

namespace ProoveGaming.Common.Localization;

public static class LanguagePreferenceStore
{
	private const string RegistryPath = @"Software\ProoveGaming\Settings";
	private const string RegistryValueName = "AppLanguage";

	public static bool TryGetLanguage(out string languageCode)
	{
        languageCode = LocalizationConstants.EnglishLanguageCode;

		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
			var value = key?.GetValue(RegistryValueName) as string;
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}

			languageCode = NormalizeLanguage(value);
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static void SetLanguage(string languageCode)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
			if (key is null)
			{
				return;
			}

			key.SetValue(RegistryValueName, NormalizeLanguage(languageCode), RegistryValueKind.String);
		}
		catch
		{
		}
	}

	public static string NormalizeLanguage(string languageCode) =>
        languageCode.Equals(LocalizationConstants.UkrainianLanguageCode, StringComparison.OrdinalIgnoreCase)
			? LocalizationConstants.UkrainianLanguageCode
			: LocalizationConstants.EnglishLanguageCode;
}
