using System.Globalization;
using System.Text.RegularExpressions;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>One Windows device node, as read from the device tree.</summary>
/// <remarks>
/// Plain data so the grouping below is testable without a real device. The P/Invoke that fills it
/// lives in <c>DeviceNodeQuery</c>.
/// <para>
/// <c>LastConnectedUtc</c> is filled for low-energy nodes only (the stale-pairing veto is a BLE
/// concern) and carries Windows' <c>DEVPKEY_Bluetooth_LastConnectedTime</c>, stamped on every
/// connect AND disconnect (measured 2026-09-02,
/// <c>docs/plans/add-stale-pairing-detection/notes.md</c>). "Utc" describes the FILETIME
/// interpretation, not a guarantee: Windows stores the value as local time, so it can read hours
/// off — callers compare against day-scale thresholds only.
/// </para>
/// </remarks>
public sealed record DeviceNode(
	string InstanceId,
	string? ContainerId,
	string? FriendlyName,
	int? BatteryPercent,
	bool IsConnected,
	DateTime? LastConnectedUtc = null);

/// <summary>One physical device reachable over classic Bluetooth, assembled from its device nodes.</summary>
public sealed record ClassicBluetoothDevice(
	string ContainerId,
	string Address,
	int VendorId,
	int ProductId,
	string? Name,
	int? BatteryPercent,
	bool IsConnected);

/// <summary>
/// Turns a flat list of Windows device nodes into the classic-Bluetooth devices they belong to.
/// </summary>
/// <remarks>
/// This exists because a classic Bluetooth audio device is invisible to the BLE discovery the App
/// uses elsewhere. PG Cosmo has **no** <c>BTHLE</c> node at all — proven by sweeping every LE node on
/// the machine (2026-08-17) — so <c>Bluetooth.GetPairedDevicesAsync</c> never returns it and the GATT
/// battery service it would be read through does not exist. See
/// <c>docs/plans/add-cosmo-bluetooth/add-cosmo-bluetooth-plan.md</c>.
///
/// Windows groups every node of one physical device under a shared **Container ID**, which is what
/// makes this work: the headset's Bluetooth nodes, its Hands-Free node and its audio endpoints all
/// share one container. That container is also the identity <c>IAudioEndpointService</c> already
/// addresses endpoints by, so presence, battery and audio all resolve from a single value.
///
/// Pure and allocation-modest: runs during discovery, never on a device-poll hot path.
/// </remarks>
public static class ClassicBluetoothDeviceResolver
{
	/// <summary>Instance-id prefix of a classic Bluetooth function node.</summary>
	private const string ClassicPrefix = "BTHENUM";

	/// <summary>The address Windows publishes for a node with no peer behind it.</summary>
	private const string EmptyAddress = "000000000000";

