using System.Runtime.InteropServices;
using System.Text;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── A classic-BLUETOOTH headset — discovery and battery, without BLE ──────────────────────────
//
// Phase 0b of docs/plans/add-cosmo-bluetooth, and it exists because Phase 0 disproved the plan's
// premise: Cosmo does NOT appear in `Bluetooth.GetPairedDevicesAsync()`. It connects as a classic
// Bluetooth audio device (A2DP + HFP), not a BLE/GATT peripheral, so the App's entire Bluetooth
// discovery path — built on InTheHand's BLE APIs — cannot see it, and the GATT battery service it
// reads does not exist on this device.
//
// What Phase 0 DID give us is a solid identity: the live audio endpoints report Container ID
// 9ca05de4-…, and Windows groups every device node belonging to one physical device under a shared
// container. So this probe inverts the question — instead of asking Bluetooth for the headset, it
// asks the Windows device tree what else lives in the same container as the audio endpoint.
//
// It answers both blocking questions at once:
//   1. Is there a device node we can enumerate/watch to know the headset is connected?
//   2. Does a battery value exist as a device property, which is how Windows shows one for HFP
//      headsets (there being no GATT here)?
//
// READ-ONLY: enumerates device instance IDs and reads properties. Nothing is opened, written,
// paired or configured.
internal static class ClassicBluetoothDiscoveryProbe
{
	// Parameterised 2026-08-19: the technique is not Cosmo-specific — it asks the Windows device
	// tree what shares a container with a live audio endpoint, which is how ANY classic-Bluetooth
	// headset has to be found. Starlight reuses it unchanged.
	public static void Run(string matchName = "cosmo")
	{
		Console.WriteLine($"'{matchName}' over Bluetooth — device-tree discovery + battery probe (read-only)");
		Console.WriteLine();

		// Start from the audio endpoint, because that is the one identity Phase 0 established.
		var endpoints = AudioEndpointInterop.ReadAll()
			.Where(e => e.Name.Contains(matchName, StringComparison.OrdinalIgnoreCase))
			.ToList();

		var live = endpoints.FirstOrDefault(e => e.State == "ACTIVE" && e.ContainerId is not null);
		if (live is null)
		{
			Console.WriteLine($"No ACTIVE '{matchName}' audio endpoint. Connect the headset over Bluetooth and re-run.");
			Console.WriteLine("(Endpoints seen: " + (endpoints.Count == 0 ? "none" : string.Join(", ", endpoints.Select(e => e.Name))) + ")");
			return;
		}

		var target = live.ContainerId!.Value;
		Console.WriteLine($"Live audio endpoint : {live.Name}");
		Console.WriteLine($"Container to match  : {target}");
		Console.WriteLine();

		var ids = EnumeratePresentDeviceIds();
		Console.WriteLine($"Present device nodes enumerated: {ids.Count}");
		Console.WriteLine();

		var inContainer = new List<(string Id, string? Name, string? Class, byte? Battery)>();
		foreach (var id in ids)
		{
			if (!TryLocate(id, out var devinst)) continue;
			var container = GetGuidProperty(devinst, DevpkeyDeviceContainerId);
			if (container != target) continue;

			inContainer.Add((
				id,
				GetStringProperty(devinst, DevpkeyDeviceFriendlyName) ?? GetStringProperty(devinst, DevpkeyNameProperty),
				GetStringProperty(devinst, DevpkeyDeviceClass),
				GetByteProperty(devinst, DevpkeyBluetoothBattery)));
		}

		Console.WriteLine(new string('=', 92));
		Console.WriteLine($"DEVICE NODES SHARING THAT CONTAINER: {inContainer.Count}");
		Console.WriteLine();

		foreach (var (id, name, cls, battery) in inContainer)
		{
			Console.WriteLine($"  {name ?? "(no name)"}");
			Console.WriteLine($"    instance : {id}");
			Console.WriteLine($"    class    : {cls ?? "(none)"}");
			Console.WriteLine($"    battery  : {(battery is null ? "(property absent)" : battery + " %")}");
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 92));
		Console.WriteLine("VERDICT");
		Console.WriteLine();

