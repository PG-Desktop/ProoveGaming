using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The low-battery announcement rule: one toast the first time the level reaches 20 %, one more
// the first time it reaches 10 %, re-armed only by a genuine recharge (QA 2026-08-20 — the
// per-card guard was cleared by every null reading, so a sleeping mouse re-announced the same
// percentage on every wake-up, i.e. "at random moments"). Like DeriveHidTransports, the rule is
// a static helper because HomeViewModel's constructor needs the full device/session graph.
public class HomeViewModelLowBatteryTests
{
	private static (bool Rearm, int? Announce) Decide(
		int? level, int announcedBand = 0, bool connected = true, bool isCharging = false,
		bool hasLiveReading = true) =>
		HomeViewModel.LowBatteryDecision(level, connected, isCharging, hasLiveReading, announcedBand);

	[Fact]
	public void ReachingTheLowBand_AnnouncesOnce()
	{
		Assert.Equal((false, HomeViewModel.LowBatteryBand), Decide(20));

		// Further readings inside the band stay quiet — this is the "once".
		Assert.Equal((false, (int?)null), Decide(19, announcedBand: HomeViewModel.LowBatteryBand));
		Assert.Equal((false, (int?)null), Decide(14, announcedBand: HomeViewModel.LowBatteryBand));
	}

	[Fact]
	public void ReachingTheCriticalBand_AnnouncesASecondTime()
	{
		Assert.Equal(
			(false, HomeViewModel.CriticalBatteryBand),
			Decide(10, announcedBand: HomeViewModel.LowBatteryBand));

		Assert.Equal(
			(false, (int?)null),
			Decide(9, announcedBand: HomeViewModel.CriticalBatteryBand));
	}

	// Dropping straight past both bands (e.g. the first reading after a reconnect is 8 %) gives
	// the user one toast, not a backlog of two.
	[Fact]
	public void DroppingPastBothBands_AnnouncesOnlyTheLowerOne()
	{
		Assert.Equal((false, HomeViewModel.CriticalBatteryBand), Decide(8));
	}

	// THE bug: a null reading is a missed poll or a sleeping device, not a recharge. It must not
	// re-arm the announcement — that is what fired the same 14 % toast at random moments.
	[Fact]
	public void NullReading_DecidesNothing_AndKeepsTheAnnouncedState()
	{
		Assert.Equal((false, (int?)null), Decide(null, announcedBand: HomeViewModel.LowBatteryBand));
	}

	// 21–24 is the hysteresis zone: above the band, but not provably recharged. A reading
	// jittering 19↔21 must neither re-arm nor re-announce.
	[Fact]
	public void HysteresisZone_HoldsTheState()
	{
		Assert.Equal((false, (int?)null), Decide(22, announcedBand: HomeViewModel.LowBatteryBand));
		Assert.Equal((false, (int?)null), Decide(22));
	}

	[Fact]
	public void RechargingAboveTheRearmLevel_RearmsBothBands()
	{
		Assert.Equal(
			(true, (int?)null),
			Decide(HomeViewModel.LowBatteryRearmLevel, announcedBand: HomeViewModel.CriticalBatteryBand));
	}

	// A high reading only counts as a recharge when it is trustworthy. A restored card's stale
	// snapshot (no live reading yet) and a charge-time value (TAIPAN's cable readings derive
	// from charge voltage, not charge level) must HOLD the announced state — re-arming on them
	// let the next live sub-20 % reading toast again, the "random moments" report of
	// 2026-08-31. The genuine re-arm fires on the first live, off-charger reading instead.
	[Fact]
	public void StaleOrChargingHighReading_DoesNotRearm()
	{
		Assert.Equal(
			(false, (int?)null),
			Decide(80, announcedBand: HomeViewModel.LowBatteryBand, hasLiveReading: false));
		Assert.Equal(
			(false, (int?)null),
			Decide(80, announcedBand: HomeViewModel.LowBatteryBand, isCharging: true));

		// Unplugged with the charge done, the first live reading re-arms as before.
		Assert.Equal((true, (int?)null), Decide(80, announcedBand: HomeViewModel.LowBatteryBand));
	}

	// A rise back out of the critical band without a full recharge (a short stint on the charger,
	// or plain jitter) does not re-open the 20 % band — the next drop stays quiet until re-armed.
	[Fact]
	public void PartialRecovery_DoesNotReannounceTheUpperBand()
	{
		Assert.Equal((false, (int?)null), Decide(15, announcedBand: HomeViewModel.CriticalBatteryBand));
	}

	// A device on the charger is not draining, and a disconnected card's level is a stored
	// snapshot — neither may toast. The announced state is held, not cleared: the IsCharging /
	// Connected subscriptions re-run the decision when the situation changes.
	[Fact]
	public void ChargingOrDisconnected_StaysQuiet()
	{
		Assert.Equal((false, (int?)null), Decide(14, isCharging: true));
		Assert.Equal((false, (int?)null), Decide(14, connected: false));
	}

	// A restored card carries a stored snapshot (HasBattery false until a live reading lands),
	// and on the Bluetooth found path the transports flip BEFORE the fresh value is applied —
	// so a connected card without a live reading must not toast the snapshot (review R1 S1:
	// a mouse stored at 14 % but charged to 80 % while the app was closed toasted at connect).
	[Fact]
	public void StoredSnapshotWithoutALiveReading_StaysQuiet()
	{
		Assert.Equal((false, (int?)null), Decide(14, hasLiveReading: false));

		// The live reading lands (HasBattery flips true) — now it announces.
		Assert.Equal((false, HomeViewModel.LowBatteryBand), Decide(14));
	}

	// The trigger list is part of the contract: every input the rule gates on must re-run the
	// check when it flips. Dropping an entry fails no rule test — it silently kills that
	// trigger's acceptance criterion (unplug-at-14% needs IsCharging, reconnect-at-the-same-
	// percentage needs HasBattery) — so the composition is pinned here (PR #117 m1).
	[Fact]
	public void TriggerProperties_CoverEveryGateInput()
	{
		Assert.Equal(
			[
				nameof(DeviceCardViewModel.BatteryLevel),
				nameof(DeviceCardViewModel.IsCharging),
				nameof(DeviceCardViewModel.Connected),
				nameof(DeviceCardViewModel.HasBattery),
			],
			HomeViewModel.LowBatteryTriggerProperties);
	}
}
