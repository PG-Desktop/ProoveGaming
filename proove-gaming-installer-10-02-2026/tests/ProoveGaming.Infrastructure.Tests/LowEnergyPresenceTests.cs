using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Fast disconnect detection for Bluetooth LOW ENERGY devices.
//
// A BLE device's disconnect used to be noticed only by the 30 s BLE sweep (five minutes when nothing
// else was tracked), because that sweep is the only thing that clears a card's Bluetooth state. The
// App kept showing a switched-off Bluetooth mouse as connected for well over twenty seconds.
//
// Measured on a PG Taipan (2026-08-20) polling the Windows device tree every 2 s:
//
//   switched OFF               -> the BTHLE node's IsConnected flipped True -> False
//   pairing removed in Windows -> the node DISAPPEARED outright
//
// Both appeared immediately, so Windows was never the delay — the cadence was. These tests pin the
// two rules that decide whether a device counts as live, because both have a silent failure mode:
// getting them wrong does not throw, it just shows a working mouse as absent (or a dead one as
// present) and nothing else in the suite would notice.
public class LowEnergyPresenceTests
{
	private const string TaipanAddress = "19D5E987093A";

	private static DeviceNode Node(string instanceId, bool isConnected) =>
		new(instanceId, ContainerId: null, FriendlyName: "PG Taipan", BatteryPercent: 51, isConnected);

	private static DeviceNode BleNode(string address, bool isConnected) =>
		Node($@"BTHLE\Dev_{address}\8&1a2b3c4d&0&{address}", isConnected);

	// ── Which addresses count as live ─────────────────────────────────────────────────────────

	[Fact]
	public void AConnectedNodeCountsAsLive()
	{
		var live = BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(
			[BleNode(TaipanAddress, isConnected: true)]);

		Assert.Contains(TaipanAddress, live);
	}

	// The switched-off case, measured at 22:04:34. The node is still there; only the flag moved.
	[Fact]
	public void ADisconnectedNodeDoesNotCountAsLive()
	{
		var live = BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(
			[BleNode(TaipanAddress, isConnected: false)]);

		Assert.Empty(live);
	}

	// The removed-from-Windows case, measured at 22:03:47. Absence and IsConnected=false have to be
	// the same answer — a device that is gone must not stay live just because no node said otherwise.
	[Fact]
	public void AnAbsentNodeIsSimplyNotLive()
	{
		Assert.Empty(BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses([]));
	}

	[Fact]
	public void OneDeviceGoingAwayDoesNotTakeTheOthersWithIt()
	{
		var live = BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(
		[
			BleNode(TaipanAddress, isConnected: false),
			BleNode("AABBCCDDEEFF", isConnected: true),
		]);

		Assert.Equal(["AABBCCDDEEFF"], live);
	}

	// ── Present vs live: two different events ─────────────────────────────────────────────────
	//
	// Collapsing these is what made a removed device's card stay active. Switching the mouse OFF
	// leaves its node with IsConnected=false; REMOVING it in Windows deletes the node. The first
	// means "keep tracking, it will come back"; the second means "let go, including the GATT link we
	// are holding open" — because while we hold it, battery reads keep succeeding, IsPaired keeps
	// reporting true and the advertisement watcher keeps reconnecting, all of which re-assert
	// "connected" within seconds.

	[Fact]
	public void ASwitchedOffDeviceIsStillPresentEvenThoughItIsNotLive()
	{
		var nodes = new[] { BleNode(TaipanAddress, isConnected: false) };

		Assert.Contains(TaipanAddress, BluetoothDeviceDiscoveryService.PresentLowEnergyAddresses(nodes));
		Assert.DoesNotContain(TaipanAddress, BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(nodes));
	}

	[Fact]
	public void ARemovedDeviceIsNeitherPresentNorLive()
	{
		Assert.Empty(BluetoothDeviceDiscoveryService.PresentLowEnergyAddresses([]));
		Assert.Empty(BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses([]));
	}

