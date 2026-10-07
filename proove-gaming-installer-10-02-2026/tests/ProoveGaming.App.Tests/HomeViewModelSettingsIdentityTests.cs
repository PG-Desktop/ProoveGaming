using System.Linq;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The identity half of docs/plans/fix-settings-bucket-identity. Two defects, one symptom -- the
// user's RGB, DPI, actuation, key remaps and presets "disappear":
//
//   1. The settings bucket was keyed by the LIVE vendor id. RAMPAGE's 2.4 GHz receiver enumerates
//      under vendor 1389 while the board itself is 10473 (and COSMO's and STARLIGHT's Bluetooth
//      links under 1494 against a product vendor of 3468), so one physical device owned TWO
//      buckets and a transport switch moved which one was read.
//   2. A card restored from storage derived its settings bucket from the stored ROUTING product
//      id, so a page opened before discovery reported in read -- and WROTE -- a third bucket that
//      no discovered card would ever address again.
//
// ApplyBluetoothIdentity is exercised by HomeViewModelBluetoothIdentityTests; this file covers the
// pinning rule itself and the restore round-trip.
public class HomeViewModelSettingsIdentityTests
{
	private const int BoardVid = 10473;
	private const int DongleVid = 1389;
	private const int WiredPid = 12888;
	private const int DonglePid = 49271;

	private static DeviceCatalogEntry RampageEntry(int vendorId, int productId) => new()
	{
		Name = "RAMPAGE",
		MergeTransports = true,
		VendorId = vendorId,
		ProductId = productId,
		// Both pins come from the same primary (wired) row, whichever transport this entry is.
		SettingsVendorId = BoardVid,
		SettingsProductId = WiredPid,
	};

	private static SingleCardViewModel Card() => new()
	{
		Name = "RAMPAGE",
		DeviceTypeLabel = "Wireless Keyboard",
	};

	// THE REPRODUCTION for defect 1. Stamp a card from the dongle entry, then from the wired
	// entry -- exactly what unplugging the cable and plugging the receiver does. The settings
	// identity must be identical across both.
	//
	// Pre-fix: DeviceCardViewModel had no SettingsVendorId at all and every settings call site
	// passed card.VendorId, which ApplyDeviceIdentity overwrites with the live transport's vendor
	// on every discovery tick -- so the two stamps addressed (1389, 12888) and (10473, 12888).
	[Fact]
	public void MergedProduct_OnAlternateVendorTransport_KeepsOneSettingsBucket()
	{
		var card = Card();

		HomeViewModel.ApplyBluetoothIdentity(card, RampageEntry(DongleVid, DonglePid));
		var onDongle = (card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);

		HomeViewModel.ApplyBluetoothIdentity(card, RampageEntry(BoardVid, WiredPid));
		var onCable = (card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);

		Assert.Equal(onDongle, onCable);
		Assert.Equal((BoardVid, WiredPid, ""), onCable);
	}

	// The routing identity must keep following the live transport: device COMMANDS go out under
	// the vendor/product the device actually enumerates as. A "fix" that pinned both identities
	// would silently stop addressing the hardware.
	[Fact]
	public void PinningTheSettingsVendor_LeavesTheRoutingVendorOnTheLiveTransport()
	{
		var card = Card();

		HomeViewModel.ApplyBluetoothIdentity(card, RampageEntry(DongleVid, DonglePid));

		Assert.Equal(DongleVid, card.VendorId);
		Assert.Equal(DonglePid, card.ProductId);
		Assert.Equal(BoardVid, card.SettingsVendorId);
	}

	// An older DeviceHost sends settings_vendor_id = 0. The card must fall back to the live vendor
	// -- today's behaviour exactly -- rather than keying settings under vendor 0, which is the
	// "identity unknown" sentinel and would address every pre-scoping row in the database.
	[Fact]
	public void EntryWithoutASettingsVendor_FallsBackToTheLiveVendor()
	{
		var card = Card();
		var entry = RampageEntry(DongleVid, DonglePid);
		entry.SettingsVendorId = 0;

		HomeViewModel.ApplyBluetoothIdentity(card, entry);

		Assert.Equal(DongleVid, card.SettingsVendorId);
	}

	// A single-transport device has one vendor, so its settings vendor is its routing vendor and
	// nothing about it changes.
	[Fact]
	public void NonMergedDevice_KeepsItsSettingsVendorEqualToItsRoutingVendor()
	{
		var card = Card();
		var entry = new DeviceCatalogEntry
		{
			Name = "BLAZE",
			MergeTransports = false,
			VendorId = 0x04D9,
			ProductId = 0xA23F,
			SettingsVendorId = 0x04D9,
			SettingsProductId = 0xA23F,
		};

		HomeViewModel.ApplyBluetoothIdentity(card, entry);

		Assert.Equal(card.VendorId, card.SettingsVendorId);
		Assert.Equal(card.ProductId, card.SettingsProductId);
	}

