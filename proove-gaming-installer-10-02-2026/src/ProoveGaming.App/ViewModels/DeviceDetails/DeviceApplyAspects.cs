using System;

namespace ProoveGaming.App.ViewModels;

/// <summary>
/// Which parts of a device's configuration an apply is carrying. Anything not flagged is sent as
/// null — the configuration frame's existing contract for "leave this alone".
/// </summary>
/// <remarks>
/// This exists for a hardware reason, not tidiness. On BLAZE every block of a configuration write is
/// a FLASH commit, so pushing the whole state after a colour change re-commits the key matrix and
/// the DPI table too. Clicking "Random lighting" nine times in half a minute did exactly that and the
/// mouse came back with left and right click dead (2026-08-17 host log; see
/// <c>docs/mouse/blaze/key-matrix.md</c>).
///
/// Only the debounced auto-apply narrows what it sends. Preset switch, Save Preset, Discard and the
/// connect-time restore all pass <see cref="All"/>, because "make the device match this state" is
/// precisely what those actions mean.
/// </remarks>
[Flags]
internal enum DeviceApplyAspects
{
	None = 0,
	Led = 1 << 0,
	Buttons = 1 << 1,
	Settings = 1 << 2,
	DpiStages = 1 << 3,
	ReportRate = 1 << 4,
	All = Led | Buttons | Settings | DpiStages | ReportRate,
}
