using System.Collections.Generic;
using System.Linq;
using ProoveGaming.App.Classes;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Which battery SOURCE a card is read from. Two sources exist for a Bluetooth mouse and they do not
// agree: the GATT Battery Service (the mouse's own percentage, and what Windows shows in the tray)
// and the TAIPAN protocol cache reached over HID. The protocol curve treats raw 985 as full while
// real hardware pushes up to 1008, so it clamps to 100 % — reading the wrong source showed a
// permanent 100 % against GATT's real 93 %.
//
// The trap is that both sources are live at once whenever the 2.4G receiver is still plugged in
// while the mouse is switched to Bluetooth: the receiver enumerates whether or not a mouse is
// talking through it, so "a dongle is present" is not evidence that the dongle is the live link.
// HomeViewModel's constructor needs the full device/session service graph, so — as with
// ApplyBluetoothIdentity — the rule is a static helper driven directly.
public class HomeViewModelBatterySourceTests
{
	private static SingleCardViewModel Card(bool bluetooth, bool supportsBattery = true) => new()
	{
		Name = "PG TAIPAN",
		DeviceTypeLabel = "Wireless Mouse",
		HasBluetooth = bluetooth,
		SupportsBattery = supportsBattery,
	};

	// ── Transport flags after Bluetooth stops being live ─────────────────────────────────────
	//
	// FillBluetoothCard asserts exclusivity: while Bluetooth is live it writes false into HasWire and
	// HasDongle, deliberately, because an idle 2.4G receiver enumerates whether or not anything is
	// talking through it. That assertion had no counterpart on the way back out, so when Bluetooth
	// dropped the card went dark with both flags stale-false — reproduced on Cosmo, which is the
	// first device where both transports are genuinely live at once: switching the headset from
	// Bluetooth to its dongle left the card inactive while audio kept playing.

	private static IEnumerable<(string? ClientName, string? ConnectionType)> Enumerated(
		params (string, string)[] devices) => devices.Select(d => ((string?)d.Item1, (string?)d.Item2));

	[Fact]
	public void DeriveHidTransports_FindsTheDongleStillPresentForThatCard()
	{
		var (hasWire, hasDongle) = HomeViewModel.DeriveHidTransports(
			Enumerated(("Cosmo", "Dongle2_4G")), "Cosmo");

		Assert.False(hasWire);
		Assert.True(hasDongle);
	}

	// The cable is the active data path when present, so a receiver alongside it must not claim the
	// card — the same precedence the HID path applies.
	[Fact]
	public void DeriveHidTransports_PrefersTheCableOverAReceiver()
	{
		var (hasWire, hasDongle) = HomeViewModel.DeriveHidTransports(
			Enumerated(("Cosmo", "Dongle2_4G"), ("Cosmo", "Wired")), "Cosmo");

		Assert.True(hasWire);
		Assert.False(hasDongle);
	}

	// Another device's transports must not resurrect this card.
	[Fact]
	public void DeriveHidTransports_IgnoresOtherDevices()
	{
		var (hasWire, hasDongle) = HomeViewModel.DeriveHidTransports(
			Enumerated(("PG TAIPAN", "Dongle2_4G"), ("BLAZE", "Wired")), "Cosmo");

		Assert.False(hasWire);
		Assert.False(hasDongle);
	}

	// Nothing enumerated over HID: the headset really is gone, and the card should stay dark.
	[Fact]
	public void DeriveHidTransports_WithNothingEnumerated_ReportsNoTransport()
	{
		var (hasWire, hasDongle) = HomeViewModel.DeriveHidTransports([], "Cosmo");

		Assert.False(hasWire);
		Assert.False(hasDongle);
	}

	[Fact]
	public void DeriveHidTransports_WithNoCardName_ReportsNoTransport()
	{
		var (hasWire, hasDongle) = HomeViewModel.DeriveHidTransports(
			Enumerated(("Cosmo", "Dongle2_4G")), null);

		Assert.False(hasWire);
		Assert.False(hasDongle);
	}

	// ── The charging indicator ────────────────────────────────────────────────────────────────
	//
	// A cable means charging, whatever the device declares about its battery. This was briefly gated
	// on SupportsBattery to keep the icon off BLAZE (a cable-only mouse with nothing to charge), but
	// that flag means "the level is readable on THIS transport" — TAIPAN on its cable declares it
	// false, so the gate also took the icon from a mouse that genuinely was charging. Reverted
	// deliberately on 2026-08-17; see ShouldShowCharging.

