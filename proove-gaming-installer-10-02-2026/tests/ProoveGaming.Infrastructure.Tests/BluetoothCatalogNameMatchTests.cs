using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Asserts the SHIPPED catalog against the Bluetooth name matcher, rather than a hand-picked sample
// of it — the same reason StarlightCatalogTests reads the real appsettings.json.
//
// These exist because the 2026-09-02 audit filed the case-sensitive name match as a shipped bug
// (F4: "no Bluetooth keyboard is ever tracked"). Writing this file disproved that — see the
// correction in the audit. `DeviceMapExpander` sets `Name = device.Name?.ToUpper()`, so every row
// reaching the matcher is already upper-cased and the case-sensitive comparison worked. The real
// finding is the one this file now pins: correctness depended on an invariant established three
// layers away, stated nowhere at the call site, whose failure mode is silent — a device that is
// never matched produces no error, no log line and no exception. It simply never appears.
//
// So the invariant is asserted directly, AND the matcher is asserted to no longer need it.
//
// Written against the whole catalog on purpose: a per-device assertion would have to be remembered
// when the next device is added, which is exactly the kind of thing that does not get remembered.
public class BluetoothCatalogNameMatchTests
{
	// Every Bluetooth-capable row the DeviceHost actually ships, through the same expander the
	// DeviceHost uses (HasBluetooth is derived per transport, so this cannot be read off the
	// catalog directly).
	private static List<DeviceMap> BluetoothRows() =>
		[.. DeviceMapExpander.Expand(DeviceHostCatalog.Load())
			.Where(m => m.HasBluetooth && !string.IsNullOrEmpty(m.Name))];

	[Fact]
	public void TheCatalogShipsBluetoothRowsToMatchAgainst()
	{
		// Guards the guard: if the expander or the catalog shape ever changes so this comes back
		// empty, every assertion below would vacuously pass.
		Assert.NotEmpty(BluetoothRows());
	}

	// The invariant the matcher used to depend on, asserted where it is actually established. If
	// someone "tidies" the `?.ToUpper()` out of DeviceMapExpander, this is the test that says so —
	// and with the matcher now case-insensitive, this failing is a warning rather than an outage.
	[Fact]
	public void TheExpanderUpperCasesEveryCatalogName()
	{
		foreach (var row in BluetoothRows())
			Assert.Equal(row.Name!.ToUpperInvariant(), row.Name);
	}

	// The device tells us its own name over GATT, and nothing makes its casing agree with the
	// catalog's — a Taipan reports "PG Taipan" against a catalog row of "PG TAIPAN". Both cases are
	// pinned so the matcher stays independent of the invariant above.
	public static TheoryData<string, string> BluetoothRowNames()
	{
		var data = new TheoryData<string, string>();
		foreach (var row in BluetoothRows())
		{
			data.Add(row.Name!, row.Name!.ToUpperInvariant());
			data.Add(row.Name!, row.Name!.ToLowerInvariant());
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(BluetoothRowNames))]
	public void EveryBluetoothCatalogRowIsMatchableWhateverTheDeviceReportsItsCaseAs(
		string catalogName, string deviceName)
	{
		Assert.Equal(catalogName,
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(BluetoothRows(), deviceName));
	}

	// Real devices prefix their catalog name ("PG Rampage", "PG Taipan"), which is why the match is
	// a substring test rather than an equality one. Pinned so a future "tighten it to Equals" edit
	// fails here instead of on a user's desk.
	[Theory]
	[MemberData(nameof(BluetoothRowNames))]
	public void EveryBluetoothCatalogRowIsMatchableWithTheUsualVendorPrefix(
		string catalogName, string deviceName)
	{
		Assert.Equal(catalogName,
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(BluetoothRows(), $"PG {deviceName}"));
	}

	// The matcher must stay selective against the real catalog too: a foreign peripheral picking up
	// one of our names would put a stranger's device on the home page under a PG product name.
	[Theory]
	[InlineData("JBL Xtreme 2")]
	[InlineData("Galaxy Buds")]
	[InlineData("MX Master 3S")]
	[InlineData("")]
	public void AForeignDeviceIsNotNamedAfterAnythingInTheCatalog(string deviceName)
	{
		Assert.Equal(string.Empty,
			BluetoothDeviceDiscoveryService.MatchCatalogNameByDeviceName(BluetoothRows(), deviceName));
	}

	// No catalog name may be a substring of another, or the matcher's FirstOrDefault would hand one
	// device's card to the other and which one wins would depend on catalog order. Currently true
	// and worth keeping true — this is the constraint the substring match quietly relies on.
	[Fact]
	public void NoBluetoothCatalogNameIsASubstringOfAnother()
	{
		var names = BluetoothRows().Select(m => m.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		foreach (var name in names)
		{
			foreach (var other in names)
			{
				if (string.Equals(name, other, StringComparison.OrdinalIgnoreCase)) continue;

				Assert.False(other.Contains(name, StringComparison.OrdinalIgnoreCase),
					$"catalog name '{name}' is a substring of '{other}' — the substring match cannot tell them apart");
			}
		}
	}
}
