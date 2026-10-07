using System.Text.Json;
using ProoveGaming.DeviceHost.Services;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Tests;

// The catalog → registry → gRPC seam, which had no coverage at all until PG-608 and is exactly where
// a shared Bluetooth id pair does its damage.
//
// Cosmo and Starlight both advertise 0x05D6/0x000A over Bluetooth. `MatchCatalog` was taught to tell
// them apart by name (see ClassicBluetoothDeviceResolverTests), and that fixed WHICH device the poll
// says is connected — but the CAPABILITIES the App receives are resolved separately, here, by
// `FindBluetoothCatalogEntry`. That lookup used to read `_mapsByPidVid`, a dictionary keyed by
// vid/pid that keeps only the first row per key, so one of the two headsets was missing from it
// entirely and fell through to a by-name lookup that returns the DONGLE row.
//
// The visible result was a Starlight that looked correct — right name, right artwork, card active —
// while silently having no battery indicator and an ENABLED Save Preset on a link that cannot carry
// configuration. Every other test in the suite passed throughout.
//
// These assertions are on per-transport capabilities rather than on row identity, because a row that
// is merely "found" is not the contract; carrying the live transport's capabilities is.
public class HidDeviceMapRegistryBluetoothTests
{
	private sealed class FakeDeviceInfoProvider(IReadOnlyList<DeviceMap> maps) : IDeviceInfoProvider
	{
		public IReadOnlyList<DeviceMap> DevicesMaps { get; } = maps;

		public string GetClientName(string productId, string vendorId, string? productName = null) =>
			throw new NotSupportedException();

		public string GetClientName(string deviceId) => throw new NotSupportedException();
	}

	private static HidDeviceMapRegistry Registry() =>
		new(new FakeDeviceInfoProvider(DeviceMapExpander.Expand(LoadCatalog())));

	// ── Display name ──────────────────────────────────────────────────────────────────────────

	// The TAIPAN is shown to users as TAIPAN MAX through the catalog's DisplayName, while its Name
	// (which device matching, storage and the protocol choice key on) stays PG TAIPAN. Pinned end to
	// end on the real catalog: a copy dropped anywhere between appsettings.json and the catalog
	// entry would silently bring the old name back.
	[Fact]
	public void TheTaipanCatalogEntry_IsShownAsTaipanMax_AndKeepsItsName()
	{
		var entry = Registry().FindCatalogEntry("PG TAIPAN", productName: null);

		Assert.NotNull(entry);
		Assert.Equal("PG TAIPAN", entry.Name);
		Assert.Equal("TAIPAN MAX", entry.DisplayName);
	}

	[Fact]
	public void ADeviceWithoutADisplayName_SendsNone()
	{
		var entry = Registry().FindCatalogEntry("BLAZE", productName: null);

		Assert.NotNull(entry);
		Assert.Equal(string.Empty, entry.DisplayName);
	}

	// ── The two headsets sharing one Bluetooth id pair ────────────────────────────────────────

	// Pins the premise. If a firmware revision ever gives these distinct ids this test should be
	// updated deliberately, not silently outlived by the tests below.
	[Fact]
	public void CosmoAndStarlightStillShareOneBluetoothIdPair()
	{
		var bluetooth = DeviceMapExpander.Expand(LoadCatalog())
			.Where(m => m.HasBluetooth && m.VendorId == "1494" && m.ProductId == "10")
			.Select(m => m.Name)
			.Order()
			.ToList();

		// DeviceMapExpander upper-cases Name, and the registry indexes/looks up through ToUpper(),
		// so casing is an implementation detail rather than part of this contract.
		Assert.Equal(["COSMO", "STARLIGHT"], bluetooth);
	}

	// The regression test for B1. Before the by-name index existed this returned Starlight's DONGLE
	// row: BatteryUnsupported = true (no indicator, though Windows reports ~70 % on the Hands-Free AG
	// node). The battery override is what still tells the two rows apart here — since 2026-09-02 both
	// rows are configurable (headset audio targets the OS endpoint, so the Bluetooth row's
	// SupportsConfiguration: false was removed; see
	// docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md).
	[Fact]
	public void StarlightOverBluetoothGetsItsOwnBluetoothCapabilities()
	{
		var entry = Registry().FindBluetoothCatalogEntry("Starlight");

		Assert.NotNull(entry);
		Assert.Equal("Starlight", entry!.Name, ignoreCase: true);
		Assert.False(entry.BatteryUnsupported);       // Windows exposes a percentage over Bluetooth
		Assert.False(entry.ConfigurationUnsupported); // configurable over Bluetooth since 2026-09-02
	}

