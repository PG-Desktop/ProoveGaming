using System.Runtime.InteropServices;
using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── What can identify ONE physical device? ────────────────────────────────────────────────────
//
// The app currently keys per-device state on (VendorID, ProductID, Container ID). Container ID is
// stable across a device's own interfaces but is derived from where the device is attached, so
// moving a mouse to another USB port makes it look like a new device. This probe enumerates every
// identity-bearing value the platform actually exposes, so the gap can be described precisely
// instead of guessed at.
//
// Two layers are dumped for each interface:
//   1. HidSharp — what our own code can read today, cross-platform.
//   2. Windows device properties (CfgMgr32) — what exists underneath but we do not currently use,
//      including the USB serial-number string that would be the real per-unit key if the firmware
//      set one.
//
// Read-only.
internal static class HidIdentityProbe
{
	public static void Run(int? onlyVendorId = null)
	{
		var devices = DeviceList.Local.GetHidDevices()
			.Where(d => onlyVendorId is null || d.VendorID == onlyVendorId)
			.OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ThenBy(d => d.DevicePath)
			.ToList();

		Console.WriteLine($"HID identity probe — {devices.Count} interface(s)"
			+ (onlyVendorId is { } v ? $" on VID 0x{v:X4}" : " (all vendors)"));
		Console.WriteLine();

		foreach (var d in devices)
		{
			Console.WriteLine(new string('=', 100));
			Console.WriteLine(d.DevicePath);
			Console.WriteLine(new string('-', 100));

			Console.WriteLine("  -- HidSharp (what our code can read today) --");
			Console.WriteLine($"    VendorID              : 0x{d.VendorID:X4} ({d.VendorID})");
			Console.WriteLine($"    ProductID             : 0x{d.ProductID:X4} ({d.ProductID})");
			Console.WriteLine($"    ReleaseNumber         : {Try(() => d.ReleaseNumber.ToString())} (bcdDevice 0x{ReleaseBcd(d):X4})");
			Console.WriteLine($"    SerialNumber          : {Try(d.GetSerialNumber)}");
			Console.WriteLine($"    Manufacturer          : {Try(d.GetManufacturer)}");
			Console.WriteLine($"    ProductName           : {Try(d.GetProductName)}");
			Console.WriteLine($"    FriendlyName          : {Try(() => d.GetFriendlyName())}");
			Console.WriteLine($"    FileSystemName        : {Try(() => d.GetFileSystemName())}");
			Console.WriteLine($"    DevicePath            : {d.DevicePath}");
			Console.WriteLine($"    Max report in/out/feat: {Try(() => d.GetMaxInputReportLength().ToString())}/" +
				$"{Try(() => d.GetMaxOutputReportLength().ToString())}/{Try(() => d.GetMaxFeatureReportLength().ToString())}");

			Console.WriteLine("  -- Windows device properties (available, NOT used by us today) --");
			foreach (var (label, key, type) in InterfaceProperties)
				Console.WriteLine($"    {label,-22}: {ReadInterfaceProperty(d.DevicePath, key, type)}");

			Console.WriteLine();
		}

		PrintSummary(devices);
	}

	// ── Summary: can two identical units be told apart, and does identity survive a port move? ──
	private static void PrintSummary(List<HidDevice> devices)
	{
		Console.WriteLine(new string('=', 100));
		Console.WriteLine("SUMMARY — grouped by (VID, PID)");
		Console.WriteLine(new string('=', 100));

		foreach (var group in devices.GroupBy(d => (d.VendorID, d.ProductID)))
		{
			var (vid, pid) = group.Key;
			var serials = group.Select(d => Try(d.GetSerialNumber)).Distinct().ToList();
			var containers = group
				.Select(d => ReadInterfaceProperty(d.DevicePath, DevpkeyDeviceContainerId, PropType.Guid))
				.Distinct().ToList();

			Console.WriteLine($"VID 0x{vid:X4} PID 0x{pid:X4} — {group.Count()} interface(s)");
			Console.WriteLine($"  distinct SerialNumber : {string.Join(" | ", serials)}");
			Console.WriteLine($"  distinct ContainerId  : {string.Join(" | ", containers)}");
			Console.WriteLine($"  -> physical units visible here: {containers.Count(c => !c.StartsWith('<'))}");
		}
	}

	// ── Windows device-interface properties worth reporting ──────────────────────────────────
	private enum PropType { String, Guid, StringList, UInt32 }

	private static readonly (string Label, DevPropKey Key, PropType Type)[] InterfaceProperties =
	[
		("ContainerId", DevpkeyDeviceContainerId, PropType.Guid),
		("InstanceId", DevpkeyDeviceInstanceId, PropType.String),
	];

	private static string ReadInterfaceProperty(string devicePath, DevPropKey key, PropType type)
	{
		if (!OperatingSystem.IsWindows()) return "<non-Windows>";
		try
		{
			uint size = 0;
			CM_Get_Device_Interface_PropertyW(devicePath, ref key, out _, null, ref size, 0);
			if (size == 0) return "<not set>";

			var buffer = new byte[size];
			var cr = CM_Get_Device_Interface_PropertyW(devicePath, ref key, out var propType, buffer, ref size, 0);
			if (cr != 0) return $"<error cr={cr}>";

			return type switch
			{
				PropType.Guid when size == 16 => new Guid(buffer).ToString(),
				PropType.UInt32 when size == 4 => BitConverter.ToUInt32(buffer).ToString(),
				_ => System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Replace("\0", " | "),
			};
		}
		catch (Exception ex)
		{
			return $"<{ex.GetType().Name}>";
		}
	}

	private static string Try(Func<string?> f)
	{
		try
		{
			var v = f();
			return string.IsNullOrWhiteSpace(v) ? "<empty>" : v;
		}
		catch (Exception ex)
		{
			return $"<{ex.GetType().Name}>";
		}
	}

	// bcdDevice is BCD and HidSharp already decodes it into a decimal Major.Minor, so each decimal
	// digit has to go back into its own nibble to recover the raw word the vendor quotes.
	private static int ReleaseBcd(HidDevice d)
	{
		try
		{
			var v = d.ReleaseNumber;
			return ((v.Major / 10) << 12) | ((v.Major % 10) << 8) | ((v.Minor / 10) << 4) | (v.Minor % 10);
		}
		catch { return 0; }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DevPropKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	private static DevPropKey DevpkeyDeviceContainerId => new()
	{
		Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
		Pid = 2,
	};

	private static DevPropKey DevpkeyDeviceInstanceId => new()
	{
		Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
		Pid = 256,
	};

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern int CM_Get_Device_Interface_PropertyW(
		string pszDeviceInterface,
		ref DevPropKey propertyKey,
		out uint propertyType,
		byte[]? propertyBuffer,
		ref uint propertyBufferSize,
		uint ulFlags);
}