	// ---- T1: the restore mapping (defect 2, acceptance criterion 8) ---------
	// HomeViewModel.SettingsIdentityFor is the rule a card restored from storage uses BEFORE
	// discovery reports in, when it has no catalog entry to ask.

	// A row written by the fixed app carries its bucket, and the restore uses it verbatim rather
	// than deriving one from the routing pair beside it. The two are deliberately different here:
	// a dongle-last RAMPAGE routes as (1389, 49271) while its settings live at (10473, 12888).
	[Fact]
	public void RestoredCard_AddressesTheBucketItWasPersistedUnder()
	{
		var record = new DeviceRecord
		{
			Name = "RAMPAGE",
			VendorId = DongleVid,
			ProductId = DonglePid,
			DeviceKey = "receiver-container-id",
			MergeTransports = true,
			SettingsVendorId = BoardVid,
			SettingsProductId = WiredPid,
			SettingsDeviceKey = "",
		};

		Assert.Equal((BoardVid, WiredPid, ""), HomeViewModel.SettingsIdentityFor(record));
	}

	// Acceptance criterion 8. A row written before the settings columns existed reads back as
	// 0 / 0 / "", and must behave EXACTLY as it did then — settings product id from the routing
	// product id, empty settings key — so a legacy card still finds the data it has instead of
	// opening on an empty page. ApplyDeviceIdentity corrects it once the device reports in.
	[Fact]
	public void LegacyRecord_WithoutSettingsColumns_FallsBackToTheRoutingPair()
	{
		var record = new DeviceRecord
		{
			Name = "RAMPAGE",
			VendorId = DongleVid,
			ProductId = DonglePid,
			DeviceKey = "receiver-container-id",
			MergeTransports = true,
			// SettingsVendorId / SettingsProductId / SettingsDeviceKey left at their defaults —
			// what an ALTER TABLE ... DEFAULT 0 column yields for a pre-existing row.
		};

		Assert.Equal((DongleVid, DonglePid, ""), HomeViewModel.SettingsIdentityFor(record));
	}

	// The fallback is per field, not all-or-nothing: a vendor of 0 must not drag a valid product id
	// down with it, and vice versa. Vendor 0 is the "identity unknown" sentinel and must never
	// become a real key.
	[Theory]
	[InlineData(0, WiredPid, BoardVid, WiredPid)]      // vendor unset -> routing vendor, stored pid
	[InlineData(BoardVid, 0, BoardVid, DonglePid)]     // product unset -> stored vendor, routing pid
	public void SettingsIdentity_FallsBackPerField(
		int storedVendorId, int storedProductId, int expectedVendorId, int expectedProductId)
	{
		var record = new DeviceRecord
		{
			Name = "RAMPAGE",
			VendorId = BoardVid,
			ProductId = DonglePid,
			SettingsVendorId = storedVendorId,
			SettingsProductId = storedProductId,
		};

		var (vendorId, productId, _) = HomeViewModel.SettingsIdentityFor(record);
		Assert.Equal(expectedVendorId, vendorId);
		Assert.Equal(expectedProductId, productId);
	}

	// A per-unit device's settings key must survive the restore: it is what keeps two identical
	// mice from sharing one bucket, and ApplyDeviceIdentity only adopts a key when the card has
	// none, so a key lost here is lost for good.
	[Fact]
	public void RestoredCard_KeepsItsPerUnitSettingsDeviceKey()
	{
		var record = new DeviceRecord
		{
			Name = "BLAZE",
			VendorId = 0x04D9,
			ProductId = 0xA23F,
			SettingsVendorId = 0x04D9,
			SettingsProductId = 0xA23F,
			SettingsDeviceKey = "unit-a",
		};

		Assert.Equal("unit-a", HomeViewModel.SettingsIdentityFor(record).DeviceKey);
	}

	// ---- T2: the heal trigger (acceptance criterion 4) ----------------------
	// HomeViewModel.PendingVendorRekeys decides WHICH re-key passes a discovered device calls for.
	// Everything below is the decision only; the passes themselves are covered by
	// LocalStorageServiceSettingsVendorRekeyTests.

	private static DeviceCatalogEntry MergedEntry(params int[] transportVendorIds)
	{
		var entry = new DeviceCatalogEntry
		{
			Name = "RAMPAGE",
			MergeTransports = true,
			VendorId = DongleVid,
			ProductId = DonglePid,
			SettingsVendorId = BoardVid,
			SettingsProductId = WiredPid,
		};
		entry.TransportVendorIds.AddRange(transportVendorIds);
		return entry;
	}

	// The RAMPAGE case: two transport vendors, one of them the pin, so exactly one pass — move
	// everything at (1389, 12888) into (10473, 12888).
	[Fact]
	public void MergedEntry_YieldsOnePassPerAlternateVendor()
	{
		var passes = HomeViewModel.PendingVendorRekeys(MergedEntry(BoardVid, DongleVid));

		Assert.Equal((DongleVid, BoardVid, WiredPid), Assert.Single(passes));
	}