	// The other side of the same collision: Cosmo must keep the PG-564 behaviour exactly.
	[Fact]
	public void CosmoOverBluetoothKeepsItsOwnBluetoothCapabilities()
	{
		var entry = Registry().FindBluetoothCatalogEntry("Cosmo");

		Assert.NotNull(entry);
		Assert.Equal("Cosmo", entry!.Name, ignoreCase: true);
		Assert.False(entry.BatteryUnsupported);
	}

	// Neither headset may answer to the other's name — the failure mode is silent, because both
	// resolve to a well-formed entry either way.
	[Theory]
	[InlineData("Starlight")]
	[InlineData("Cosmo")]
	public void EachHeadsetResolvesToItself(string name)
	{
		Assert.Equal(name, Registry().FindBluetoothCatalogEntry(name)!.Name, ignoreCase: true);
	}

	// ── Devices that never needed a token ─────────────────────────────────────────────────────

	// TAIPAN's Bluetooth transport declares SupportsConfiguration: false. Resolving it through the
	// dongle or wired row would re-enable Save Preset on a link that drops the writes — the original
	// bug the by-name preference was written for, which must stay fixed.
	[Fact]
	public void TaipanOverBluetoothStillResolvesToItsBluetoothRow()
	{
		var entry = Registry().FindBluetoothCatalogEntry("PG TAIPAN");

		Assert.NotNull(entry);
		Assert.True(entry!.ConfigurationUnsupported);
	}

	// A device with no Bluetooth transport at all must still fall back to its name lookup rather
	// than returning null, which is how the pre-PG-608 behaviour is preserved.
	[Fact]
	public void ADeviceWithNoBluetoothRowFallsBackToItsNameLookup()
	{
		Assert.NotNull(Registry().FindBluetoothCatalogEntry("BLAZE"));
	}

	[Fact]
	public void AnUnknownNameResolvesToNothing()
	{
		Assert.Null(Registry().FindBluetoothCatalogEntry("Not A Real Product"));
	}

	// ── Settings-bucket pinning (S1) ──────────────────────────────────────────────────────────

	// Starlight has no cable, so the pin falls past the wired preference. It must land on the
	// DONGLE row's product id and never on the Bluetooth row's, whose ids are an ODM constant shared
	// with Cosmo — pinning there would collapse both headsets into one settings bucket and orphan
	// every stored preset. Asserted through the expander with the Bluetooth transport deliberately
	// placed FIRST, since the old code took whatever came first.
	[Fact]
	public void AMergedProductWithNoCablePinsSettingsToItsNonBluetoothTransport()
	{
		var starlight = LoadCatalog().Single(d => d.Name == "Starlight");
		starlight.Transports = [.. starlight.Transports.OrderByDescending(t =>
			string.Equals(t.ConnectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase))];

		Assert.Equal("Bluetooth", starlight.Transports[0].ConnectionType);

		var registry = new HidDeviceMapRegistry(
			new FakeDeviceInfoProvider(DeviceMapExpander.Expand([starlight])));

		// Asserted through the public surface: SettingsProductId is what every stored preset,
		// binding and RGB row is keyed by. 877 is the dongle's product id; 10 is the shared
		// Bluetooth one that would also be Cosmo's.
		Assert.Equal(877, registry.FindBluetoothCatalogEntry("Starlight")!.SettingsProductId);
	}

	// ---- The pinned settings VENDOR (fix-settings-bucket-identity) -----------
	// The product id was already pinned per product; the vendor was not, and three shipped products
	// enumerate a transport under a vendor of their own. Asserted against the REAL catalog, because
	// the pin is computed from configuration and a config edit is exactly how it would silently
	// regress -- the App-side tests load no catalog and would not notice.

	// Pins the premise, like CosmoAndStarlightStillShareOneBluetoothIdPair does for the id pair.
	[Fact]
	public void RampagesReceiverStillEnumeratesUnderItsOwnVendor()
	{
		var vendors = DeviceMapExpander.Expand(LoadCatalog())
			.Where(m => m.Name == "RAMPAGE")
			.Select(m => m.VendorId)
			.Distinct()
			.Order()
			.ToList();

		Assert.Equal(["10473", "1389"], vendors);
	}

