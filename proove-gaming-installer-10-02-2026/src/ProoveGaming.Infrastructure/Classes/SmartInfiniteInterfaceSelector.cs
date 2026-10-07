using HidSharp;
using Microsoft.Extensions.Logging;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Picks the writable vendor-config HID interface on a Smart Infinite keyboard (Rampage, Aress, Cyrex).
/// </summary>
/// <remarks>
/// <para>
/// These devices expose several vendor-defined interfaces with an identical 64/64 report shape, and
/// only one of them accepts writes. Choosing by shape alone therefore reduces to enumeration order —
/// observed in the field as <c>ERROR_INVALID_FUNCTION</c> ("Incorrect function") on every write,
/// because the enumerator happened to return an input-only collection first.
/// </para>
/// <para>
/// The vendor's own enumeration code matches on HID usage <c>0x20</c> / usage page <c>0xFF87</c> in
/// addition to VID and PID. That is the rule used here, with the shape heuristic kept only as a last
/// resort for a device whose report descriptor cannot be parsed.
/// </para>
/// </remarks>
public static class SmartInfiniteInterfaceSelector
{
	public const int VendorUsagePage = 0xFF87; // 65415
	public const int VendorUsageId = 0x20;     // 32

	private const int VendorReportLength = 64;

	/// <summary>Returns the vendor-config interface for the given VID/PID, or <c>null</c>.</summary>
	/// <param name="logTag">Prefix for log lines, e.g. "KEY Cyrex".</param>
	public static HidDevice? Select(
		IEnumerable<HidDevice> devices,
		int vendorId,
		int productId,
		ILogger? logger,
		string logTag)
	{
		// Inspect each candidate once. Parsing a report descriptor is not free, and this runs on every
		// read, every write and every RGB change — a separate pass just to build the log line would
		// double it. Descriptor probing at Debug for the same reason: at Information it grew with user
		// activity and buried the transport diagnostics the log exists for.
		//
		// Non-Bluetooth paths sort first: a board connected over USB and BLE at once can expose the
		// vendor collection on both, and the cable is the faster, proven link — it must stay the
		// write target whenever it is present.
		var matching = devices
			.Where(d => d.VendorID == vendorId && d.ProductID == productId)
			.OrderBy(d => IsBluetoothDevicePath(d.DevicePath) ? 1 : 0)
			.ThenBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.Select(d => new
			{
				Device = d,
				HasVendorUsage = HasVendorConfigUsage(d, logger, logTag),
				OutLength = SafeReportLength(d, output: true),
				InLength = SafeReportLength(d, output: false),
			})
			.ToList();

		foreach (var candidate in matching)
			logger?.LogDebug("[{Tag}] candidate out={Out} in={In} vendorUsage={Usage} path={Path}",
				logTag, candidate.OutLength, candidate.InLength,
				candidate.HasVendorUsage, candidate.Device.DevicePath);

		var device = matching.FirstOrDefault(c => c.HasVendorUsage)?.Device;
		if (device != null)
		{
			logger?.LogInformation("[{Tag}] selected path={Path}", logTag, device.DevicePath);
			return device;
		}

		device = matching
			.FirstOrDefault(c => c.OutLength == VendorReportLength && c.InLength == VendorReportLength)
			?.Device;

		if (device != null)
		{
			logger?.LogWarning(
				"[{Tag}] usage 0x{Usage:X2}/page 0x{Page:X4} matched nothing; falling back to the 64/64 shape heuristic on {Path}",
				logTag, VendorUsageId, VendorUsagePage, device.DevicePath);
			return device;
		}

		logger?.LogWarning("[{Tag}] no vendor interface found for VID={VID} PID={PID} ({N} candidates)",
			logTag, vendorId, productId, matching.Count);
		return null;
	}

	// The HID service UUID (0x1812) expanded to the full Bluetooth base-UUID form. Windows builds
	// HID-over-GATT device paths from it: hid#{00001812-...}_dev_vid&...  — observed on the real
	// Rampage over BLE (see docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md).
	private const string HogpServiceUuid = "{00001812-0000-1000-8000-00805f9b34fb}";

	// A HID interface reached over the OS Bluetooth stack: classic Bluetooth enumerates under
	// BTHENUM, Bluetooth LE under BTHLEDEVICE or (current Windows 11) under a path built from the
	// HOGP service UUID. Everything else (USB, dongle) has none of the markers.
	internal static bool IsBluetoothDevicePath(string? devicePath) =>
		devicePath != null &&
		(devicePath.Contains("bthenum", StringComparison.OrdinalIgnoreCase) ||
		 devicePath.Contains("bthle", StringComparison.OrdinalIgnoreCase) ||
		 devicePath.Contains(HogpServiceUuid, StringComparison.OrdinalIgnoreCase));

	public static bool HasVendorConfigUsage(HidDevice device, ILogger? logger = null, string logTag = "HID")
	{
		try
		{
			// HidSharp packs a usage as (usagePage << 16) | usageId.
			return device.GetReportDescriptor().DeviceItems
				.SelectMany(item => item.Usages.GetAllValues())
				.Any(usage => (usage >> 16) == VendorUsagePage && (usage & 0xFFFF) == VendorUsageId);
		}
		catch (Exception ex)
		{
			// Some collections refuse to yield a report descriptor; that only means this candidate
			// cannot be confirmed, not that anything is broken.
			logger?.LogDebug("[{Tag}] could not read report descriptor for {Path}: {Msg}",
				logTag, device.DevicePath, ex.Message);
			return false;
		}
	}

	private static int SafeReportLength(HidDevice device, bool output)
	{
		try { return output ? device.GetMaxOutputReportLength() : device.GetMaxInputReportLength(); }
		catch { return -1; }
	}
}
