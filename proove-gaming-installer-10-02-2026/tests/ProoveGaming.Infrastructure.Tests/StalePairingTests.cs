using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// The stale-ghost-pairing veto on the LE scan-need computation.
//
// A paired BLE entry whose device re-paired under a new address (PG Taipan's increments on every
// re-pair) can never reconnect, yet it stays tracked and disconnected — which kept "a tracked
// device needs the scan" permanently true and the scan windows reopening forever on a tester
// machine. The veto excludes a device whose DEVPKEY_Bluetooth_LastConnectedTime — measured
// 2026-09-02 stamping every connect and disconnect (docs/plans/add-stale-pairing-detection/) —
// is more than StalePairingThreshold old.
//
// These tests pin the rule's fail-open directions, because every wrong answer is silent: a veto
// that fires too eagerly kills reconnection for a real device with nothing in the log a user
// would connect to the cause, and one that never fires just restores the perpetual scan.
public class StalePairingTests
{
	private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan Threshold = TimeSpan.FromDays(14);

	[Fact]
	public void DisconnectedBeyondThreshold_IsStale()
	{
		// The golden path: the tree says disconnected and the last link was months ago — a ghost.
		Assert.True(BluetoothDeviceDiscoveryService.IsStalePairing(
			Now.AddDays(-60), treeSaysConnected: false, Now, Threshold));
	}

	[Fact]
	public void DisconnectedInsideThreshold_IsNotStale()
	{
		// A device on holiday is not a ghost: switched off yesterday, still proactively scanned for.
		Assert.False(BluetoothDeviceDiscoveryService.IsStalePairing(
			Now.AddDays(-1), treeSaysConnected: false, Now, Threshold));
	}

	[Fact]
	public void ExactlyAtThreshold_IsNotStale()
	{
		// Strictly `>` — pinned so the boundary cannot quietly become `>=`. This also covers the
		// measured local-time-FILETIME skew (a stamp can read hours "in the future"): anything not
		// clearly beyond the threshold is fresh.
		Assert.False(BluetoothDeviceDiscoveryService.IsStalePairing(
			Now - Threshold, treeSaysConnected: false, Now, Threshold));
	}

	[Fact]
	public void ConnectedDevice_IsNeverStale()
	{
		// A live link outranks any timestamp — a connected device with an odd stamp must not be
		// vetoed.
		Assert.False(BluetoothDeviceDiscoveryService.IsStalePairing(
			Now.AddDays(-60), treeSaysConnected: true, Now, Threshold));
	}

	[Fact]
	public void TreeWithNoOpinion_IsNotStale()
	{
		// The tri-state trap ResolveConnected documents: null means the tree has NOT answered (no
		// successful poll yet, or an unresolvable address), and reading it as "disconnected" would
		// veto every device on startup — fail open.
		Assert.False(BluetoothDeviceDiscoveryService.IsStalePairing(
			Now.AddDays(-60), treeSaysConnected: null, Now, Threshold));
	}

	[Fact]
	public void MissingTimestamp_IsNotStale()
	{
		// No timestamp is a device we cannot judge, not a ghost — fail open, keep scanning.
		Assert.False(BluetoothDeviceDiscoveryService.IsStalePairing(
			lastConnectedUtc: null, treeSaysConnected: false, Now, Threshold));
	}

	// ── ResolveScanNeed — the sweep's aggregation over the per-device verdicts ────────────────
	//
	// PR #130 review m4: the veto used to be purely per-device, so a machine whose ONLY paired
	// entry was 14+ days cold never opened a scan window again — reconnection for a real device
	// back from a long holiday silently died. These pin the anchor rule that fixed it.

	[Fact]
	public void LoneStaleGhost_StillNeedsTheScan()
	{
		// No sibling exists whose window would reconnect it, so the veto yields — fail open.
		Assert.True(BluetoothDeviceDiscoveryService.ResolveScanNeed(
			[(Paired: true, Connected: false, Stale: true)]));
	}

	[Fact]
	public void GhostBesideItsConnectedRepairedSibling_IsVetoed()
	{
		// The canonical ghost case (Taipan re-pairs under a new address): the live sibling is the
		// anchor, the dead entry must not hold the scan open forever — the DAS-growth fix stands.
		Assert.False(BluetoothDeviceDiscoveryService.ResolveScanNeed(
		[
			(Paired: true, Connected: true, Stale: false),
			(Paired: true, Connected: false, Stale: true),
		]));
	}

	[Fact]
	public void GhostBesideADisconnectedFreshDevice_ScansForTheFreshOne()
	{
		// The fresh disconnected device needs the window on its own; the ghost rides along
		// (the exclusion log's promise: any open window still reconnects it).
		Assert.True(BluetoothDeviceDiscoveryService.ResolveScanNeed(
		[
			(Paired: true, Connected: false, Stale: false),
			(Paired: true, Connected: false, Stale: true),
		]));
	}

	[Fact]
	public void EverythingConnected_NeedsNoScan()
	{
		Assert.False(BluetoothDeviceDiscoveryService.ResolveScanNeed(
			[(Paired: true, Connected: true, Stale: false)]));
	}

	[Fact]
	public void UnpairedDevicesAreNotAnchors()
	{
		// An unpaired entry can neither reconnect nor anchor the veto: the lone ghost still wins.
		Assert.True(BluetoothDeviceDiscoveryService.ResolveScanNeed(
		[
			(Paired: false, Connected: false, Stale: false),
			(Paired: true, Connected: false, Stale: true),
		]));
	}

	[Fact]
	public void NoTrackedDevices_NeedsNoScan()
	{
		Assert.False(BluetoothDeviceDiscoveryService.ResolveScanNeed([]));
	}
}
