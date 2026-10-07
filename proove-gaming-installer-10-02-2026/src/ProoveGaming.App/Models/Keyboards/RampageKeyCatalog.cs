using System;
using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// The Rampage physical-key catalog and its factory USB HID key mapping.
/// </summary>
/// <remarks>
/// <para>
/// Vendor identifiers: VID <c>10473</c> (<c>0x28E9</c>), PID <c>12888</c> (<c>0x3258</c>).
/// Unlike <see cref="RasterKeyCatalog"/> and <see cref="TRacerKeyCatalog"/>, whose matrix indices
/// are seeded guesses corrected at runtime by a probe, every index here is transcribed verbatim
/// from the vendor's own factory table (<c>key_matrix_tab</c> in
/// <c>docs/knowledge-base/rampage-keyboard-protocol/keyboard_user_flash.c</c>) and is exact. There
/// is no runtime slot probe for Rampage — the firmware slots are compile-time constants.
/// </para>
/// <para>
/// The firmware table has 96 slots. This catalog exposes the 86 that are remappable, omitting:
/// the eight structurally empty slots (<c>60, 65, 77, 83, 84, 85, 87, 88</c>), the factory-blank
/// custom-function slot <c>79</c>, and <c>FN</c> at slot <c>90</c> — remapping FN would break every
/// FN+X hardware shortcut, the same reason Raster and T-Racer exclude it.
/// </para>
/// </remarks>
public static class RampageKeyCatalog
{
	public const int RampageVendorId = 10473;
	public const int RampageProductId = 12888;

	/// <summary>Slot holding the FN key in the factory table. Excluded from <see cref="Keys"/>.</summary>
	public const byte FnMatrixIndex = 90;

	// Modifier bitmask values from the vendor header (keyboard_key_define.h).
	private const byte ModCtrlL = 0x01;
	private const byte ModShiftL = 0x02;
	private const byte ModAltL = 0x04;
	private const byte ModWinL = 0x08;
	private const byte ModCtrlR = 0x10;
	private const byte ModShiftR = 0x20;
	private const byte ModAltR = 0x40;

	private const string CategoryFunctionRow = "Function Row";
	private const string CategoryNumberRow = "Number Row";
	private const string CategoryAlphaRow1 = "Alpha Row 1";
	private const string CategoryAlphaRow2 = "Alpha Row 2";
	private const string CategoryAlphaRow3 = "Alpha Row 3";
	private const string CategoryBottomRow = "Bottom Row";
	private const string CategoryNavigation = "Navigation";

	public static bool IsRampage(int vendorId, int productId) =>
		vendorId == RampageVendorId && productId == RampageProductId;

