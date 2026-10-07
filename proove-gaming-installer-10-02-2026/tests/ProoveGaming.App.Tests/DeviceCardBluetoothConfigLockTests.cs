using System.Collections.Generic;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// IsBluetoothConfigLocked drives the "BLUETOOTH MODE" banner and the details page's section
// locking: locked only while the LIVE transport is Bluetooth AND the catalog says that transport
// cannot carry configuration. Headsets (Cosmo, Starlight) are configurable over Bluetooth — their
// audio targets the OS endpoint — so they never lock; keyboards/mice keep SupportsConfiguration
// false on their Bluetooth rows and lock exactly as before (ticket 2026-09-02, superseding the
// uniform HasBluetooth lock of 2026-08-30).
// Plan: docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md
//
// The INPC cases matter as much as the truth table: the XAML bindings are only as live as the
// notifications, and both inputs are re-stamped on an already-bound card when the device switches
// transport (cable in/out, dongle <-> Bluetooth).
public class DeviceCardBluetoothConfigLockTests
{
	private static SingleCardViewModel Card() => new()
	{
		Name = "Starlight",
		DeviceTypeLabel = "Wireless Headset",
	};

	// The keyboard/mouse case: a Bluetooth link the catalog marks unconfigurable stays locked.
	[Fact]
	public void LockedOnBluetoothWhenTheTransportCannotCarryConfiguration()
	{
		var card = Card();
		card.SupportsConfiguration = false;
		card.HasBluetooth = true;

		Assert.True(card.IsBluetoothConfigLocked);
	}

	// The headset case: Bluetooth alone must not lock — configuration works over this link.
	[Fact]
	public void UnlockedOnBluetoothWhenTheTransportCarriesConfiguration()
	{
		var card = Card();
		card.HasBluetooth = true;

		Assert.True(card.SupportsConfiguration);
		Assert.False(card.IsBluetoothConfigLocked);
	}

	// The TAIPAN-on-dongle case: unconfigurable but NOT Bluetooth shows no banner and no lock —
	// unchanged behavior, the read-only dongle link is a separate product decision.
	[Fact]
	public void UnlockedOnADongleEvenWhenUnconfigurable()
	{
		var card = Card();
		card.SupportsConfiguration = false;
		card.HasDongle = true;

		Assert.False(card.IsBluetoothConfigLocked);
	}

	// Both inputs are re-stamped on transport switches, so each must re-raise the computed lock or
	// the banner and section IsEnabled bindings go stale on an already-open details page.
	[Fact]
	public void FlippingEitherInputRaisesIsBluetoothConfigLocked()
	{
		var card = Card();
		var raised = new List<string?>();
		card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		card.HasBluetooth = true;
		Assert.Contains(nameof(DeviceCardViewModel.IsBluetoothConfigLocked), raised);

		raised.Clear();
		card.SupportsConfiguration = false;
		Assert.Contains(nameof(DeviceCardViewModel.IsBluetoothConfigLocked), raised);
		Assert.True(card.IsBluetoothConfigLocked);
	}
}
