using System.Text.Json;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Adding a device to this product is a pure CONFIG change — the Starlight entry is a block of JSON
// and no code at all, because the Cosmo work made every headset capability catalog-driven. That is
// good design and a testing blind spot at the same time: nothing else in the suite reads the real
// appsettings.json, so a typo'd product ID or a dropped capability would ship silently and the only
// symptom would be a device that never appears on the home page.
//
// This reads the DeviceHost's actual catalog and pins the Starlight entry through the same expander
// the DeviceHost uses. Hardware facts are from the read-only probe, 2026-08-07:
// VID 0x0D8C (3468) / PID 0x036D (877), "Jieli Technology" / "PG Starlight".
// Plan: docs/plans/add-starlight-headset/add-starlight-headset-plan.md
public class StarlightCatalogTests
{
	private const string StarlightVendorId = "3468";   // 0x0D8C, C-Media
	private const string StarlightProductId = "877";   // 0x036D

	private static DeviceGroupConfig Starlight()
	{
		var device = LoadCatalog().FirstOrDefault(d => d.Name == "Starlight");
		Assert.NotNull(device);
		return device!;
	}

	[Fact]
	public void CatalogDeclaresStarlightWithTheProbedIds()
	{
		var device = Starlight();

		Assert.Equal(StarlightVendorId, device.VendorId);
		Assert.Equal(StarlightProductId, device.ProductId);
	}

	// The card shape: a single card with the headset artwork, like Cosmo.
	[Fact]
	public void StarlightIsASingleCardHeadsetWithItsOwnArtwork()
	{
		var device = Starlight();

		Assert.Equal("Wireless Headset", device.DeviceType);
		Assert.Equal("single", device.CardType);
		Assert.Equal("/Assets/Headphones/Starlight-device-standard.png", device.ImagePath);
	}

	// Drives the Audio section. Without both keys the section renders with no sliders, and without
	// the volume target the sliders would not reach the Windows endpoint.
	[Fact]
	public void StarlightDeclaresBothAudioSlidersAgainstTheOsEndpoint()
	{
		var device = Starlight();

		Assert.Equal(["OutputVolume", "MicrophoneVolume"], device.SupportedAudioSettings);
		Assert.Equal("OperatingSystem", device.AudioVolumeTarget);
	}

	// Deliberate, and the one field Starlight sets differently from every other device in the
	// catalog. Battery is not readable on this hardware yet, and Cosmo shipping an unconfirmed
	// battery byte is the mistake this avoids repeating — a wrong indicator is worse than none,
	// because the user cannot tell it apart from a real reading.
	//
	// SupportsBattery defaults to TRUE, so a dropped line here reads as "has a battery" and the
	// card would show a red critical indicator with no level to fill it.
	[Fact]
	public void StarlightDeclaresNoBattery()
	{
		Assert.False(Starlight().SupportsBattery);
	}

	// Lighting is gated on the Phase 0b hardware probe confirming the LED channel. The hardware HAS
	// RGB, but the LED specification's own report ID (0xFF) is absent from this device, so nothing
	// may claim RGB support until a write to 0xFD is proven to drive it.
	[Fact]
	public void StarlightDoesNotClaimRgbUntilTheChannelIsProven()
	{
		Assert.False(Starlight().SupportsRgb);
	}

	// The expander is what the DeviceHost actually runs, so the assertions that matter are on the
	// rows it produces, not only on the raw config.
	[Fact]
	public void StarlightExpandsToADongleRowCarryingEveryCapability()
	{
		var row = Assert.Single(DeviceMapExpander.Expand([Starlight()]), r => !r.HasBluetooth);

		Assert.Equal(StarlightVendorId, row.VendorId);
		Assert.Equal(StarlightProductId, row.ProductId);
		Assert.Equal(["OutputVolume", "MicrophoneVolume"], row.SupportedAudioSettings);
		Assert.Equal("OperatingSystem", row.AudioVolumeTarget);
		Assert.False(row.SupportsRgb);

		// No battery on the RECEIVER. The device-level SupportsBattery stays false so a dropped
		// transport override can never light an indicator the dongle has nothing to fill.
		Assert.False(row.SupportsBattery);
	}

