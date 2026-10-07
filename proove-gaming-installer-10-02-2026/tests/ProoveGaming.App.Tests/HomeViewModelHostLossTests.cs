using System.Collections.Generic;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The card half of "the DeviceHost went away".
//
// Device presence reaches the App only as a push over the SubscribeOnDevices stream. When the
// service stops, the last push before it died stands unchallenged: nothing in the App re-derives
// presence on a timer, so a device that was connected kept showing as connected for the rest of the
// session. UsbHidDeviceService now reports the host going away (see UsbHidDeviceServiceHostAbsentTests)
// and HomeViewModel answers by deactivating the cards, which is what this pins.
//
// Deactivating rather than removing: these are saved devices, and this is the same state a card
// reaches when its device is unplugged. Recovery needs nothing extra -- the host replies to a fresh
// subscribe with the current device lists, so the ordinary DeviceFound path lights them back up.
public class HomeViewModelHostLossTests
{
	private static DoubleCardViewModel Card(string name) =>
		new() { Name = name, DeviceTypeLabel = "Wired Keyboard" };

	private static DoubleCardViewModel WiredCard(string name)
	{
		var card = Card(name);
		card.HasWire = true;
		return card;
	}

	[Fact]
	public void HostLoss_DeactivatesAConnectedCard()
	{
		var card = WiredCard("T800");
		Assert.True(card.Connected);

		HomeViewModel.DeactivateCards([card]);

		Assert.False(card.Connected);
	}

	// Every transport, not just the one the card happened to be showing. A headset on Bluetooth and
	// a mouse behind its receiver are just as stale as a cabled board once the host stops answering.
	[Fact]
	public void HostLoss_ClearsEveryTransport()
	{
		var card = Card("Cosmo");
		card.HasWire = true;
		card.HasDongle = true;
		card.HasBluetooth = true;
		card.BluetoothLinkUp = true;

		HomeViewModel.DeactivateCards([card]);

		Assert.False(card.HasWire);
		Assert.False(card.HasDongle);
		Assert.False(card.HasBluetooth);
		Assert.False(card.BluetoothLinkUp);
	}

	// The battery indicator is fed by the same dead stream, so a level left on screen would be just
	// as much a stale claim as the connection itself.
	[Fact]
	public void HostLoss_ClearsTheBatteryIndicator()
	{
		var card = WiredCard("TAIPAN");
		card.BatteryLevel = 62;
		card.HasBattery = true;
		card.IsCharging = true;

		HomeViewModel.DeactivateCards([card]);

		Assert.Null(card.BatteryLevel);
		Assert.False(card.HasBattery);
		Assert.False(card.IsCharging);
	}

	[Fact]
	public void HostLoss_DeactivatesEveryConnectedCard()
	{
		var cards = new List<DeviceCardViewModel> { WiredCard("T800"), WiredCard("BLAZE"), WiredCard("Raster") };

		HomeViewModel.DeactivateCards(cards);

		Assert.All(cards, c => Assert.False(c.Connected));
	}

	// An already-inactive card must not be touched at all: MakeInactive would drive a burst of
	// PropertyChanged out to every bound view for no change in state, and the App can hold a long
	// list of saved devices that are not plugged in.
	[Fact]
	public void HostLoss_LeavesAnAlreadyDisconnectedCardAlone()
	{
		var card = Card("Rampage");
		var changes = 0;
		card.PropertyChanged += (_, _) => changes++;

		HomeViewModel.DeactivateCards([card]);

		Assert.False(card.Connected);
		Assert.Equal(0, changes);
	}

	[Fact]
	public void HostLoss_WithNoCardsIsHarmless()
	{
		HomeViewModel.DeactivateCards([]);
	}
}