	[Fact]
	public void WiredDeviceWithNoDeclaredBattery_StillShowsAsCharging()
	{
		var blaze = Card(bluetooth: false, supportsBattery: false);

		Assert.True(HomeViewModel.ShouldShowCharging(blaze, hasWired: true, bluetooth: false));
	}

	[Fact]
	public void WiredDeviceWithABattery_ShowsAsCharging()
	{
		var wired = Card(bluetooth: false);

		Assert.True(HomeViewModel.ShouldShowCharging(wired, hasWired: true, bluetooth: false));
	}

	// The cable is what charges, and a card can light more than one transport at once — so a device
	// live on Bluetooth is not charging even while its receiver or cable is still plugged in.
	[Fact]
	public void BluetoothLiveDevice_IsNotCharging_EvenWithACablePresent()
	{
		var bt = Card(bluetooth: true);

		Assert.False(HomeViewModel.ShouldShowCharging(bt, hasWired: true, bluetooth: true));
	}

	[Fact]
	public void DeviceWithNoCable_IsNotCharging()
	{
		Assert.False(HomeViewModel.ShouldShowCharging(Card(bluetooth: false), hasWired: false, bluetooth: false));
	}

	// The regression itself: dongle plugged in, mouse on Bluetooth. Before the fix this polled HID
	// and overwrote the accurate GATT level.
	[Fact]
	public void BluetoothLiveCard_IsNotPolledOverHid_EvenWithAReceiverPresent()
	{
		Assert.False(HomeViewModel.ShouldPollHidBattery(Card(bluetooth: true), hasHidBatterySource: true));
	}

	// A transport whose catalog row declares no usable battery must not be polled either. TAIPAN on
	// its cable is the case: FillConnectionCard sets HasBattery=false for it, and before this check
	// the acquire ran anyway, got ~100 % from the DeviceHost and set HasBattery=true straight back
	// over it — so the indicator lit at 100 % and, because PollBatteriesAsync DOES filter on
	// SupportsBattery, nothing ever refreshed it away. Both filters have to agree.
	[Fact]
	public void CardThatDeclaresNoBattery_IsNotPolled_EvenOnAWiredOrDongleSource()
	{
		var wiredTaipan = Card(bluetooth: false, supportsBattery: false);

		Assert.False(HomeViewModel.ShouldPollHidBattery(wiredTaipan, hasHidBatterySource: true));
	}

	[Fact]
	public void NonBluetoothCard_IsPolledOverHid()
	{
		Assert.True(HomeViewModel.ShouldPollHidBattery(Card(bluetooth: false), hasHidBatterySource: true));
	}

	// No cable and no receiver: nothing to poll, Bluetooth or not. Guards against the fix being
	// "fixed" into always polling non-Bluetooth cards.
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void NoHidSource_IsNeverPolled(bool bluetooth)
	{
		Assert.False(HomeViewModel.ShouldPollHidBattery(Card(bluetooth), hasHidBatterySource: false));
	}

	// ── The DeviceHost's battery push ──────────────────────────────────────────────────────────
	//
	// TAIPAN pushes a battery report the moment it links up, and the DeviceHost sees it immediately.
	// Applying that straight to the card is what removes the up-to-60-second wait for the App's own
	// poll to notice a mouse that was just switched back on.

	[Fact]
	public void BatteryPush_FillsTheIndicator()
	{
		var card = Card(bluetooth: false);

		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 94, isCharging: false);

