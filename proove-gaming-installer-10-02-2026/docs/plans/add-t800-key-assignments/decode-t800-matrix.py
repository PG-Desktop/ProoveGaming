"""Generate `T800KeyCatalog.cs` from the device's own factory key table.

The catalog is generated rather than hand-written for the same reason Cyrex's was: 67 records
transcribed by hand is where a silent off-by-one enters, and a wrong matrix slot does not fail —
it remaps the wrong key.

Input is `samples/factory-matrix.txt`, captured from a connected T800 with
`dotnet run --project ./src/ProoveGaming.Device.Testing -- keys`. See `notes.md` for how the
record encoding and the column-major slot layout were established.

Run from the repository root:
    python docs/plans/add-t800-key-assignments/decode-t800-matrix.py

Strict by design: every assertion below guards a failure that would otherwise ship as
plausible-looking but wrong key mappings rather than as an error.
"""
import re
import sys

SAMPLE = "docs/plans/add-t800-key-assignments/samples/factory-matrix.txt"
OUT = "src/ProoveGaming.App/Models/Keyboards/T800KeyCatalog.cs"

MATRIX_SLOTS = 128        # 512 bytes / 4-byte records
FN_SLOT = 65
EXPECTED_KEYS = 68        # 67 remappable + FN
EXPECTED_ROWS = {1: 15, 2: 15, 3: 14, 4: 14, 5: 10}

NUMBER, A1, A2, A3, BOTTOM, NAV = (
    "CategoryNumberRow", "CategoryAlphaRow1", "CategoryAlphaRow2",
    "CategoryAlphaRow3", "CategoryBottomRow", "CategoryNavigation",
)

# name -> (DevKeyName, KeyLabel, Category). Labels follow the Cyrex catalog's conventions so the
# assignment list reads the same across boards.
KEYS = {
    "Escape": ("Escape", "ESC", NUMBER),
    "1": ("D1", "1", NUMBER), "2": ("D2", "2", NUMBER), "3": ("D3", "3", NUMBER),
    "4": ("D4", "4", NUMBER), "5": ("D5", "5", NUMBER), "6": ("D6", "6", NUMBER),
    "7": ("D7", "7", NUMBER), "8": ("D8", "8", NUMBER), "9": ("D9", "9", NUMBER),
    "0": ("D0", "0", NUMBER),
    "Minus": ("Minus", "- _", NUMBER), "Equal": ("Equal", "= +", NUMBER),
    "Backspace": ("Backspace", "BACKSPACE", NUMBER),

    "Tab": ("Tab", "TAB", A1),
    "BracketLeft": ("BracketLeft", "[ {", A1), "BracketRight": ("BracketRight", "] }", A1),
    "Backslash": ("Backslash", "\\\\ |", A1),

    "CapsLock": ("CapsLock", "CAPS", A2),
    "Semicolon": ("Semicolon", "; :", A2), "Quote": ("Quote", "' \\\"", A2),
    "Enter": ("Enter", "ENTER", A2),

    "LeftShift": ("LeftShift", "SHIFT (L)", A3), "RightShift": ("RightShift", "SHIFT (R)", A3),
    "Comma": ("Comma", ", <", A3), "Period": ("Period", ". >", A3), "Slash": ("Slash", "/ ?", A3),

    "LeftCtrl": ("LeftCtrl", "CTRL (L)", BOTTOM), "RightCtrl": ("RightCtrl", "CTRL (R)", BOTTOM),
    "LeftWin": ("LeftWin", "WIN (L)", BOTTOM),
    "LeftAlt": ("LeftAlt", "ALT (L)", BOTTOM), "RightAlt": ("RightAlt", "ALT (R)", BOTTOM),
    "Space": ("Space", "SPACE", BOTTOM),

    "Home": ("Home", "HOME", NAV), "Delete": ("Delete", "DEL", NAV),
    "PageUp": ("PageUp", "PGUP", NAV), "PageDown": ("PageDown", "PGDN", NAV),
    "Up": ("Up", "\\u2191", NAV), "Down": ("Down", "\\u2193", NAV),
    "Left": ("Left", "\\u2190", NAV), "Right": ("Right", "\\u2192", NAV),
}
for letter in "QWERTYUIOPASDFGHJKLZXCVBNM":
    row = A1 if letter in "QWERTYUIOP" else A2 if letter in "ASDFGHJKL" else A3
    KEYS[letter] = (letter, letter, row)


def die(msg):
    sys.exit(f"decode-t800-matrix.py: {msg}")


