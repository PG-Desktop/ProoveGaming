using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Pixel-space bounds (relative to a fixed keyboard-image canvas) for each T-Racer physical key.
/// </summary>
/// <remarks>
/// Same 75% footprint and unit grid as <see cref="RasterVisualLayout"/> — the two keyboards
/// share the vendor family and physical dimensions. The single difference is that T-Racer has
/// a dedicated <c>PrintScreen</c> key (Raster's top-right slot is a printed logo instead), so
/// this layout emits one extra overlay.
/// </remarks>
public static class TRacerVisualLayout
{
	public const double UnitSize = 34.0;
	public const double CanvasWidth = 19.56 * UnitSize; // 664.56
	public const double CanvasHeight = 6.5 * UnitSize; // 221.0

	public static IReadOnlyDictionary<string, RasterKeyBounds> Layout { get; } =
		new Dictionary<string, RasterKeyBounds>
		{
			// Function row
			["Escape"] = U(0, 0, 1, 1),
			["F1"] = U(1.25, 0, 1, 1),
			["F2"] = U(2.25, 0, 1, 1),
			["F3"] = U(3.25, 0, 1, 1),
			["F4"] = U(4.25, 0, 1, 1),
			["F5"] = U(5.5, 0, 1, 1),
			["F6"] = U(6.5, 0, 1, 1),
			["F7"] = U(7.5, 0, 1, 1),
			["F8"] = U(8.5, 0, 1, 1),
			["F9"] = U(9.75, 0, 1, 1),
			["F10"] = U(10.75, 0, 1, 1),
			["F11"] = U(11.75, 0, 1, 1),
			["F12"] = U(12.75, 0, 1, 1),
			// T-Racer-only. Placeholder position — top-right of the F-row, where the knob or
			// logo typically sits on 75% boards. Verify against a photo of the actual device.
			["PrintScreen"] = U(14, 0, 1, 1),

			// Number row + Del
			["Grave"] = U(0, 1.25, 1, 1),
			["1"] = U(1, 1.25, 1, 1),
			["2"] = U(2, 1.25, 1, 1),
			["3"] = U(3, 1.25, 1, 1),
			["4"] = U(4, 1.25, 1, 1),
			["5"] = U(5, 1.25, 1, 1),
			["6"] = U(6, 1.25, 1, 1),
			["7"] = U(7, 1.25, 1, 1),
			["8"] = U(8, 1.25, 1, 1),
			["9"] = U(9, 1.25, 1, 1),
			["0"] = U(10, 1.25, 1, 1),
			["Minus"] = U(11, 1.25, 1, 1),
			["Equal"] = U(12, 1.25, 1, 1),
			["Backspace"] = U(13, 1.25, 2, 1),
			["Del"] = U(15, 1.25, 1, 1),

			// Top alpha row + PgUp
			["Tab"] = U(0, 2.25, 1.5, 1),
			["Q"] = U(1.5, 2.25, 1, 1),
			["W"] = U(2.5, 2.25, 1, 1),
			["E"] = U(3.5, 2.25, 1, 1),
			["R"] = U(4.5, 2.25, 1, 1),
			["T"] = U(5.5, 2.25, 1, 1),
			["Y"] = U(6.5, 2.25, 1, 1),
			["U"] = U(7.5, 2.25, 1, 1),
			["I"] = U(8.5, 2.25, 1, 1),
			["O"] = U(9.5, 2.25, 1, 1),
			["P"] = U(10.5, 2.25, 1, 1),
			["LeftBracket"] = U(11.5, 2.25, 1, 1),
			["RightBracket"] = U(12.5, 2.25, 1, 1),
			["Backslash"] = U(13.5, 2.25, 1.5, 1),
			["PgUp"] = U(15, 2.25, 1, 1),

			// Mid alpha row + PgDn
			["CapsLock"] = U(0, 3.25, 1.75, 1),
			["A"] = U(1.75, 3.25, 1, 1),
			["S"] = U(2.75, 3.25, 1, 1),
			["D"] = U(3.75, 3.25, 1, 1),
			["F"] = U(4.75, 3.25, 1, 1),
			["G"] = U(5.75, 3.25, 1, 1),
			["H"] = U(6.75, 3.25, 1, 1),
			["J"] = U(7.75, 3.25, 1, 1),
			["K"] = U(8.75, 3.25, 1, 1),
			["L"] = U(9.75, 3.25, 1, 1),
			["Semicolon"] = U(10.75, 3.25, 1, 1),
			["Apostrophe"] = U(11.75, 3.25, 1, 1),
			["Enter"] = U(12.75, 3.25, 2.25, 1),
			["PgDn"] = U(15, 3.25, 1, 1),

			// Bottom alpha row + Up + End
			["LeftShift"] = U(0, 4.25, 2.25, 1),
			["Z"] = U(2.25, 4.25, 1, 1),
			["X"] = U(3.25, 4.25, 1, 1),
			["C"] = U(4.25, 4.25, 1, 1),
			["V"] = U(5.25, 4.25, 1, 1),
			["B"] = U(6.25, 4.25, 1, 1),
			["N"] = U(7.25, 4.25, 1, 1),
			["M"] = U(8.25, 4.25, 1, 1),
			["Comma"] = U(9.25, 4.25, 1, 1),
			["Period"] = U(10.25, 4.25, 1, 1),
			["Slash"] = U(11.25, 4.25, 1, 1),
			["RightShift"] = U(12.25, 4.25, 1.75, 1),
			["Up"] = U(14, 4.25, 1, 1),
			["End"] = U(15, 4.25, 1, 1),

			// Bottom row
			["LeftCtrl"] = U(0, 5.25, 1.25, 1),
			["Win"] = U(1.25, 5.25, 1.25, 1),
			["LeftAlt"] = U(2.5, 5.25, 1.25, 1),
			["Space"] = U(3.75, 5.25, 6.25, 1),
			["RightAlt"] = U(10, 5.25, 1, 1),
			["Left"] = U(13, 5.25, 1, 1),
			["Down"] = U(14, 5.25, 1, 1),
			["Right"] = U(15, 5.25, 1, 1),
		};

	private static RasterKeyBounds U(double x, double y, double w, double h) =>
		new(x * UnitSize, y * UnitSize, w * UnitSize, h * UnitSize);
}
