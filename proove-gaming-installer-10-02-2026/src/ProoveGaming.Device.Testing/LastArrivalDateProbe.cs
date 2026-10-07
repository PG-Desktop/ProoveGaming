using System.Runtime.InteropServices;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── Does DEVPKEY_Device_LastArrivalDate on a BTHLE node track RECONNECTS? ──────────────────────
//
// A stale ghost pairing — a paired BLE entry whose device re-paired under a new address and will
// never connect again — keeps BluetoothDeviceDiscoveryService's scan-need condition permanently
// true, so the LE scan windows reopen forever (docs/plans/add-stale-pairing-detection/). The plan
// wants to exclude a tracked device that has not connected for weeks, and the candidate signal is
// DEVPKEY_Device_LastArrivalDate on the device node: persistent across reboots, readable on the
// same CfgMgr32 path the 3 s tree poll already walks, and — hypothetically — updated every time
// the devnode starts, i.e. on every reconnect.
//
// That hypothesis is exactly what is undocumented for BTHLE nodes, so it must be MEASURED before
// anything is built on it:
//
//   1. Does a BTHLE node expose LastArrivalDate at all?
//   2. Does powering the device off and on move it to "now" — or does it only record the original
//      pairing / last boot, which would make it useless as a liveness clock?
//
// The probe prints a baseline of every BTHLE node with its candidate timestamps, then polls and
// prints only CHANGES while you power the device off and on. If a reconnect moves a timestamp to
// the reconnect time, the plan's option 1 holds on that key; if nothing moves, option 1 is dead
// and the plan says to stop and go back to the user.
//
// MEASURED 2026-09-02 (first run, this machine): LastArrivalDate is FALSIFIED — all five BTHLE
// nodes carried the identical value (an adapter event, not per-device), unmoved by the Rampage's
// many reconnects since. A full property dump of the live node surfaced the real candidates,
// which this probe now reads alongside it: DEVPKEY_Bluetooth_LastConnectedTime (read "today" on
// the connected node) and the battery-GUID FILETIME at pid 7 (minutes-fresh).
//
// READ-ONLY: enumerates device instance ids and reads properties. Nothing is opened or written.
internal static class LastArrivalDateProbe
{
	private const string Enumerator = "BTHLE";

	public static void Run(string? nameFilter = null, int seconds = 120, int intervalMs = 2000)
	{
		Console.WriteLine("LastArrivalDate probe — does a BTHLE node's arrival timestamp track reconnects? (READ-ONLY)");
		Console.WriteLine(nameFilter is null
			? "Watching every BTHLE node."
			: $"Watching nodes whose name contains '{nameFilter}'.");
		Console.WriteLine();
		Console.WriteLine($"Polling every {intervalMs} ms for {seconds}s. Only CHANGES are printed.");
		Console.WriteLine();
		Console.WriteLine("Power the Bluetooth device OFF, wait ~10 s, then power it ON and let it reconnect.");
		Console.WriteLine("Success looks like: after the reconnect, LastConnected moves to the reconnect time.");
		Console.WriteLine();

		var previous = Snapshot(nameFilter);
		Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] baseline — {previous.Count} node(s)");
		foreach (var (_, state) in previous.OrderBy(k => k.Key, StringComparer.Ordinal))
			Console.WriteLine($"    {state}");
		Console.WriteLine();


		var deadline = DateTime.UtcNow.AddSeconds(seconds);
		while (DateTime.UtcNow < deadline)
		{
			Thread.Sleep(intervalMs);
			var current = Snapshot(nameFilter);

			foreach (var id in previous.Keys.Union(current.Keys, StringComparer.Ordinal)
				.OrderBy(k => k, StringComparer.Ordinal))
			{
				var before = previous.TryGetValue(id, out var b) ? b : "(node absent)";
				var after = current.TryGetValue(id, out var a) ? a : "(node absent)";
				if (string.Equals(before, after, StringComparison.Ordinal)) continue;

				Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] CHANGED");
				Console.WriteLine($"    before: {before}");
				Console.WriteLine($"    after : {after}");
			}