	// "VID&000105d6_PID&000a" — the vendor field carries a source prefix before the 4-hex id (0001 =
	// Bluetooth SIG, 02 = USB-IF), and the prefix is NOT a fixed width, so the id is taken as the
	// last four hex digits rather than by slicing at a position.
	private static readonly Regex VidPidPattern = new(
		@"VID&(?<vid>[0-9A-Fa-f]+)_PID&(?<pid>[0-9A-Fa-f]+)",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// "\Dev_9200025FD240" — the plainest form of the address, on the device node.
	private static readonly Regex DevAddressPattern = new(
		@"\\Dev_(?<addr>[0-9A-Fa-f]{12})",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// Groups nodes into devices. A container yields a device only when at least one of its nodes is
	/// a classic Bluetooth node carrying an address, so audio endpoints and other members of the same
	/// container contribute their battery and name without inventing devices of their own.
	/// </summary>
	public static IReadOnlyList<ClassicBluetoothDevice> FromNodes(IEnumerable<DeviceNode> nodes)
	{
		ArgumentNullException.ThrowIfNull(nodes);

		var devices = new List<ClassicBluetoothDevice>();

		foreach (var group in nodes
			.Where(n => !string.IsNullOrEmpty(n.ContainerId))
			.GroupBy(n => n.ContainerId!, StringComparer.OrdinalIgnoreCase))
		{
			var classic = group
				.Where(n => n.InstanceId.StartsWith(ClassicPrefix, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (classic.Count == 0) continue;

			// An all-zero address is not a device. Windows publishes one for the generic
			// "Standard Serial over Bluetooth link" nodes, which have no peer behind them — seen on
			// the machine this was written against (2026-08-17).
			var address = classic
				.Select(n => TryParseAddress(n.InstanceId))
				.FirstOrDefault(a => a is not null and not EmptyAddress);
			if (address is null) continue;

			// VID/PID live on the service nodes, not on the Dev_ node, so they are taken from
			// whichever node carries them.
			var identity = classic
				.Select(n => TryParseVidPid(n.InstanceId))
				.FirstOrDefault(v => v.VendorId != 0);

			// Connected means a node SAYS so, never "the node exists". Windows keeps every BTHENUM
			// node of a PAIRED device enumerated while it is disconnected, so presence proves only
			// that the two have been introduced — which is why the first cut of this showed the card
			// permanently active (2026-08-17).
			var isConnected = classic.Any(n => n.IsConnected);

			// The battery is published by ONE node — the Hands-Free AG — so it is picked up from
			// whichever member of the container reports it rather than assumed to be on the device
			// node. Verified 2026-08-17: PG Cosmo reports it on
			// BTHENUM\{0000111e-...} (Handsfree Audio Gateway) and nowhere else.
			//
			// Only while connected: the property KEEPS its last value after the headset goes away
			// (measured at a stale 80 % with the headset switched to its dongle), so reporting it
			// unconditionally would show a live-looking level for a device that is not there.
			var battery = isConnected
				? group.Select(n => n.BatteryPercent).FirstOrDefault(b => b is not null)
				: null;

			// Prefer the plainest name: the Dev_ node is called "PG Cosmo" while its siblings carry
			// profile suffixes such as "PG Cosmo Hands-Free AG".
			var name = classic
				.Where(n => n.InstanceId.Contains("\\Dev_", StringComparison.OrdinalIgnoreCase))
				.Select(n => n.FriendlyName)
				.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
				?? classic.Select(n => n.FriendlyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

			devices.Add(new ClassicBluetoothDevice(
				group.Key, address, identity.VendorId, identity.ProductId, name, battery, isConnected));
		}

		return devices;
	}

	/// <summary>
	/// The catalog row describing this device's Bluetooth transport, or null when it is not a device
	/// we know.
	/// </summary>
	/// <remarks>
	/// Matched on the BLUETOOTH vendor/product id — not the device's USB ids: Cosmo is 0x05D6/0x000A
	/// over Bluetooth and 0x0D8C/0x0334 over its dongle, which is why the catalog carries them per
	/// transport.
	/// <para>
	/// The id pair alone is NOT enough. Starlight reports the same 0x05D6/0x000A as Cosmo (measured
	/// 2026-08-19), so it is an ODM constant rather than an identity — the same way both headsets
	/// ship a byte-identical USB iSerialNumber. While this matched on ids alone it returned the first
	/// row that fitted, and a connected Starlight was drawn as a Cosmo card. A row may therefore
	/// declare <see cref="DeviceMap.BluetoothName"/>, a case-insensitive substring of the Windows
	/// device name, and a row that matches by name outranks one that does not declare a token at all.
	/// </para>
	/// <para>
	/// This does mean a device the user RENAMES in Windows stops matching its tokened row (an earlier
	/// revision of this method avoided names for exactly that reason). That is accepted: matching the
	/// wrong product is a worse failure than matching none, and the ids provably cannot separate these
	/// two headsets. Distinct per-model Bluetooth PIDs have been raised with the vendor —
	/// docs/plans/add-starlight-bluetooth/.
	/// </para>
	/// </remarks>
	public static DeviceMap? MatchCatalog(IReadOnlyList<DeviceMap> maps, ClassicBluetoothDevice device)
	{
		ArgumentNullException.ThrowIfNull(maps);
		ArgumentNullException.ThrowIfNull(device);

		if (device.VendorId == 0 && device.ProductId == 0) return null;

		// Tracked separately so the outcome cannot depend on catalog ORDER — the fragility that let
		// whichever headset happened to be listed first claim the other's identity.
		DeviceMap? untokened = null;

		foreach (var map in maps)
		{
			if (!map.HasBluetooth) continue;
			if (!TryParseId(map.VendorId, out var vendorId) || vendorId != device.VendorId) continue;
			if (!TryParseId(map.ProductId, out var productId) || productId != device.ProductId) continue;

			if (string.IsNullOrWhiteSpace(map.BluetoothName))
			{
				untokened ??= map;
				continue;
			}

			// A tokened row is the more specific statement about these ids, so it wins outright.
			if (device.Name is { } name &&
				name.Contains(map.BluetoothName, StringComparison.OrdinalIgnoreCase))
			{
				return map;
			}
		}

		// No token matched. Falling back to an untokened row keeps every device that never needed a
		// token (TAIPAN, Rampage, T-Racer) on exactly its previous behaviour. With no such row the
		// answer is null rather than an arbitrary tokened one — otherwise a third product on these
		// shared ids would silently inherit whichever headset was listed first, which is the bug.
		return untokened;
	}

	// Catalog ids are decimal strings, as every other entry in appsettings.json is.
	private static bool TryParseId(string? value, out int parsed) =>
		int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

	/// <summary>The 48-bit Bluetooth address from a device-node instance id, or null.</summary>
	/// <remarks>
	/// Deliberately not "the first twelve hex digits in the string". A service node's id embeds the
	/// Bluetooth base UUID — <c>{0000111e-0000-1000-8000-00805f9b34fb}</c> — whose final group is
	/// ALSO twelve hex digits, so a naive scan returns `00805F9B34FB` for every device on every
	/// machine. The address is only ever a whole token in the last path segment, or the tail of
	/// <c>\Dev_</c>.
	/// </remarks>
	public static string? TryParseAddress(string instanceId)
	{
		if (string.IsNullOrEmpty(instanceId)) return null;

		var dev = DevAddressPattern.Match(instanceId);
		if (dev.Success) return dev.Groups["addr"].Value.ToUpperInvariant();

		// The instance-specific segment holds it as a token among others, in either order:
		//   9&8e04a68&0&9200025FD240_C00000000     (address, then a suffix)
		//   9&8e04a68&0&BluetoothDevice_9200025FD240   (a label, then the address)
		var lastSegment = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
		foreach (var token in lastSegment.Split('&', '_'))
		{
			if (token.Length == 12 && IsHex(token))
				return token.ToUpperInvariant();
		}

		return null;
	}

	private static bool IsHex(string value)
	{
		foreach (var c in value)
		{
			if (!Uri.IsHexDigit(c)) return false;
		}
		return true;
	}

	/// <summary>
	/// The Bluetooth VID/PID from a device-node instance id, or (0, 0). Note this is the device's
	/// BLUETOOTH identity, which differs from its USB one — PG Cosmo is 0x05D6/0x000A here and
	/// 0x0D8C/0x0334 over USB.
	/// </summary>
	public static (int VendorId, int ProductId) TryParseVidPid(string instanceId)
	{
		if (string.IsNullOrEmpty(instanceId)) return (0, 0);

		var match = VidPidPattern.Match(instanceId);
		if (!match.Success) return (0, 0);

		return (LastFourHex(match.Groups["vid"].Value), LastFourHex(match.Groups["pid"].Value));
	}

	private static int LastFourHex(string value)
	{
		if (string.IsNullOrEmpty(value)) return 0;

		var tail = value.Length <= 4 ? value : value[^4..];
		return int.TryParse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}
}
