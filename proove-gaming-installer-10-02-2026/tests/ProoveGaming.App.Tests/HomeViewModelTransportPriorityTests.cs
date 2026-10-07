using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Which transport a card DISPLAYS versus which links are really up. The two diverge on purpose
// (PG-494): a live wired data interface is the strongest evidence of the device's physical switch
// position, while the OS Bluetooth stack is known to keep reporting a stale "connected" ghost after
// the device leaves Bluetooth — so the cable wins the icon. But the suppressed display flag must not
// erase the KNOWLEDGE that Bluetooth is up: HandleUsbHidDeviceFoundAsync's guard reads it to keep a
// Bluetooth-alive card from being declared disconnected the moment its cable is pulled.
//
// HomeViewModel's constructor needs the full device/session service graph, so — as with
// ShouldShowCharging — the rules are a static helper driven directly.
public class HomeViewModelTransportPriorityTests
{
	private static (bool Wire, bool Bluetooth, bool Dongle, bool BluetoothLinkUp) Resolve(
		bool linkUp = false, bool bluetoothHid = false, bool wired = false, bool dongle = false) =>
		HomeViewModel.ResolveTransports(linkUp, bluetoothHid, wired, dongle);

	// ── The cable wins the display ────────────────────────────────────────────────────────────

	// The PG-494 defect itself: the user switches a paired board to USB, the OS keeps reporting
	// the Bluetooth ghost. The card must flip to the cable icon.
	[Fact]
	public void CableWithABluetoothGhost_ShowsTheCableOnly()
	{
		var r = Resolve(linkUp: true, wired: true);

		Assert.True(r.Wire);
		Assert.False(r.Bluetooth);
		Assert.False(r.Dongle);
	}

	// The other half of the contract: suppressing the DISPLAY must not erase the knowledge that
	// the Bluetooth link is up. That knowledge is the disconnect guard — without it, pulling the
	// cable read as "device disconnected" for a board still working over Bluetooth.
	[Fact]
	public void CableWithABluetoothGhost_KeepsTheLinkKnowledge()
	{
		var r = Resolve(linkUp: true, wired: true);

		Assert.True(r.BluetoothLinkUp);
	}

	[Fact]
	public void CableWithBluetoothHidEvidence_AlsoKeepsTheLinkKnowledge()
	{
		var r = Resolve(bluetoothHid: true, wired: true);

		Assert.False(r.Bluetooth);
		Assert.True(r.BluetoothLinkUp);
	}

	// The M1 scenario end-to-end: cable in (display suppressed), cable pulled while the device
	// stays on Bluetooth. The link knowledge carried across the wired episode restores the
	// Bluetooth display on the next snapshot instead of leaving a dead card.
	[Fact]
	public void UnpluggingTheCable_RestoresBluetooth_FromTheKeptKnowledge()
	{
		var whileWired = Resolve(linkUp: true, wired: true);

		var afterUnplug = Resolve(linkUp: whileWired.BluetoothLinkUp);

		Assert.True(afterUnplug.Bluetooth);
		Assert.False(afterUnplug.Wire);
	}

	// ── Bluetooth outranks the dongle ─────────────────────────────────────────────────────────

	// A 2.4G receiver enumerates whenever it is plugged in, whether or not a device is talking
	// through it — its presence is NOT evidence of a live link, so a live Bluetooth wins.
	[Fact]
	public void BluetoothWithAnIdleReceiver_ShowsBluetooth()
	{
		var r = Resolve(linkUp: true, dongle: true);

		Assert.False(r.Wire);
		Assert.True(r.Bluetooth);
		Assert.False(r.Dongle);
	}

	[Fact]
	public void CableWithAReceiver_ShowsTheCableOnly()
	{
		var r = Resolve(wired: true, dongle: true);

		Assert.True(r.Wire);
		Assert.False(r.Bluetooth);
		Assert.False(r.Dongle);
	}

	// ── Single-transport states stay plain ────────────────────────────────────────────────────

	[Fact]
	public void BluetoothAlone_ShowsBluetooth()
	{
		var r = Resolve(bluetoothHid: true);

		Assert.False(r.Wire);
		Assert.True(r.Bluetooth);
		Assert.False(r.Dongle);
		Assert.True(r.BluetoothLinkUp);
	}

	[Fact]
	public void DongleAlone_ShowsTheDongle()
	{
		var r = Resolve(dongle: true);

		Assert.False(r.Wire);
		Assert.False(r.Bluetooth);
		Assert.True(r.Dongle);
		Assert.False(r.BluetoothLinkUp);
	}

	[Fact]
	public void NothingLive_ShowsNothing()
	{
		var r = Resolve();

		Assert.False(r.Wire);
		Assert.False(r.Bluetooth);
		Assert.False(r.Dongle);
		Assert.False(r.BluetoothLinkUp);
	}

	// ── MakeInactive is the only reset ────────────────────────────────────────────────────────

	// The link knowledge is sticky by design (the OS gives no reliable edge back), so the one
	// legitimate way it clears with the display flags is the card going inactive.
	[Fact]
	public void MakeInactive_ClearsTheLinkKnowledgeWithTheDisplayFlags()
	{
		var card = new SingleCardViewModel
		{
			Name = "Rampage",
			DeviceTypeLabel = "Wireless Keyboard",
			HasBluetooth = true,
			BluetoothLinkUp = true,
		};

		card.MakeInactive();

		Assert.False(card.HasBluetooth);
		Assert.False(card.BluetoothLinkUp);
	}
}
