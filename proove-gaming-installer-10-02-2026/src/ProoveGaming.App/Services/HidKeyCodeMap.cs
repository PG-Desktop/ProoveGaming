using System.Collections.Generic;
using Avalonia.Input;

namespace ProoveGaming.App.Services;

// Maps Avalonia keys to USB HID keyboard usage codes (the value stored in a macro event).
// Only keys the firmware macro format accepts — plain keyboard keys; NOT multimedia/consumer
// keys (see docs/mouse/blaze/macros.md). Unmapped keys are ignored by the recorder.
public static class HidKeyCodeMap
{
	// Avalonia Key → (HID usage code, display label).
	private static readonly Dictionary<Key, (byte Code, string Label)> Map = new()
	{
		// Letters (0x04–0x1D)
		[Key.A] = (0x04, "A"), [Key.B] = (0x05, "B"), [Key.C] = (0x06, "C"), [Key.D] = (0x07, "D"),
		[Key.E] = (0x08, "E"), [Key.F] = (0x09, "F"), [Key.G] = (0x0A, "G"), [Key.H] = (0x0B, "H"),
		[Key.I] = (0x0C, "I"), [Key.J] = (0x0D, "J"), [Key.K] = (0x0E, "K"), [Key.L] = (0x0F, "L"),
		[Key.M] = (0x10, "M"), [Key.N] = (0x11, "N"), [Key.O] = (0x12, "O"), [Key.P] = (0x13, "P"),
		[Key.Q] = (0x14, "Q"), [Key.R] = (0x15, "R"), [Key.S] = (0x16, "S"), [Key.T] = (0x17, "T"),
		[Key.U] = (0x18, "U"), [Key.V] = (0x19, "V"), [Key.W] = (0x1A, "W"), [Key.X] = (0x1B, "X"),
		[Key.Y] = (0x1C, "Y"), [Key.Z] = (0x1D, "Z"),

		// Number row (0x1E–0x27)
		[Key.D1] = (0x1E, "1"), [Key.D2] = (0x1F, "2"), [Key.D3] = (0x20, "3"), [Key.D4] = (0x21, "4"),
		[Key.D5] = (0x22, "5"), [Key.D6] = (0x23, "6"), [Key.D7] = (0x24, "7"), [Key.D8] = (0x25, "8"),
		[Key.D9] = (0x26, "9"), [Key.D0] = (0x27, "0"),

		// Editing / whitespace
		[Key.Enter] = (0x28, "Enter"), [Key.Escape] = (0x29, "Esc"), [Key.Back] = (0x2A, "Backspace"),
		[Key.Tab] = (0x2B, "Tab"), [Key.Space] = (0x2C, "Space"),

		// Punctuation
		[Key.OemMinus] = (0x2D, "-"), [Key.OemPlus] = (0x2E, "="),
		[Key.OemOpenBrackets] = (0x2F, "["), [Key.OemCloseBrackets] = (0x30, "]"),
		[Key.OemPipe] = (0x31, "\\"), [Key.OemSemicolon] = (0x33, ";"), [Key.OemQuotes] = (0x34, "'"),
		[Key.OemTilde] = (0x35, "`"), [Key.OemComma] = (0x36, ","), [Key.OemPeriod] = (0x37, "."),
		[Key.OemQuestion] = (0x38, "/"), [Key.CapsLock] = (0x39, "Caps Lock"),

		// Function keys (0x3A–0x45)
		[Key.F1] = (0x3A, "F1"), [Key.F2] = (0x3B, "F2"), [Key.F3] = (0x3C, "F3"), [Key.F4] = (0x3D, "F4"),
		[Key.F5] = (0x3E, "F5"), [Key.F6] = (0x3F, "F6"), [Key.F7] = (0x40, "F7"), [Key.F8] = (0x41, "F8"),
		[Key.F9] = (0x42, "F9"), [Key.F10] = (0x43, "F10"), [Key.F11] = (0x44, "F11"), [Key.F12] = (0x45, "F12"),

		// Navigation
		[Key.Insert] = (0x49, "Insert"), [Key.Home] = (0x4A, "Home"), [Key.PageUp] = (0x4B, "Page Up"),
		[Key.Delete] = (0x4C, "Delete"), [Key.End] = (0x4D, "End"), [Key.PageDown] = (0x4E, "Page Down"),
		[Key.Right] = (0x4F, "Right"), [Key.Left] = (0x50, "Left"), [Key.Down] = (0x51, "Down"), [Key.Up] = (0x52, "Up"),

		// Modifiers (0xE0–0xE7)
		[Key.LeftCtrl] = (0xE0, "Left Ctrl"), [Key.LeftShift] = (0xE1, "Left Shift"),
		[Key.LeftAlt] = (0xE2, "Left Alt"), [Key.LWin] = (0xE3, "Left Win"),
		[Key.RightCtrl] = (0xE4, "Right Ctrl"), [Key.RightShift] = (0xE5, "Right Shift"),
		[Key.RightAlt] = (0xE6, "Right Alt"), [Key.RWin] = (0xE7, "Right Win"),
	};

	// HID usage code → display label, for rendering an existing macro's recorded steps.
	private static readonly Dictionary<byte, string> LabelByCode = BuildLabelByCode();

	private static Dictionary<byte, string> BuildLabelByCode()
	{
		var map = new Dictionary<byte, string>();
		foreach (var (code, label) in Map.Values)
			map[code] = label;
		return map;
	}

	public static bool TryMap(Key key, out byte code, out string label)
	{
		if (Map.TryGetValue(key, out var entry))
		{
			code = entry.Code;
			label = entry.Label;
			return true;
		}

		code = 0;
		label = string.Empty;
		return false;
	}

	// Label for a stored HID code; falls back to a hex code for anything not in the table.
	public static string LabelFor(byte code) =>
		LabelByCode.TryGetValue(code, out var label) ? label : $"0x{code:X2}";
}