	// Acceptance criterion 4 is driven by transport_vendor_ids, NOT by the live transport: a user
	// who moved permanently to the cable must still heal their dongle-era rows. The entry here is
	// the WIRED one (live vendor == the pin) and it must still produce the dongle pass.
	[Fact]
	public void MergedEntry_OnTheCanonicalTransport_StillHealsTheAlternateBucket()
	{
		var wired = MergedEntry(BoardVid, DongleVid);
		wired.VendorId = BoardVid;
		wired.ProductId = WiredPid;

		Assert.Equal((DongleVid, BoardVid, WiredPid), Assert.Single(HomeViewModel.PendingVendorRekeys(wired)));
	}

	// A product with three transport vendors gets a pass for each alternate, never for the pin.
	[Fact]
	public void MergedEntry_SkipsThePinnedVendor_AndCoversEveryOtherOne()
	{
		var passes = HomeViewModel.PendingVendorRekeys(MergedEntry(BoardVid, DongleVid, 1494));

		Assert.Equal([DongleVid, 1494], passes.Select(p => p.FromVendorId).Order());
		Assert.All(passes, p => Assert.Equal(BoardVid, p.ToVendorId));
		Assert.All(passes, p => Assert.Equal(WiredPid, p.ProductId));
	}

	// Every transport already on the pinned vendor: nothing is split, so nothing is migrated.
	[Fact]
	public void MergedEntry_WhoseTransportsAllShareThePin_YieldsNoPass()
	{
		Assert.Empty(HomeViewModel.PendingVendorRekeys(MergedEntry(BoardVid)));
	}

	// A single-transport device has one vendor and nothing to collapse. The DeviceHost sends it an
	// empty transport list precisely so the App never considers a migration for it.
	[Fact]
	public void NonMergedEntry_YieldsNoPass()
	{
		var entry = MergedEntry(BoardVid, DongleVid);
		entry.MergeTransports = false;

		Assert.Empty(HomeViewModel.PendingVendorRekeys(entry));
	}

	[Fact]
	public void MergedEntry_WithNoTransportList_YieldsNoPass()
	{
		Assert.Empty(HomeViewModel.PendingVendorRekeys(MergedEntry()));
	}

	// 0 is the "identity unknown" sentinel on the wire and must never reach the re-key as a source:
	// WHERE vendor_id = 0 would address every pre-scoping row in the database.
	[Fact]
	public void MergedEntry_IgnoresAZeroTransportVendor()
	{
		Assert.Empty(HomeViewModel.PendingVendorRekeys(MergedEntry(BoardVid, 0)));
	}

	// ---- T3: the wrong-direction regression (review round 1, S1) ------------
	// The pin MUST come from the catalog entry and from nothing else.
	//
	// A card's SettingsVendorId is not always a pin: ApplyDeviceIdentity falls back to the LIVE
	// routing vendor when the entry reports 0, and RecordToViewModel falls back to a legacy
	// devices row's stored ROUTING pair. If the heal took its target from the card, a legacy
	// dongle-last RAMPAGE would run RekeySettingsVendor(from: 10473, to: 1389, product: 49271) —
	// migrating the CANONICAL rows into the ALTERNATE bucket. That is the wrong direction: it is
	// silent, it destroys the good copy, and the pre-migration backup is the only way back.
	//
	// Reading the entry makes it unrepresentable, and these tests are what keep it that way.

	// An older DeviceHost sends no pin. No pin, no migration — today's behaviour exactly
	// (acceptance criterion 11), rather than a migration aimed at a fallback value.
	[Fact]
	public void EntryWithoutASettingsVendor_YieldsNoPass()
	{
		var entry = MergedEntry(BoardVid, DongleVid);
		entry.SettingsVendorId = 0;

		Assert.Empty(HomeViewModel.PendingVendorRekeys(entry));
	}

	[Fact]
	public void EntryWithoutASettingsProductId_YieldsNoPass()
	{
		var entry = MergedEntry(BoardVid, DongleVid);
		entry.SettingsProductId = 0;

		Assert.Empty(HomeViewModel.PendingVendorRekeys(entry));
	}

	// The decision must not be reachable from card state at all. This entry describes a device
	// whose routing identity is the DONGLE, which is what a card restored from a legacy row would
	// have been carrying — and the passes it produces still point AT the board, never at 1389.
	[Fact]
	public void PassesAlwaysPointAtThePin_NeverAtTheLiveTransport()
	{
		var onDongle = MergedEntry(BoardVid, DongleVid);
		onDongle.VendorId = DongleVid;
		onDongle.ProductId = DonglePid;

		var pass = Assert.Single(HomeViewModel.PendingVendorRekeys(onDongle));

		Assert.Equal(BoardVid, pass.ToVendorId);      // towards the board
		Assert.Equal(DongleVid, pass.FromVendorId);   // away from the receiver
		Assert.Equal(WiredPid, pass.ProductId);       // the pinned product, not the routing one
		Assert.NotEqual(DonglePid, pass.ProductId);
	}
}