def main():
    records = []
    for line in open(SAMPLE, encoding="utf-8"):
        m = re.match(r"slot\s+(\d+):\s+([0-9A-F]{2}) ([0-9A-F]{2}) ([0-9A-F]{2}) ([0-9A-F]{2})\s+(.+)$",
                     line.strip())
        if m:
            slot = int(m.group(1))
            rec = tuple(int(m.group(i), 16) for i in (2, 3, 4, 5))
            records.append((slot, rec, m.group(6).strip()))

    if len(records) != EXPECTED_KEYS:
        die(f"expected {EXPECTED_KEYS} records, parsed {len(records)}")

    # The fourth byte being universally zero is what lets these records ride the App's 3-byte
    # KeyMatrixRecord. If a future capture breaks that, the shared model has to change — fail loudly
    # rather than silently truncating.
    nonzero = [(s, r) for s, r, _ in records if r[3] != 0]
    if nonzero:
        die(f"Code3 is not zero for {nonzero} — KeyMatrixRecord can no longer carry these records")

    rows = {}
    for slot, _, _ in records:
        rows.setdefault(slot % 6, []).append(slot)
    counts = {r: len(v) for r, v in rows.items()}
    if counts != EXPECTED_ROWS:
        die(f"row structure {counts} != {EXPECTED_ROWS} (artwork cross-check would disagree)")

    fn = [s for s, r, _ in records if r[0] == 0x0A and r[1] == 0x01]
    if fn != [FN_SLOT]:
        die(f"expected exactly one FN at slot {FN_SLOT}, found {fn}")

    unknown = [(s, n) for s, _, n in records if n not in KEYS and n != "FN"]
    if unknown:
        die(f"no catalog entry for {unknown}")

    # Emit in visual reading order — row then column. Matrix index is deliberately NOT the sort key:
    # this matrix is column-major, so slot order would scatter the rows and make the generated file
    # unreadable against the physical board.
    emitted = sorted((s for s, _, _ in records if s != FN_SLOT), key=lambda s: (s % 6, s // 6))
    by_slot = {s: (r, n) for s, r, n in records}

    lines = []
    current_row = None
    row_titles = {
        1: "row 1 — Esc / digits / Backspace / Home",
        2: "row 2 — Tab / QWERTY / Delete",
        3: "row 3 — Caps / home row / Enter / PageUp",
        4: "row 4 — Shift / ZXCV / Up / PageDown",
        5: "row 5 — modifiers / Space / FN / arrows",
    }
    for slot in emitted:
        row = slot % 6
        if row != current_row:
            if current_row is not None:
                lines.append("")
            lines.append(f"\t\t// ── {row_titles[row]} ──")
            current_row = row
        rec, name = by_slot[slot]
        dev, label, category = KEYS[name]
        lines.append(f'\t\tnew("{dev}", "{label}", {category}, {slot}, 0x{rec[2]:02X}),')

    body = "\n".join(lines)
    remappable = len(emitted)

    open(OUT, "w", encoding="utf-8", newline="\r\n").write(f'''using System;
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
/// hand-editing. The firmware exposes {MATRIX_SLOTS} slots (512 bytes of 4-byte records) of which
/// {EXPECTED_KEYS} carry a real key; slot <b>{FN_SLOT}</b> holds FN and is excluded here, leaving
/// <b>{remappable} remappable keys</b>.
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
{{
	public const int T800VendorId = 12625;
	public const int T800ProductId = 20525;

	/// <summary>Matrix slots the firmware reserves (512 bytes of 4-byte records).</summary>
	public const int MatrixSlots = {MATRIX_SLOTS};

	/// <summary>Slot holding FN in the factory table. Excluded from <see cref="Keys"/>.</summary>
	public const byte FnMatrixIndex = {FN_SLOT};

	private const string {NUMBER} = "Number Row";
	private const string {A1} = "Alpha Row 1";
	private const string {A2} = "Alpha Row 2";
	private const string {A3} = "Alpha Row 3";
	private const string {BOTTOM} = "Bottom Row";
	private const string {NAV} = "Navigation";

	public static bool IsT800(int vendorId, int productId) =>
		vendorId == T800VendorId && productId == T800ProductId;

	public static bool IsT800(string? name) =>
		!string.IsNullOrEmpty(name) &&
		name.Contains("t800", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// {remappable} remappable keys: the {EXPECTED_KEYS} real keys minus FN. Ordered by visual row.
	/// </summary>
	public static IReadOnlyList<PhysicalKey> Keys {{ get; }} =
	[
{body}
	];
}}
''')
    print(f"wrote {OUT}: {remappable} remappable keys, FN at slot {FN_SLOT}")


main()
