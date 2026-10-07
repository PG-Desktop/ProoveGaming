using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Pixel-space bounds (relative to a fixed keyboard-image canvas) for each Rampage physical key.
/// </summary>
/// <remarks>
/// <para>
/// Rampage is a standard TKL board: a 15-unit main block, a three-column navigation block
/// (Insert/Home/PageUp over Delete/End/PageDown), an inverted-T arrow cluster, and PrintScreen /
/// ScrollLock / Pause across the top right. Coordinates are "unit keys" × <see cref="UnitSize"/>,
/// same convention as <see cref="RasterVisualLayout"/> and <see cref="TRacerVisualLayout"/>.
/// </para>
/// <para>
/// Positions were originally <b>derived</b> from the vendor's factory matrix interpreted as a TKL
/// grid; the navigation block has since been corrected against the vendor artwork and its legend
/// layer (the first guess placed the six nav keys as a two-column block — the artwork shows the
/// standard three-column arrangement). Nothing functional depends on these numbers: firmware slots
/// come from <see cref="RampageKeyCatalog"/> and are independent of pixel positions.
/// </para>
/// <para>
/// The gap at unit x=11.25 on the bottom row is intentional: that is the FN key, which is not
/// remappable and therefore gets no overlay.
/// </para>
/// </remarks>
public static class RampageVisualLayout
{
	public const double UnitSize = 34.0;
	public const double CanvasWidth = 18.25 * UnitSize; // 620.5
	public const double CanvasHeight = 6.25 * UnitSize; // 212.5

	// Row baselines. The function row is separated from the number row by a quarter-unit gutter.
	private const double RowFunction = 0;
	private const double RowNumber = 1.25;
	private const double RowAlpha1 = 2.25;
	private const double RowAlpha2 = 3.25;
	private const double RowAlpha3 = 4.25;
	private const double RowBottom = 5.25;

	// The navigation cluster occupies these two columns; Pause and the right arrow use a third.
	private const double NavColumn1 = 15.25;
	private const double NavColumn2 = 16.25;
	private const double NavColumn3 = 17.25;