	[Fact]
	public void AConnectedDeviceIsBothPresentAndLive()
	{
		var nodes = new[] { BleNode(TaipanAddress, isConnected: true) };

		Assert.Contains(TaipanAddress, BluetoothDeviceDiscoveryService.PresentLowEnergyAddresses(nodes));
		Assert.Contains(TaipanAddress, BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(nodes));
	}

	// Presence must not be decided by one device's state — releasing a device is destructive
	// (it drops the GATT link), so it may only ever follow that device's own node being gone.
	[Fact]
	public void OneRemovedDeviceDoesNotMakeAnotherLookRemoved()
	{
		var present = BluetoothDeviceDiscoveryService.PresentLowEnergyAddresses(
			[BleNode("AABBCCDDEEFF", isConnected: false)]);

		Assert.Contains("AABBCCDDEEFF", present);
		Assert.DoesNotContain(TaipanAddress, present);
	}

	// ── Resolving a tracked device's address ──────────────────────────────────────────────────

	[Theory]
	[InlineData("19D5E987093A")]
	[InlineData("19:D5:E9:87:09:3A")]
	[InlineData("19-d5-e9-87-09-3a")]
	public void ABareAddressIsAcceptedInAnySeparatorForm(string id)
	{
		Assert.Equal(TaipanAddress, BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress(id));
	}

	[Fact]
	public void ADeviceInstanceIdIsParsedRatherThanScannedForHex()
	{
		Assert.Equal(TaipanAddress, BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress(
			$@"BTHLE\Dev_{TaipanAddress}\8&1a2b3c4d&0&{TaipanAddress}"));
	}

	// The trap this rule exists for. A service node's id ends in the Bluetooth base UUID, whose tail
	// is ALSO twelve hex digits — so "the last twelve hex characters" returns 00805F9B34FB for every
	// device on every machine. As a tracked address that matches no live node, which would disconnect
	// a perfectly healthy device on every single poll.
	[Fact]
	public void TheBluetoothBaseUuidIsNeverMistakenForAnAddress()
	{
		var id = $@"BTHLE\{{0000180f-0000-1000-8000-00805f9b34fb}}_LOCALMFG&0000\8&1a2b3c4d&0&0000";

		Assert.NotEqual("00805F9B34FB", BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress(id));
	}

	// Unresolvable means "do not answer the question", never "disconnect it".
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not-an-address")]
	[InlineData("ABCDEF")]
	public void AnUnresolvableIdYieldsNull(string? id)
	{
		Assert.Null(BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress(id));
	}

	// ── Which signal decides "connected" ──────────────────────────────────────────────────────

	// The tree wins when it has an opinion. Gatt.IsConnected reflects our GATT client rather than the
	// OS link and is wrong in BOTH directions: stale `true` once the radio is off, and stuck `false`
	// on the dead instance left behind when a device is removed and re-paired in Windows — which is
	// why a re-added device's card stayed offline until the App was restarted.
	[Theory]
	[InlineData(true, false, false)]   // tree says live, stale instance says otherwise -> live
	[InlineData(false, true, true)]    // tree says gone, Gatt still claims connected  -> gone
	public void TheDeviceTreeOutranksGattIsConnected(bool tree, bool gatt, bool paired)
	{
		Assert.Equal(tree, BluetoothDeviceDiscoveryService.ResolveConnected(tree, gatt, paired));
	}

	// No opinion is NOT "disconnected". Before the first successful enumeration the tree knows
	// nothing, and collapsing that to false would disconnect every tracked device at startup.
	[Theory]
	[InlineData(true, true, true)]
	[InlineData(false, true, false)]
	[InlineData(true, false, false)]
	[InlineData(false, false, false)]
	public void WithNoTreeOpinionTheOldGattRuleStillApplies(bool gatt, bool paired, bool expected)
	{
		Assert.Equal(expected, BluetoothDeviceDiscoveryService.ResolveConnected(null, gatt, paired));
	}

