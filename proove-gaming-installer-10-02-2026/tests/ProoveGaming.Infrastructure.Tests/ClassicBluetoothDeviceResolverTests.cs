using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Assembles Windows device nodes into the classic-Bluetooth devices they belong to.
//
// Every instance id below is a REAL one, captured from PG Cosmo connected over Bluetooth on
// 2026-08-17 (`dotnet run --project src/ProoveGaming.Device.Testing -- cosmo-bt discovery`). They are
// not invented shapes: the parsing here is the only thing standing between the device tree's format
// and the App, and a made-up id would test the parser against the author's imagination.
//
// Why this path exists at all: PG Cosmo is a classic Bluetooth audio device with NO BTHLE node
// anywhere on the machine, so the BLE discovery the App uses elsewhere cannot see it and the GATT
// battery service it reads does not exist. See docs/plans/add-cosmo-bluetooth/.
public class ClassicBluetoothDeviceResolverTests
{
	private const string Container = "9ca05de4-f4be-5821-a6d6-5ddd91e2c9c7";

	// The device node — carries the address and the plain name, but no VID/PID and no battery.
	private const string DevNode =
		@"BTHENUM\Dev_9200025FD240\9&8e04a68&0&BluetoothDevice_9200025FD240";

	// The Hands-Free Audio Gateway node — carries VID/PID and is the ONLY node publishing a battery.
	private const string HandsFreeAgNode =
		@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a\9&8e04a68&0&9200025FD240_C00000000";

	private const string AvrcpNode =
		@"BTHENUM\{0000110e-0000-1000-8000-00805f9b34fb}_VID&000105d6_PID&000a\9&8e04a68&0&9200025FD240_C00000000";

	// An audio endpoint in the same container. Not a Bluetooth node, and must not become a device.
	private const string AudioEndpointNode =
		@"SWD\MMDEVAPI\{0.0.0.00000000}.{8257ee23-de5c-4455-877a-c056e3bd9188}";

	private static List<DeviceNode> CosmoNodes() =>
	[
		new(DevNode, Container, "PG Cosmo", null, true),
		new(HandsFreeAgNode, Container, "PG Cosmo Hands-Free AG", 80, true),
		new(AvrcpNode, Container, "PG Cosmo Avrcp Transport", null, true),
		new(AudioEndpointNode, Container, "Headphones (2- PG Cosmo)", null, false),
	];

	[Fact]
	public void FromNodes_AssemblesOneDeviceFromItsContainer()
	{
		var devices = ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes());

