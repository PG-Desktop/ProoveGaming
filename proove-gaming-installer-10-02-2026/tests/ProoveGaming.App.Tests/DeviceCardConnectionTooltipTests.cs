using System;
using System.Collections.Generic;
using System.ComponentModel;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Hover text for the home card's connection indicator: how the device is connected, plus its
// battery level when it reports one.
//
// LocalizationManager.GetString falls back to the resource KEY when no resource dictionary is
// loaded, which is the case in a unit test. That is what these assertions match on — it pins which
// string was selected without depending on the English wording, so rewording the resource does not
// break the tests.
public class DeviceCardConnectionTooltipTests
{
	private static SingleCardViewModel Card() => new()
	{
		Name = "Test Device",
		DeviceTypeLabel = "Wired Mouse",
	};

	[Fact]
	public void NamesTheTransport_Bluetooth()
	{
		var card = Card();
		card.HasBluetooth = true;

		Assert.Equal("Card_Connection_Bluetooth", card.ConnectionTooltip);
	}

	[Fact]
	public void NamesTheTransport_Dongle()
	{
		var card = Card();
		card.HasDongle = true;

		Assert.Equal("Card_Connection_Dongle", card.ConnectionTooltip);
	}

	[Fact]
	public void NamesTheTransport_Cable()
	{
		var card = Card();
		card.HasWire = true;

		Assert.Equal("Card_Connection_Cable", card.ConnectionTooltip);
	}

	[Fact]
	public void DisconnectedCard_SaysNotConnected()
	{
		Assert.Equal("Card_Connection_None", Card().ConnectionTooltip);
	}

	// A TAIPAN lights the cable and dongle icons together. The tooltip describes the CONNECTION, not
	// the icon row, so the cable — the active data path whenever present — wins.
	[Fact]
	public void WithSeveralTransportsLit_TheCableWins()
	{
		var card = Card();
		card.HasWire = true;
		card.HasDongle = true;
		card.HasBluetooth = true;

		Assert.Equal("Card_Connection_Cable", card.ConnectionTooltip);
	}

	[Fact]
	public void AddsTheBatteryLineWhenALevelIsReported()
	{
		var card = Card();
		card.HasBluetooth = true;
		card.HasBattery = true;
		card.BatteryLevel = 87;

		var lines = card.ConnectionTooltip.Split(Environment.NewLine);

		Assert.Equal(2, lines.Length);
		Assert.Equal("Card_Connection_Bluetooth", lines[0]);
		Assert.Equal("Card_Connection_BatteryLevel", lines[1]);
	}

	// A device that supports a battery but has not reported a level yet must not show a battery line
	// with a hole where the number goes.
	[Fact]
	public void OmitsTheBatteryLineWhenNoLevelIsKnown()
	{
		var card = Card();
		card.HasBluetooth = true;
		card.HasBattery = true;
		card.BatteryLevel = null;

		Assert.Equal("Card_Connection_Bluetooth", card.ConnectionTooltip);
	}

	[Fact]
	public void OmitsTheBatteryLineOnADeviceWithNoBattery()
	{
		var card = Card();
		card.HasWire = true;
		card.BatteryLevel = 50; // stale value with HasBattery false

		Assert.Equal("Card_Connection_Cable", card.ConnectionTooltip);
	}

	// The tooltip is a computed property, so every input that changes it has to raise it — otherwise
	// the card shows the transport it had when the binding was first evaluated. A device that
	// connects, or reports its battery, after the card is built is the normal case, not the edge one.
	[Theory]
	[InlineData(nameof(DeviceCardViewModel.HasWire))]
	[InlineData(nameof(DeviceCardViewModel.HasDongle))]
	[InlineData(nameof(DeviceCardViewModel.HasBluetooth))]
	[InlineData(nameof(DeviceCardViewModel.HasBattery))]
	[InlineData(nameof(DeviceCardViewModel.BatteryLevel))]
	public void RaisesConnectionTooltip_WhenAnyInputChanges(string property)
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		switch (property)
		{
			case nameof(DeviceCardViewModel.HasWire): card.HasWire = true; break;
			case nameof(DeviceCardViewModel.HasDongle): card.HasDongle = true; break;
			case nameof(DeviceCardViewModel.HasBluetooth): card.HasBluetooth = true; break;
			case nameof(DeviceCardViewModel.HasBattery): card.HasBattery = true; break;
			case nameof(DeviceCardViewModel.BatteryLevel): card.BatteryLevel = 42; break;
		}

		Assert.Contains(nameof(DeviceCardViewModel.ConnectionTooltip), raised);
	}
}