	// ── A battery reading may never revive a device ───────────────────────────────────────────
	//
	// The read allows up to 5 s, so one already in flight can land AFTER the device was marked
	// disconnected or released. Publishing it used to assert isConnected: true unconditionally, which
	// resurrected the card — captured in the log as "marked disconnected" at 23:15:09 followed by
	// "GATT battery 46% -> connected=true" three seconds later. Connection state has one owner (the
	// device tree); this path only ever reports a level for a device already considered connected.

	[Fact]
	public void ABatteryReadingIsPublishedForALiveTrackedDevice()
	{
		Assert.True(BluetoothDeviceDiscoveryService.ShouldPublishBattery(
			battery: 46, stillTracked: true, currentlyConnected: true));
	}

	// The race the blocker was about: released mid-read.
	[Fact]
	public void ABatteryReadingIsDroppedForAReleasedDevice()
	{
		Assert.False(BluetoothDeviceDiscoveryService.ShouldPublishBattery(
			battery: 46, stillTracked: false, currentlyConnected: true));
	}

	// Still tracked (switched off, not removed) but the tree says it is not connected. A reading that
	// completes now must not undo that.
	[Fact]
	public void ABatteryReadingIsDroppedForADeviceTheTreeCallsDisconnected()
	{
		Assert.False(BluetoothDeviceDiscoveryService.ShouldPublishBattery(
			battery: 46, stillTracked: true, currentlyConnected: false));
	}

	// A non-reading carries no information and must not be published as one.
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void ANonReadingIsNeverPublished(int battery)
	{
		Assert.False(BluetoothDeviceDiscoveryService.ShouldPublishBattery(
			battery, stillTracked: true, currentlyConnected: true));
	}

	// -- A failed tree read must never look like "no devices" (M1) -----------------------------
	//
	// CfgMgr32 reports failure through CONFIGRET codes, never exceptions, so a try/catch around the
	// enumeration cannot see one. Both error paths used to return an empty list, indistinguishable
	// from "this machine has no BLE devices" - and the poll releases (drops the GATT link, disposes
	// the instance) every tracked device it cannot find. The size query and the fetch are separate
	// calls, so any device arriving between them returns CR_BUFFER_SMALL: a real race, not a
	// theoretical one.
	//
	// The contract is now a tri-state, and these pin the two halves that must stay distinct.

	[Fact]
	public void AnEmptyNodeListStillMeansNothingIsPresent()
	{
		// The honest empty answer: no BLE devices, so nothing is live and nothing is present.
		Assert.Empty(BluetoothDeviceDiscoveryService.PresentLowEnergyAddresses([]));
		Assert.Empty(BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses([]));
	}

	[Fact]
	public void TheEnumerationReportsFailureAsNullRatherThanAsEmpty()
	{
		// Guards the contract itself: a caller must be able to tell the two apart. If this ever
		// becomes non-nullable again, the release path silently regains a single-sample trigger.
		var method = typeof(DeviceNodeQuery).GetMethod(nameof(DeviceNodeQuery.EnumerateLowEnergyNodes));
		Assert.NotNull(method);

		var nullability = new System.Reflection.NullabilityInfoContext()
			.Create(method!.ReturnParameter);

		Assert.Equal(System.Reflection.NullabilityState.Nullable, nullability.ReadState);
	}

	[Fact]
	public void TheClassicEnumerationReportsFailureAsNullToo()
	{
		// Same exposure: the classic poll disconnects every device it does not see.
		var method = typeof(DeviceNodeQuery).GetMethod(nameof(DeviceNodeQuery.EnumerateClassicBluetoothNodes));
		Assert.NotNull(method);

		var nullability = new System.Reflection.NullabilityInfoContext()
			.Create(method!.ReturnParameter);

		Assert.Equal(System.Reflection.NullabilityState.Nullable, nullability.ReadState);
	}