	// The heart of the fix: every RAMPAGE transport reports the SAME settings vendor, so cable,
	// dongle and Bluetooth all address one settings bucket. Before this, the dongle row's live
	// vendor (1389) became the bucket key and the board's configuration split in two.
	[Fact]
	public void EveryRampageTransportPinsTheSameSettingsVendor()
	{
		var registry = Registry();

		// Wired / dongle come through the vid/pid lookup, Bluetooth through the by-name one.
		Assert.Equal(10473, registry.FindCatalogEntryByPidVid("10473", "12888")!.SettingsVendorId);
		Assert.Equal(10473, registry.FindCatalogEntryByPidVid("1389", "49271")!.SettingsVendorId);
		Assert.Equal(10473, registry.FindBluetoothCatalogEntry("Rampage")!.SettingsVendorId);
	}

	// The two pins must name the SAME transport as canonical, which is why they are computed from
	// one `primary` row. A settings vendor from the dongle row with a product id from the wired row
	// would yield (1389, 12888) -- a pair naming no bucket any transport ever wrote to.
	[Fact]
	public void RampagesSettingsVendorAndProductIdComeFromTheSameTransport()
	{
		var dongle = Registry().FindCatalogEntryByPidVid("1389", "49271")!;

		Assert.Equal(10473, dongle.SettingsVendorId);
		Assert.Equal(12888, dongle.SettingsProductId);
	}

	// transport_vendor_ids is what makes the heal CONVERGE: with the settings vendor alone, a user
	// who moved permanently to the cable never triggers a re-key and their dongle-era rows stay
	// orphaned forever. Every transport carries the full list so any connection heals every bucket.
	[Fact]
	public void EveryRampageTransportCarriesEveryTransportVendorId()
	{
		var registry = Registry();

		foreach (var entry in new[]
		{
			registry.FindCatalogEntryByPidVid("10473", "12888")!,
			registry.FindCatalogEntryByPidVid("1389", "49271")!,
			registry.FindBluetoothCatalogEntry("Rampage")!,
		})
		{
			Assert.Equal([1389, 10473], entry.TransportVendorIds.Order());
		}
	}

	// Cosmo and Starlight are the other two split products: both Bluetooth links are vendor 1494
	// against a product vendor of 3468. They must pin to their own product's vendor AND keep their
	// distinct product ids -- collapsing them would give two headsets one settings bucket, which is
	// the failure StarlightAndCosmoPinDifferentSettingsBuckets already guards for the product id.
	[Fact]
	public void CosmoAndStarlightBluetoothPinTheirOwnProductVendor()
	{
		var registry = Registry();

		var cosmo = registry.FindBluetoothCatalogEntry("Cosmo")!;
		var starlight = registry.FindBluetoothCatalogEntry("Starlight")!;

		Assert.Equal(3468, cosmo.SettingsVendorId);
		Assert.Equal(3468, starlight.SettingsVendorId);
		Assert.Equal(820, cosmo.SettingsProductId);
		Assert.Equal(877, starlight.SettingsProductId);
	}

	// A single-transport device has one vendor and nothing to collapse. It must report its own
	// vendor as the settings vendor and an EMPTY transport list -- a one-element list would invite
	// the App to consider a migration for a device that can never need one.
	[Fact]
	public void NonMergedDeviceMirrorsItsOwnVendor_AndOffersNoTransportList()
	{
		var raster = Registry().FindCatalogEntryByPidVid("14000", "12385")!;

		Assert.False(raster.MergeTransports);
		Assert.Equal(raster.VendorId, raster.SettingsVendorId);
		Assert.Empty(raster.TransportVendorIds);
	}

	// ── Loading the real catalog ──────────────────────────────────────────────────────────────

	// The DeviceHost's real catalog, copied into the test output by the csproj as
	// appsettings.DeviceHost.json — see the note in StarlightCatalogTests.
	private static List<DeviceGroupConfig> LoadCatalog()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "appsettings.DeviceHost.json");
		Assert.True(File.Exists(path),
			$"The DeviceHost catalog was not copied to the test output ({path}). " +
			"Check the <None Include=...appsettings.json> item in ProoveGaming.Infrastructure.Tests.csproj.");

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var devices = document.RootElement.GetProperty("AllowedDevices").GetProperty("Devices");

		var catalog = devices.Deserialize<List<DeviceGroupConfig>>(
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

		Assert.NotNull(catalog);
		return catalog!;
	}
}
