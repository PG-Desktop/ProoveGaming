using System.Runtime.InteropServices;
using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// One-off probe: with two BLAZE units plugged in, dump each HID interface's DevicePath, Container
	// ID (same DEVPKEY_Device_ContainerId lookup ProoveGaming.Infrastructure.Services.DeviceContainerId
	// uses for the app's per-device DeviceKey) and hardware SerialNumber, so we can tell whether BLAZE
	// reports a real per-unit serial that could replace the port-derived Container ID as the identity
	// key (see docs/plans/add-per-device-identity's deferred "Serial check" open question).
	public static class BlazeIdentityProbe
	{
		public static void Run()
		{
			var devices = DeviceList.Local.GetHidDevices(X43Finder.VendorId, X43Finder.ProductId).ToList();
			Console.WriteLine($"Found {devices.Count} BLAZE HID interface(s) (VID=0x{X43Finder.VendorId:X4} PID=0x{X43Finder.ProductId:X4})");
			Console.WriteLine();

			foreach (var device in devices)
			{
				Console.WriteLine($"DevicePath: {device.DevicePath}");

				var containerId = TryGetContainerId(device.DevicePath);
				Console.WriteLine($"  ContainerId: {containerId ?? "(unavailable)"}");

				var serial = TryRead(() => device.GetSerialNumber());
				Console.WriteLine($"  SerialNumber: {(string.IsNullOrEmpty(serial) ? "(empty/unavailable)" : serial)}");

				var manufacturer = TryRead(() => device.GetManufacturer());
				var product = TryRead(() => device.GetProductName());
				Console.WriteLine($"  Manufacturer: {manufacturer ?? "(unavailable)"}  ProductName: {product ?? "(unavailable)"}");

				Console.WriteLine();
			}
		}

		private static string? TryRead(Func<string> read)
		{
			try { return read(); }
			catch { return null; }
		}

		// Inlined copy of ProoveGaming.Infrastructure.Services.DeviceContainerId.TryGet — this probe
		// project doesn't reference Infrastructure, and it's throwaway diagnostic code.
		private static string? TryGetContainerId(string devicePath)
		{
			if (string.IsNullOrEmpty(devicePath) || !OperatingSystem.IsWindows())
				return null;

			try
			{
				var key = DevpkeyDeviceContainerId;
				var buffer = new byte[16];
				var size = (uint)buffer.Length;

				var cr = CM_Get_Device_Interface_PropertyW(devicePath, ref key, out var propType, buffer, ref size, 0);
				if (cr != CrSuccess || propType != DevpropTypeGuid || size != 16)
					return null;

				return new Guid(buffer).ToString();
			}
			catch
			{
				return null;
			}
		}

		private const int CrSuccess = 0;
		private const uint DevpropTypeGuid = 0x0000000D;

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

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
		private static extern int CM_Get_Device_Interface_PropertyW(
			string pszDeviceInterface,
			ref DevPropKey propertyKey,
			out uint propertyType,
			byte[] propertyBuffer,
			ref uint propertyBufferSize,
			uint ulFlags);
	}
}