	// -- Battery from the device tree ---------------------------------------------------------
	//
	// A BLE device's level used to be refreshed only by the 5-minute GATT read, so the card lagged
	// the Windows tray by up to five minutes - reported as the App showing 44 % against a tray
	// showing 43 %, correcting only when the details page was opened. The tree carries the SAME
	// property the tray reads, EnumerateLowEnergyNodes already fetches it on the 3 s presence clock,
	// and it was simply being discarded. Reading it costs nothing extra and makes the two agree by
	// construction rather than by luck.

	[Fact]
	public void TheTreeBatteryIsReadPerAddress()
	{
		var byAddress = BluetoothDeviceDiscoveryService.LowEnergyBatteryByAddress(
			[BleNode(TaipanAddress, isConnected: true)]);

		Assert.Equal(51, byAddress[TaipanAddress]);
	}

	[Fact]
	public void ANodeWithNoBatteryIsSimplyAbsentFromTheMap()
	{
		var node = new DeviceNode(
			BleNode(TaipanAddress, isConnected: true).InstanceId, ContainerId: null,
			FriendlyName: "PG Taipan", BatteryPercent: null, IsConnected: true);

		Assert.Empty(BluetoothDeviceDiscoveryService.LowEnergyBatteryByAddress([node]));
	}

	[Fact]
	public void AChangedReadingIsPublished()
	{
		Assert.True(BluetoothDeviceDiscoveryService.ShouldPublishTreeBattery(43, lastPublished: 44));
	}

	// The poll runs every 3 s; a steady device must not re-announce the same figure each time,
	// because publishing logs and crosses gRPC to the App.
	[Fact]
	public void AnUnchangedReadingIsNotRepublished()
	{
		Assert.False(BluetoothDeviceDiscoveryService.ShouldPublishTreeBattery(43, lastPublished: 43));
	}

	[Fact]
	public void TheFirstReadingIsAlwaysPublished()
	{
		Assert.True(BluetoothDeviceDiscoveryService.ShouldPublishTreeBattery(43, lastPublished: null));
	}

	// A node with no battery property yields 0 through GetValueOrDefault. That is the absence of a
	// reading, not a flat battery, and publishing it would show a false 0 % on the card.
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void ANonReadingIsNeverPublishedFromTheTree(int treeBattery)
	{
		Assert.False(BluetoothDeviceDiscoveryService.ShouldPublishTreeBattery(treeBattery, lastPublished: 43));
	}

	// ── Naming a device (S2) ──────────────────────────────────────────────────────────────────
	//
	// An empty ClientName is not cosmetic: the App matches its card by that string and discards the
	// event outright, so a device that cannot be named silently never appears — no error, no card.
	// That is exactly how a re-paired TAIPAN stayed missing, and it went unnoticed because the rule
	// had no test. It is the only identification path that survives an address change, since a
	// TAIPAN's BLE address increments on every re-pair.

	private static DeviceMap BtRow(string name) => new()
	{
		Name = name, VendorId = "9354", ProductId = "33382", HasBluetooth = true,
	};

	private static DeviceMap UsbRow(string name) => new()
	{
		Name = name, VendorId = "9354", ProductId = "64257", HasBluetooth = false,
	};

	[Fact]
	public void ADeviceIsNamedFromItsOwnNameWhenTheIdIsUnknown()
	{
		Assert.Equal("PG TAIPAN", BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(
			[BtRow("PG TAIPAN")], "PG Taipan"));
	}

	// The address moved, so nothing can be looked up by id — the name has to carry it.
	[Theory]
	[InlineData("PG Taipan")]
	[InlineData("PG TAIPAN")]
	[InlineData("pg taipan")]
	public void NamingSurvivesAnAddressChangeAndIsCaseInsensitive(string deviceName)
	{
		Assert.Equal("PG TAIPAN", BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(
			[BtRow("PG TAIPAN")], deviceName));
	}