		Assert.Equal(94, card.BatteryLevel);
		Assert.True(card.HasBattery);
	}

	// known=false is the dongle reporting no mouse behind it — switched off, out of range or flat.
	[Fact]
	public void BatteryPush_NotKnown_ClearsTheIndicator()
	{
		var card = Card(bluetooth: false);
		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 94, isCharging: false);

		HomeViewModel.ApplyBatteryPush(card, known: false, percent: 0, isCharging: false);

		Assert.Null(card.BatteryLevel);
		Assert.False(card.HasBattery);
	}

	// A Bluetooth-live card takes its level from GATT; this push is the HID/dongle path, and must
	// not overwrite the more accurate value — the same rule ShouldPollHidBattery applies.
	[Fact]
	public void BatteryPush_IsIgnoredForABluetoothLiveCard()
	{
		var card = Card(bluetooth: true);
		card.BatteryLevel = 99;
		card.HasBattery = true;

		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 100, isCharging: false);

		Assert.Equal(99, card.BatteryLevel);
	}

	// A transport that declares no usable battery (TAIPAN on its cable) must not be filled in by a
	// push either, or the indicator would come back by the side door.
	[Fact]
	public void BatteryPush_IsIgnoredForACardThatDeclaresNoBattery()
	{
		var card = Card(bluetooth: false, supportsBattery: false);

		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 100, isCharging: false);

		Assert.Null(card.BatteryLevel);
		Assert.False(card.HasBattery);
	}

	// The one card state that must never occur: HasBattery asserted with no level. BatteryText falls
	// through to "-" and BatteryIconKind falls past every level comparison to Critical, so the card
	// shows a RED battery reading a dash — for a device whose level simply is not known. It used to
	// happen whenever a mouse was switched off behind its dongle, because FillConnectionCard set the
	// flag from the presence of a receiver rather than from having read anything.
	[Fact]
	public void ClearedBattery_ShowsNoIndicatorRatherThanARedDash()
	{
		var card = Card(bluetooth: false);
		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 94, isCharging: false);

		HomeViewModel.ApplyBatteryPush(card, known: false, percent: 0, isCharging: false);

		Assert.False(card.HasBatteryIndicator);
		Assert.Equal(string.Empty, card.BatteryText);
		Assert.Equal(BatteryIconKind.None, card.BatteryIconKind);
	}

	// The state itself, stated directly, so the rendering contract is documented where it is easy to
	// find: this is what the code above exists to avoid producing.
	[Fact]
	public void HasBatteryWithNoLevel_IsTheRedDashState()
	{
		var card = Card(bluetooth: false);
		card.HasBattery = true;
		card.BatteryLevel = null;

		Assert.Equal("-", card.BatteryText);
		Assert.Equal(BatteryIconKind.Critical, card.BatteryIconKind);
	}

	// ── Matching a pushed change to a card ─────────────────────────────────────────────────────
	//
	// This is the half a battery push spends outside ApplyBatteryPush, and the half that used to run
	// on the gRPC stream thread against a UI-owned collection. Extracted so the matching rules can
	// be tested without a dispatcher.

	private static SingleCardViewModel CardWith(int vid, int pid, string key) => new()
	{
		Name = "PG TAIPAN",
		DeviceTypeLabel = "Wireless Mouse",
		VendorId = vid,
		ProductId = pid,
		DeviceKey = key,
	};

	[Fact]
	public void MatchBatteryCard_PrefersTheDeviceKey_SoTwoIdenticalMiceDoNotCross()
	{
		var first = CardWith(9354, 33651, "container-A");
		var second = CardWith(9354, 33651, "container-B");

		var match = HomeViewModel.MatchBatteryCard([first, second], 9354, 33651, "container-B");

		Assert.Same(second, match);
	}

	// An empty key means the host could not read a Container ID — the single-device fallback.
	[Fact]
	public void MatchBatteryCard_EmptyKey_FallsBackToVidPid()
	{
		var card = CardWith(9354, 33651, "container-A");

		var match = HomeViewModel.MatchBatteryCard([card], 9354, 33651, deviceKey: "");

		Assert.Same(card, match);
	}

	// A key that names a device we do not have must match NOTHING. Falling back on VID/PID here
	// would land one mouse's reading on another mouse's card.
	[Fact]
	public void MatchBatteryCard_UnknownKey_DoesNotFallBack()
	{
		var card = CardWith(9354, 33651, "container-A");

		var match = HomeViewModel.MatchBatteryCard([card], 9354, 33651, "container-Z");

		Assert.Null(match);
	}

	[Fact]
	public void MatchBatteryCard_DifferentDevice_DoesNotMatch()
	{
		var card = CardWith(9354, 33651, "container-A");

		Assert.Null(HomeViewModel.MatchBatteryCard([card], 9354, 64257, "container-A"));
		Assert.Null(HomeViewModel.MatchBatteryCard([], 9354, 33651, "container-A"));
	}

	// ── Clearing on a failed poll ──────────────────────────────────────────────────────────────
	//
	// The App sees -1 for every unsuccessful battery answer, and several causes are temporary: the
	// device gate being held through a key-table write over 2.4G, an unanswered HID read, a parsed
	// value outside 1-100, a null gRPC client. So a single miss is not evidence that a battery is
	// gone, and clearing on one blanked the indicator of a healthy mouse while its key assignments
	// were being edited.

	[Fact]
	public void ASingleMissedPoll_DoesNotClearTheIndicator()
	{
		Assert.False(HomeViewModel.ShouldClearAfterMisses(1));
	}

	[Fact]
	public void ASecondConsecutiveMiss_ClearsTheIndicator()
	{
		Assert.True(HomeViewModel.ShouldClearAfterMisses(2));
		Assert.True(HomeViewModel.ShouldClearAfterMisses(5));
	}

	// IsCharging is not covered by HasBattery: the indicator is shown when `HasBattery ||
	// IsCharging`, and BatteryIconKind tests IsCharging FIRST. So a clear that leaves it set renders
	// a charging icon with an empty label for a device with no reading. The neighbouring
	// ClearedBattery_ShowsNoIndicator... test passes only because its setup never charges.
	[Fact]
	public void ClearingWhileChargingFlagIsSet_AlsoDropsTheChargingIcon()
	{
		var card = Card(bluetooth: false);
		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 94, isCharging: true);
		Assert.Equal(BatteryIconKind.Charging, card.BatteryIconKind);   // the state under test

		HomeViewModel.ApplyBatteryPush(card, known: false, percent: 0, isCharging: false);

		Assert.False(card.IsCharging);
		Assert.False(card.HasBatteryIndicator);
		Assert.Equal(BatteryIconKind.None, card.BatteryIconKind);
	}

	// On a cable the charging is real whether or not a level is readable — TAIPAN's wired transport
	// declares no battery at all — so the flag follows HasWire rather than being forced false.
	[Fact]
	public void ClearingOnACable_KeepsTheChargingIcon()
	{
		var card = Card(bluetooth: false);
		card.HasWire = true;
		HomeViewModel.ApplyBatteryPush(card, known: true, percent: 94, isCharging: true);

		HomeViewModel.ApplyBatteryPush(card, known: false, percent: 0, isCharging: false);

		Assert.True(card.IsCharging);
		Assert.Equal(BatteryIconKind.Charging, card.BatteryIconKind);
	}

	// ── A dongle-derived level must not survive a switch to Bluetooth ─────────────────────────
	//
	// The two sources are computed differently: over the dongle the level is DERIVED through
	// TaipanStateReport.PercentFromRaw, over Bluetooth it is the mouse's own GATT percentage, which
	// is what Windows shows in the tray. Compared properly they agree to within a point — the point
	// of this rule is not that the curve is wrong, but that a reading must not outlive the transport
	// it came from, unrefreshed, once the other transport takes over.
	//
	// ShouldPollHidBattery and ApplyBatteryPush both correctly stop touching a Bluetooth-live card —
	// which is exactly why the stale figure survived: nothing was left to refresh it away. The App
	// showed 100 % beside a tray showing 56 %.

	[Fact]
	public void SwitchingToBluetoothWithNoGattLevelYetDiscardsTheDongleReading()
	{
		Assert.True(HomeViewModel.ShouldInvalidateBatteryOnTransportSwitch(
			wasBluetoothLive: false, isBluetoothLiveNow: true, incomingLevel: -1));
	}

	// A steady Bluetooth card must not flicker every time a connection-state push arrives without a
	// reading — hence gating on the transition rather than on "no level".
	[Fact]
	public void AnAlreadyBluetoothCardIsNotClearedByALevellessPush()
	{
		Assert.False(HomeViewModel.ShouldInvalidateBatteryOnTransportSwitch(
			wasBluetoothLive: true, isBluetoothLiveNow: true, incomingLevel: -1));
	}

	// A real GATT reading arrives with the switch: nothing to discard, it is about to be overwritten.
	[Fact]
	public void ASwitchThatCarriesARealLevelDoesNotNeedInvalidating()
	{
		Assert.False(HomeViewModel.ShouldInvalidateBatteryOnTransportSwitch(
			wasBluetoothLive: false, isBluetoothLiveNow: true, incomingLevel: 51));
	}

	// Leaving Bluetooth is the dongle path's business; this rule must not touch it.
	[Fact]
	public void LeavingBluetoothIsNotThisRulesConcern()
	{
		Assert.False(HomeViewModel.ShouldInvalidateBatteryOnTransportSwitch(
			wasBluetoothLive: true, isBluetoothLiveNow: false, incomingLevel: -1));
	}

	// Zero is not a reading either — the dongle returns a well-formed zero when no mouse is behind it.
	[Fact]
	public void ZeroCountsAsNoReading()
	{
		Assert.True(HomeViewModel.ShouldInvalidateBatteryOnTransportSwitch(
			wasBluetoothLive: false, isBluetoothLiveNow: true, incomingLevel: 0));
	}
}
