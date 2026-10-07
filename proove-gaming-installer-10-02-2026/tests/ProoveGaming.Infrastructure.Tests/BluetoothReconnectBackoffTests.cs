using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// The two rate limits that stand between a Bluetooth device that will not connect and a Windows
// service holding 12 GB.
//
// Background (docs/audits/2026-09-02-device-association-memory-audit.md): a mouse and a keyboard
// left connected overnight took DeviceAssociationService — the Windows service that brokers
// DeviceInformation enumeration and GATT sessions — to ~12 GB. Neither of the enumeration paths the
// codebase suspected moved its memory when measured; both loops on the WinRT path did, because
// neither had a bound:
//
//   F1  _pendingConnections stopped CONCURRENT connect attempts and nothing else, so the instant one
//       failed the next advertisement started another. A BLE peripheral advertises many times a
//       second, and every attempt creates a native GattSession.
//   F3  TriggerRediscoveryIfUnexplained was guarded by a flat 10 s cooldown, which is not a bound at
//       all: the "unexplained" condition can stay true forever, and each pass is a full
//       DeviceInformation enumeration.
//
// Both schedules are pure functions precisely so they can be pinned here — the loops themselves need
// a live radio, a paired device and hours of wall-clock time, so this is the only level at which the
// bound can be asserted at all. A future edit that flattens either schedule back to a constant is
// the exact regression these guard, and it would be invisible in every other test.
public class BluetoothReconnectBackoffTests
{
	// ── F1: the GATT connect backoff ──────────────────────────────────────────────────────────

