using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// A single remappable physical key on the Raster keyboard.
/// <para><c>MatrixIndex</c> is the vendor firmware's flat key-matrix index used by the
/// SetUserKey command (memory offset = <c>3 * MatrixIndex</c>). See vendor webapp
/// chunk <c>page-ba6d4e19076b59c9.js</c>: <c>setKeyMatrixData(profile, keyIndex,
/// type, code1, code2)</c>.</para>
/// </summary>
/// <param name="ModifierMask">
/// Modifier bitmask for a type-0x10 key record (<c>CTRL_L=0x01, SHIFT_L=0x02, ALT_L=0x04,
/// WIN_L=0x08, CTRL_R=0x10, SHIFT_R=0x20, ALT_R=0x40, WIN_R=0x80</c>). Zero for ordinary keys,
/// which carry their identity in <paramref name="HidUsageCode"/>. Modifier keys are the inverse:
/// mask set, usage code <c>0x00</c> — so a key is identified by the (mask, usage) pair and never by
/// the usage code alone. Raster and T-Racer leave this at the default; Cyrex and Rampage populate it.
/// </param>
public sealed record PhysicalKey(
	string DevKeyName,
	string KeyLabel,
	string Category,
	byte MatrixIndex,
	byte HidUsageCode,
	byte ModifierMask = 0);

/// <summary>
/// The Raster physical-key catalog and its default USB HID key mapping.
/// </summary>
/// <remarks>
/// Vendor identifiers: VID <c>0x36B0</c> (14000), PID <c>0x3061</c> (12385).
/// Matrix indices are sourced from the vendor's QK75 Pro V2 layout JSON
/// (<c>36b0_304C_0.json</c>, PID <c>0x304C</c>) — the closest 75/TKL sibling.
/// Right-column labels (DEL/PGUP/PGDN/END) match the shortcut card
/// <c>Shortcut Card Proove Gaming Raster 20.05.2026.pdf</c>.
/// </remarks>
public static class RasterKeyCatalog
{
	public const int RasterVendorId = 14000;
	public const int RasterProductId = 12385;

	private const string CategoryFunctionRow = "Function Row";
	private const string CategoryNumberRow = "Number Row";
	private const string CategoryAlphaRow1 = "Alpha Row 1";
	private const string CategoryAlphaRow2 = "Alpha Row 2";
	private const string CategoryAlphaRow3 = "Alpha Row 3";
	private const string CategoryBottomRow = "Bottom Row";
	private const string CategoryNavigation = "Navigation";

	public static bool IsRaster(int vendorId, int productId) =>
		vendorId == RasterVendorId && productId == RasterProductId;

