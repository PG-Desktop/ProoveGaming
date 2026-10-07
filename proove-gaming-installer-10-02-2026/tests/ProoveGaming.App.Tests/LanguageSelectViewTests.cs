using System.Linq;
using ProoveGaming.Common.Controls;
using ProoveGaming.Common.Localization;
using Xunit;

namespace ProoveGaming.App.Tests;

// Guards the static language-list logic — we don't instantiate the UserControl (that would
// need Avalonia bootstrap); just exercise the static `Languages` list.
public class LanguageSelectViewTests
{
    [Fact]
    public void Languages_HasExactlyEnglishAndUkrainian()
    {
        // Order matters: it defines the dropdown row order.
        Assert.Equal(2, LanguageSelectView.Languages.Count);
        Assert.Equal(LocalizationConstants.EnglishLanguageCode, LanguageSelectView.Languages[0].Code);
        Assert.Equal(LocalizationConstants.UkrainianLanguageCode, LanguageSelectView.Languages[1].Code);
    }

    [Fact]
    public void Languages_DisplayNamesAreEndonyms()
    {
        // Names stay in their own language regardless of the current UI language — guards
        // the convention and the regression back to "Ukrainian".
        Assert.Equal("English", LanguageSelectView.Languages[0].DisplayName);
        Assert.Equal("Українська", LanguageSelectView.Languages[1].DisplayName);
    }

    [Fact]
    public void Languages_FlagIconUrisAreNonEmptyAndDistinct()
    {
        var uris = LanguageSelectView.Languages.Select(l => l.FlagIconUri).ToArray();
        Assert.All(uris, uri => Assert.StartsWith("avares://ProoveGaming.Common/", uri));
        Assert.Equal(uris.Length, uris.Distinct().Count());
    }
}
