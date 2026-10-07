using System.Runtime.InteropServices;
using System.Text;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Services;

// Reads Windows device NODES, as opposed to DeviceContainerId which reads a property off a device
// INTERFACE path. Both sit on the same CfgMgr32 family; they differ in what they are handed.
//
// This exists for classic Bluetooth devices, which the BLE discovery path cannot see at all — PG
// Cosmo has no BTHLE node anywhere on the machine, so it never appears in the paired-device list and
// exposes no GATT battery service. The device tree does know about it, and groups its nodes under a
// shared Container ID. See docs/plans/add-cosmo-bluetooth/.
//
// Pure P/Invoke, no state. Runs during discovery, never on a device-poll hot path.
public static class DeviceNodeQuery
{
	/// <summary>
	/// Every present classic-Bluetooth device node, with its container, name and battery.
	/// </summary>
	/// <remarks>
	/// Filtered to the <c>BTHENUM</c> enumerator rather than walking the whole device tree: the
	/// machine this was written against reports 221 present nodes and only 7 of them are the
	/// headset's, so the filter turns three property reads per node from a few hundred calls into a
	/// handful. Audio endpoints are deliberately NOT enumerated here — the App resolves those from
	/// the Container ID this returns.
	/// </remarks>
	/// <remarks>
	/// Nullable for the same reason as <see cref="EnumerateLowEnergyNodes"/>: the classic poll marks
	/// every device it does NOT see as disconnected, so an empty list produced by a failed
	/// enumeration would disconnect every classic headset on one bad CfgMgr32 call.
	/// </remarks>
	public static IReadOnlyList<DeviceNode>? EnumerateClassicBluetoothNodes()
	{
		if (!OperatingSystem.IsWindows())
			return null;

		try
		{
			var ids = EnumerateIds(ClassicBluetoothEnumerator);
			if (ids is null) return null;

			var nodes = new List<DeviceNode>();
			foreach (var instanceId in ids)
			{
				if (CM_Locate_DevNodeW(out var devinst, instanceId, CmLocateDevnodeNormal) != CrSuccess)
					continue;

				nodes.Add(new DeviceNode(
					instanceId,
					GetGuidProperty(devinst, DevpkeyDeviceContainerId)?.ToString(),
					GetStringProperty(devinst, DevpkeyDeviceFriendlyName),
					GetByteProperty(devinst, DevpkeyBluetoothBattery),
					GetBoolProperty(devinst, DevpkeyDeviceIsConnected) ?? false));
			}

			return nodes;
		}
		catch
		{
			// Not an empty list: the caller must be able to tell "could not read" from "nothing is
			// there", because it disconnects on the latter.
			return null;
		}
	}

	/// <summary>
	/// Every present Bluetooth LOW ENERGY device node, or <c>null</c> when the tree could not be
	/// enumerated at all.
	/// </summary>
	/// <remarks>
	/// The nullable return is the whole point, and it is why this is not just
	/// <see cref="EnumerateClassicBluetoothNodes"/> with a different enumerator string. There, an
	/// empty list is the truthful answer — nothing is connected. Here the caller uses absence to
	/// DISCONNECT a device, so "the query failed" and "nothing is present" must not look alike: a
	/// failed enumeration returning empty would drop every Bluetooth device the App is showing.
	/// <para>
	/// Filtered to <c>BTHLE</c>, the enumerator a BLE peripheral lives under — classic devices are
	/// under <c>BTHENUM</c> and are handled by the method above. Measured 2026-08-20: a BLE mouse
	/// exposes <c>DEVPKEY_Device_IsConnected</c> here and flips it within ~2 s of being switched
	/// off, and its node disappears outright when the pairing is removed in Windows settings. Both
	/// are disconnect signals; neither was previously visible to the App inside 30 s.
	/// </para>
	/// </remarks>
	public static IReadOnlyList<DeviceNode>? EnumerateLowEnergyNodes()
	{
		if (!OperatingSystem.IsWindows())
			return null;

		try
		{
			var ids = EnumerateIds(LowEnergyBluetoothEnumerator);
			if (ids is null) return null;

			var nodes = new List<DeviceNode>();
			foreach (var instanceId in ids)
			{
				// A node that vanishes between the id list and the property read is ordinary while
				// something powers down. Skipping it is correct — it is gone, which is exactly the
				// state the caller is looking for.
				if (CM_Locate_DevNodeW(out var devinst, instanceId, CmLocateDevnodeNormal) != CrSuccess)
					continue;

				nodes.Add(new DeviceNode(
					instanceId,
					GetGuidProperty(devinst, DevpkeyDeviceContainerId)?.ToString(),
					GetStringProperty(devinst, DevpkeyDeviceFriendlyName),
					GetByteProperty(devinst, DevpkeyBluetoothBattery),
					GetBoolProperty(devinst, DevpkeyDeviceIsConnected) ?? false,
					GetFileTimeProperty(devinst, DevpkeyBluetoothLastConnectedTime)));
			}

			return nodes;
		}
		catch
		{
			// Explicitly NOT an empty list — see the remarks. The caller must be able to tell this
			// apart and leave every tracked device alone.
			return null;
		}
	}