			previous = current;
		}

		Console.WriteLine();
		Console.WriteLine("Watch finished.");
		Console.WriteLine();
		Console.WriteLine("READING THE RESULT");
		Console.WriteLine("  LastConnected moved to the reconnect time after the power-on");
		Console.WriteLine("     -> the timestamp is a liveness clock; a node whose LastConnected is weeks old is");
		Console.WriteLine("        a pairing that has not connected for weeks. The plan's option 1 holds (on the");
		Console.WriteLine("        Bluetooth key, not LastArrivalDate).");
		Console.WriteLine();
		Console.WriteLine("  LastConnected did NOT move (and neither did BattSeen)");
		Console.WriteLine("     -> no per-connection clock exists on the node; excluding on any of these would");
		Console.WriteLine("        misjudge every device. STOP — the plan gates its remaining steps on this.");
		Console.WriteLine();
		Console.WriteLine("  The properties are absent on every node");
		Console.WriteLine("     -> nothing to build on; same STOP as above.");
	}

	private static Dictionary<string, string> Snapshot(string? nameFilter)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var instanceId in EnumerateIds(Enumerator))
		{
			if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0) continue;

			var name = ReadString(devInst, FriendlyName) ?? ReadString(devInst, DeviceDesc);
			if (nameFilter is not null &&
				!(name ?? instanceId).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
				continue;

			var connected = ReadBool(devInst, IsConnected);
			var arrival = ReadFileTime(devInst, LastArrivalDate);
			var lastConnected = ReadFileTime(devInst, BluetoothLastConnectedTime);
			var batterySeen = ReadFileTime(devInst, BluetoothBatteryTimestamp);

			result[instanceId] =
				$"{name ?? "(no name)",-34} IsConnected={connected?.ToString() ?? "(absent)",-8} " +
				$"LastConnected={Format(lastConnected),-20} LastArrival={Format(arrival),-20} " +
				$"BattSeen={Format(batterySeen)}  [{instanceId}]";
		}

		return result;
	}

	private static string Format(DateTime? utc) =>
		utc is null ? "(absent)" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

	// ── Property reads ────────────────────────────────────────────────────────────────────────

	private static string? ReadString(uint devInst, DevPropKey key)
	{
		var buffer = ReadRaw(devInst, key, out _);
		return buffer is null ? null : Encoding.Unicode.GetString(buffer).TrimEnd('\0');
	}

	private static bool? ReadBool(uint devInst, DevPropKey key)
	{
		var buffer = ReadRaw(devInst, key, out _);
		return buffer is null || buffer.Length == 0 ? null : buffer[0] != 0;
	}

	private static DateTime? ReadFileTime(uint devInst, DevPropKey key)
	{
		var buffer = ReadRaw(devInst, key, out var type);
		if (buffer is null || buffer.Length < 8 || type != DevPropTypeFileTime) return null;

		var fileTime = BitConverter.ToInt64(buffer, 0);
		return fileTime <= 0 ? null : DateTime.FromFileTimeUtc(fileTime);
	}

	// The size probe passes a genuine NULL pointer: a null byte[] through the array overload does
	// not reliably marshal as NULL, and every read then reports no data — which looks exactly like
	// "this device exposes nothing" rather than like a bug. See BluetoothPresenceWatchProbe.
	private static byte[]? ReadRaw(uint devInst, DevPropKey key, out uint type)
	{
		type = 0;
		var size = 0;
		CM_Get_DevNode_PropertyW(devInst, ref key, ref type, IntPtr.Zero, ref size, 0);
		if (size == 0) return null;

		var buffer = new byte[size];
		return CM_Get_DevNode_PropertyW(devInst, ref key, ref type, buffer, ref size, 0) == 0 ? buffer : null;
	}

	private static List<string> EnumerateIds(string enumerator)
	{
		const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;

		var size = 0;
		if (CM_Get_Device_ID_List_SizeW(ref size, enumerator, flags) != 0 || size == 0) return [];

		var buffer = new char[size];
		if (CM_Get_Device_ID_ListW(enumerator, buffer, size, flags) != 0) return [];

		return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
	}

	// ── DEVPKEYs ──────────────────────────────────────────────────────────────────────────────

	private static readonly DevPropKey FriendlyName =
		new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
	private static readonly DevPropKey DeviceDesc =
		new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 2 };
	private static readonly DevPropKey IsConnected =
		new() { Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 15 };
	private static readonly DevPropKey LastArrivalDate =
		new() { Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 102 };

	// DEVPKEY_Bluetooth_LastConnectedTime — the timestamp Windows Settings shows for a paired
	// device. Found by dumping every property of a live node with Get-PnpDeviceProperty after
	// LastArrivalDate was falsified (2026-09-02: identical adapter-event time on all five nodes,
	// unmoved by reconnects). This one read "today" on the connected Rampage while LastArrival
	// sat five days stale — the primary candidate.
	private static readonly DevPropKey BluetoothLastConnectedTime =
		new() { Fmtid = new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), Pid = 11 };

	// Same GUID as the Bluetooth battery property (pid 2) at pid 7 — a FILETIME that also read
	// minutes-fresh on the connected node. Secondary candidate: likely "battery last reported",
	// which only moves while the link is up.
	private static readonly DevPropKey BluetoothBatteryTimestamp =
		new() { Fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), Pid = 7 };

	private const uint DevPropTypeFileTime = 0x00000010;
	private const uint CmGetIdListFilterEnumerator = 0x00000001;
	private const uint CmGetIdListFilterPresent = 0x00000100;

	[StructLayout(LayoutKind.Sequential)]
	private struct DevPropKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_List_SizeW(ref int length, string? filter, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int bufferLength, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, ref uint type,
		byte[]? buffer, ref int size, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
	private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, ref uint type,
		IntPtr buffer, ref int size, uint flags);
}