	/// <summary>
	/// Name fallback. A Rampage on a non-wired transport arrives with a different identity — the
	/// catalog-stamped BT identity, or the 2.4G receiver's own VID/PID (0x056D:0xC077) — so the
	/// VID/PID overload above misses and identity has to come from the name. Key writes work over
	/// the cable and the 2.4G receiver (the latter with 24-byte table chunking); Bluetooth remains
	/// display-only — its firmware carries no writable config channel.
	/// </summary>
	public static bool IsRampage(string? name) =>
		!string.IsNullOrEmpty(name) &&
		name.Contains("rampage", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 86 remappable keys, ordered by firmware matrix index (which also happens to be visual
	/// scan order, top-left to bottom-right).
	/// </summary>
	public static IReadOnlyList<PhysicalKey> Keys { get; } =
	[
		// ── R0: slots 0-15 ────────────────────────────────────────────────────
		new("Escape", "ESC", CategoryFunctionRow, 0, 0x29),
		new("F1", "F1", CategoryFunctionRow, 1, 0x3A),
		new("F2", "F2", CategoryFunctionRow, 2, 0x3B),
		new("F3", "F3", CategoryFunctionRow, 3, 0x3C),
		new("F4", "F4", CategoryFunctionRow, 4, 0x3D),
		new("F5", "F5", CategoryFunctionRow, 5, 0x3E),
		new("F6", "F6", CategoryFunctionRow, 6, 0x3F),
		new("F7", "F7", CategoryFunctionRow, 7, 0x40),
		new("F8", "F8", CategoryFunctionRow, 8, 0x41),
		new("F9", "F9", CategoryFunctionRow, 9, 0x42),
		new("F10", "F10", CategoryFunctionRow, 10, 0x43),
		new("F11", "F11", CategoryFunctionRow, 11, 0x44),
		new("F12", "F12", CategoryFunctionRow, 12, 0x45),
		new("PrintScreen", "PRTSC", CategoryFunctionRow, 13, 0x46),
		new("ScrollLock", "SCRLK", CategoryFunctionRow, 14, 0x47),
		new("Pause", "PAUSE", CategoryFunctionRow, 15, 0x48),

		// ── R1: slots 16-31 ───────────────────────────────────────────────────
		new("Grave", "` ~", CategoryNumberRow, 16, 0x35),
		new("D1", "1", CategoryNumberRow, 17, 0x1E),
		new("D2", "2", CategoryNumberRow, 18, 0x1F),
		new("D3", "3", CategoryNumberRow, 19, 0x20),
		new("D4", "4", CategoryNumberRow, 20, 0x21),
		new("D5", "5", CategoryNumberRow, 21, 0x22),
		new("D6", "6", CategoryNumberRow, 22, 0x23),
		new("D7", "7", CategoryNumberRow, 23, 0x24),
		new("D8", "8", CategoryNumberRow, 24, 0x25),
		new("D9", "9", CategoryNumberRow, 25, 0x26),
		new("D0", "0", CategoryNumberRow, 26, 0x27),
		new("Minus", "- _", CategoryNumberRow, 27, 0x2D),
		new("Equal", "= +", CategoryNumberRow, 28, 0x2E),
		new("Backspace", "BACKSPACE", CategoryNumberRow, 29, 0x2A),
		new("Insert", "INS", CategoryNavigation, 30, 0x49),
		new("Home", "HOME", CategoryNavigation, 31, 0x4A),

		// ── R2: slots 32-47 ───────────────────────────────────────────────────
		new("Tab", "TAB", CategoryAlphaRow1, 32, 0x2B),
		new("Q", "Q", CategoryAlphaRow1, 33, 0x14),
		new("W", "W", CategoryAlphaRow1, 34, 0x1A),
		new("E", "E", CategoryAlphaRow1, 35, 0x08),
		new("R", "R", CategoryAlphaRow1, 36, 0x15),
		new("T", "T", CategoryAlphaRow1, 37, 0x17),
		new("Y", "Y", CategoryAlphaRow1, 38, 0x1C),
		new("U", "U", CategoryAlphaRow1, 39, 0x18),
		new("I", "I", CategoryAlphaRow1, 40, 0x0C),
		new("O", "O", CategoryAlphaRow1, 41, 0x12),
		new("P", "P", CategoryAlphaRow1, 42, 0x13),
		new("BracketLeft", "[ {", CategoryAlphaRow1, 43, 0x2F),
		new("BracketRight", "] }", CategoryAlphaRow1, 44, 0x30),
		// KEY_K29 (0x31) — the ISO/ANSI backslash position.
		new("Backslash", "\\ |", CategoryAlphaRow1, 45, 0x31),
		new("Delete", "DEL", CategoryNavigation, 46, 0x4C),
		new("End", "END", CategoryNavigation, 47, 0x4D),

		// ── R3: slots 48-63 (slot 60 is structurally empty) ───────────────────
		new("CapsLock", "CAPS", CategoryAlphaRow2, 48, 0x39),
		new("A", "A", CategoryAlphaRow2, 49, 0x04),
		new("S", "S", CategoryAlphaRow2, 50, 0x16),
		new("D", "D", CategoryAlphaRow2, 51, 0x07),
		new("F", "F", CategoryAlphaRow2, 52, 0x09),
		new("G", "G", CategoryAlphaRow2, 53, 0x0A),
		new("H", "H", CategoryAlphaRow2, 54, 0x0B),
		new("J", "J", CategoryAlphaRow2, 55, 0x0D),
		new("K", "K", CategoryAlphaRow2, 56, 0x0E),
		new("L", "L", CategoryAlphaRow2, 57, 0x0F),
		new("Semicolon", "; :", CategoryAlphaRow2, 58, 0x33),
		new("Quote", "' \"", CategoryAlphaRow2, 59, 0x34),
		new("Enter", "ENTER", CategoryAlphaRow2, 61, 0x28),
		new("PageUp", "PGUP", CategoryNavigation, 62, 0x4B),
		new("PageDown", "PGDN", CategoryNavigation, 63, 0x4E),

		// ── R4: slots 64-79 (65 and 77 empty, 79 factory-blank custom) ────────
		new("LeftShift", "SHIFT (L)", CategoryAlphaRow3, 64, 0x00, ModShiftL),
		new("Z", "Z", CategoryAlphaRow3, 66, 0x1D),
		new("X", "X", CategoryAlphaRow3, 67, 0x1B),
		new("C", "C", CategoryAlphaRow3, 68, 0x06),
		new("V", "V", CategoryAlphaRow3, 69, 0x19),
		new("B", "B", CategoryAlphaRow3, 70, 0x05),
		new("N", "N", CategoryAlphaRow3, 71, 0x11),
		new("M", "M", CategoryAlphaRow3, 72, 0x10),
		new("Comma", ", <", CategoryAlphaRow3, 73, 0x36),
		new("Period", ". >", CategoryAlphaRow3, 74, 0x37),
		new("Slash", "/ ?", CategoryAlphaRow3, 75, 0x38),
		new("RightShift", "SHIFT (R)", CategoryAlphaRow3, 76, 0x00, ModShiftR),
		new("Up", "↑", CategoryNavigation, 78, 0x52),

		// ── R5: slots 80-95 (83-85, 87-88 empty; 90 is FN) ────────────────────
		new("LeftCtrl", "CTRL (L)", CategoryBottomRow, 80, 0x00, ModCtrlL),
		new("LeftWin", "WIN (L)", CategoryBottomRow, 81, 0x00, ModWinL),
		new("LeftAlt", "ALT (L)", CategoryBottomRow, 82, 0x00, ModAltL),
		new("Space", "SPACE", CategoryBottomRow, 86, 0x2C),
		new("RightAlt", "ALT (R)", CategoryBottomRow, 89, 0x00, ModAltR),
		new("Menu", "MENU", CategoryBottomRow, 91, 0x65),
		new("RightCtrl", "CTRL (R)", CategoryBottomRow, 92, 0x00, ModCtrlR),
		new("Left", "←", CategoryNavigation, 93, 0x50),
		new("Down", "↓", CategoryNavigation, 94, 0x51),
		new("Right", "→", CategoryNavigation, 95, 0x4F),
	];
}