		var classic = inContainer.Where(d => d.Id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)).ToList();
		var le = inContainer.Where(d => d.Id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)).ToList();
		var withBattery = inContainer.Where(d => d.Battery is not null).ToList();

		Console.WriteLine($"  classic BT nodes (BTHENUM) : {classic.Count}");
		Console.WriteLine($"  LE nodes (BTHLE)           : {le.Count}");
		Console.WriteLine($"  nodes exposing a battery   : {withBattery.Count}");
		Console.WriteLine();

		Console.WriteLine(inContainer.Count == 0
			? "  The container groups NO device nodes — presence cannot be tracked this way, and the\n" +
			  "  audio endpoint's own ACTIVE state is the only signal available."
			: "  DISCOVERY: the container groups real device nodes, so the App can enumerate these\n" +
			  "  (CM_Get_Device_ID_List + container match, the same CfgMgr32 family DeviceContainerId\n" +
			  "  already uses) and treat their presence as the headset being connected — no BLE needed.");

		// Answers "could we just use BLE after all?" with data rather than inference. The container
		// scan above only sees nodes belonging to THIS headset, so a BLE interface living under a
		// separate container would not have shown up there. This sweeps every LE node on the machine.
		Console.WriteLine();
		Console.WriteLine("  --- every BLE node present on this machine (is Cosmo reachable over LE at all?) ---");
		var allLe = ids.Where(i => i.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)).ToList();
		if (allLe.Count == 0)
		{
			Console.WriteLine("    none at all");
		}
		else
		{
			foreach (var id in allLe)
			{
				var name = TryLocate(id, out var d) ? GetStringProperty(d, DevpkeyDeviceFriendlyName) : null;
				var isMatch = (name ?? id).Contains(matchName, StringComparison.OrdinalIgnoreCase);
				Console.WriteLine($"    {name ?? "(no name)"}{(isMatch ? "   <-- MATCH IS ON LE" : "")}");
				Console.WriteLine($"      {id}");
			}
		}
		Console.WriteLine(allLe.Any(i => i.Contains(matchName, StringComparison.OrdinalIgnoreCase))
			? "    -> Cosmo HAS an LE node; the BLE path may be usable after all."
			: "    -> No Cosmo LE node anywhere. BLE is ruled out for this headset, not merely unused.");

		// Which property actually distinguishes CONNECTED from merely PAIRED. Enumeration alone does
		// not: a paired-but-disconnected device keeps its BTHENUM nodes, which is why the first cut
		// of this feature showed the card as permanently active. Run this connected AND disconnected
		// and compare.
		Console.WriteLine();
		Console.WriteLine("  --- connection-state candidates on EVERY classic BT node (not just the live container) ---");
		Console.WriteLine("      A paired-but-disconnected device keeps its BTHENUM nodes AND its last battery");
		Console.WriteLine("      value, so enumeration alone cannot tell the two states apart. Run connected and");
		Console.WriteLine("      disconnected, and use whichever column actually changes.");
		Console.WriteLine();
		// null means the device tree could not be read at all — distinct from "no nodes", which is
		// why the production call is nullable. For a probe, saying so is the whole point.
		var classicNodes = DeviceNodeQuery.EnumerateClassicBluetoothNodes();
		if (classicNodes is null)
		{
			Console.WriteLine("    device-tree enumeration FAILED — nothing can be concluded from this run.");
			return;
		}

		foreach (var node in classicNodes)
		{
			if (!TryLocate(node.InstanceId, out var devinst)) continue;

			var isConnected = GetBoolProperty(devinst, DevpkeyDeviceIsConnected);
			Console.WriteLine($"    {node.FriendlyName ?? "(no name)"}");
			Console.WriteLine($"      IsConnected : {(isConnected is null ? "(absent)" : isConnected.ToString())}" +
				$"   battery : {(node.BatteryPercent is null ? "(none)" : node.BatteryPercent + "%")}" +
				$"   {GetStatus(devinst)}");
		}

		// Drives the PRODUCTION path, not a reimplementation of it: whatever the App will see is
		// what prints here. A probe that agreed with itself would prove nothing.
		Console.WriteLine();
		Console.WriteLine("  --- through the production code (DeviceNodeQuery + ClassicBluetoothDeviceResolver) ---");
		var resolved = ClassicBluetoothDeviceResolver.FromNodes(classicNodes);
		if (resolved.Count == 0)
		{
			Console.WriteLine("    resolved nothing — the production path does NOT see this headset");
		}
		else
		{
			foreach (var d in resolved)
			{
				var mark = string.Equals(d.ContainerId, target.ToString(), StringComparison.OrdinalIgnoreCase)
					? "   <-- the headset"
					: string.Empty;
				Console.WriteLine($"    {d.Name ?? "(no name)"}{mark}");
				Console.WriteLine($"      address   : {d.Address}");
				Console.WriteLine($"      BT VID/PID: 0x{d.VendorId:X4} / 0x{d.ProductId:X4}");
				Console.WriteLine($"      battery   : {(d.BatteryPercent is null ? "(none)" : d.BatteryPercent + " %")}");
				Console.WriteLine($"      container : {d.ContainerId}");
			}
		}

		Console.WriteLine();
		Console.WriteLine(withBattery.Count > 0
			? $"  BATTERY: readable as a device property — {withBattery[0].Battery} % on\n" +
			  $"           '{withBattery[0].Name}'. This is how Windows shows it, and it is as cheap\n" +
			  "           to read as the GATT path was assumed to be."
			: "  BATTERY: no node exposes DEVPKEY_Bluetooth_Battery. Either the headset reports it\n" +
			  "           only while the Hands-Free profile is engaged, or Windows sources it\n" +
			  "           elsewhere. Re-run while a call/recording holds the mic open before\n" +
			  "           concluding it is unavailable.");
	}

	// ── CfgMgr32 ─────────────────────────────────────────────────────────────────────────────
	// Same family as DeviceContainerId, which reads a property off an interface path; this walks
	// device NODES instead, because a Bluetooth headset's battery and identity live on the node.

	private static List<string> EnumeratePresentDeviceIds()
	{
		if (CM_Get_Device_ID_List_SizeW(out var size, null, CmGetIdListFilterPresent) != 0 || size == 0)
			return [];

		var buffer = new char[size];
		if (CM_Get_Device_ID_ListW(null, buffer, size, CmGetIdListFilterPresent) != 0)
			return [];

		// A REG_MULTI_SZ-style double-null-terminated list.
		var ids = new List<string>();
		var sb = new StringBuilder();
		foreach (var c in buffer)
		{
			if (c == '\0')
			{
				if (sb.Length == 0) break;
				ids.Add(sb.ToString());
				sb.Clear();
			}
			else sb.Append(c);
		}
		return ids;
	}

	private static bool TryLocate(string deviceId, out uint devinst) =>
		CM_Locate_DevNodeW(out devinst, deviceId, CmLocateDevnodePhantom) == 0;

	private static Guid? GetGuidProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[16];
		var size = (uint)buffer.Length;
		var k = key;
		if (CM_Get_DevNode_PropertyW(devinst, ref k, out var type, buffer, ref size, 0) != 0) return null;
		return type != DevpropTypeGuid || size < 16 ? null : new Guid(buffer);
	}

	private static string? GetStringProperty(uint devinst, DevPropKey key)
	{
		uint size = 0;
		var k = key;
		CM_Get_DevNode_PropertyW(devinst, ref k, out _, null, ref size, 0);
		if (size == 0) return null;

		var buffer = new byte[size];
		k = key;
		if (CM_Get_DevNode_PropertyW(devinst, ref k, out var type, buffer, ref size, 0) != 0) return null;
		if (type != DevpropTypeString) return null;

		return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
	}

	private static byte? GetByteProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[1];
		var size = (uint)buffer.Length;
		var k = key;
		if (CM_Get_DevNode_PropertyW(devinst, ref k, out var type, buffer, ref size, 0) != 0) return null;
		return type != DevpropTypeByte || size < 1 ? null : buffer[0];
	}

	private static bool? GetBoolProperty(uint devinst, DevPropKey key)
	{
		var buffer = new byte[1];
		var size = (uint)buffer.Length;
		var k = key;
		if (CM_Get_DevNode_PropertyW(devinst, ref k, out var type, buffer, ref size, 0) != 0) return null;
		return type != DevpropTypeBoolean ? null : buffer[0] != 0;
	}

	private static string GetStatus(uint devinst) =>
		CM_Get_DevNode_Status(out var status, out var problem, devinst, 0) == 0
			? $"status=0x{status:X8} problem={problem}"
			: "(unavailable)";

	// DEVPKEY_Device_IsConnected — the documented "is this Bluetooth device currently connected"
	// flag, as opposed to merely paired and enumerated.
	private static DevPropKey DevpkeyDeviceIsConnected => new()
	{ Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), Pid = 15 };

	private const uint DevpropTypeBoolean = 0x00000011;

	[DllImport("cfgmgr32.dll")]
	private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devinst, uint flags);

	private const uint CmGetIdListFilterPresent = 0x00000100;
	private const uint CmLocateDevnodePhantom = 0x00000001;
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
	{ Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Pid = 2 };

	// DEVPKEY_Device_FriendlyName
	private static DevPropKey DevpkeyDeviceFriendlyName => new()
	{ Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };

	// DEVPKEY_NAME
	private static DevPropKey DevpkeyNameProperty => new()
	{ Fmtid = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), Pid = 10 };

	// DEVPKEY_Device_Class
	private static DevPropKey DevpkeyDeviceClass => new()
	{ Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 9 };

	// DEVPKEY_Bluetooth_Battery — how Windows surfaces an HFP headset's battery. Undocumented in the
	// SDK headers but stable and widely used; this probe exists to confirm it on THIS device rather
	// than assume it.
	private static DevPropKey DevpkeyBluetoothBattery => new()
	{ Fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), Pid = 2 };

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint bufferLen, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Locate_DevNodeW(out uint devinst, string deviceId, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_DevNode_PropertyW(uint devinst, ref DevPropKey propertyKey,
		out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);
}
