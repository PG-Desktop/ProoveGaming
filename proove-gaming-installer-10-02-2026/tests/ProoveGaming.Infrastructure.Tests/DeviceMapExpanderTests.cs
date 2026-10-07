using System.Linq;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down DeviceMapExpander: one grouped device × N transports must expand to the same flat
// DeviceMap rows the old per-transport config produced, because the registry / catalog / App match
// on (VendorId, ProductId, HasBluetooth) and read the copied-down fields. HasBluetooth is derived
// from ConnectionType; MergeTransports is the device's explicit flag; every other field is
// transport-override ?? device-default.
public class DeviceMapExpanderTests
{
	// ── Golden characterization: mirrors the real catalog's tricky cases ───────

	// TAIPAN: one merged device, three transports; wired PID differs from dongle/BT which share a
	// PID and are told apart only by HasBluetooth. Rich settings live on the device once.
	private static DeviceGroupConfig Taipan() => new()
	{
		Name = "PG TAIPAN",
		DisplayName = "TAIPAN MAX",
		DeviceName = "PG TAIPAN",
		DeviceType = "Wireless Mouse",
		CardType = "single",
		ImagePath = "/Assets/Taipan.svg",
		VendorId = "9354",
		SupportsButtonRemap = true,
		SupportsMacros = true,
		MergeTransports = true,
		DpiStages = [800, 1200, 2400, 3600, 4800, 6000, 12000],
		SupportedMouseSettings = ["LiftOffDistance"],
		Hotspots = [new DeviceHotspotDefinition { Id = "left-click", Disabled = true }],
		Transports =
		[
			new DeviceTransportConfig { ConnectionType = "Wired",      ProductId = "64257" },
			new DeviceTransportConfig { ConnectionType = "Dongle2_4G", ProductId = "33651" },
			new DeviceTransportConfig { ConnectionType = "Bluetooth",  ProductId = "33651" },
		],
	};

	// Rampage: two transports, same VID/PID, NOT merged; DeviceType varies per transport and the
	// battery command exists on the wired transport only.
	private static DeviceGroupConfig Rampage() => new()
	{
		Name = "Rampage",
		DeviceName = "Rampage",
		CardType = "double",
		VendorId = "10473",
		ProductId = "12888",
		SupportsRgb = true,
		Transports =
		[
			new DeviceTransportConfig { ConnectionType = "Wired",     DeviceType = "Wired Keyboard",    BatteryCommand = 31 },
			new DeviceTransportConfig { ConnectionType = "Bluetooth", DeviceType = "Wireless Keyboard" },
		],
	};

	// T-Racer: three transports, merged; ProductId varies (dongle vs wired/BT), and ProductName /
	// SupportsRgb are wired-only overrides. Macro support is device-level — there is no
	// per-transport override for it, which is what the macro test below pins.
	private static DeviceGroupConfig TRacer() => new()
	{
		Name = "T-Racer",
		DeviceName = "T-Racer",
		CardType = "double",
		VendorId = "14000",
		SupportsMacros = true,
		Macros = new DeviceMacrosConfig { MaxDelayMs = 1000, MaxRepetitions = 255 },
		MergeTransports = true,
		Transports =
		[
			new DeviceTransportConfig { ConnectionType = "Dongle2_4G", ProductId = "12290", DeviceType = "Wireless Keyboard" },
			new DeviceTransportConfig { ConnectionType = "Wired",      ProductId = "12370", DeviceType = "Wired Keyboard", ProductName = "PG DragonFang", SupportsRgb = true },
			new DeviceTransportConfig { ConnectionType = "Bluetooth",  ProductId = "12370", DeviceType = "Wireless Keyboard" },
		],
	};

	private static DeviceMap Row(System.Collections.Generic.List<DeviceMap> rows, string productId, bool bt) =>
		rows.Single(r => r.ProductId == productId && r.HasBluetooth == bt);

	[Fact]
	public void Taipan_ExpandsToThreeRowsMatchingTheFlatConfig()
	{
		var rows = DeviceMapExpander.Expand([Taipan()]);

		Assert.Equal(3, rows.Count);
		Assert.All(rows, r => Assert.Equal("PG TAIPAN", r.Name));           // upper-cased already
		Assert.All(rows, r => Assert.Equal("TAIPAN MAX", r.DisplayName));   // device-level, copied down
		Assert.All(rows, r => Assert.Equal("9354", r.VendorId));           // device-level default
		Assert.All(rows, r => Assert.True(r.MergeTransports));             // explicit flag on every row

		var wired = Row(rows, "64257", bt: false);
		Assert.Equal("Wired", wired.ConnectionType);
		Assert.False(wired.HasBluetooth);

		var dongle = rows.Single(r => r.ConnectionType == "Dongle2_4G");
		Assert.Equal("33651", dongle.ProductId);
		Assert.False(dongle.HasBluetooth);

		var bluetooth = Row(rows, "33651", bt: true);
		Assert.Equal("Bluetooth", bluetooth.ConnectionType);
		Assert.True(bluetooth.HasBluetooth);   // dongle vs BT share PID 33651, split only by this
	}

