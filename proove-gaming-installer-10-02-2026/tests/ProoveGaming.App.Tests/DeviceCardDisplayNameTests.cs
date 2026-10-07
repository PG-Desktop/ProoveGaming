using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// A card shows the catalog's DisplayName when it has one (the "PG TAIPAN" mouse is shown as
// TAIPAN MAX) and its Name otherwise. Name itself never changes: device matching, the stored card
// row and the protocol choice all key on it.
public class DeviceCardDisplayNameTests
{
    [Fact]
    public void WithACatalogDisplayName_TheCardShowsIt_AndKeepsItsName()
    {
        var card = new SingleCardViewModel { Name = "PG TAIPAN", DeviceTypeLabel = "Wireless Mouse" };

        card.CatalogDisplayName = "TAIPAN MAX";

        Assert.Equal("TAIPAN MAX", card.DisplayName);
        Assert.Equal("PG TAIPAN", card.Name);
    }

    [Fact]
    public void WithoutOne_TheCardShowsItsName()
    {
        var card = new SingleCardViewModel { Name = "BLAZE", DeviceTypeLabel = "Wired Mouse" };

        Assert.Equal("BLAZE", card.DisplayName);
    }

    [Fact]
    public void SettingTheDisplayName_RefreshesWhatTheCardShows()
    {
        var card = new SingleCardViewModel { Name = "PG TAIPAN", DeviceTypeLabel = "Wireless Mouse" };
        var raised = false;
        card.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(DeviceCardViewModel.DisplayName);

        card.CatalogDisplayName = "TAIPAN MAX";

        Assert.True(raised);
    }
}