		var device = Assert.Single(devices);
		Assert.Equal(Container, device.ContainerId);
		Assert.Equal("9200025FD240", device.Address);
	}

	// The Bluetooth identity is NOT the USB one: Cosmo is 0x05D6/0x000A here and 0x0D8C/0x0334 over
	// its dongle. The catalog matches the Bluetooth transport on these, so getting them wrong means
	// the headset is never recognised.
	[Fact]
	public void FromNodes_ReadsTheBluetoothVidPid_NotTheUsbOne()
	{
		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes()));

		Assert.Equal(0x05D6, device.VendorId);
		Assert.Equal(0x000A, device.ProductId);
		Assert.NotEqual(0x0D8C, device.VendorId);   // the USB identity
	}

	// The battery is published by exactly one node in the container. Reading it only from the device
	// node — the obvious place — finds nothing.
	[Fact]
	public void FromNodes_TakesTheBatteryFromWhicheverNodePublishesIt()
	{
		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes()));

		Assert.Equal(80, device.BatteryPercent);
	}

	// "PG Cosmo", not "PG Cosmo Hands-Free AG" or "PG Cosmo Avrcp Transport".
	[Fact]
	public void FromNodes_PrefersThePlainDeviceName()
	{
		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes()));

		Assert.Equal("PG Cosmo", device.Name);
	}

	// A container holding only non-Bluetooth nodes is not a Bluetooth device. Without this, every
	// audio endpoint and software device on the machine would become one.
	[Fact]
	public void FromNodes_IgnoresContainersWithNoBluetoothNode()
	{
		var nodes = new List<DeviceNode> { new(AudioEndpointNode, Container, "Headphones", null, false) };

		Assert.Empty(ClassicBluetoothDeviceResolver.FromNodes(nodes));
	}

	[Fact]
	public void FromNodes_IgnoresNodesWithNoContainer()
	{
		var nodes = new List<DeviceNode> { new(DevNode, null, "PG Cosmo", null, true) };

		Assert.Empty(ClassicBluetoothDeviceResolver.FromNodes(nodes));
	}

	// Two headsets on one machine must not merge into one card.
	[Fact]
	public void FromNodes_SeparatesTwoDevicesByContainer()
	{
		var other = "11111111-2222-3333-4444-555555555555";
		var nodes = CosmoNodes();
		nodes.Add(new(@"BTHENUM\Dev_AABBCCDDEEFF\9&x&0&BluetoothDevice_AABBCCDDEEFF", other, "Other", 42, true));

		var devices = ClassicBluetoothDeviceResolver.FromNodes(nodes);

		Assert.Equal(2, devices.Count);
		Assert.Contains(devices, d => d.Address == "9200025FD240" && d.BatteryPercent == 80);
		Assert.Contains(devices, d => d.Address == "AABBCCDDEEFF" && d.BatteryPercent == 42);
	}

	// THE bug this path shipped with: Windows keeps every BTHENUM node of a PAIRED device enumerated
	// while it is disconnected, so "it is in the list" meant the card showed active forever. Measured
	// 2026-08-17 with the headset switched to its dongle — the nodes stayed and DEVPKEY_Device_
	// IsConnected read False on all of them.
	[Fact]
	public void FromNodes_PairedButDisconnected_IsNotConnected()
	{
		var nodes = CosmoNodes().Select(n => n with { IsConnected = false }).ToList();

		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(nodes));

		Assert.False(device.IsConnected);
	}

	// The battery property KEEPS its last value after the headset goes away — measured at a stale
	// 80 %. Reporting it would put a live-looking level on a device that is not there.
	[Fact]
	public void FromNodes_PairedButDisconnected_ReportsNoBattery()
	{
		var nodes = CosmoNodes().Select(n => n with { IsConnected = false }).ToList();

		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(nodes));

		Assert.Null(device.BatteryPercent);
	}

	// One connected node is enough: the flag is not set on every member of the container.
	[Fact]
	public void FromNodes_IsConnectedWhenAnyNodeSaysSo()
	{
		var device = Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes()));

		Assert.True(device.IsConnected);
		Assert.Equal(80, device.BatteryPercent);
	}

	[Fact]
	public void FromNodes_WithNothingConnected_ReturnsEmpty()
	{
		Assert.Empty(ClassicBluetoothDeviceResolver.FromNodes([]));
	}

	// Windows publishes a generic "Standard Serial over Bluetooth link" node with an all-zero
	// address and no peer behind it. Seen on a real machine (2026-08-17) — it is not a device and
	// must not become a card.
	[Fact]
	public void FromNodes_IgnoresNodesWithAnAllZeroAddress()
	{
		var nodes = new List<DeviceNode>
		{
			new(@"BTHENUM\{00001101-0000-1000-8000-00805f9b34fb}_LOCALMFG&0000\7&a&0&000000000000_C00000000",
				"9cf5d03e-ea61-5376-a800-3381541c5131",
				"Standard Serial over Bluetooth link (COM3)",
				null,
				false),
		};

		Assert.Empty(ClassicBluetoothDeviceResolver.FromNodes(nodes));
	}

	// ── Catalog matching ──────────────────────────────────────────────────────────────────────

	private static ClassicBluetoothDevice Cosmo() =>
		Assert.Single(ClassicBluetoothDeviceResolver.FromNodes(CosmoNodes()));

	// Cosmo's Bluetooth ids in the decimal form appsettings.json uses: 0x05D6 = 1494, 0x000A = 10.
	private static DeviceMap CosmoBluetoothRow() => new()
	{
		Name = "Cosmo",
		VendorId = "1494",
		ProductId = "10",
		HasBluetooth = true,
	};

	private static DeviceMap CosmoDongleRow() => new()
	{
		Name = "Cosmo",
		VendorId = "3468",   // the USB identity
		ProductId = "820",
		HasBluetooth = false,
	};

	[Fact]
	public void MatchCatalog_FindsTheBluetoothRowByItsBluetoothIds()
	{
		var match = ClassicBluetoothDeviceResolver.MatchCatalog([CosmoDongleRow(), CosmoBluetoothRow()], Cosmo());

		Assert.NotNull(match);
		Assert.Equal("Cosmo", match!.Name);
		Assert.True(match.HasBluetooth);
	}

	// The dongle row carries the same product under different ids. Matching it would give the card
	// the wrong transport's capabilities.
	[Fact]
	public void MatchCatalog_DoesNotMatchTheDongleRow()
	{
		Assert.Null(ClassicBluetoothDeviceResolver.MatchCatalog([CosmoDongleRow()], Cosmo()));
	}

	[Fact]
	public void MatchCatalog_ReturnsNullForAnUnknownDevice()
	{
		var stranger = new ClassicBluetoothDevice("c", "AABBCCDDEEFF", 0x1234, 0x5678, "Someone else", null, true);

		Assert.Null(ClassicBluetoothDeviceResolver.MatchCatalog([CosmoBluetoothRow()], stranger));
	}

	// A node with no ids must not fall through onto the first Bluetooth row in the catalog.
	[Fact]
	public void MatchCatalog_ReturnsNullWhenTheDeviceHasNoIds()
	{
		var idless = new ClassicBluetoothDevice("c", "AABBCCDDEEFF", 0, 0, "No ids", null, true);

		Assert.Null(ClassicBluetoothDeviceResolver.MatchCatalog([CosmoBluetoothRow()], idless));
	}

	// ── Two headsets sharing one Bluetooth VID/PID (PG-608) ───────────────────────────────────
	//
	// Starlight and Cosmo both report 0x05D6/0x000A over Bluetooth — the id pair is an ODM constant,
	// not an identity, exactly like the byte-identical USB iSerialNumber they already share. Before
	// BluetoothName existed, MatchCatalog took the first VID/PID hit, so a connected Starlight
	// resolved to COSMO's row and the App drew a Cosmo card. These are the regression tests for that.

	private static ClassicBluetoothDevice Headset(string? name, string address, string container) =>
		new(container, address, 0x05D6, 0x000A, name, 70, true);

	private static DeviceMap TokenedRow(string name, string token) => new()
	{
		Name = name,
		VendorId = "1494",
		ProductId = "10",
		HasBluetooth = true,
		BluetoothName = token,
	};

	[Fact]
	public void MatchCatalog_TellsStarlightFromCosmoOnTheSameIds()
	{
		DeviceMap[] catalog = [TokenedRow("Cosmo", "cosmo"), TokenedRow("Starlight", "starlight")];

		Assert.Equal("Starlight", ClassicBluetoothDeviceResolver.MatchCatalog(
			catalog, Headset("PG Starlight", "19D5E987093A", "df5cd15f"))!.Name);
		Assert.Equal("Cosmo", ClassicBluetoothDeviceResolver.MatchCatalog(
			catalog, Headset("PG Cosmo", "9200025FD240", "9ca05de4"))!.Name);
	}

	// The whole point of the token is that the answer stops depending on catalog order.
	[Fact]
	public void MatchCatalog_IsNotDecidedByCatalogOrder()
	{
		DeviceMap[] reversed = [TokenedRow("Starlight", "starlight"), TokenedRow("Cosmo", "cosmo")];

		Assert.Equal("Cosmo", ClassicBluetoothDeviceResolver.MatchCatalog(
			reversed, Headset("PG Cosmo", "9200025FD240", "9ca05de4"))!.Name);
	}

	// Every other Bluetooth device in the catalog declares no token, and none of them may change.
	[Fact]
	public void MatchCatalog_StillMatchesARowWithNoToken()
	{
		Assert.Equal("Cosmo", ClassicBluetoothDeviceResolver.MatchCatalog(
			[CosmoBluetoothRow()], Cosmo())!.Name);
	}

	// A tokened row is the more specific statement, so it outranks a catch-all on the same ids
	// however the catalog happens to be ordered.
	[Fact]
	public void MatchCatalog_PrefersATokenedRowOverAnUntokenedOne()
	{
		var starlight = Headset("PG Starlight", "19D5E987093A", "df5cd15f");

		Assert.Equal("Starlight", ClassicBluetoothDeviceResolver.MatchCatalog(
			[CosmoBluetoothRow(), TokenedRow("Starlight", "starlight")], starlight)!.Name);
		Assert.Equal("Starlight", ClassicBluetoothDeviceResolver.MatchCatalog(
			[TokenedRow("Starlight", "starlight"), CosmoBluetoothRow()], starlight)!.Name);
	}

	// Matching no token is not a licence to pick a tokened row anyway — that is how a third headset
	// on these shared ids would silently inherit Starlight's capabilities.
	[Fact]
	public void MatchCatalog_ReturnsNullWhenNoTokenMatchesAndNoneIsUntokened()
	{
		Assert.Null(ClassicBluetoothDeviceResolver.MatchCatalog(
			[TokenedRow("Cosmo", "cosmo"), TokenedRow("Starlight", "starlight")],
			Headset("PG Nebula", "AABBCCDDEEFF", "other")));
	}

	// The name arrives in several shapes across a device's nodes ("PG Starlight",
	// "PG Starlight Hands-Free AG"), so the token is a case-insensitive substring, not an equality.
	[Theory]
	[InlineData("PG Starlight")]
	[InlineData("PG Starlight Hands-Free AG")]
	[InlineData("pg starlight avrcp transport")]
	public void MatchCatalog_MatchesTheTokenAsACaseInsensitiveSubstring(string name)
	{
		Assert.Equal("Starlight", ClassicBluetoothDeviceResolver.MatchCatalog(
			[TokenedRow("Starlight", "starlight")], Headset(name, "19D5E987093A", "c"))!.Name);
	}

	// A node with no name must not match a tokened row, and must not throw.
	[Fact]
	public void MatchCatalog_DoesNotMatchATokenedRowWhenTheDeviceHasNoName()
	{
		Assert.Null(ClassicBluetoothDeviceResolver.MatchCatalog(
			[TokenedRow("Starlight", "starlight")], Headset(null, "19D5E987093A", "c")));
	}

	// ── Parsing, pinned against the real formats ──────────────────────────────────────────────

	[Theory]
	[InlineData(DevNode)]
	[InlineData(HandsFreeAgNode)]
	[InlineData(AvrcpNode)]
	public void TryParseAddress_ReadsTheAddressFromEveryNodeShape(string instanceId)
	{
		Assert.Equal("9200025FD240", ClassicBluetoothDeviceResolver.TryParseAddress(instanceId));
	}

	[Fact]
	public void TryParseAddress_ReturnsNullWhenThereIsNoAddress()
	{
		Assert.Null(ClassicBluetoothDeviceResolver.TryParseAddress(AudioEndpointNode));
	}

	// The vendor field carries a source prefix of NON-fixed width before the four-hex id — Cosmo's
	// classic node reads VID&000105d6 (prefix 0001, Bluetooth SIG) while a BLE node reads
	// VID&02248a (prefix 02, USB-IF). Slicing at a fixed offset works for one and not the other.
	[Theory]
	[InlineData("VID&000105d6_PID&000a", 0x05D6, 0x000A)]      // Cosmo, classic, SIG-assigned
	[InlineData("VID&02248a_PID&8266", 0x248A, 0x8266)]        // TAIPAN, LE, USB-IF-assigned
	[InlineData("VID&023151_PID&402c", 0x3151, 0x402C)]        // the other LE device present
	public void TryParseVidPid_TakesTheIdRegardlessOfPrefixWidth(string fragment, int vid, int pid)
	{
		Assert.Equal((vid, pid), ClassicBluetoothDeviceResolver.TryParseVidPid($@"BTHENUM\{{x}}_{fragment}\9&a&0&b"));
	}

	[Fact]
	public void TryParseVidPid_ReturnsZeroesWhenAbsent()
	{
		Assert.Equal((0, 0), ClassicBluetoothDeviceResolver.TryParseVidPid(DevNode));
	}
}
