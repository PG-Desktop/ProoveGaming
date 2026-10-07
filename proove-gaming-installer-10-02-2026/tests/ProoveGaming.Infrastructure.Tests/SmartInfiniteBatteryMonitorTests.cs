using ProoveGaming.Infrastructure.Classes;
using BatteryMonitor = ProoveGaming.DeviceHost.Services.SmartInfiniteBatteryMonitor;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the two pure halves of SmartInfiniteBatteryMonitor, the DeviceHost's fast battery/charge
// poll for Smart Infinite keyboards on their 2.4G dongle (Rampage today):
//
// - ResolveTrackedDevices against the REAL catalog: the tracked set is exactly the dongle rows
//   with a usable battery and a declared BatteryCommand. This is the guard that the new 3 s poll
//   can never touch a device another monitor owns — TAIPAN (TaipanStateMonitor) and Cosmo
//   (CosmoStateMonitor) carry no BatteryCommand, and wired/Bluetooth transports are excluded by
//   connection type.
// - ComputePush, the debounce table: a valid level change pushes immediately; a charge-flag flip
//   pushes only on the second consecutive agreeing read (one malformed 0x1F reply must not
//   flicker the ⚡ icon); -1 is no evidence — no push, cache kept, flip counter reset.
//
// Plan: docs/plans/add-keyboard-charge-push/add-keyboard-charge-push-plan.md
public class SmartInfiniteBatteryMonitorTests
{
	// The Rampage 2.4G receiver's own ids as the catalog declares them, distinct from the
	// board's wired 10473 / 12888 pair.
	private const int RampageDongleVendorId = 1389;
	private const int RampageDongleProductId = 49271;

	private static List<DeviceMap> ExpandedCatalog() => DeviceMapExpander.Expand(DeviceHostCatalog.Load());

	private static List<BatteryMonitor.TrackedDevice> Tracked()
		=> BatteryMonitor.ResolveTrackedDevices(ExpandedCatalog());

	// ── ResolveTrackedDevices against the real catalog ────────────────────────

	[Fact]
	public void TrackedSetIsExactlyTheRampageDongleRow()
	{
		var tracked = Assert.Single(Tracked());

		Assert.Equal(RampageDongleVendorId, tracked.VendorId);
		Assert.Equal(RampageDongleProductId, tracked.ProductId);
		Assert.Equal("RAMPAGE", tracked.Name);
	}

	[Fact]
	public void EveryTrackedDeviceIsADongleRowWithAUsableBatteryCommand()
	{
		var rows = ExpandedCatalog();

		foreach (var tracked in Tracked())
		{
			var row = rows.FirstOrDefault(r =>
				r.VendorId == tracked.VendorId.ToString() && r.ProductId == tracked.ProductId.ToString());

			Assert.NotNull(row);
			Assert.Equal("Dongle2_4G", row!.ConnectionType);
			Assert.True(row.SupportsBattery);
			Assert.NotNull(row.BatteryCommand);
		}
	}

	// TAIPAN's dongle is TaipanStateMonitor's device (battery arrives as 0x48 pushes/replies on
	// its own reader, no BatteryCommand in the catalog) and Cosmo's is CosmoStateMonitor's
	// (battery read from vendor report 0xBC, likewise no BatteryCommand). If either ever shows
	// up here, two monitors are talking to one interface.
	[Fact]
	public void TaipanAndTheHeadsetsAreNeverTracked()
	{
		var monitoredElsewhere = ExpandedCatalog()
			.Where(r => r.Name is "TAIPAN" or "COSMO" or "STARLIGHT")
			.Select(r => (r.VendorId, r.ProductId))
			.ToList();

		Assert.NotEmpty(monitoredElsewhere);

		foreach (var tracked in Tracked())
			Assert.DoesNotContain(
				(tracked.VendorId.ToString(), tracked.ProductId.ToString()),
				monitoredElsewhere);
	}

	[Fact]
	public void WiredAndBluetoothRowsAreNeverTracked()
	{
		var excludedTransports = ExpandedCatalog()
			.Where(r => r.HasBluetooth
				|| !string.Equals(r.ConnectionType, "Dongle2_4G", StringComparison.OrdinalIgnoreCase))
			.Select(r => (r.VendorId, r.ProductId))
			.ToList();

		Assert.NotEmpty(excludedTransports);

		foreach (var tracked in Tracked())
			Assert.DoesNotContain(
				(tracked.VendorId.ToString(), tracked.ProductId.ToString()),
				excludedTransports);
	}

