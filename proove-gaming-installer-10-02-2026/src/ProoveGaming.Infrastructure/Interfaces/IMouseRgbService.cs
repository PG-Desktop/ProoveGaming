using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Interfaces;

// Bound to a single physical mouse (constructed with its HidDevice). Implementations encode
// the wire protocol for one device family — e.g. EasenMouseService for the Holtek
// HT68FB571-based mice (BLAZE/X43), TelinkMouseService for TAIPAN. Adds the mouse-only settings
// on top of the shared RGB capability in IDeviceRgbService. A device that does not (yet) support
// a given member returns false / no-ops rather than throwing.
public interface IMouseRgbService : IDeviceRgbService
{
	/// <summary>
	/// Write up to 8 DPI stage values to param_1 block 0. Per-stage indicator colours on
	/// DpiStageParams are ignored (not written to the device — kept for UI display only).
	/// </summary>
	Task<bool> SetDpiStagesAsync(DpiStageParams[] stages);

	/// <summary>
	/// Switch the firmware's active DPI stage (0-7) by patching param_0 byte 1.
	/// </summary>
	Task<bool> SetActiveDpiStageAsync(byte stageIndex);

	/// <summary>
	/// Set the USB report (polling) rate. Accepts 125, 250, 500 or 1000 Hz.
	/// </summary>
	Task<bool> SetReportRateAsync(int hz);

	/// <summary>
	/// Ask the device to report its current state (active DPI stage, report rate,
	/// LED effect) by sending the 0x80 query command. The firmware answers by pushing
	/// a 0x05 notification on its vendor collection, which the DpiStageMonitor reads.
	/// This is the only way to learn the active DPI stage on startup — the device
	/// returns zeros to feature reads. See docs/mouse/blaze/dpi-stage.md.
	/// </summary>
	Task<bool> RequestCurrentStateAsync();

	/// <summary>
	/// Assign functions to the device's remappable physical buttons. Receives the full desired
	/// set; the implementation rebuilds the device's button table from its factory baseline,
	/// overlays the requested buttons and leaves the rest untouched, then writes it. Callers
	/// persist the set themselves (the source of truth is the App's store, not the device).
	/// </summary>
	Task<bool> SetButtonBindingsAsync(IReadOnlyList<ButtonBinding> bindings);

	/// <summary>
	/// Apply the full desired mouse configuration (RGB, bindings, settings, DPI table, active stage,
	/// report rate) in ONE coordinated device write, instead of the per-aspect calls above. Members
	/// left null on <paramref name="config"/> are not touched. Used by the App's preset-apply path.
	/// </summary>
	Task<bool> ApplyConfigurationAsync(DeviceConfiguration config);

	/// <summary>
	/// Align the cached param_0 (report rate, DPI stage, LED effect) with what the
	/// device actually reports on its 0x05 vendor collection. Physical DPI / polling /
	/// LED buttons change device state without going through us, so without this a
	/// later param_0 write (e.g. a UI report-rate change) would clobber the real active
	/// stage/effect with a stale cache. Fed by DpiStageMonitor on every 0x05 report.
	/// </summary>
	void SyncStateFromDevice(byte reportRateRaw, byte dpiStage, byte ledEffect);
}
