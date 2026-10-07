using System;
using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// The T800 physical-key catalog and its factory USB HID key mapping.
/// </summary>
/// <remarks>
/// <para>
/// Vendor identifiers: VID <c>12625</c> (<c>0x3151</c>), PID <c>20525</c> (<c>0x502D</c>). T800 is a
/// magnetic-axis 65% board on the Rongyuan RY5088 MCU — a different protocol family from both the
/// Smart Infinite boards and Raster.
/// </para>
/// <para>
/// <b>Every entry below was generated from the device's own factory key table</b>, read over the
/// vendor interface with <c>GET_KEYMATRIX (0x8A)</c>, rather than transcribed from documentation.
/// Regenerate with <c>docs/plans/add-t800-key-assignments/decode-t800-matrix.py</c> rather than
/// hand-editing. The firmware exposes 128 slots (512 bytes of 4-byte records) of which
/// 68 carry a real key; slot <b>65</b> holds FN and is excluded here, leaving
/// <b>67 remappable keys</b>.
/// </para>
/// <para>
/// The matrix is <b>column-major</b> on a 6-row grid — <c>column = slot / 6</c>,
/// <c>row = slot % 6</c> — and row 0 of every column is empty, being the function row this 65%
/// board does not have. Entries below are ordered by visual row, not by slot, because slot order
/// would scatter the rows. The row counts (15 / 15 / 14 / 14 / 10) match the keycaps measured from
/// the artwork exactly, which is how the decode was verified.
/// </para>
/// <para>
/// <b>Two differences from <see cref="CyrexKeyCatalog"/> that matter.</b> A plain key's type byte is
/// <c>0x00</c> here, not Cyrex's <c>0x10</c>. And modifiers are ordinary HID usages
/// <c>0xE0</c>–<c>0xE7</c> rather than the modifier-bitmask-with-zero-usage form Cyrex uses — so on
/// this board a key is identified by its usage code alone and <c>ModifierMask</c> stays 0
/// throughout. Copying Cyrex's convention here would produce eight keys that never match.
/// </para>
/// </remarks>
public static class T800KeyCatalog
{
	public const int T800VendorId = 12625;
	public const int T800ProductId = 20525;

	/// <summary>Matrix slots the firmware reserves (512 bytes of 4-byte records).</summary>
	public const int MatrixSlots = 128;

	/// <summary>Slot holding FN in the factory table. Excluded from <see cref="Keys"/>.</summary>
	public const byte FnMatrixIndex = 65;

	private const string CategoryNumberRow = "Number Row";
	private const string CategoryAlphaRow1 = "Alpha Row 1";
	private const string CategoryAlphaRow2 = "Alpha Row 2";
	private const string CategoryAlphaRow3 = "Alpha Row 3";
	private const string CategoryBottomRow = "Bottom Row";
	private const string CategoryNavigation = "Navigation";

	public static bool IsT800(int vendorId, int productId) =>
		vendorId == T800VendorId && productId == T800ProductId;