	// A dongle row whose battery the catalog calls unusable (SupportsBattery: false) must never
	// be polled, whatever command it declares — a wrong indicator is worse than none.
	[Fact]
	public void ADongleRowWithoutABatteryOrACommandIsNotTracked()
	{
		var rows = new List<DeviceMap>
		{
			new() { Name = "NOBATT", ConnectionType = "Dongle2_4G", SupportsBattery = false, BatteryCommand = 31, VendorId = "1", ProductId = "2" },
			new() { Name = "NOCMD", ConnectionType = "Dongle2_4G", SupportsBattery = true, BatteryCommand = null, VendorId = "3", ProductId = "4" },
			new() { Name = "BADIDS", ConnectionType = "Dongle2_4G", SupportsBattery = true, BatteryCommand = 31, VendorId = "not-a-vid", ProductId = "5" },
			new() { Name = "GOOD", ConnectionType = "Dongle2_4G", SupportsBattery = true, BatteryCommand = 31, VendorId = "6", ProductId = "7" },
		};

		var tracked = Assert.Single(BatteryMonitor.ResolveTrackedDevices(rows));
		Assert.Equal("GOOD", tracked.Name);
	}

	// ── ComputePush: the debounce table ───────────────────────────────────────

	private static BatteryMonitor.DebounceState Confirmed(int percent, bool isCharging)
		=> new(true, percent, isCharging, false, 0);

	[Fact]
	public void FirstEverValidReadingPushesImmediately()
	{
		var (next, push) = BatteryMonitor.ComputePush(default, 85, true);

		Assert.True(push);
		Assert.True(next.HasReading);
		Assert.Equal(85, next.Percent);
		Assert.True(next.IsCharging);
	}

	[Fact]
	public void UnchangedReadingDoesNotPush()
	{
		var (next, push) = BatteryMonitor.ComputePush(Confirmed(85, false), 85, false);

		Assert.False(push);
		Assert.Equal(Confirmed(85, false), next);
	}

	[Fact]
	public void LevelChangeWithAValidReadingPushesImmediately()
	{
		var (next, push) = BatteryMonitor.ComputePush(Confirmed(85, false), 84, false);

		Assert.True(push);
		Assert.Equal(84, next.Percent);
		Assert.False(next.IsCharging);
	}

	[Fact]
	public void ChargeFlipPushesOnlyOnTheSecondAgreeingRead()
	{
		var previous = Confirmed(85, false);

		// Cable in, first read: the flip is observed but not yet trusted.
		var (afterFirst, firstPush) = BatteryMonitor.ComputePush(previous, 85, true);
		Assert.False(firstPush);
		Assert.False(afterFirst.IsCharging);
		Assert.Equal(1, afterFirst.PendingCount);

		// Second agreeing read: confirmed, pushed.
		var (afterSecond, secondPush) = BatteryMonitor.ComputePush(afterFirst, 85, true);
		Assert.True(secondPush);
		Assert.True(afterSecond.IsCharging);
		Assert.Equal(0, afterSecond.PendingCount);
	}

	[Fact]
	public void ChargeFlipThatRevertsAfterOneReadNeverPushes()
	{
		var previous = Confirmed(85, true);

		// One reply misses the charge byte (transient) ...
		var (afterGlitch, glitchPush) = BatteryMonitor.ComputePush(previous, 85, false);
		Assert.False(glitchPush);

		// ... and the next read agrees with the confirmed state again: no flicker, counter gone.
		var (afterRecovery, recoveryPush) = BatteryMonitor.ComputePush(afterGlitch, 85, true);
		Assert.False(recoveryPush);
		Assert.True(afterRecovery.IsCharging);
		Assert.Equal(0, afterRecovery.PendingCount);
	}

	// 0 and 101 are the same "malformed reply" family as -1 — raw bytes that escaped parsing
	// must never move the card. Pinned at the boundary because the guard is `< 1 or > 100`,
	// not `< 0`.
	[Theory]
	[InlineData(0)]
	[InlineData(101)]
	public void OutOfRangeReadingIsNoEvidenceJustLikeMinusOne(int percent)
	{
		var previous = Confirmed(85, true);

		var (next, push) = BatteryMonitor.ComputePush(previous, percent, false);

		Assert.False(push);
		Assert.Equal(85, next.Percent);
		Assert.True(next.IsCharging);
		Assert.Equal(0, next.PendingCount);
	}

	// ... while 1 and 100, the extremes of the valid range, are real readings and push.
	[Theory]
	[InlineData(1)]
	[InlineData(100)]
	public void BoundaryPercentagesAreValidReadingsAndPush(int percent)
	{
		var (next, push) = BatteryMonitor.ComputePush(Confirmed(50, false), percent, false);

		Assert.True(push);
		Assert.Equal(percent, next.Percent);
	}

	[Fact]
	public void InvalidReadingDoesNotPushAndKeepsTheCache()
	{
		var previous = Confirmed(85, true);

		var (next, push) = BatteryMonitor.ComputePush(previous, -1, false);

		Assert.False(push);
		Assert.True(next.HasReading);
		Assert.Equal(85, next.Percent);
		Assert.True(next.IsCharging);
	}

