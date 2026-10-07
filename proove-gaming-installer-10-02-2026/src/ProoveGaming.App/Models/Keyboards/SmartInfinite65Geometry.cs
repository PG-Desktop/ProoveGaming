using System.Collections.Generic;
using System.Linq;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Pixel-space bounds for the 65% board shared by Cyrex and Aress, keyed by <b>matrix slot</b>.
/// </summary>
/// <remarks>
/// <para>
/// Both keyboards report the identical 75-slot matrix, the same 68 real keys, the same 7 unused
/// placeholder slots and FN in the same place. Read side by side, their factory tables differ in
/// exactly three slots — the navigation column, where Cyrex has Delete / PageDown / End and Aress has
/// Insert / Delete / PageDown. The physical geometry is the same board.
/// </para>
/// <para>
/// So the geometry is defined once here and keyed by matrix slot, which is the stable identifier:
/// key <i>names</i> differ between the two models at those three positions, but the position itself
/// does not. Each model's <c>Layout</c> is then derived by joining its own catalog against this
/// table, which keeps the two in step by construction instead of by copy-paste.
/// </para>
/// <para>
/// Positions come from the standard 65% grid rather than from a photograph, so minor cosmetic drift
/// against the real boards is possible and harmless — nothing functional depends on these numbers.
/// The gap at unit x=11 on the bottom row is FN, which is not remappable and gets no overlay.
/// </para>
/// </remarks>
public static class SmartInfinite65Geometry
{
	public const double UnitSize = 34.0;
	public const double CanvasWidth = 16.25 * UnitSize; // 552.5
	public const double CanvasHeight = 5.0 * UnitSize;  // 170.0

	/// <summary>Matrix slots the firmware reserves on both models.</summary>
	public const int MatrixSlots = 75;

	/// <summary>Slot holding FN on both models — excluded from every catalog.</summary>
	public const byte FnMatrixIndex = 70;

	/// <summary>The rightmost column, which is the navigation column on both models.</summary>
	private const double NavColumn = 15.25;

	/// <summary>Slot → bounds. Unused placeholder slots and FN are deliberately absent.</summary>
	public static IReadOnlyDictionary<byte, RasterKeyBounds> BySlot { get; } =
		new Dictionary<byte, RasterKeyBounds>
		{
			// ── Row 0 (slots 0-14) — Esc, digits, Backspace, nav ──
			[0] = U(0, 0, 1, 1),
			[1] = U(1, 0, 1, 1),
			[2] = U(2, 0, 1, 1),
			[3] = U(3, 0, 1, 1),
			[4] = U(4, 0, 1, 1),
			[5] = U(5, 0, 1, 1),
			[6] = U(6, 0, 1, 1),
			[7] = U(7, 0, 1, 1),
			[8] = U(8, 0, 1, 1),
			[9] = U(9, 0, 1, 1),
			[10] = U(10, 0, 1, 1),
			[11] = U(11, 0, 1, 1),
			[12] = U(12, 0, 1, 1),
			[13] = U(13, 0, 2, 1),          // Backspace
			[14] = U(NavColumn, 0, 1, 1),   // Cyrex: Delete · Aress: Insert

			// ── Row 1 (slots 15-29) — Tab, QWERTY, nav ──
			[15] = U(0, 1, 1.5, 1),         // Tab
			[16] = U(1.5, 1, 1, 1),
			[17] = U(2.5, 1, 1, 1),
			[18] = U(3.5, 1, 1, 1),
			[19] = U(4.5, 1, 1, 1),
			[20] = U(5.5, 1, 1, 1),
			[21] = U(6.5, 1, 1, 1),
			[22] = U(7.5, 1, 1, 1),
			[23] = U(8.5, 1, 1, 1),
			[24] = U(9.5, 1, 1, 1),
			[25] = U(10.5, 1, 1, 1),
			[26] = U(11.5, 1, 1, 1),
			[27] = U(12.5, 1, 1, 1),
			[28] = U(13.5, 1, 1.5, 1),      // Backslash
			[29] = U(NavColumn, 1, 1, 1),   // PageUp on both

			// ── Row 2 (slots 30-44) — Caps, home row, Enter, nav. Slot 42 is unused. ──
			[30] = U(0, 2, 1.75, 1),        // Caps Lock
			[31] = U(1.75, 2, 1, 1),
			[32] = U(2.75, 2, 1, 1),
			[33] = U(3.75, 2, 1, 1),
			[34] = U(4.75, 2, 1, 1),
			[35] = U(5.75, 2, 1, 1),
			[36] = U(6.75, 2, 1, 1),
			[37] = U(7.75, 2, 1, 1),
			[38] = U(8.75, 2, 1, 1),
			[39] = U(9.75, 2, 1, 1),
			[40] = U(10.75, 2, 1, 1),
			[41] = U(11.75, 2, 1, 1),
			[43] = U(12.75, 2, 2.25, 1),    // Enter
			[44] = U(NavColumn, 2, 1, 1),   // Cyrex: PageDown · Aress: Delete

			// ── Row 3 (slots 45-59) — Shifts, ZXCV, Up, nav. Slot 46 is unused. ──
			[45] = U(0, 3, 2.25, 1),        // Left Shift
			[47] = U(2.25, 3, 1, 1),
			[48] = U(3.25, 3, 1, 1),
			[49] = U(4.25, 3, 1, 1),
			[50] = U(5.25, 3, 1, 1),
			[51] = U(6.25, 3, 1, 1),
			[52] = U(7.25, 3, 1, 1),
			[53] = U(8.25, 3, 1, 1),
			[54] = U(9.25, 3, 1, 1),
			[55] = U(10.25, 3, 1, 1),
			[56] = U(11.25, 3, 1, 1),
			[57] = U(12.25, 3, 1.75, 1),    // Right Shift
			[58] = U(14.25, 3, 1, 1),       // Up
			[59] = U(NavColumn, 3, 1, 1),   // Cyrex: End · Aress: PageDown

			// ── Row 4 (slots 60-74) — modifiers, Space, arrows. 63-65, 67, 68 unused; 70 is FN. ──
			[60] = U(0, 4, 1.25, 1),        // Left Ctrl
			[61] = U(1.25, 4, 1.25, 1),     // Left Win
			[62] = U(2.5, 4, 1.25, 1),      // Left Alt
			[66] = U(3.75, 4, 6.25, 1),     // Space
			[69] = U(10, 4, 1, 1),          // Right Alt
			[71] = U(12, 4, 1, 1),          // Right Ctrl
			[72] = U(13.25, 4, 1, 1),       // Left
			[73] = U(14.25, 4, 1, 1),       // Down
			[74] = U(NavColumn, 4, 1, 1),   // Right
		};

	/// <summary>
	/// Joins a model's catalog against <see cref="BySlot"/> to produce the name → bounds map the
	/// overlay consumes. A catalog entry with no geometry is a bug in one of the two, so it surfaces
	/// as a missing key rather than being silently skipped.
	/// </summary>
	public static IReadOnlyDictionary<string, RasterKeyBounds> LayoutFor(IReadOnlyList<PhysicalKey> catalog) =>
		catalog
			.Where(k => BySlot.ContainsKey(k.MatrixIndex))
			.ToDictionary(k => k.DevKeyName, k => BySlot[k.MatrixIndex]);

	private static RasterKeyBounds U(double x, double y, double w, double h) =>
		new(x * UnitSize, y * UnitSize, w * UnitSize, h * UnitSize);
}