	public static bool IsT800(string? name) =>
		!string.IsNullOrEmpty(name) &&
		name.Contains("t800", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 67 remappable keys: the 68 real keys minus FN. Ordered by visual row.
	/// </summary>
	public static IReadOnlyList<PhysicalKey> Keys { get; } =
	[
		// ── row 1 — Esc / digits / Backspace / Home ──
		new("Escape", "ESC", CategoryNumberRow, 1, 0x29),
		new("D1", "1", CategoryNumberRow, 7, 0x1E),
		new("D2", "2", CategoryNumberRow, 13, 0x1F),
		new("D3", "3", CategoryNumberRow, 19, 0x20),
		new("D4", "4", CategoryNumberRow, 25, 0x21),
		new("D5", "5", CategoryNumberRow, 31, 0x22),
		new("D6", "6", CategoryNumberRow, 37, 0x23),
		new("D7", "7", CategoryNumberRow, 43, 0x24),
		new("D8", "8", CategoryNumberRow, 49, 0x25),
		new("D9", "9", CategoryNumberRow, 55, 0x26),
		new("D0", "0", CategoryNumberRow, 61, 0x27),
		new("Minus", "- _", CategoryNumberRow, 67, 0x2D),
		new("Equal", "= +", CategoryNumberRow, 73, 0x2E),
		new("Backspace", "BACKSPACE", CategoryNumberRow, 79, 0x2A),
		new("Home", "HOME", CategoryNavigation, 85, 0x4A),

		// ── row 2 — Tab / QWERTY / Delete ──
		new("Tab", "TAB", CategoryAlphaRow1, 2, 0x2B),
		new("Q", "Q", CategoryAlphaRow1, 8, 0x14),
		new("W", "W", CategoryAlphaRow1, 14, 0x1A),
		new("E", "E", CategoryAlphaRow1, 20, 0x08),
		new("R", "R", CategoryAlphaRow1, 26, 0x15),
		new("T", "T", CategoryAlphaRow1, 32, 0x17),
		new("Y", "Y", CategoryAlphaRow1, 38, 0x1C),
		new("U", "U", CategoryAlphaRow1, 44, 0x18),
		new("I", "I", CategoryAlphaRow1, 50, 0x0C),
		new("O", "O", CategoryAlphaRow1, 56, 0x12),
		new("P", "P", CategoryAlphaRow1, 62, 0x13),
		new("BracketLeft", "[ {", CategoryAlphaRow1, 68, 0x2F),
		new("BracketRight", "] }", CategoryAlphaRow1, 74, 0x30),
		new("Backslash", "\\ |", CategoryAlphaRow1, 80, 0x31),
		new("Delete", "DEL", CategoryNavigation, 86, 0x4C),

		// ── row 3 — Caps / home row / Enter / PageUp ──
		new("CapsLock", "CAPS", CategoryAlphaRow2, 3, 0x39),
		new("A", "A", CategoryAlphaRow2, 9, 0x04),
		new("S", "S", CategoryAlphaRow2, 15, 0x16),
		new("D", "D", CategoryAlphaRow2, 21, 0x07),
		new("F", "F", CategoryAlphaRow2, 27, 0x09),
		new("G", "G", CategoryAlphaRow2, 33, 0x0A),
		new("H", "H", CategoryAlphaRow2, 39, 0x0B),
		new("J", "J", CategoryAlphaRow2, 45, 0x0D),
		new("K", "K", CategoryAlphaRow2, 51, 0x0E),
		new("L", "L", CategoryAlphaRow2, 57, 0x0F),
		new("Semicolon", "; :", CategoryAlphaRow2, 63, 0x33),
		new("Quote", "' \"", CategoryAlphaRow2, 69, 0x34),
		new("Enter", "ENTER", CategoryAlphaRow2, 81, 0x28),
		new("PageUp", "PGUP", CategoryNavigation, 87, 0x4B),

		// ── row 4 — Shift / ZXCV / Up / PageDown ──
		new("LeftShift", "SHIFT (L)", CategoryAlphaRow3, 4, 0xE1),
		new("Z", "Z", CategoryAlphaRow3, 16, 0x1D),
		new("X", "X", CategoryAlphaRow3, 22, 0x1B),
		new("C", "C", CategoryAlphaRow3, 28, 0x06),
		new("V", "V", CategoryAlphaRow3, 34, 0x19),
		new("B", "B", CategoryAlphaRow3, 40, 0x05),
		new("N", "N", CategoryAlphaRow3, 46, 0x11),
		new("M", "M", CategoryAlphaRow3, 52, 0x10),
		new("Comma", ", <", CategoryAlphaRow3, 58, 0x36),
		new("Period", ". >", CategoryAlphaRow3, 64, 0x37),
		new("Slash", "/ ?", CategoryAlphaRow3, 70, 0x38),
		new("RightShift", "SHIFT (R)", CategoryAlphaRow3, 76, 0xE5),
		new("Up", "\u2191", CategoryNavigation, 82, 0x52),
		new("PageDown", "PGDN", CategoryNavigation, 88, 0x4E),

		// ── row 5 — modifiers / Space / FN / arrows ──
		new("LeftCtrl", "CTRL (L)", CategoryBottomRow, 5, 0xE0),
		new("LeftWin", "WIN (L)", CategoryBottomRow, 11, 0xE3),
		new("LeftAlt", "ALT (L)", CategoryBottomRow, 17, 0xE2),
		new("Space", "SPACE", CategoryBottomRow, 41, 0x2C),
		new("RightAlt", "ALT (R)", CategoryBottomRow, 59, 0xE6),
		new("RightCtrl", "CTRL (R)", CategoryBottomRow, 71, 0xE4),
		new("Left", "\u2190", CategoryNavigation, 77, 0x50),
		new("Down", "\u2193", CategoryNavigation, 83, 0x51),
		new("Right", "\u2192", CategoryNavigation, 89, 0x4F),
	];
}