	/// <summary>Present device ids for an enumerator, or <c>null</c> when the list could not be read.</summary>
	/// <remarks>
	/// The tri-state is load-bearing and used to be absent. CfgMgr32 reports failure through CONFIGRET
	/// codes, never exceptions, so a caller's try/catch cannot see one: both error paths here returned
	/// an empty list, indistinguishable from "this machine has no such devices". A caller that treats
	/// absence as "the device is gone" — <see cref="EnumerateLowEnergyNodes"/>'s does, and releases the
	/// GATT link on it — would then destroy every tracked device on one bad call.
	/// <para>
	/// This is a real race, not a theoretical one: the size query and the fetch are separate calls, so
	/// a device arriving between them (any pairing, a headset connecting, a peripheral waking) grows
	/// the list past the measured buffer and the fetch returns CR_BUFFER_SMALL.
	/// </para>
	/// </remarks>
	private static List<string>? EnumerateIds(string enumerator)
	{
		var flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;

		if (CM_Get_Device_ID_List_SizeW(out var length, enumerator, flags) != CrSuccess)
			return null;

		// Genuinely nothing present — a truthful empty answer, unlike the failures above.
		if (length == 0)
			return [];

		var buffer = new char[length];
		if (CM_Get_Device_ID_ListW(enumerator, buffer, length, flags) != CrSuccess)
			return null;

		// A double-null-terminated list, as REG_MULTI_SZ is.
		var ids = new List<string>();
		var current = new StringBuilder();
		foreach (var c in buffer)
		{
			if (c != '\0')
			{
				current.Append(c);
				continue;
			}

			if (current.Length == 0) break;
			ids.Add(current.ToString());
			current.Clear();
		}

		return ids;
	}

	private static Guid? GetGuidProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[16];
		var size = (uint)buffer.Length;
		if (CM_Get_DevNode_PropertyW(devinst, ref key, out var type, buffer, ref size, 0) != CrSuccess)
			return null;

