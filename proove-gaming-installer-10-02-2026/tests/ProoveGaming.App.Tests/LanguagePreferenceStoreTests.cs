using ProoveGaming.Common.Localization;
using Xunit;

namespace ProoveGaming.App.Tests;

// Only the pure mapping rule is tested here: TryGetLanguage/SetLanguage target the real
// HKCU\Software\ProoveGaming\Settings and must not be exercised against the developer's
// actual app preference — the registry round trip is covered by manual QA.
public class LanguagePreferenceStoreTests
{
    [Theory]
    [InlineData("uk")]
    [InlineData("UK")]
    [InlineData("Uk")]
    public void NormalizeLanguage_MapsUkCaseInsensitively(string input)
    {
        Assert.Equal(LocalizationConstants.UkrainianLanguageCode, LanguagePreferenceStore.NormalizeLanguage(input));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("de")]
    [InlineData("")]
    public void NormalizeLanguage_MapsEnAndUnknownToEnglish(string input)
    {
        // Everything that isn't Ukrainian falls back to English — the whole persistence
        // chain (App, Installer, Uninstaller) relies on this rule.
        Assert.Equal(LocalizationConstants.EnglishLanguageCode, LanguagePreferenceStore.NormalizeLanguage(input));
    }
}
