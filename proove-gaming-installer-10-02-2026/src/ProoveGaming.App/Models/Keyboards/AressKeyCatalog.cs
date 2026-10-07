using System;
using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// The Aress physical-key catalog and its factory USB HID key mapping.
/// </summary>
/// <remarks>
/// <para>
/// Vendor identifiers: VID <c>10473</c> (<c>0x28E9</c>), PID <c>12910</c> (<c>0x326E</c>). Aress is a
/// magnetic-switch 65% keyboard in the Smart Infinite family (siblings: Cyrex, Rampage) speaking the
/// same <c>0x06</c> vendor protocol.
/// </para>
/// <para>
/// <b>Every entry below was generated from the device's own factory key table</b>, read over the
/// vendor interface with command <c>0x16</c>, rather than transcribed from documentation. The device
/// reports a 75-slot matrix (<c>75 x 3 = 225</c> bytes) of which 7 are unused placeholders
/// (<c>{0x10, 0x00, 0x00}</c> at 42, 46, 63, 64, 65, 67, 68), leaving <b>68 real keys</b> — exactly
/// the count the firmware reports in Device Info byte 6. Slot <b>70</b> is FN and is excluded:
/// remapping it would break every FN+X hardware shortcut.
/// </para>
/// <para>
/// Aress and Cyrex are the same physical board. Read side by side their factory tables differ in
/// exactly three slots — the navigation column, where Aress has Insert / Delete / PageDown and Cyrex
/// has Delete / PageDown / End. The shared geometry therefore lives in
/// <see cref="SmartInfinite65Geometry"/> and is keyed by matrix slot, not by key name.
/// </para>
/// </remarks>
public static class AressKeyCatalog
{
	public const int AressVendorId = 10473;
	public const int AressProductId = 12910;

	/// <summary>Matrix slots the firmware reserves (Device Info byte 4 = 75).</summary>
	public const int MatrixSlots = SmartInfinite65Geometry.MatrixSlots;

	/// <summary>Slot holding FN in the factory table. Excluded from <see cref="Keys"/>.</summary>
	public const byte FnMatrixIndex = SmartInfinite65Geometry.FnMatrixIndex;

	// Modifier bitmask values from the vendor header (keyboard_key_define.h).
	private const byte ModCtrlL = 0x01;
	private const byte ModShiftL = 0x02;
	private const byte ModAltL = 0x04;
	private const byte ModWinL = 0x08;
	private const byte ModCtrlR = 0x10;
	private const byte ModShiftR = 0x20;
	private const byte ModAltR = 0x40;

	private const string CategoryNumberRow = "Number Row";
	private const string CategoryAlphaRow1 = "Alpha Row 1";
	private const string CategoryAlphaRow2 = "Alpha Row 2";
	private const string CategoryAlphaRow3 = "Alpha Row 3";
	private const string CategoryBottomRow = "Bottom Row";
	private const string CategoryNavigation = "Navigation";

	public static bool IsAress(int vendorId, int productId) =>
		vendorId == AressVendorId && productId == AressProductId;

	public static bool IsAress(string? name) =>
		!string.IsNullOrEmpty(name) &&
		name.Contains("aress", StringComparison.OrdinalIgnoreCase);