	public static IReadOnlyDictionary<string, RasterKeyBounds> Layout { get; } =
		new Dictionary<string, RasterKeyBounds>
		{
			// Function row
			["Escape"] = U(0, RowFunction, 1, 1),
			["F1"] = U(2, RowFunction, 1, 1),
			["F2"] = U(3, RowFunction, 1, 1),
			["F3"] = U(4, RowFunction, 1, 1),
			["F4"] = U(5, RowFunction, 1, 1),
			["F5"] = U(6.5, RowFunction, 1, 1),
			["F6"] = U(7.5, RowFunction, 1, 1),
			["F7"] = U(8.5, RowFunction, 1, 1),
			["F8"] = U(9.5, RowFunction, 1, 1),
			["F9"] = U(11, RowFunction, 1, 1),
			["F10"] = U(12, RowFunction, 1, 1),
			["F11"] = U(13, RowFunction, 1, 1),
			["F12"] = U(14, RowFunction, 1, 1),
			["PrintScreen"] = U(NavColumn1, RowFunction, 1, 1),
			["ScrollLock"] = U(NavColumn2, RowFunction, 1, 1),
			["Pause"] = U(NavColumn3, RowFunction, 1, 1),

			// Number row
			["Grave"] = U(0, RowNumber, 1, 1),
			["D1"] = U(1, RowNumber, 1, 1),
			["D2"] = U(2, RowNumber, 1, 1),
			["D3"] = U(3, RowNumber, 1, 1),
			["D4"] = U(4, RowNumber, 1, 1),
			["D5"] = U(5, RowNumber, 1, 1),
			["D6"] = U(6, RowNumber, 1, 1),
			["D7"] = U(7, RowNumber, 1, 1),
			["D8"] = U(8, RowNumber, 1, 1),
			["D9"] = U(9, RowNumber, 1, 1),
			["D0"] = U(10, RowNumber, 1, 1),
			["Minus"] = U(11, RowNumber, 1, 1),
			["Equal"] = U(12, RowNumber, 1, 1),
			["Backspace"] = U(13, RowNumber, 2, 1),
			["Insert"] = U(NavColumn1, RowNumber, 1, 1),
			["Home"] = U(NavColumn2, RowNumber, 1, 1),
			["PageUp"] = U(NavColumn3, RowNumber, 1, 1),

			// Alpha row 1
			["Tab"] = U(0, RowAlpha1, 1.5, 1),
			["Q"] = U(1.5, RowAlpha1, 1, 1),
			["W"] = U(2.5, RowAlpha1, 1, 1),
			["E"] = U(3.5, RowAlpha1, 1, 1),
			["R"] = U(4.5, RowAlpha1, 1, 1),
			["T"] = U(5.5, RowAlpha1, 1, 1),
			["Y"] = U(6.5, RowAlpha1, 1, 1),
			["U"] = U(7.5, RowAlpha1, 1, 1),
			["I"] = U(8.5, RowAlpha1, 1, 1),
			["O"] = U(9.5, RowAlpha1, 1, 1),
			["P"] = U(10.5, RowAlpha1, 1, 1),
			["BracketLeft"] = U(11.5, RowAlpha1, 1, 1),
			["BracketRight"] = U(12.5, RowAlpha1, 1, 1),
			["Backslash"] = U(13.5, RowAlpha1, 1.5, 1),
			["Delete"] = U(NavColumn1, RowAlpha1, 1, 1),
			["End"] = U(NavColumn2, RowAlpha1, 1, 1),
			["PageDown"] = U(NavColumn3, RowAlpha1, 1, 1),

			// Alpha row 2
			["CapsLock"] = U(0, RowAlpha2, 1.75, 1),
			["A"] = U(1.75, RowAlpha2, 1, 1),
			["S"] = U(2.75, RowAlpha2, 1, 1),
			["D"] = U(3.75, RowAlpha2, 1, 1),
			["F"] = U(4.75, RowAlpha2, 1, 1),
			["G"] = U(5.75, RowAlpha2, 1, 1),
			["H"] = U(6.75, RowAlpha2, 1, 1),
			["J"] = U(7.75, RowAlpha2, 1, 1),
			["K"] = U(8.75, RowAlpha2, 1, 1),
			["L"] = U(9.75, RowAlpha2, 1, 1),
			["Semicolon"] = U(10.75, RowAlpha2, 1, 1),
			["Quote"] = U(11.75, RowAlpha2, 1, 1),
			["Enter"] = U(12.75, RowAlpha2, 2.25, 1),

			// Alpha row 3
			["LeftShift"] = U(0, RowAlpha3, 2.25, 1),
			["Z"] = U(2.25, RowAlpha3, 1, 1),
			["X"] = U(3.25, RowAlpha3, 1, 1),
			["C"] = U(4.25, RowAlpha3, 1, 1),
			["V"] = U(5.25, RowAlpha3, 1, 1),
			["B"] = U(6.25, RowAlpha3, 1, 1),
			["N"] = U(7.25, RowAlpha3, 1, 1),
			["M"] = U(8.25, RowAlpha3, 1, 1),
			["Comma"] = U(9.25, RowAlpha3, 1, 1),
			["Period"] = U(10.25, RowAlpha3, 1, 1),
			["Slash"] = U(11.25, RowAlpha3, 1, 1),
			["RightShift"] = U(12.25, RowAlpha3, 2.75, 1),
			["Up"] = U(NavColumn2, RowAlpha3, 1, 1),

			// Bottom row. FN sits at x=11.25 and is deliberately not emitted.
			["LeftCtrl"] = U(0, RowBottom, 1.25, 1),
			["LeftWin"] = U(1.25, RowBottom, 1.25, 1),
			["LeftAlt"] = U(2.5, RowBottom, 1.25, 1),
			["Space"] = U(3.75, RowBottom, 6.25, 1),
			["RightAlt"] = U(10, RowBottom, 1.25, 1),
			["Menu"] = U(12.5, RowBottom, 1.25, 1),
			["RightCtrl"] = U(13.75, RowBottom, 1.25, 1),
			["Left"] = U(NavColumn1, RowBottom, 1, 1),
			["Down"] = U(NavColumn2, RowBottom, 1, 1),
			["Right"] = U(NavColumn3, RowBottom, 1, 1),
		};

	private static RasterKeyBounds U(double x, double y, double w, double h) =>
		new(x * UnitSize, y * UnitSize, w * UnitSize, h * UnitSize);
}
