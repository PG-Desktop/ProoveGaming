using System.Runtime.InteropServices;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── How fast does WINDOWS notice a Bluetooth device going away? ────────────────────────────────
//
// The App keeps showing a Bluetooth device as connected for well over 20 seconds after it is
// removed or switched off. The reason is structural: the card's Bluetooth state is only ever
// cleared by the BLE sweep in BluetoothDeviceDiscoveryService, and that sweep runs on a 30 s
// ActiveSweepInterval (5 min when nothing is tracked) because it calls Bluetooth.GetPairedDevicesAsync,
// a full device enumeration that is not cheap to repeat.
//
// The CLASSIC path does not have this problem — it polls the Windows device tree every 3 s through
// CfgMgr32, which is cheap, and reads DEVPKEY_Device_IsConnected per node. But DeviceNodeQuery is
// filtered to the `BTHENUM` enumerator, and a BLE device like TAIPAN lives under `BTHLE`, so that
// fast path cannot see it at all.
//
// Before moving the BLE path onto the same cheap poll, two things have to be MEASURED rather than
// assumed:
//
//   1. Does a BLE (`BTHLE`) node even expose DEVPKEY_Device_IsConnected, and is it meaningful?
//   2. How quickly does it flip when the device is switched off / removed / walks out of range?
//
// If Windows itself takes 30 s to update that property, moving to a 3 s poll buys nothing and the
// fix has to come from somewhere else. This probe answers both by polling and printing only on
// CHANGE, with timestamps, so the delay between your action and the flip is read straight off the
// transcript.
//
// READ-ONLY: enumerates device instance ids and reads properties. Nothing is opened or written.
internal static class BluetoothPresenceWatchProbe
{
	// Both enumerators, because the point is to compare them: classic devices are the ones the 3 s
	// poll already handles correctly, so they are the control group for what "working" looks like.
	private static readonly string[] Enumerators = ["BTHLE", "BTHENUM"];

	public static void Run(string? nameFilter = null, int seconds = 120, int intervalMs = 2000)
	{
		Console.WriteLine("Bluetooth presence watch — how fast does Windows report a device gone? (READ-ONLY)");
		Console.WriteLine($"Enumerators: {string.Join(", ", Enumerators)}");
		Console.WriteLine(nameFilter is null
			? "Watching every Bluetooth node."
			: $"Watching nodes whose name contains '{nameFilter}'.");
		Console.WriteLine();
		Console.WriteLine($"Polling every {intervalMs} ms for {seconds}s. Only CHANGES are printed.");
		Console.WriteLine();
		Console.WriteLine("Switch the device OFF (or remove it in Windows Bluetooth settings) and watch the");
		Console.WriteLine("timestamps: the gap between your action and the 'IsConnected True -> False' line is");
		Console.WriteLine("what Windows itself costs us, before any polling cadence we choose.");
		Console.WriteLine();

		var previous = Snapshot(nameFilter);
		Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] baseline — {previous.Count} node(s)");
		foreach (var (id, state) in previous.OrderBy(k => k.Key, StringComparer.Ordinal))
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
		Console.WriteLine("  IsConnected flips within a second or two of switching the device off");
		Console.WriteLine("     -> Windows is fast, the 30 s sweep is ours to fix, and a BTHLE-aware 3 s poll");
		Console.WriteLine("        (the mechanism the classic path already uses) closes it.");
		Console.WriteLine();
		Console.WriteLine("  The node DISAPPEARS instead of flipping");
		Console.WriteLine("     -> absence is the signal for this enumerator; the poll must treat a missing node");
		Console.WriteLine("        as disconnected rather than waiting for a property that will never change.");
		Console.WriteLine();
		Console.WriteLine("  Nothing changes for 30 s or more");
		Console.WriteLine("     -> Windows itself is the delay. Polling faster buys nothing and the fix has to be");
		Console.WriteLine("        an event subscription or a device-level signal instead.");
	}

	private static Dictionary<string, string> Snapshot(string? nameFilter)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var enumerator in Enumerators)
		{
			foreach (var instanceId in EnumerateIds(enumerator))
			{
				if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0) continue;

				var name = ReadString(devInst, FriendlyName) ?? ReadString(devInst, DeviceDesc);
				if (nameFilter is not null &&
					!(name ?? instanceId).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
					continue;

				var connected = ReadBool(devInst, IsConnected);
				var battery = ReadByte(devInst, BluetoothBattery);

				result[instanceId] =
					$"{enumerator,-8} {name ?? "(no name)",-34} IsConnected={connected?.ToString() ?? "(absent)",-8} " +
					$"battery={(battery is null ? "(none)" : battery + "%")}";
			}
		}

		return result;
	}

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

	private static byte? ReadByte(uint devInst, DevPropKey key)
	{
		var buffer = ReadRaw(devInst, key, out _);
		if (buffer is null || buffer.Length == 0) return null;
		return buffer[0] > 100 ? null : buffer[0];
	}

	// The size probe passes a genuine NULL pointer: a null byte[] through the array overload does
	// not reliably marshal as NULL, and every read then reports no data — which looks exactly like
	// "this device exposes nothing" rather than like a bug. See StarlightBatteryHuntProbe.
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
	private static readonly DevPropKey BluetoothBattery =
		new() { Fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), Pid = 2 };

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