	/// <summary>67 remappable keys: the 68 real keys minus FN. Ordered by firmware matrix index.</summary>
	public static IReadOnlyList<PhysicalKey> Keys { get; } =
	[
		// ── row 0 — Esc / digits / Backspace / Insert (slots 0-14) ──
		new("Escape", "ESC", CategoryNumberRow, 0, 0x29),
		new("D1", "1", CategoryNumberRow, 1, 0x1E),
		new("D2", "2", CategoryNumberRow, 2, 0x1F),
		new("D3", "3", CategoryNumberRow, 3, 0x20),
		new("D4", "4", CategoryNumberRow, 4, 0x21),
		new("D5", "5", CategoryNumberRow, 5, 0x22),
		new("D6", "6", CategoryNumberRow, 6, 0x23),
		new("D7", "7", CategoryNumberRow, 7, 0x24),
		new("D8", "8", CategoryNumberRow, 8, 0x25),
		new("D9", "9", CategoryNumberRow, 9, 0x26),
		new("D0", "0", CategoryNumberRow, 10, 0x27),
		new("Minus", "- _", CategoryNumberRow, 11, 0x2D),
		new("Equal", "= +", CategoryNumberRow, 12, 0x2E),
		new("Backspace", "BACKSPACE", CategoryNumberRow, 13, 0x2A),
		new("Insert", "INS", CategoryNavigation, 14, 0x49),

		// ── row 1 — Tab / QWERTY / PageUp (slots 15-29) ──
		new("Tab", "TAB", CategoryAlphaRow1, 15, 0x2B),
		new("Q", "Q", CategoryAlphaRow1, 16, 0x14),
		new("W", "W", CategoryAlphaRow1, 17, 0x1A),
		new("E", "E", CategoryAlphaRow1, 18, 0x08),
		new("R", "R", CategoryAlphaRow1, 19, 0x15),
		new("T", "T", CategoryAlphaRow1, 20, 0x17),
		new("Y", "Y", CategoryAlphaRow1, 21, 0x1C),
		new("U", "U", CategoryAlphaRow1, 22, 0x18),
		new("I", "I", CategoryAlphaRow1, 23, 0x0C),
		new("O", "O", CategoryAlphaRow1, 24, 0x12),
		new("P", "P", CategoryAlphaRow1, 25, 0x13),
		new("BracketLeft", "[ {", CategoryAlphaRow1, 26, 0x2F),
		new("BracketRight", "] }", CategoryAlphaRow1, 27, 0x30),
		new("Backslash", "\\ |", CategoryAlphaRow1, 28, 0x31),
		new("PageUp", "PGUP", CategoryNavigation, 29, 0x4B),

		// ── row 2 — Caps / home row / Enter / Delete (slots 30-44) ──
		new("CapsLock", "CAPS", CategoryAlphaRow2, 30, 0x39),
		new("A", "A", CategoryAlphaRow2, 31, 0x04),
		new("S", "S", CategoryAlphaRow2, 32, 0x16),
		new("D", "D", CategoryAlphaRow2, 33, 0x07),
		new("F", "F", CategoryAlphaRow2, 34, 0x09),
		new("G", "G", CategoryAlphaRow2, 35, 0x0A),
		new("H", "H", CategoryAlphaRow2, 36, 0x0B),
		new("J", "J", CategoryAlphaRow2, 37, 0x0D),
		new("K", "K", CategoryAlphaRow2, 38, 0x0E),
		new("L", "L", CategoryAlphaRow2, 39, 0x0F),
		new("Semicolon", "; :", CategoryAlphaRow2, 40, 0x33),
		new("Quote", "' \"", CategoryAlphaRow2, 41, 0x34),
		new("Enter", "ENTER", CategoryAlphaRow2, 43, 0x28),
		new("Delete", "DEL", CategoryNavigation, 44, 0x4C),

		// ── row 3 — Shift / ZXCV / Up / PageDown (slots 45-59) ──
		new("LeftShift", "SHIFT (L)", CategoryAlphaRow3, 45, 0x00, ModShiftL),
		new("Z", "Z", CategoryAlphaRow3, 47, 0x1D),
		new("X", "X", CategoryAlphaRow3, 48, 0x1B),
		new("C", "C", CategoryAlphaRow3, 49, 0x06),
		new("V", "V", CategoryAlphaRow3, 50, 0x19),
		new("B", "B", CategoryAlphaRow3, 51, 0x05),
		new("N", "N", CategoryAlphaRow3, 52, 0x11),
		new("M", "M", CategoryAlphaRow3, 53, 0x10),
		new("Comma", ", <", CategoryAlphaRow3, 54, 0x36),
		new("Period", ". >", CategoryAlphaRow3, 55, 0x37),
		new("Slash", "/ ?", CategoryAlphaRow3, 56, 0x38),
		new("RightShift", "SHIFT (R)", CategoryAlphaRow3, 57, 0x00, ModShiftR),
		new("Up", "↑", CategoryNavigation, 58, 0x52),
		new("PageDown", "PGDN", CategoryNavigation, 59, 0x4E),

		// ── row 4 — modifiers / Space / arrows (slots 60-74) ──
		new("LeftCtrl", "CTRL (L)", CategoryBottomRow, 60, 0x00, ModCtrlL),
		new("LeftWin", "WIN (L)", CategoryBottomRow, 61, 0x00, ModWinL),
		new("LeftAlt", "ALT (L)", CategoryBottomRow, 62, 0x00, ModAltL),
		new("Space", "SPACE", CategoryBottomRow, 66, 0x2C),
		new("RightAlt", "ALT (R)", CategoryBottomRow, 69, 0x00, ModAltR),
		new("RightCtrl", "CTRL (R)", CategoryBottomRow, 71, 0x00, ModCtrlR),
		new("Left", "←", CategoryNavigation, 72, 0x50),
		new("Down", "↓", CategoryNavigation, 73, 0x51),
		new("Right", "→", CategoryNavigation, 74, 0x4F),
	];
}
