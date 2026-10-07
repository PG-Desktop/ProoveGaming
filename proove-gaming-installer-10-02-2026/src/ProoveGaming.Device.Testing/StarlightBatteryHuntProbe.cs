using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — is a battery level reachable over the 2.4G DONGLE? ──────────────────────────
//
// Bluetooth gave us a battery (70 %, from the Hands-Free AG device node) but the dongle still has
// none, and the 2026-08-07 investigation closed the obvious doors:
//
//   - report 0xFD answers HidD_GetInputReport with 32 ZERO bytes, always;
//   - nothing is ever pushed on 0xFD, under any physical action;
//   - there are NO feature reports anywhere (feature length 0 on both collections);
//   - with the headset switched OFF the dongle stays enumerated with both its HID interface and its
//     audio endpoints intact, so neither HID nor Core Audio can even tell you it is off.
//
// So this probe deliberately does NOT retry any of that. It runs the two things that were never
// tried, both strictly read-only:
//
//   ARM A — REPORT-ID SWEEP. Every earlier read used report id 0xFD and only 0xFD. But Cosmo's
//           battery does not live on its vendor channel either: Cosmo's vendor report is 0xFD and
//           its status report is 0xBC. Firmware routinely answers control reads on ids the
//           descriptor never advertises, so this asks all 256 of them on every collection and
//           prints whatever comes back non-zero. HidD_GetInputReport is a control-transfer READ —
//           it cannot write, cannot erase and cannot brick.
//
//   ARM B — DEVICE-PROPERTY DUMP. The Bluetooth battery was not found in HID at all; it was a
//           Windows device property on a sibling node. Nobody has ever enumerated the properties of
//           Starlight's USB nodes. This walks every node under the device and prints every DEVPKEY
//           it exposes, so a battery/power property would be visible if one exists.
//
// Both arms run in PHASES — headset on, headset off, headset on again — because a value that never
// moves is not a battery, and that diff is exactly what decoded Cosmo's power byte. Whatever this
// finds is a candidate, never a conclusion: shipping Cosmo's never-moving byte 17 as a battery
// percentage is the mistake this method exists to avoid repeating.
//
// WHAT IT DOES NOT DO: it sends nothing to report 0xFD. That channel looks like a command channel
// (a read that succeeds with all zeros is a request/response channel with no reply pending), and
// the standing safety boundary on this device prohibits unknown opcodes — it persists its LED table
// to flash. Asking it a question we would have to invent an opcode for is not a read-only act.
internal static class StarlightBatteryHuntProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	public static void Run(bool phased = true)
	{
		Console.WriteLine("PG Starlight — battery hunt over the 2.4G dongle (READ-ONLY)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();
		Console.WriteLine("Nothing here writes to the device. Both arms are control-transfer reads and");
		Console.WriteLine("Windows property reads; report 0xFD is never sent to.");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("No Starlight HID interface found. Is the dongle plugged in?");
			return;
		}

		if (!phased)
		{
			Capture(interfaces, "single pass");
			return;
		}

		Console.WriteLine("Three phases. A value that does not move across them is not a battery.");
		Console.WriteLine();

		Prompt("PHASE 1 of 3 — switch the headset ON, wait for it to pair, then press Enter.");
		var on1 = Capture(interfaces, "headset ON");

		Prompt("PHASE 2 of 3 — switch the headset OFF (leave the dongle plugged in), then press Enter.");
		var off = Capture(interfaces, "headset OFF");

		Prompt("PHASE 3 of 3 — switch the headset back ON, wait for it to pair, then press Enter.");
		var on2 = Capture(interfaces, "headset ON again");

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("DIFF — what actually moved");
		Console.WriteLine();

		Diff("ON -> OFF", on1, off);
		Diff("OFF -> ON", off, on2);

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("READING THE RESULT");
		Console.WriteLine();
		Console.WriteLine("  Nothing listed under DIFF  -> the dongle exposes no power or battery state to the");
		Console.WriteLine("     host, and SupportsBattery:false stays correct for this transport. That is a real");
		Console.WriteLine("     answer, and the same one Bluetooth needed a different mechanism to escape.");
		Console.WriteLine();
		Console.WriteLine("  Something moved ON<->OFF   -> a POWER-state candidate. Check it repeats across a");
		Console.WriteLine("     second cycle before believing it.");
		Console.WriteLine();
		Console.WriteLine("  A value in 0..100 that is stable within a phase but drifts over hours -> a battery");
		Console.WriteLine("     candidate. It is NOT a battery until it tracks a real drain; ship nothing on one");
		Console.WriteLine("     reading (see the Cosmo byte-17 note in docs/CONTEXT.md).");
	}

	private static void Prompt(string message)
	{
		Console.WriteLine(new string('-', 96));
		Console.WriteLine(message);
		Console.ReadLine();
	}

	// One full capture: every readable report id on every collection, plus every device property.
	private static Dictionary<string, string> Capture(List<HidDevice> interfaces, string label)
	{
		var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"CAPTURE — {label}");
		Console.WriteLine();

		Console.WriteLine("ARM A — report-id sweep (HidD_GetInputReport, 0x00-0xFF)");
		foreach (var d in interfaces)
		{
			int length = Len(() => d.GetMaxInputReportLength(), 0);
			Console.WriteLine($"  {d.DevicePath}");
			Console.WriteLine($"    max input report length: {length}");

			if (length <= 0)
			{
				Console.WriteLine("    skipped — no input reports on this collection.");
				continue;
			}

			var answers = 0;
			for (int id = 0; id <= 0xFF; id++)
			{
				var frame = TryGetInputReport(d.DevicePath, (byte)id, length);
				if (frame is null) continue;

				// A successful read of an all-zero buffer is what 0xFD already does; it carries no
				// information and would bury a real hit in 256 lines of noise.
				if (frame.All(b => b == 0)) continue;

				answers++;
				var hex = BitConverter.ToString(frame);
				snapshot[$"hid:{Short(d.DevicePath)}:0x{id:X2}"] = hex;
				Console.WriteLine($"    0x{id:X2} -> {hex}");
			}

			Console.WriteLine(answers == 0
				? "    nothing answered with a non-zero payload."
				: $"    {answers} report id(s) answered with data.");
		}

		Console.WriteLine();
		Console.WriteLine("ARM B — Windows device properties on every Starlight node");
		foreach (var (instanceId, props) in EnumerateStarlightNodeProperties())
		{
			Console.WriteLine($"  {instanceId}");
			foreach (var (key, value) in props)
			{
				snapshot[$"prop:{instanceId}:{key}"] = value;
				Console.WriteLine($"    {key} = {value}");
			}
		}

		Console.WriteLine();
		return snapshot;
	}

	private static void Diff(string label, Dictionary<string, string> a, Dictionary<string, string> b)
	{
		var moved = a.Keys.Union(b.Keys, StringComparer.Ordinal)
			.Where(k => !string.Equals(Get(a, k), Get(b, k), StringComparison.Ordinal))
			.OrderBy(k => k, StringComparer.Ordinal)
			.ToList();

		Console.WriteLine($"  {label}: {(moved.Count == 0 ? "nothing changed" : moved.Count + " value(s) changed")}");
		foreach (var k in moved)
		{
			Console.WriteLine($"    {k}");
			Console.WriteLine($"      before: {Get(a, k)}");
			Console.WriteLine($"      after : {Get(b, k)}");
		}

		static string Get(Dictionary<string, string> d, string k) =>
			d.TryGetValue(k, out var v) ? v : "(absent)";
	}

	private static string Short(string devicePath)
	{
		var i = devicePath.IndexOf("col", StringComparison.OrdinalIgnoreCase);
		return i > 0 ? devicePath[i..].TrimEnd('}') : devicePath[^12..];
	}

	// ── ARM A interop ─────────────────────────────────────────────────────────────────────────

	// HidD_GetInputReport takes the report id in buffer[0] and fills the rest. A device that does
	// not know the id fails the call, which is why an unknown id is safe to ask for: the failure is
	// the answer, and nothing is written either way.
	private static byte[]? TryGetInputReport(string devicePath, byte reportId, int length)
	{
		SafeFileHandle? handle = null;
		try
		{
			handle = CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
			if (handle.IsInvalid) return null;

			var buffer = new byte[length];
			buffer[0] = reportId;
			return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			handle?.Dispose();
		}
	}

	// ── ARM B interop ─────────────────────────────────────────────────────────────────────────

	// Every property Windows holds on every device node whose instance id names this VID/PID. The
	// key list is asked for rather than hard-coded, because the whole point is to find a property
	// nobody thought to look for — a fixed DEVPKEY list could only rediscover what we already know.
	private static List<(string InstanceId, List<(string Key, string Value)> Props)> EnumerateStarlightNodeProperties()
	{
		var result = new List<(string, List<(string, string)>)>();
		var needle = $"VID_{Vid:X4}&PID_{Pid:X4}";

		foreach (var instanceId in EnumerateAllDeviceIds()
			.Where(id => id.Contains(needle, StringComparison.OrdinalIgnoreCase)))
		{
			if (CM_Locate_DevNodeW(out var devInst, instanceId, CmLocateNormal) != 0) continue;

			// Size probe: CR_BUFFER_SMALL (0x1A) with the required count.
			var count = 0;
			CM_Get_DevNode_Property_Keys(devInst, IntPtr.Zero, ref count, 0);
			if (count == 0) continue;

			// Filled through an explicit unmanaged buffer rather than a DEVPROPKEY[]. The array
			// overload returns CR_SUCCESS but leaves every element zeroed, and an all-zero key then
			// fails each property read with CR_INVALID_DATA — which reads as "this device exposes
			// nothing" instead of as a marshalling bug. Learned the hard way; do not switch back.
			var props = new List<(string, string)>();
			var keySize = Marshal.SizeOf<DevPropKey>();
			var block = Marshal.AllocHGlobal(keySize * count);
			try
			{
				if (CM_Get_DevNode_Property_Keys(devInst, block, ref count, 0) != 0) continue;

				for (var i = 0; i < count; i++)
				{
					var key = Marshal.PtrToStructure<DevPropKey>(block + i * keySize);
					var value = ReadProperty(devInst, key);
					if (value is not null) props.Add(($"{Name(key)}", value));
				}
			}
			finally
			{
				Marshal.FreeHGlobal(block);
			}

			result.Add((instanceId, props));
		}

		return result;
	}

	// The well-known keys, so a real find is obvious in the transcript instead of being a GUID the
	// reader has to go and look up. Anything unrecognised prints raw — that is where a surprise would
	// be, so it must never be hidden.
	private static string Name(DevPropKey key)
	{
		var raw = $"{key.Fmtid:B},{key.Pid}";
		return raw.ToUpperInvariant() switch
		{
			"{A45C254E-DF1C-4EFD-8020-67D146A850E0},14" => "FriendlyName        " + raw,
			"{A45C254E-DF1C-4EFD-8020-67D146A850E0},9" => "DeviceDesc?         " + raw,
			"{8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C},2" => "ContainerId         " + raw,
			"{83DA6326-97A6-4088-9453-A1923F573B29},15" => "IsConnected         " + raw,
			"{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2" => "Bluetooth_Battery   " + raw,
			_ => raw,
		};
	}

	// Rendered by type rather than decoded per key: the interesting thing is whether a value MOVES
	// between phases, and a stable textual rendering is enough to see that.
	private static string? ReadProperty(uint devInst, DevPropKey key)
	{
		uint type = 0;
		var size = 0;

		// Size probe with a genuine NULL pointer. Passing a null byte[] through the array overload
		// does not reliably marshal as NULL here, and the call then reports no data for every key —
		// which looks exactly like "this device exposes no properties" rather than like a bug.
		CM_Get_DevNode_PropertyW(devInst, ref key, ref type, IntPtr.Zero, ref size, 0);
		if (size == 0) return null;

		var buffer = new byte[size];
		if (CM_Get_DevNode_PropertyW(devInst, ref key, ref type, buffer, ref size, 0) != 0) return null;

		return type switch
		{
			DevpropTypeString or DevpropTypeStringList =>
				Encoding.Unicode.GetString(buffer).TrimEnd('\0').Replace('\0', '|'),
			DevpropTypeByte => buffer[0].ToString(),
			DevpropTypeBoolean => (buffer[0] != 0).ToString(),
			DevpropTypeUint16 or DevpropTypeInt16 => BitConverter.ToUInt16(buffer).ToString(),
			DevpropTypeUint32 or DevpropTypeInt32 => BitConverter.ToUInt32(buffer).ToString(),
			DevpropTypeGuid => new Guid(buffer).ToString("B"),
			_ => BitConverter.ToString(buffer),
		};
	}

	private static List<string> EnumerateAllDeviceIds()
	{
		var size = 0;
		if (CM_Get_Device_ID_List_SizeW(ref size, null, CmGetIdListFilterPresent) != 0 || size == 0)
			return [];

		var buffer = new char[size];
		if (CM_Get_Device_ID_ListW(null, buffer, size, CmGetIdListFilterPresent) != 0)
			return [];

		return new string(buffer)
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.ToList();
	}

	private static int Len(Func<int> get, int fallback)
	{
		try { return get(); }
		catch { return fallback; }
	}

	// ── P/Invoke ──────────────────────────────────────────────────────────────────────────────

	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareRead = 1;
	private const uint FileShareWrite = 2;
	private const uint OpenExisting = 3;

	private const uint CmLocateNormal = 0;
	private const uint CmGetIdListFilterPresent = 0x00000100;

	private const uint DevpropTypeString = 0x12;
	private const uint DevpropTypeStringList = 0x2012;
	private const uint DevpropTypeByte = 0x03;
	private const uint DevpropTypeBoolean = 0x11;
	private const uint DevpropTypeInt16 = 0x05;
	private const uint DevpropTypeUint16 = 0x06;
	private const uint DevpropTypeInt32 = 0x07;
	private const uint DevpropTypeUint32 = 0x08;
	private const uint DevpropTypeGuid = 0x0D;

	[StructLayout(LayoutKind.Sequential)]
	private struct DevPropKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
		IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_List_SizeW(ref int length, string? filter, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int bufferLength, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

	[DllImport("cfgmgr32.dll")]
	private static extern int CM_Get_DevNode_Property_Keys(uint devInst, IntPtr keys, ref int count, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, ref uint type,
		byte[]? buffer, ref int size, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
	private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, ref uint type,
		IntPtr buffer, ref int size, uint flags);
}