	// PG-608. Bluetooth is a second connection with genuinely different capabilities from the
	// dongle's, which is the whole reason these are per-transport overrides.
	[Fact]
	public void StarlightExpandsToAConfigurableBluetoothRowWithBattery()
	{
		var row = Assert.Single(DeviceMapExpander.Expand([Starlight()]), r => r.HasBluetooth);

		// The Bluetooth ids, NOT the USB ones — and deliberately the same pair Cosmo reports.
		Assert.Equal("1494", row.VendorId);
		Assert.Equal("10", row.ProductId);

		// Which is why the row must carry the token that tells the two headsets apart. Without it
		// MatchCatalog cannot, and a connected Starlight is drawn as a Cosmo card.
		Assert.Equal("starlight", row.BluetoothName);

		// Windows exposes a battery percentage on the Hands-Free AG node over Bluetooth (measured
		// 70 % on 2026-08-19), so this transport overrides the device-level false.
		Assert.True(row.SupportsBattery);

		// Configurable over Bluetooth. The vendor HID channel is still absent on this link, but
		// headset configuration doesn't need it — audio levels target the OS endpoint
		// (AudioVolumeTarget: OperatingSystem), which works on any transport, so Save Preset and
		// the details sections stay live. The row shipped SupportsConfiguration: false until
		// 2026-09-02; see docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md.
		Assert.True(row.SupportsConfiguration);

		Assert.Equal(["OutputVolume", "MicrophoneVolume"], row.SupportedAudioSettings);
	}

	// The pair is an ODM constant shared with Cosmo, so this is asserted as INTENDED, not as a
	// coincidence a later edit may quietly "fix" by inventing a distinct id for one of them.
	[Fact]
	public void StarlightAndCosmoDeclareTheSameBluetoothIdsAndAreSeparatedByName()
	{
		var rows = DeviceMapExpander.Expand(LoadCatalog())
			.Where(r => r.HasBluetooth && r.VendorId == "1494" && r.ProductId == "10")
			.ToList();

		Assert.Equal(2, rows.Count);
		Assert.Equal(["cosmo", "starlight"], rows.Select(r => r.BluetoothName).Order());
	}

	// The executable form of the 2026-09-02 ticket's constraint: only HEADSETS unlock over
	// Bluetooth (their audio targets the OS endpoint, which works on any link), while keyboards
	// and mice keep the read-only Bluetooth mode of 2026-08-30. IsBluetoothConfigLocked derives
	// the UI lock from SupportsConfiguration, so any non-headset Bluetooth row missing the false
	// flag would silently unlock that device — this pins the flag for every current row and for
	// any future Bluetooth transport added without one.
	// Plan: docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md
	[Fact]
	public void EveryNonHeadsetBluetoothRowDeclinesConfiguration()
	{
		var configurableNonHeadsets = DeviceMapExpander.Expand(LoadCatalog())
			.Where(r => r.HasBluetooth)
			.Where(r => r.DeviceType?.Contains("Headset", StringComparison.OrdinalIgnoreCase) != true)
			.Where(r => r.SupportsConfiguration)
			.Select(r => $"{r.DeviceName} ({r.VendorId}:{r.ProductId})")
			.ToList();

		Assert.Empty(configurableNonHeadsets);
	}

	// The mirror of the guard above — the ticket's POSITIVE half. Cosmo's Bluetooth row is
	// configurable only by inheriting the device default (no explicit flag in the catalog), so a
	// later edit that adds SupportsConfiguration: false there — or flips a headset's device-level
	// default — would silently re-lock the headset over Bluetooth and re-show the banner, and only
	// manual QA would notice. Pins both current headsets and any future headset Bluetooth row.
	// Plan: docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md
	[Fact]
	public void EveryHeadsetBluetoothRowCarriesConfiguration()
	{
		var headsetBluetoothRows = DeviceMapExpander.Expand(LoadCatalog())
			.Where(r => r.HasBluetooth)
			.Where(r => r.DeviceType?.Contains("Headset", StringComparison.OrdinalIgnoreCase) == true)
			.ToList();

		// Non-vacuous: the catalog ships two Bluetooth headsets (Cosmo, Starlight). If this
		// shrinks, the guard is no longer testing what it claims to.
		Assert.True(headsetBluetoothRows.Count >= 2,
			$"Expected at least two headset Bluetooth rows, found {headsetBluetoothRows.Count}.");

		var lockedHeadsets = headsetBluetoothRows
			.Where(r => !r.SupportsConfiguration)
			.Select(r => $"{r.DeviceName} ({r.VendorId}:{r.ProductId})")
			.ToList();

		Assert.Empty(lockedHeadsets);
	}

	// Two products sharing a VID/PID would collide on every per-device lookup in the app.
	[Fact]
	public void NoTwoCatalogDevicesShareAVendorAndProductId()
	{
		var duplicates = LoadCatalog()
			.GroupBy(d => $"{d.VendorId}:{d.ProductId}")
			.Where(g => g.Count() > 1)
			.Select(g => $"{g.Key} -> {string.Join(", ", g.Select(d => d.Name))}")
			.ToList();

		Assert.Empty(duplicates);
	}

	// ── Loading the real catalog ──────────────────────────────────────────────

	// Moved to DeviceHostCatalog when the Bluetooth name-match regression became a second caller.
	private static List<DeviceGroupConfig> LoadCatalog() => DeviceHostCatalog.Load();
}