	// 1 s doubling. The first retry has to stay quick — a device that is merely slow to answer
	// should reconnect without the user noticing — while the growth is what stops a device that
	// never will from being retried thousands of times an hour.
	[Theory]
	[InlineData(1, 1)]
	[InlineData(2, 2)]
	[InlineData(3, 4)]
	[InlineData(4, 8)]
	[InlineData(5, 16)]
	[InlineData(6, 32)]
	public void TheConnectBackoffDoublesFromOneSecond(int consecutiveFailures, int expectedSeconds)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(expectedSeconds),
			BluetoothDeviceDiscoveryService.NextConnectDelay(consecutiveFailures));
	}

	// The ceiling is the half that actually bounds the leak: without it the doubling would be
	// correct and useless, because a device is retired long before the delay grows large enough to
	// matter. With it, the worst steady state a surviving device can reach is one attempt a minute.
	[Theory]
	[InlineData(7)]
	[InlineData(10)]
	[InlineData(64)]
	[InlineData(int.MaxValue)]
	public void TheConnectBackoffIsCappedAtOneMinute(int consecutiveFailures)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(60),
			BluetoothDeviceDiscoveryService.NextConnectDelay(consecutiveFailures));
	}

	// Defensive, and the reason the clamp is on the SHIFT rather than on the result: a shift by a
	// negative count is undefined-ish in C# (it masks to 0..31), so a degenerate count could
	// silently produce a delay far longer than the ceiling — a device that then never retries at
	// all, which is the opposite failure and a much quieter one.
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void ADegenerateFailureCountFallsBackToTheFirstStep(int consecutiveFailures)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(1),
			BluetoothDeviceDiscoveryService.NextConnectDelay(consecutiveFailures));
	}

	// The schedule must be monotonic. A dip would let a device that has been failing for longer be
	// retried sooner than one that just started, which is backwards and would go unnoticed.
	[Fact]
	public void TheConnectBackoffNeverDecreases()
	{
		var previous = TimeSpan.Zero;
		for (var failures = 1; failures <= 20; failures++)
		{
			var delay = BluetoothDeviceDiscoveryService.NextConnectDelay(failures);
			Assert.True(delay >= previous,
				$"delay for {failures} failures ({delay}) is shorter than for {failures - 1} ({previous})");
			previous = delay;
		}
	}

	// What the bound is actually worth, stated in seconds rather than left implicit in the schedule.
	// A device is released after ConnectAttemptsBeforeRetire (6) consecutive failures, so the delays
	// that elapse first are the ones for failures 1..5. If someone raises the retire threshold, this
	// is the number that grows — and it is the one worth looking at, because it is how long the
	// radio stays busy on a device that is never coming back.
	[Fact]
	public void ADeviceIsRetiredAfterAboutHalfAMinuteOfRetrying()
	{
		var total = TimeSpan.Zero;
		for (var failures = 1; failures <= 5; failures++)
			total += BluetoothDeviceDiscoveryService.NextConnectDelay(failures);

		Assert.Equal(TimeSpan.FromSeconds(31), total);
	}

	// ── F3: the rediscovery schedule ──────────────────────────────────────────────────────────

	// The first step is deliberately the old cooldown. Rediscovery exists so that a device removed
	// and re-paired in Windows comes back without an App restart, and that is a human action being
	// waited on — slowing the FIRST attempt down would trade a real bug fix for this one.
	[Fact]
	public void TheFirstRediscoveryKeepsTheOriginalTenSecondCadence()
	{
		Assert.Equal(TimeSpan.FromSeconds(10), BluetoothDeviceDiscoveryService.RediscoveryDelay(0));
	}

	// ... and then backs off, because a condition that two passes have not resolved is not going to
	// be resolved by running the same enumeration 8,640 more times before morning.
	[Theory]
	[InlineData(0, 10)]
	[InlineData(1, 30)]
	[InlineData(2, 300)]
	[InlineData(3, 300)]
	[InlineData(99, 300)]
	[InlineData(int.MaxValue, 300)]
	public void RediscoveryBacksOffThenHoldsAtFiveMinutes(int consecutive, int expectedSeconds)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(expectedSeconds),
			BluetoothDeviceDiscoveryService.RediscoveryDelay(consecutive));
	}

	// The counter is reset on two separate paths (nothing live, and nothing unexplained), so a
	// negative can only arrive through a future editing mistake — but an index out of range here
	// would throw inside the 3 s poll loop, on a thread whose only handler logs at Debug.
	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public void ANegativeRediscoveryCountFallsBackToTheFirstStep(int consecutive)
	{
		Assert.Equal(TimeSpan.FromSeconds(10), BluetoothDeviceDiscoveryService.RediscoveryDelay(consecutive));
	}

	// Same monotonicity rule as the connect backoff, and the same reason.
	[Fact]
	public void TheRediscoveryScheduleNeverDecreases()
	{
		var previous = TimeSpan.Zero;
		for (var consecutive = 0; consecutive <= 10; consecutive++)
		{
			var delay = BluetoothDeviceDiscoveryService.RediscoveryDelay(consecutive);
			Assert.True(delay >= previous,
				$"delay at {consecutive} consecutive rediscoveries ({delay}) is shorter than at {consecutive - 1} ({previous})");
			previous = delay;
		}
	}

	// ── The LE scan-window reopen schedule (2026-09-08) ───────────────────────────────────────
	//
	// The third bound, added after QA left a TAIPAN connected on its 2.4G DONGLE overnight and the
	// machine ran out of memory. The two schedules above were both in place and both behaving; what
	// neither of them bounds is the SCAN, and the scan is the biggest thing we ask Device
	// Association Service for.
	//
	// A mouse that is paired over Bluetooth but switched to its dongle is paired-and-not-connected
	// for as long as it is used — and so is a Bluetooth keyboard switched off for the night. The
	// windowing introduced by the audit reopened a 10 s scan every 30 s sweep for exactly as long
	// as that lasted, i.e. indefinitely, which is a third of all wall-clock time spent scanning on
	// a machine where nothing is wrong and nothing can be reconnected.
	//
	// The schedule below is what turns "forever at 33 %" into "forever at 3 %", and the reset on
	// every connection-state transition is what keeps reconnection as fast as it was. Pure, so it
	// can be pinned here: the loop itself needs a radio, a paired device and a night.

	// Index 0 is the definition of "no quiet window has closed yet, nothing is owed". It is NOT the
	// path production takes to make the first window immediate — the close increments before it
	// looks the delay up, so the live code never reads index 0; immediacy comes from
	// ResetIdleBackoff setting the next-window time to MinValue. The invariant that actually
	// matters ("a window that spanned a change is not charged") is pinned by the
	// ShouldChargeQuietWindow cases below, not here.
	[Fact]
	public void ZeroQuietWindowsOwesNoDelay()
	{
		Assert.Equal(TimeSpan.Zero, BluetoothDeviceDiscoveryService.ScanReopenDelay(0));
	}

	// ── The invariant the backoff's safety rests on ───────────────────────────────────────────
	//
	// A scan window is open for 10 s at a time, so a device dropping *while one is open* is an
	// ordinary event, not an exotic one: at the 30 s step it is roughly one interleaving in four.
	// That case used to be silently lost. `MarkDisconnected` reset the backoff and called
	// `SyncScanState`, which correctly returned at its `Status: Started` check — a window was
	// already open — and then the close charged the window anyway and overwrote the reset with a
	// 30 s delay. The window after a fresh disconnect was therefore NOT immediate, which is exactly
	// what the plan's acceptance criterion promises and exactly the property that makes waiting
	// safe at all.
	//
	// The sweep absorbs its version of this race by repeating its first schedule step; the scan
	// cannot, because its first step is what makes the window immediate. So it says it literally
	// via an epoch, and these pin it.

	[Fact]
	public void AWindowThatSpannedNoChangeIsCharged()
	{
		// Nothing reset the backoff while it was open, so it counts toward the escalation.
		Assert.True(BluetoothDeviceDiscoveryService.ShouldChargeQuietWindow(openedEpoch: 7, currentEpoch: 7));
	}

	[Fact]
	public void AWindowThatSpannedAChangeIsNotCharged()
	{
		// A device connected or dropped while this window was open: the reset stands, the next
		// window is immediate, and this one is not evidence that nothing is happening.
		Assert.False(BluetoothDeviceDiscoveryService.ShouldChargeQuietWindow(openedEpoch: 7, currentEpoch: 8));
	}

	[Fact]
	public void SeveralChangesDuringOneWindowStillSpareIt()
	{
		// The epoch is a marker, not a counter of anything the close needs to add up — any
		// difference at all means the run of quiet this window belonged to is over.
		Assert.False(BluetoothDeviceDiscoveryService.ShouldChargeQuietWindow(openedEpoch: 7, currentEpoch: 11));
	}

	// ... and then backs off, because N windows that closed with nothing having changed are
	// evidence that the N+1th will not change anything either.
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 30)]
	[InlineData(2, 120)]
	[InlineData(3, 300)]
	[InlineData(4, 300)]
	[InlineData(99, 300)]
	[InlineData(int.MaxValue, 300)]
	public void TheScanReopenScheduleBacksOffThenHoldsAtFiveMinutes(int consecutiveWindows, int expectedSeconds)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(expectedSeconds),
			BluetoothDeviceDiscoveryService.ScanReopenDelay(consecutiveWindows));
	}

	// The counter is only ever incremented and reset to zero, so a negative is a future editing
	// mistake — but an index out of range would throw inside the scheduled close, under
	// _watcherLock, and leave the watcher Started with no close pending: the unbounded scan, back.
	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public void ADegenerateWindowCountFallsBackToTheFirstStep(int consecutiveWindows)
	{
		Assert.Equal(TimeSpan.Zero, BluetoothDeviceDiscoveryService.ScanReopenDelay(consecutiveWindows));
	}

	// Same monotonicity rule as the two schedules above.
	[Fact]
	public void TheScanReopenScheduleNeverDecreases()
	{
		var previous = TimeSpan.Zero;
		for (var windows = 0; windows <= 10; windows++)
		{
			var delay = BluetoothDeviceDiscoveryService.ScanReopenDelay(windows);
			Assert.True(delay >= previous,
				$"delay at {windows} closed windows ({delay}) is shorter than at {windows - 1} ({previous})");
			previous = delay;
		}
	}

	// What the bound is worth, stated rather than left implicit — this is the number the incident
	// was about. Steady state for a device that is not coming back is one 10 s window per ceiling
	// step: ~48 minutes of scanning a day instead of ~8 hours.
	[Fact]
	public void TheSteadyStateScanDutyIsAboutThreePercent()
	{
		var ceiling = BluetoothDeviceDiscoveryService.ScanReopenDelay(int.MaxValue);
		var window = TimeSpan.FromSeconds(10);

		var duty = window.TotalSeconds / (window + ceiling).TotalSeconds;

		Assert.True(duty < 0.04, $"steady-state scan duty is {duty:P1}, which is no longer a bound worth having");
	}

	// ── The sweep cadence (2026-09-08) ────────────────────────────────────────────────────────
	//
	// The fourth and last thing this service asks the Device Association Framework for: the
	// paired-device enumeration on the periodic sweep. `Bluetooth.GetPairedDevicesAsync` resolves a
	// native BluetoothLEDevice per paired device on the machine, so a flat 30 s cadence is 2,880 of
	// them a day — every one of which, on a machine where nothing is happening, learns nothing.
	//
	// Same shape as the scan: escalate while nothing changes, collapse on any transition. What
	// makes it safe is that every job the sweep does is covered faster by something cheaper while
	// it waits (the 3 s tree poll, the 3 s classic poll, the rediscovery trigger) — see the comment
	// on SweepIntervalSchedule.

	// The first step is the original cadence, and so is the second: a change landing inside a delay
	// zeroes the counter, which the following increment takes straight back to 1. Without the
	// repeat, that entirely ordinary race would punish a real event with a longer interval.
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	public void TheFirstTwoSweepsAfterAChangeKeepTheActiveCadence(int quietSweeps)
	{
		Assert.Equal(TimeSpan.FromSeconds(30), BluetoothDeviceDiscoveryService.SweepInterval(quietSweeps));
	}

	[Theory]
	[InlineData(0, 30)]
	[InlineData(1, 30)]
	[InlineData(2, 60)]
	[InlineData(3, 120)]
	[InlineData(4, 300)]
	[InlineData(5, 300)]
	[InlineData(99, 300)]
	[InlineData(int.MaxValue, 300)]
	public void TheSweepIntervalBacksOffThenHoldsAtFiveMinutes(int quietSweeps, int expectedSeconds)
	{
		Assert.Equal(
			TimeSpan.FromSeconds(expectedSeconds),
			BluetoothDeviceDiscoveryService.SweepInterval(quietSweeps));
	}

	// Same clamp reasoning as the other three: an out-of-range index would throw inside the sweep
	// loop, whose only handler logs a warning and goes round again — an enumeration cadence that
	// silently became "as fast as the loop can spin".
	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public void ADegenerateQuietSweepCountFallsBackToTheActiveCadence(int quietSweeps)
	{
		Assert.Equal(TimeSpan.FromSeconds(30), BluetoothDeviceDiscoveryService.SweepInterval(quietSweeps));
	}

	[Fact]
	public void TheSweepScheduleNeverDecreases()
	{
		var previous = TimeSpan.Zero;
		for (var quiet = 0; quiet <= 10; quiet++)
		{
			var interval = BluetoothDeviceDiscoveryService.SweepInterval(quiet);
			Assert.True(interval >= previous,
				$"interval at {quiet} quiet sweeps ({interval}) is shorter than at {quiet - 1} ({previous})");
			previous = interval;
		}
	}

	// ── The bounds as a set ───────────────────────────────────────────────────────────────────

	// The two idle escalations are driven by ONE reset hook (ResetIdleBackoff), so a machine at
	// rest settles into a single rhythm: one scan window and one enumeration per ceiling step. If
	// someone raises one ceiling without the other, the service starts scanning on one clock and
	// enumerating on another for no reason anybody wrote down — this is the assertion that says
	// the pairing was deliberate.
	[Fact]
	public void TheScanAndSweepCeilingsAgree()
	{
		Assert.Equal(
			BluetoothDeviceDiscoveryService.ScanReopenDelay(int.MaxValue),
			BluetoothDeviceDiscoveryService.SweepInterval(int.MaxValue));
	}

	// Every rate limit in this file must actually limit. A schedule whose ceiling is zero — or
	// whose steps were flattened to a constant during some refactor — reads as "bounded" at the
	// call site while being nothing of the kind, which is exactly how the 2026-09-08 recurrence
	// happened: the scan WAS windowed, and the window was reopened forever.
	[Fact]
	public void EveryScheduleHasARealCeiling()
	{
		Assert.True(BluetoothDeviceDiscoveryService.NextConnectDelay(int.MaxValue) > TimeSpan.Zero);
		Assert.True(BluetoothDeviceDiscoveryService.RediscoveryDelay(int.MaxValue) > TimeSpan.Zero);
		Assert.True(BluetoothDeviceDiscoveryService.ScanReopenDelay(int.MaxValue) > TimeSpan.Zero);
		Assert.True(BluetoothDeviceDiscoveryService.SweepInterval(int.MaxValue) > TimeSpan.Zero);

		Assert.True(BluetoothDeviceDiscoveryService.ScanReopenDelay(int.MaxValue)
			> BluetoothDeviceDiscoveryService.ScanReopenDelay(0));
		Assert.True(BluetoothDeviceDiscoveryService.SweepInterval(int.MaxValue)
			> BluetoothDeviceDiscoveryService.SweepInterval(0));
	}
}