	[Fact]
	public void Rampage_TwoTransportsWithoutMergeStayUnmerged_AndOverridesApply()
	{
		var rows = DeviceMapExpander.Expand([Rampage()]);

		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => Assert.False(r.MergeTransports));   // no flag → not merged (preserved)

		var wired = Row(rows, "12888", bt: false);
		Assert.Equal("Wired Keyboard", wired.DeviceType);        // per-transport override
		Assert.Equal((byte)31, wired.BatteryCommand);            // wired-only override

		var bt = Row(rows, "12888", bt: true);
		Assert.Equal("Wireless Keyboard", bt.DeviceType);        // per-transport override
		Assert.Null(bt.BatteryCommand);                          // omitted → device default (none)
		Assert.True(wired.SupportsRgb);                          // device-level default on both
		Assert.True(bt.SupportsRgb);
	}

	[Fact]
	public void TRacer_WiredOnlyOverridesApply_OthersFallBack()
	{
		var rows = DeviceMapExpander.Expand([TRacer()]);

		Assert.Equal(3, rows.Count);
		Assert.All(rows, r => Assert.True(r.MergeTransports));

		var wired = rows.Single(r => r.ConnectionType == "Wired");
		Assert.Equal("12370", wired.ProductId);
		Assert.Equal("PG DragonFang", wired.ProductName);        // wired-only
		Assert.True(wired.SupportsRgb);                          // wired-only

		var dongle = rows.Single(r => r.ConnectionType == "Dongle2_4G");
		Assert.Equal("12290", dongle.ProductId);
		Assert.Null(dongle.ProductName);                         // fell back (device has none)
		Assert.False(dongle.SupportsRgb);                        // fell back to default false
	}

	// Macro support is device-level and copied down verbatim — unlike SupportsRgb there is no
	// per-transport override for it. So enabling macros on T-Racer advertises them on Bluetooth
	// and the dongle too, where the macro factory will not resolve a service. That is a known
	// limitation rather than an accident, and this test is where it is written down: if a
	// per-transport override is ever added, this test is the one that should fail first.
	[Fact]
	public void TRacer_MacroSupport_ReachesEveryTransportIncludingBluetooth()
	{
		var rows = DeviceMapExpander.Expand([TRacer()]);

		Assert.Equal(3, rows.Count);
		Assert.All(rows, r =>
		{
			Assert.True(r.SupportsMacros);
			Assert.NotNull(r.Macros);
			Assert.Equal(1000, r.Macros!.MaxDelayMs);
			Assert.Equal(255, r.Macros.MaxRepetitions);
		});
	}

	// The flag must not leak on by default — the App's whole macro UI is gated on it, so a device
	// that never opted in must expand to rows that say so.
	[Fact]
	public void DeviceWithoutMacroSupport_ExpandsWithMacrosOff()
	{
		var rows = DeviceMapExpander.Expand([Rampage()]);

		Assert.NotEmpty(rows);
		Assert.All(rows, r =>
		{
			Assert.False(r.SupportsMacros);
			Assert.Null(r.Macros);
		});
	}

	// ── HasBluetooth derivation ────────────────────────────────────────────────

	[Theory]
	[InlineData("Bluetooth", true)]
	[InlineData("bluetooth", true)]   // case-insensitive
	[InlineData("Wired", false)]
	[InlineData("Dongle2_4G", false)]
	public void HasBluetooth_IsDerivedFromConnectionType(string connectionType, bool expected)
	{
		var rows = DeviceMapExpander.Expand([new DeviceGroupConfig
		{
			Name = "X", VendorId = "1", ProductId = "2",
			Transports = [new DeviceTransportConfig { ConnectionType = connectionType }],
		}]);

		Assert.Equal(expected, rows.Single().HasBluetooth);
	}

	// ── Shared list fields copied by value (no aliasing between rows) ───────────

	[Fact]
	public void ListFields_AreClonedPerRow_NotShared()
	{
		var rows = DeviceMapExpander.Expand([Taipan()]);

		Assert.All(rows, r => Assert.Equal(7, r.DpiStages!.Count));
		Assert.All(rows, r => Assert.Single(r.Hotspots!));

		// Mutating one row's list must not affect another.
		rows[0].DpiStages!.Add(99999);
		Assert.Equal(7, rows[1].DpiStages!.Count);
		Assert.NotSame(rows[0].Hotspots, rows[1].Hotspots);
	}

	// ── Edge cases ─────────────────────────────────────────────────────────────

	[Fact]
	public void DeviceWithNoTransports_YieldsNoRows()
	{
		var rows = DeviceMapExpander.Expand([new DeviceGroupConfig { Name = "Empty" }]);
		Assert.Empty(rows);
	}

	[Fact]
	public void NullInput_YieldsEmpty()
	{
		Assert.Empty(DeviceMapExpander.Expand(null));
	}

	// ── Device defaults ───────────────────────────────────────────────────────
	// Regression: DeviceGroupConfig is the class appsettings binds to, and it had no Defaults
	// property. The JSON block was silently dropped before the expander ever ran, so every device
	// reached the App with no defaults at all — appsettings.json and DeviceMap both looked correct,
	// which is what made it hard to see. The symptom was BLAZE restoring to 800 DPI (the ladder's
	// first stage, the no-default fallback) instead of its configured 1200.

	[Fact]
	public void Expand_CopiesDeviceDefaultsToEveryTransportRow()
	{
		var device = Taipan();
		device.Defaults = new DeviceDefaultsConfig
		{
			Dpi = 1200,
			ReportRateHz = 1000,
			ButtonDebounceMs = 8,
			LiftOffDistanceMm = 2,
			RgbEnabled = true,
			RgbEffect = "Breathing",
			RgbColorHex = "#7620EE",
			RgbBrightness = 60,
			RgbSpeed = 50,
		};

		var rows = DeviceMapExpander.Expand([device]);

		Assert.Equal(3, rows.Count);
		Assert.All(rows, r =>
		{
			Assert.NotNull(r.Defaults);
			Assert.Equal(1200, r.Defaults!.Dpi);
			Assert.Equal(1000, r.Defaults.ReportRateHz);
			Assert.Equal(8, r.Defaults.ButtonDebounceMs);
			Assert.Equal(2, r.Defaults.LiftOffDistanceMm);
			Assert.True(r.Defaults.RgbEnabled);
			Assert.Equal("Breathing", r.Defaults.RgbEffect);
			Assert.Equal("#7620EE", r.Defaults.RgbColorHex);
			Assert.Equal(60, r.Defaults.RgbBrightness);
			Assert.Equal(50, r.Defaults.RgbSpeed);
		});
	}

	[Fact]
	public void Expand_LeavesDefaultsNull_WhenTheDeviceDeclaresNone()
	{
		var rows = DeviceMapExpander.Expand([Rampage()]);

		Assert.All(rows, r => Assert.Null(r.Defaults));
	}

	// Each row gets its own instance, so mutating one transport's defaults cannot reach the others.
	[Fact]
	public void Expand_DoesNotAliasDefaultsBetweenTransportRows()
	{
		var device = Taipan();
		device.Defaults = new DeviceDefaultsConfig { Dpi = 1200 };

		var rows = DeviceMapExpander.Expand([device]);
		rows[0].Defaults!.Dpi = 4800;

		Assert.Equal(1200, rows[1].Defaults!.Dpi);
		Assert.Equal(1200, rows[2].Defaults!.Dpi);
	}

	// SupportsConfiguration is a per-TRANSPORT capability: TAIPAN can be configured over its cable
	// but not over the 2.4G receiver or Bluetooth, so it cannot be a device-level flag.
	[Fact]
	public void Expand_AppliesSupportsConfigurationPerTransport()
	{
		var device = Taipan();
		device.Transports[1].SupportsConfiguration = false; // Dongle2_4G
		device.Transports[2].SupportsConfiguration = false; // Bluetooth

		var rows = DeviceMapExpander.Expand([device]);

		Assert.True(rows.Single(r => r.ConnectionType == "Wired").SupportsConfiguration);
		Assert.False(rows.Single(r => r.ConnectionType == "Dongle2_4G").SupportsConfiguration);
		Assert.False(rows.Single(r => r.ConnectionType == "Bluetooth").SupportsConfiguration);
	}

	// Anything that says nothing keeps working exactly as before.
	[Fact]
	public void Expand_DefaultsSupportsConfigurationToTrue()
	{
		var rows = DeviceMapExpander.Expand([Rampage()]);
		Assert.All(rows, r => Assert.True(r.SupportsConfiguration));
	}

	// SupportsBattery is per-TRANSPORT for the same reason: a battery reading is a property of the
	// link, not only of the device. Over its cable TAIPAN reports charge VOLTAGE, which rises while
	// charging (raw 1002 on the cable against 979-982 at rest minutes earlier), so it pins at 100 %
	// and is worse than showing nothing — while the same mouse reports a usable level on 2.4G.
	[Fact]
	public void Expand_AppliesSupportsBatteryPerTransport()
	{
		var device = Taipan();
		device.Transports[0].SupportsBattery = false; // Wired — charge voltage, not charge level

		var rows = DeviceMapExpander.Expand([device]);

		Assert.False(rows.Single(r => r.ConnectionType == "Wired").SupportsBattery);
		Assert.True(rows.Single(r => r.ConnectionType == "Dongle2_4G").SupportsBattery);
		Assert.True(rows.Single(r => r.ConnectionType == "Bluetooth").SupportsBattery);
	}

	// A transport that says nothing inherits the device-level value, in both directions.
	[Fact]
	public void Expand_TransportInheritsDeviceLevelSupportsBattery()
	{
		var device = Taipan();
		device.SupportsBattery = false;

		var rows = DeviceMapExpander.Expand([device]);

		Assert.All(rows, r => Assert.False(r.SupportsBattery));
	}

	// ── Headset capabilities (Cosmo) ──────────────────────────────────────────

	// Cosmo: a single-transport dongle headset. Audio settings and the volume target are
	// device-level, and it declares no readable battery.
	private static DeviceGroupConfig Cosmo() => new()
	{
		Name = "Cosmo",
		DeviceName = "Cosmo",
		DeviceType = "Wireless Headset",
		CardType = "single",
		VendorId = "3468",
		ProductId = "820",
		SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"],
		AudioVolumeTarget = "OperatingSystem",
		SupportsBattery = false,
		Transports = [new DeviceTransportConfig { ConnectionType = "Dongle2_4G" }],
	};

	[Fact]
	public void Expand_CopiesAudioSettingsToEveryTransport()
	{
		var device = Cosmo();
		device.Transports.Add(new DeviceTransportConfig { ConnectionType = "Bluetooth" });

		var rows = DeviceMapExpander.Expand([device]);

		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => Assert.Equal(["OutputVolume", "MicrophoneVolume"], r.SupportedAudioSettings));
		Assert.All(rows, r => Assert.Equal("OperatingSystem", r.AudioVolumeTarget));
	}

	// Same rule as every other list on the row: mutating one transport's copy must not reach the
	// others, or two rows of one product silently share state.
	[Fact]
	public void Expand_ClonesAudioSettingsPerTransport()
	{
		var device = Cosmo();
		device.Transports.Add(new DeviceTransportConfig { ConnectionType = "Bluetooth" });

		var rows = DeviceMapExpander.Expand([device]);
		rows[0].SupportedAudioSettings!.Add("Sidetone");

		Assert.Equal(2, rows[1].SupportedAudioSettings!.Count);
		Assert.DoesNotContain("Sidetone", rows[1].SupportedAudioSettings!);
	}

	// SupportsBattery is device-level: a product either has a readable battery or it does not, and
	// Cosmo's dongle transport would otherwise imply one it cannot report.
	[Fact]
	public void Expand_CopiesSupportsBatteryToEveryTransport()
	{
		var device = Cosmo();
		device.Transports.Add(new DeviceTransportConfig { ConnectionType = "Bluetooth" });

		var rows = DeviceMapExpander.Expand([device]);

		Assert.All(rows, r => Assert.False(r.SupportsBattery));
	}

	// Every device that says nothing about audio or battery keeps behaving exactly as before —
	// this is what protects the eight existing devices from the new fields.
	[Fact]
	public void Expand_DefaultsAudioAndBatteryForDevicesThatDeclareNeither()
	{
		var rows = DeviceMapExpander.Expand([Taipan(), Rampage()]);

		Assert.All(rows, r => Assert.Null(r.SupportedAudioSettings));
		Assert.All(rows, r => Assert.Null(r.AudioVolumeTarget));
		Assert.All(rows, r => Assert.True(r.SupportsBattery));
	}

	// A multi-transport product must expose its BLUETOOTH row to a Bluetooth lookup. The name-keyed
	// index deliberately prefers the non-Bluetooth entry, so resolving a Bluetooth discovery through
	// it returned the WIRED row, whose per-transport fields describe the wrong transport — that is
	// how SupportsConfiguration = true was stamped onto a TAIPAN card connected by its dongle.
	[Fact]
	public void Expand_KeepsABluetoothRowDistinguishableFromTheWiredOne()
	{
		var device = Taipan();
		device.Transports[1].SupportsConfiguration = false; // Dongle2_4G
		device.Transports[2].SupportsConfiguration = false; // Bluetooth

		var rows = DeviceMapExpander.Expand([device]);

		var bt = rows.Single(r => r.HasBluetooth);
		var wired = rows.Single(r => r.ConnectionType == "Wired");

		Assert.False(bt.SupportsConfiguration);
		Assert.True(wired.SupportsConfiguration);
		// Same product name on both, which is exactly why a name-only lookup cannot tell them apart.
		Assert.Equal(wired.Name, bt.Name);
	}
}