	[Fact]
	public void InvalidReadingResetsTheChargeFlipCounter()
	{
		var previous = Confirmed(85, false);

		// Flip observed once, then a failed read: the half-confirmed flip must not survive it.
		var (pending, _) = BatteryMonitor.ComputePush(previous, 85, true);
		Assert.Equal(1, pending.PendingCount);

		var (reset, resetPush) = BatteryMonitor.ComputePush(pending, -1, true);
		Assert.False(resetPush);
		Assert.Equal(0, reset.PendingCount);

		// The debounce starts over: one agreeing read is again not enough ...
		var (firstAgain, pushAgain) = BatteryMonitor.ComputePush(reset, 85, true);
		Assert.False(pushAgain);
		Assert.Equal(1, firstAgain.PendingCount);

		// ... and the second confirms.
		var (confirmed, confirmedPush) = BatteryMonitor.ComputePush(firstAgain, 85, true);
		Assert.True(confirmedPush);
		Assert.True(confirmed.IsCharging);
	}

	// ── ComputeCadence: the failed-poll backoff (review round 1, S1) ──────────
	//
	// The 2.4G receiver enumerates whether or not the keyboard behind it is on, so presence
	// alone would keep a switched-off keyboard on the 3 s cadence forever — with every tick a
	// full failing candidate scan. These pin the rule: back off to IdleInterval after
	// FailedTicksBeforeBackoff consecutive no-reading polls on a present device; any valid
	// reading (or, in the loop, a device-list change) restores the fast cadence.

	[Fact]
	public void ValidReadingKeepsTheFastCadenceAndResetsTheFailureCount()
	{
		var (failures, wait) = BatteryMonitor.ComputeCadence(
			devicePresent: true, gotValidReading: true,
			consecutiveFailures: BatteryMonitor.FailedTicksBeforeBackoff + 3);

		Assert.Equal(0, failures);
		Assert.Equal(BatteryMonitor.PollInterval, wait);
	}

	[Fact]
	public void NoDevicePresentIdlesAndResetsTheFailureCount()
	{
		var (failures, wait) = BatteryMonitor.ComputeCadence(
			devicePresent: false, gotValidReading: false, consecutiveFailures: 3);

		Assert.Equal(0, failures);
		Assert.Equal(BatteryMonitor.IdleInterval, wait);
	}

	[Fact]
	public void FailuresBelowTheThresholdKeepTheFastCadence()
	{
		// A multi-second key-table/macro write holding the DeviceGate over a few ticks must
		// not trigger the backoff — that is why the threshold is more than two.
		var failures = 0;
		for (var tick = 1; tick < BatteryMonitor.FailedTicksBeforeBackoff; tick++)
		{
			TimeSpan wait;
			(failures, wait) = BatteryMonitor.ComputeCadence(true, false, failures);

			Assert.Equal(tick, failures);
			Assert.Equal(BatteryMonitor.PollInterval, wait);
		}
	}

	[Fact]
	public void ReachingTheThresholdBacksOffToIdleAndStaysThere()
	{
		var atThreshold = BatteryMonitor.ComputeCadence(true, false,
			BatteryMonitor.FailedTicksBeforeBackoff - 1);
		Assert.Equal(BatteryMonitor.FailedTicksBeforeBackoff, atThreshold.NextConsecutiveFailures);
		Assert.Equal(BatteryMonitor.IdleInterval, atThreshold.NextWait);

		// Still failing on the next (slow) tick: stay backed off, keep counting.
		var beyond = BatteryMonitor.ComputeCadence(true, false, atThreshold.NextConsecutiveFailures);
		Assert.Equal(BatteryMonitor.FailedTicksBeforeBackoff + 1, beyond.NextConsecutiveFailures);
		Assert.Equal(BatteryMonitor.IdleInterval, beyond.NextWait);

		// The keyboard comes back: one valid reading restores the fast cadence at once.
		var recovered = BatteryMonitor.ComputeCadence(true, true, beyond.NextConsecutiveFailures);
		Assert.Equal(0, recovered.NextConsecutiveFailures);
		Assert.Equal(BatteryMonitor.PollInterval, recovered.NextWait);
	}

	[Fact]
	public void LevelChangeWhileAFlipRevertsPushesTheConfirmedChargeState()
	{
		var previous = Confirmed(85, false);

		var (pending, _) = BatteryMonitor.ComputePush(previous, 85, true);

		// The level moves on the read where the half-confirmed flip reverts: the push goes out
		// for the level, carrying the CONFIRMED charge state — the glitched flip never leaks.
		var (next, push) = BatteryMonitor.ComputePush(pending, 84, false);
		Assert.True(push);
		Assert.Equal(84, next.Percent);
		Assert.False(next.IsCharging);
		Assert.Equal(0, next.PendingCount);
	}

	[Fact]
	public void LevelChangeOnTheConfirmingFlipReadPushesBothTogether()
	{
		var previous = Confirmed(85, false);

		var (pending, _) = BatteryMonitor.ComputePush(previous, 85, true);

		// The level moves on the very read that confirms the charge flip: a single push carries
		// both the new level and the newly-confirmed charge state.
		var (next, push) = BatteryMonitor.ComputePush(pending, 84, true);
		Assert.True(push);
		Assert.Equal(84, next.Percent);
		Assert.True(next.IsCharging);
		Assert.Equal(0, next.PendingCount);
	}
}
