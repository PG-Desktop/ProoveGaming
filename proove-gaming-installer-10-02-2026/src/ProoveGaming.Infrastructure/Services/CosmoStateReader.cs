using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Services;

// Reads the PG Cosmo headset's battery and power state.
//
// The dongle pushes nothing on report 0xBC (proved by a full guided capture), so the state is
// fetched with HidD_GetInputReport — a USB control-transfer READ of the input report. This is not
// a host->device write: nothing is sent to the vendor channel, so none of the risk that applies to
// writing undocumented commands applies here.
//
// Pure P/Invoke with no state, matching the convention in DeviceContainerId. Cheap enough to call
// on the existing battery-poll path rather than needing a background monitor of its own.
public static class CosmoStateReader
{
	public const int VendorId = 0x0D8C;
	public const int ProductId = 0x0334;

	private const byte VendorReportId = 0xBC;
	private const int ReportLength = 32;

	public static bool Matches(int vendorId, int productId)
		=> vendorId == VendorId && productId == ProductId;

	// Returns null when the device is absent, cannot be opened, or does not answer.
	public static CosmoStateReport? TryRead(string devicePath)
	{
		if (string.IsNullOrEmpty(devicePath) || !OperatingSystem.IsWindows())
			return null;

		SafeFileHandle? handle = null;
		try
		{
			// Shared read/write: the DeviceHost and Windows both hold this interface open, so an
			// exclusive open would fail. We only read.
			handle = CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

			if (handle.IsInvalid)
				return null;

			var buffer = new byte[ReportLength];
			buffer[0] = VendorReportId;

			return HidD_GetInputReport(handle, buffer, buffer.Length)
				? CosmoStateReport.TryParse(buffer)
				: null;
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

	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareRead = 1;
	private const uint FileShareWrite = 2;
	private const uint OpenExisting = 3;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