	// Only Bluetooth rows may name a Bluetooth device; a wired row of the same product must not.
	[Fact]
	public void AWiredRowNeverNamesABluetoothDevice()
	{
		Assert.Equal(string.Empty, BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(
			[UsbRow("PG TAIPAN")], "PG Taipan"));
	}

	// An unknown device must yield empty rather than borrow the first row's identity — that would
	// put a stranger's card on screen under one of our product names.
	[Fact]
	public void AnUnknownDeviceIsNotNamedAfterSomethingElse()
	{
		Assert.Equal(string.Empty, BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(
			[BtRow("PG TAIPAN"), BtRow("COSMO")], "JBL Xtreme 2"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void ANamelessDeviceYieldsEmptyRatherThanThrowing(string? deviceName)
	{
		Assert.Equal(string.Empty, BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(
			[BtRow("PG TAIPAN")], deviceName));
	}

	// HARDENING (2026-09-02), not a shipped bug — see the correction in
	// docs/audits/2026-09-02-device-association-memory-audit.md (F4).
	//
	// The match read `deviceName.ToUpperInvariant().Contains(m.Name)`: an upper-cased haystack
	// against the RAW catalog name, with the ordinal, case-SENSITIVE Contains overload. In isolation
	// that only matches a row whose own Name is already upper case — but every DeviceMap reaching
	// this function comes from DeviceMapExpander, which sets `Name = device.Name?.ToUpper()`, so in
	// production the invariant held and mixed-case rows never occurred.
	//
	// The coupling is what these pin. Nothing at the call site stated the dependency, and nothing
	// would have failed if the expander stopped upper-casing: the match would silently return
	// nothing for every row, and a device that is never matched produces no error, no log line and
	// no exception — it just never appears on the home page. Note that every test above this line
	// happens to use an UPPERCASE row, so none of them would have noticed either.
	[Theory]
	[InlineData("Rampage", "PG Rampage")]
	[InlineData("Rampage", "PG RAMPAGE")]
	[InlineData("Rampage", "pg rampage")]
	[InlineData("T-Racer", "PG T-Racer")]
	[InlineData("Raster", "PG Raster Keyboard")]
	[InlineData("Cosmo", "PG COSMO")]
	[InlineData("Starlight", "pg starlight")]
	public void AMixedCaseCatalogRowStillNamesItsDevice(string catalogName, string deviceName)
	{
		Assert.Equal(catalogName,
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName([BtRow(catalogName)], deviceName));
	}

	// The genuinely latent defect, as opposed to the hardening above. `"anything".Contains("")` is
	// TRUE, and DeviceMapExpander renders a catalog entry with no Name as `string.Empty` — so an
	// unnamed row matches the first device examined and shadows every real row behind it, putting a
	// stranger's device on screen under a PG product name. `MatchCatalogNameByDeviceName` and
	// `IsCatalogBluetoothDevice` already guarded against it; `DeviceNeedToTrack` did not, and now
	// all three do.
	[Fact]
	public void AnUnnamedCatalogRowDoesNotClaimEveryDevice()
	{
		DeviceMap[] maps = [BtRow(""), BtRow("Rampage")];

		Assert.Equal("Rampage",
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(maps, "PG Rampage"));
	}

	// Two headsets whose names differ only by suffix must not collapse into one another.
	[Fact]
	public void EachCatalogNameMatchesOnlyItsOwnDevice()
	{
		DeviceMap[] maps = [BtRow("COSMO"), BtRow("STARLIGHT")];

		Assert.Equal("STARLIGHT",
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(maps, "PG Starlight"));
		Assert.Equal("COSMO",
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(maps, "PG Cosmo"));
	}

	// Casing must not decide liveness: the tree and InTheHand do not agree on it.
	[Fact]
	public void MatchingIsCaseInsensitiveEndToEnd()
	{
		var live = BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(
			[BleNode(TaipanAddress.ToLowerInvariant(), isConnected: true)]);

		Assert.Contains(
			BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress("19:d5:e9:87:09:3a")!, live);
	}
}
