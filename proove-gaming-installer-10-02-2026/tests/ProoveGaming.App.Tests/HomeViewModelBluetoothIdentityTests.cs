using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Identity stamping for a card discovered over the OS Bluetooth stack. BluetoothDeviceResponse
// carries no VID/PID, so the values come from the catalog entry's vendor_id/product_id — without
// them a Bluetooth-only Rampage sent SetDeviceLedColor as VID=0/PID=0 and the DeviceHost dropped
// it. The helper is static for the same reason as NotificationGroupViewModel's: HomeViewModel's
// constructor needs the full device/session service graph, so tests drive the helper directly.
public class HomeViewModelBluetoothIdentityTests
{
	private static SingleCardViewModel Card() => new()
	{
		Name = "RAMPAGE",
		DeviceTypeLabel = "Wireless Keyboard",
	};

	private static DeviceCatalogEntry RampageBluetoothEntry() => new()
	{
		Name = "RAMPAGE",
		VendorId = 10473,
		ProductId = 12888,
		SettingsVendorId = 10473,
		SettingsProductId = 12888,
	};

	[Fact]
	public void StampsRoutingAndSettingsIdentity_FromTheEntry()
	{
		var card = Card();

		HomeViewModel.ApplyBluetoothIdentity(card, RampageBluetoothEntry());

		Assert.Equal(10473, card.VendorId);
		Assert.Equal(12888, card.ProductId);
		Assert.Equal(12888, card.SettingsProductId);
	}

	// An entry from an older DeviceHost carries 0/0 — the card must stay exactly as it was, so a
	// mixed-version install behaves like the app did before the fields existed.
	[Fact]
	public void EntryWithoutIdentity_LeavesTheCardUntouched()
	{
		var card = Card();
		card.VendorId = 10473;
		card.ProductId = 12888;
		card.SettingsProductId = 12888;
		card.SettingsDeviceKey = "container-id";

		HomeViewModel.ApplyBluetoothIdentity(card, new DeviceCatalogEntry { Name = "RAMPAGE" });

		Assert.Equal(10473, card.VendorId);
		Assert.Equal(12888, card.ProductId);
		Assert.Equal(12888, card.SettingsProductId);
		Assert.Equal("container-id", card.SettingsDeviceKey);
	}

	// A merged-transport product pins its settings bucket to the product-wide id with an empty
	// key, so a transport switch never moves saved RGB/buttons — same rule as ApplyDeviceIdentity.
	[Fact]
	public void MergedTransportEntry_PinsTheSharedSettingsBucket()
	{
		var card = Card();
		card.SettingsDeviceKey = "container-id";
		var entry = RampageBluetoothEntry();
		entry.MergeTransports = true;
		entry.ProductId = 33382;
		entry.SettingsProductId = 64257;
		// The transport enumerates under a vendor of its own — RAMPAGE's receiver is 1389 against
		// the board's 10473, COSMO's and STARLIGHT's Bluetooth links 1494 against 3468 — which is
		// precisely the case that used to give one physical device a second settings bucket.
		entry.VendorId = 1494;

		HomeViewModel.ApplyBluetoothIdentity(card, entry);

		Assert.Equal(33382, card.ProductId);
		Assert.Equal(64257, card.SettingsProductId);
		// The pin, not the live transport: the whole point of the fix.
		Assert.Equal(10473, card.SettingsVendorId);
		// ...while ROUTING still follows the live transport, so commands reach the hardware.
		Assert.Equal(1494, card.VendorId);
		Assert.True(card.MergeTransports);
		Assert.Equal(string.Empty, card.SettingsDeviceKey);
	}

	// Windows can report the board's Bluetooth as connected while the cable (or the 2.4G receiver)
	// is the transport actually carrying traffic. The card's identity must stay with the live
	// transport: re-stamping it from the Bluetooth entry would route writes at the interface that
	// does not carry them and repoint the settings bucket the details page reads.
	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void CardOnLiveNonBluetoothTransport_KeepsItsIdentity(bool hasWire, bool hasDongle)
	{
		var card = Card();
		card.HasWire = hasWire;
		card.HasDongle = hasDongle;
		card.VendorId = 10473;
		card.ProductId = 12888;
		card.SettingsProductId = 12888;
		card.SettingsDeviceKey = "container-id";
		var entry = RampageBluetoothEntry();
		entry.ProductId = 12288;
		entry.SettingsProductId = 12288;

		HomeViewModel.ApplyBluetoothIdentity(card, entry);

		Assert.Equal(10473, card.VendorId);
		Assert.Equal(12888, card.ProductId);
		Assert.Equal(12888, card.SettingsProductId);
		Assert.Equal("container-id", card.SettingsDeviceKey);
	}

	// A non-merged card keeps whatever settings key it owns: Bluetooth has no Container ID to
	// offer, and rewriting the key is how stored settings get stranded.
	[Fact]
	public void NonMergedEntry_KeepsTheCardsSettingsKey()
	{
		var card = Card();
		card.SettingsDeviceKey = "container-id";

		HomeViewModel.ApplyBluetoothIdentity(card, RampageBluetoothEntry());

		Assert.Equal("container-id", card.SettingsDeviceKey);
	}
}
