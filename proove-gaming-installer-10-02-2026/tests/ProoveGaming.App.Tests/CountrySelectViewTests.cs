using System.Linq;
using ProoveGaming.Common.Controls;
using Xunit;

namespace ProoveGaming.App.Tests;

// Guards the static country-list logic — we don't instantiate the UserControl (that would
// need Avalonia bootstrap); just exercise the static `Countries` list and `ResolveCountryIndex`.
public class CountrySelectViewTests
{
    [Fact]
    public void Countries_IsNonEmpty()
    {
        Assert.NotEmpty(CountrySelectView.Countries);
    }

    [Fact]
    public void Countries_SortedByOrdinalEnglishName()
    {
        var sorted = CountrySelectView.Countries.OrderBy(n => n, System.StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, CountrySelectView.Countries);
    }

    [Theory]
    [InlineData("Ukraine")]
    [InlineData("United States")]
    [InlineData("Poland")]
    public void Countries_ContainsRepresentativeEntries(string name)
    {
        Assert.Contains(name, CountrySelectView.Countries);
    }

    [Theory]
    [InlineData("Russia")]
    [InlineData("Belarus")]
    [InlineData("North Korea")]
    public void Countries_ExcludesProductBlockedRegions(string name)
    {
        Assert.DoesNotContain(name, CountrySelectView.Countries);
    }

    [Fact]
    public void ResolveCountryIndex_ExactDisplayName_Matches()
    {
        var idx = CountrySelectView.ResolveCountryIndex("Ukraine");
        Assert.True(idx >= 0);
        Assert.Equal("Ukraine", CountrySelectView.Countries[idx]);
    }

    [Fact]
    public void ResolveCountryIndex_IsCaseInsensitive()
    {
        var idx = CountrySelectView.ResolveCountryIndex("ukraine");
        Assert.True(idx >= 0);
        Assert.Equal("Ukraine", CountrySelectView.Countries[idx]);
    }

    [Theory]
    [InlineData("UA", "Ukraine")]
    [InlineData("UKR", "Ukraine")]
    [InlineData("US", "United States")]
    [InlineData("USA", "United States")]
    public void ResolveCountryIndex_IsoCode_ResolvesToDisplayName(string alias, string expectedDisplay)
    {
        var idx = CountrySelectView.ResolveCountryIndex(alias);
        Assert.True(idx >= 0);
        Assert.Equal(expectedDisplay, CountrySelectView.Countries[idx]);
    }

    [Fact]
    public void ResolveCountryIndex_ExcludedRegion_ReturnsMinusOne()
    {
        Assert.Equal(-1, CountrySelectView.ResolveCountryIndex("Russia"));
        Assert.Equal(-1, CountrySelectView.ResolveCountryIndex("RU"));
    }

    [Fact]
    public void ResolveCountryIndex_UnknownCountry_ReturnsMinusOne()
    {
        Assert.Equal(-1, CountrySelectView.ResolveCountryIndex("Atlantis"));
    }
}
