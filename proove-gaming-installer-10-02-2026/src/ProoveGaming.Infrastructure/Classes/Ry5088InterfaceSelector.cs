using HidSharp;
using Microsoft.Extensions.Logging;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Selects the RY5088 vendor interface for the T800, shared by every factory that talks to it.
/// </summary>
/// <remarks>
/// T800 speaks RY5088 over HID <b>feature</b> reports on <c>mi_02</c> — no output endpoint at
/// all — so it is selected on feature-report length rather than the out/in shape the other
/// keyboard families use. The board also exposes vendor-defined collections under <c>mi_01</c>
/// that can present a feature report of the same size; path order alone would pick between
/// them arbitrarily (the trap <see cref="SmartInfiniteInterfaceSelector"/> exists to avoid),
/// so <c>mi_02</c> is matched explicitly and the bare length match is kept only as a fallback.
/// </remarks>
public static class Ry5088InterfaceSelector
{
	/// <param name="tag">Log prefix of the calling subsystem (<c>KEY</c>, <c>ACT</c>, <c>MACRO</c>…).</param>
	public static HidDevice? Select(
		IEnumerable<HidDevice> devices, int vendorId, int productId, ILogger? logger, string tag)
	{
		var candidates = devices
			.Where(d => d.VendorID == vendorId && d.ProductID == productId &&
			            SafeFeatureReportLength(d) == Ry5088Protocol.ReportLength)
			.OrderBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var device = candidates
			.FirstOrDefault(d => d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true)
			?? candidates.FirstOrDefault();

		if (device == null)
		{
			logger?.LogWarning("[{Tag}] T800 vendor feature interface (featLen={Len}) not found for VID={VID} PID={PID}",
				tag, Ry5088Protocol.ReportLength, vendorId, productId);
			return null;
		}

		logger?.LogInformation("[{Tag}] T800 selected path={Path}", tag, device.DevicePath);
		return device;
	}

	private static int SafeFeatureReportLength(HidDevice device)
	{
		try { return device.GetMaxFeatureReportLength(); }
		catch { return -1; }
	}
}