	public static bool IsRaster(string? name) =>
		!string.IsNullOrEmpty(name) &&
		name.Contains("raster", System.StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 81 remappable keys. Order matches visual scan (top-left to bottom-right).
	/// </summary>
	public static IReadOnlyList<PhysicalKey> Keys { get; } =
	[
		// Function row
		new("Escape", "ESC", CategoryFunctionRow, 0, 0x29),
		new("F1", "F1", CategoryFunctionRow, 2, 0x3A),
		new("F2", "F2", CategoryFunctionRow, 3, 0x3B),
		new("F3", "F3", CategoryFunctionRow, 4, 0x3C),
		new("F4", "F4", CategoryFunctionRow, 5, 0x3D),
		new("F5", "F5", CategoryFunctionRow, 6, 0x3E),
		new("F6", "F6", CategoryFunctionRow, 7, 0x3F),
		new("F7", "F7", CategoryFunctionRow, 8, 0x40),
		new("F8", "F8", CategoryFunctionRow, 9, 0x41),
		new("F9", "F9", CategoryFunctionRow, 10, 0x42),
		new("F10", "F10", CategoryFunctionRow, 11, 0x43),
		new("F11", "F11", CategoryFunctionRow, 12, 0x44),
		new("F12", "F12", CategoryFunctionRow, 13, 0x45),

		// Number row + Del (nav column top)
		new("Grave", "` ~", CategoryNumberRow, 21, 0x35),
		new("1", "1", CategoryNumberRow, 22, 0x1E),
		new("2", "2", CategoryNumberRow, 23, 0x1F),
		new("3", "3", CategoryNumberRow, 24, 0x20),
		new("4", "4", CategoryNumberRow, 25, 0x21),
		new("5", "5", CategoryNumberRow, 26, 0x22),
		new("6", "6", CategoryNumberRow, 27, 0x23),
		new("7", "7", CategoryNumberRow, 28, 0x24),
		new("8", "8", CategoryNumberRow, 29, 0x25),
		new("9", "9", CategoryNumberRow, 30, 0x26),
		new("0", "0", CategoryNumberRow, 31, 0x27),
		new("Minus", "- _", CategoryNumberRow, 32, 0x2D),
		new("Equal", "= +", CategoryNumberRow, 33, 0x2E),
		new("Backspace", "Backspace", CategoryNumberRow, 34, 0x2A),
		new("Del", "DEL", CategoryNavigation, 16, 0x4C),

		// Top alpha row + PgUp
		new("Tab", "TAB", CategoryAlphaRow1, 42, 0x2B),
		new("Q", "Q", CategoryAlphaRow1, 43, 0x14),
		new("W", "W", CategoryAlphaRow1, 44, 0x1A),
		new("E", "E", CategoryAlphaRow1, 45, 0x08),
		new("R", "R", CategoryAlphaRow1, 46, 0x15),
		new("T", "T", CategoryAlphaRow1, 47, 0x17),
		new("Y", "Y", CategoryAlphaRow1, 48, 0x1C),
		new("U", "U", CategoryAlphaRow1, 49, 0x18),
		new("I", "I", CategoryAlphaRow1, 50, 0x0C),
		new("O", "O", CategoryAlphaRow1, 51, 0x12),
		new("P", "P", CategoryAlphaRow1, 52, 0x13),
		new("LeftBracket", "[ {", CategoryAlphaRow1, 53, 0x2F),
		new("RightBracket", "] }", CategoryAlphaRow1, 54, 0x30),
		new("Backslash", "\\ |", CategoryAlphaRow1, 55, 0x31),
		new("PgUp", "PGUP", CategoryNavigation, 37, 0x4B),

		// Mid alpha row + PgDn
		new("CapsLock", "CAPS", CategoryAlphaRow2, 63, 0x39),
		new("A", "A", CategoryAlphaRow2, 64, 0x04),
		new("S", "S", CategoryAlphaRow2, 65, 0x16),
		new("D", "D", CategoryAlphaRow2, 66, 0x07),
		new("F", "F", CategoryAlphaRow2, 67, 0x09),
		new("G", "G", CategoryAlphaRow2, 68, 0x0A),
		new("H", "H", CategoryAlphaRow2, 69, 0x0B),
		new("J", "J", CategoryAlphaRow2, 70, 0x0D),
		new("K", "K", CategoryAlphaRow2, 71, 0x0E),
		new("L", "L", CategoryAlphaRow2, 72, 0x0F),
		new("Semicolon", "; :", CategoryAlphaRow2, 73, 0x33),
		new("Apostrophe", "' \"", CategoryAlphaRow2, 74, 0x34),
		new("Enter", "ENTER", CategoryAlphaRow2, 76, 0x28),
		new("PgDn", "PGDN", CategoryNavigation, 56, 0x4E),

		// Bottom alpha row + Up + End
		new("LeftShift", "SHIFT (L)", CategoryAlphaRow3, 84, 0xE1),
		new("Z", "Z", CategoryAlphaRow3, 85, 0x1D),
		new("X", "X", CategoryAlphaRow3, 86, 0x1B),
		new("C", "C", CategoryAlphaRow3, 87, 0x06),
		new("V", "V", CategoryAlphaRow3, 88, 0x19),
		new("B", "B", CategoryAlphaRow3, 89, 0x05),
		new("N", "N", CategoryAlphaRow3, 90, 0x11),
		new("M", "M", CategoryAlphaRow3, 91, 0x10),
		new("Comma", ", <", CategoryAlphaRow3, 92, 0x36),
		new("Period", ". >", CategoryAlphaRow3, 93, 0x37),
		new("Slash", "/ ?", CategoryAlphaRow3, 94, 0x38),
		new("RightShift", "SHIFT (R)", CategoryAlphaRow3, 96, 0xE5),
		new("Up", "↑", CategoryNavigation, 99, 0x52),
		new("End", "END", CategoryNavigation, 77, 0x4D),

		// Bottom row
		new("LeftCtrl", "CTRL (L)", CategoryBottomRow, 105, 0xE0),
		new("Win", "WIN", CategoryBottomRow, 106, 0xE3),
		new("LeftAlt", "ALT (L)", CategoryBottomRow, 107, 0xE2),
		new("Space", "SPACE", CategoryBottomRow, 110, 0x2C),
		new("RightAlt", "ALT (R)", CategoryBottomRow, 115, 0xE6),
		// Fn (matrix index 116, HID scancode 0xAF per QK75 layout) is intentionally
		// omitted from Keys — remapping Fn breaks every FN+X shortcut on the shortcut
		// card, so we prevent users from targeting it.
		new("Left", "←", CategoryNavigation, 119, 0x50),
		new("Down", "↓", CategoryNavigation, 120, 0x51),
		new("Right", "→", CategoryNavigation, 121, 0x4F),
	];
}