		return type == DevpropTypeGuid && size == 16 ? new Guid(buffer) : null;
	}

	private static string? GetStringProperty(uint devinst, DevPropKey key)
	{
		uint size = 0;
		var probe = key;
		CM_Get_DevNode_PropertyW(devinst, ref probe, out _, null, ref size, 0);
		if (size == 0) return null;

		var buffer = new byte[size];
		if (CM_Get_DevNode_PropertyW(devinst, ref key, out var type, buffer, ref size, 0) != CrSuccess ||
			type != DevpropTypeString)
			return null;

		return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
	}

	// The battery is a single byte. Range-guarded: a device reporting something that is not a
	// percentage must read as "unknown" rather than reach the user as a nonsense level — the same
	// rule Cosmo's dongle battery already follows.
	private static int? GetByteProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[1];
		var size = (uint)buffer.Length;
		if (CM_Get_DevNode_PropertyW(devinst, ref key, out var type, buffer, ref size, 0) != CrSuccess)
			return null;
		if (type != DevpropTypeByte || size < 1) return null;

		return buffer[0] <= 100 ? buffer[0] : null;
	}

	// Whether the device is CONNECTED, as opposed to merely paired and therefore enumerated. Windows
	// keeps a paired device's nodes present while it is away, so without this every paired headset
	// reads as connected forever — measured 2026-08-17 with Cosmo switched to its dongle, where the
	// nodes remained and this property read False on every one of them.
	private static DevPropKey DevpkeyDeviceIsConnected => new()
	{
		Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
		Pid = 15,
	};

	private static bool? GetBoolProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[1];
		var size = (uint)buffer.Length;
		if (CM_Get_DevNode_PropertyW(devinst, ref key, out var type, buffer, ref size, 0) != CrSuccess)
			return null;

		return type == DevpropTypeBoolean ? buffer[0] != 0 : null;
	}

	// A FILETIME property, interpreted as UTC. Note DEVPKEY_Bluetooth_LastConnectedTime is
	// actually stored as LOCAL time serialised into the FILETIME (measured 2026-09-02 — values
	// read hours ahead of the wall-clock event through this interpretation), so the result can be
	// off by a timezone and even sit slightly in the future. Callers must only compare it against
	// day-scale thresholds, where that error is noise and a "future" value reads as fresh.
	private static DateTime? GetFileTimeProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[8];
		var size = (uint)buffer.Length;
		if (CM_Get_DevNode_PropertyW(devinst, ref key, out var type, buffer, ref size, 0) != CrSuccess)
			return null;
		if (type != DevpropTypeFileTime || size < 8) return null;

		var fileTime = BitConverter.ToInt64(buffer, 0);
		if (fileTime <= 0) return null;

		try
		{
			return DateTime.FromFileTimeUtc(fileTime);
		}
		catch (ArgumentOutOfRangeException)
		{
			// A corrupt value past DateTime.MaxValue must read as "no timestamp", not abort the
			// caller's whole enumeration pass (review R1 nit).
			return null;
		}
	}

	private const uint DevpropTypeBoolean = 0x00000011;
	private const uint DevpropTypeFileTime = 0x00000010;

	private const string ClassicBluetoothEnumerator = "BTHENUM";
	private const string LowEnergyBluetoothEnumerator = "BTHLE";

	private const int CrSuccess = 0;
	private const uint CmGetIdListFilterEnumerator = 0x00000001;
	private const uint CmGetIdListFilterPresent = 0x00000100;
	private const uint CmLocateDevnodeNormal = 0x00000000;
	private const uint DevpropTypeString = 0x00000012;
	private const uint DevpropTypeGuid = 0x0000000D;
	private const uint DevpropTypeByte = 0x00000003;

	[StructLayout(LayoutKind.Sequential)]
	private struct DevPropKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	// DEVPKEY_Device_ContainerId
	private static DevPropKey DevpkeyDeviceContainerId => new()
	{
		Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
		Pid = 2,
	};

	// DEVPKEY_Device_FriendlyName
	private static DevPropKey DevpkeyDeviceFriendlyName => new()
	{
		Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
		Pid = 14,
	};

	// DEVPKEY_Bluetooth_Battery — how Windows surfaces an HFP headset's battery, there being no GATT
	// battery service on a classic device. Absent from the SDK headers but stable; confirmed on PG
	// Cosmo (2026-08-17), which publishes it on its Hands-Free AG node and nowhere else.
	private static DevPropKey DevpkeyBluetoothBattery => new()
	{
		Fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"),
		Pid = 2,
	};

	// DEVPKEY_Bluetooth_LastConnectedTime — when the device was last on the link. Stamped on every
	// connect AND disconnect (measured 2026-09-02 on a PG Rampage through a live power cycle:
	// both transitions moved it to "now"), so a pairing whose value is weeks old is one that has
	// not connected for weeks — the stale-ghost-pairing signal. Absent from the SDK headers like
	// the battery key above; the same Get-PnpDeviceProperty name resolution vouches for it.
	// See docs/plans/add-stale-pairing-detection/notes.md.
	private static DevPropKey DevpkeyBluetoothLastConnectedTime => new()
	{
		Fmtid = new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a"),
		Pid = 11,
	};

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int CM_Get_Device_ID_ListW(string? pszFilter, char[] buffer, uint bufferLen, uint ulFlags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int CM_Get_DevNode_PropertyW(
		uint dnDevInst,
		ref DevPropKey propertyKey,
		out uint propertyType,
		byte[]? propertyBuffer,
		ref uint propertyBufferSize,
		uint ulFlags);
}
