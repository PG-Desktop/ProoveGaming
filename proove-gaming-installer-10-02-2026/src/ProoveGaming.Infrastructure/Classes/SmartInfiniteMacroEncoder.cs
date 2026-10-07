namespace ProoveGaming.Infrastructure.Classes;

// Encodes the Smart Infinite (Cyrex / Aress / Rampage) macro library into the buffer the
// firmware's own driver writes with sethongMessage (0x1C). Source of truth: the vendor Keyborder
// app's formatHongRect serializer (index-CDyQl7nP.js), transcribed in
// docs/knowledge-base/smart-infinite-protocol/macro-format-gamingkeyboard2.md.
// Layout, in little-endian:
//
//   [0..1]                = 0xAA 0x55        magic
//   [2..3]                = buffer length    the WHOLE region size (LE uint16) — not the used
//                                            length; formatHongRect writes hong_Rect.length here
//   [4..5]                = slot count       always 20, empty slots included
//   [6..7]                = 0 0              reserved
//   [8..8+2*20-1]         = offset table     2 bytes per slot; 0x0000 = empty slot
//   per-macro block at c (first block at 48):
//     [c..c+3]            = [count_lo, count_hi, 0, 0]    action count
//     per action 4 bytes  = [time_lo, time_hi, code, attr]
//                           attr bit 7 = press(1)/release(0); low bits select the action kind:
//                             0x01 normal key   — code is the HID usage
//                             0x02 modifier     — code is the MODIFIER BITMASK (LCtrl 0x01 …
//                                                 RWin 0x80), the "hot key" form the vendor app
//                                                 emits for usages 0xE0..0xE7
//
// This deliberately shares nothing with RasterMacroEncoder: the two formats differ in the header
// length semantics, the slot count, the buffer sizing (Device Info macro space, not a constant)
// and the modifier encoding, and Raster's encoder is hardware-verified as it stands.
public static class SmartInfiniteMacroEncoder
{
	/// <summary>The firmware's fixed macro slot count — hongCount in the vendor app.</summary>
	public const int SlotCount = 20;

	private const int HeaderSize = 8;             // magic (2) + region length (2) + slot count (2) + reserved (2)
	private const int OffsetEntrySize = 2;
	private const int MacroBlockHeaderSize = 4;   // [count_lo, count_hi, 0, 0]
	public const int EventSize = 4;               // [time_lo, time_hi, code, attr]

	/// <summary>First byte a macro block can start at: header + the full 20-entry offset table.</summary>
	public const int BlocksStart = HeaderSize + OffsetEntrySize * SlotCount;

	// Same per-macro and delay budgets as the Raster path — these are recorder UX limits, not
	// device properties, and the App's recorder enforces the same caps for every keyboard.
	public const int MaxEventsPerMacro = 60;
	public const int DelayMinMs = 10;
	public const int DelayMaxMs = 1000;

	// attr low bits — action kind per the vendor sheet and formatHongRect's codeType.
	private const byte ActionNormalKey = 0x01;
	private const byte ActionModifier = 0x02;
	private const byte PressBit = 0x80;

	// HID modifier usages translate to the bitmask form: 1 << (usage - 0xE0).
	private const byte ModifierUsageFirst = 0xE0;
	private const byte ModifierUsageLast = 0xE7;

	/// <summary>
	/// Outcome of packing the library — same contract as
	/// <see cref="RasterMacroEncoder.PackResult"/>: the encoder is a pure static with no logger,
	/// so anything it had to discard is counted for the caller to report.
	/// </summary>
	/// <param name="Buffer">The full region-sized buffer; the tail past the last block is zero.</param>
	/// <param name="DataLen">Position after the last written byte (header + table when empty).</param>
	/// <param name="DroppedMouseEvents">Mouse events removed across the whole library.</param>
	/// <param name="TruncatedMacros">Macros that had events past <see cref="MaxEventsPerMacro"/> cut.</param>
	/// <param name="SkippedMacros">Macros left out entirely — no slot or no room in the region.</param>
	public readonly record struct PackResult(
		byte[] Buffer,
		int DataLen,
		int DroppedMouseEvents,
		int TruncatedMacros,
		int SkippedMacros)
	{
		/// <summary>True when any macro content did not make it onto the device.</summary>
		public bool HasDiscards =>
			DroppedMouseEvents > 0 || TruncatedMacros > 0 || SkippedMacros > 0;
	}

	/// <summary>
	/// Builds the packed buffer for the whole library, sized to <paramref name="regionSize"/> —
	/// the macro space Device Info reports (byte 5 × 256; 1024 on Cyrex). Entries are positional:
	/// <c>entries[i]</c> lands in slot <c>i</c>, matching how key bindings reference slots.
	/// </summary>
	public static PackResult EncodePacked(IReadOnlyList<MacroLibraryEntry> entries, int regionSize)
	{
		if (entries == null) throw new ArgumentNullException(nameof(entries));
		if (regionSize < BlocksStart)
			throw new ArgumentOutOfRangeException(nameof(regionSize),
				$"Region of {regionSize} bytes cannot hold the {BlocksStart}-byte header and offset table.");

		var buf = new byte[regionSize];
		buf[0] = 0xAA;
		buf[1] = 0x55;
		// The whole region length, not the used length — formatHongRect writes
		// hong_Rect.length here and the firmware sizes its region the same way.
		buf[2] = (byte)(regionSize & 0xFF);
		buf[3] = (byte)((regionSize >> 8) & 0xFF);
		buf[4] = SlotCount;

		var cursor = BlocksStart;

		var droppedMouseEvents = 0;
		var truncatedMacros = 0;
		var skippedMacros = Math.Max(0, entries.Count - SlotCount);

		var count = Math.Min(entries.Count, SlotCount);
		for (int i = 0; i < count; i++)
		{
			var offsetSlot = HeaderSize + OffsetEntrySize * i;

			var events = FilterAndCap(entries[i].Events, out var droppedHere, out var truncatedHere);
			droppedMouseEvents += droppedHere;
			if (truncatedHere) truncatedMacros++;

			if (events.Count == 0)
				continue; // table entry stays 0x0000 = empty slot

			var blockSize = MacroBlockHeaderSize + events.Count * EventSize;
			if (cursor + blockSize > regionSize)
			{
				// No room for this block; later, smaller macros can still fit.
				skippedMacros++;
				continue;
			}

			buf[offsetSlot] = (byte)(cursor & 0xFF);
			buf[offsetSlot + 1] = (byte)((cursor >> 8) & 0xFF);

			buf[cursor] = (byte)(events.Count & 0xFF);
			buf[cursor + 1] = (byte)((events.Count >> 8) & 0xFF);
			// bytes cursor+2, cursor+3 stay zero
			cursor += MacroBlockHeaderSize;

			foreach (var e in events)
			{
				var time = Math.Clamp(e.DelayMs <= 0 ? DelayMinMs : e.DelayMs, DelayMinMs, DelayMaxMs);
				buf[cursor] = (byte)(time & 0xFF);
				buf[cursor + 1] = (byte)((time >> 8) & 0xFF);

				// Modifiers travel as the "hot key" form: the bitmask value with action kind 2.
				// Getting this wrong is not cosmetic — a modifier sent as a normal key with usage
				// 0xE0 is a code the firmware's normal-key path does not type.
				var isModifier = e.KeyCode >= ModifierUsageFirst && e.KeyCode <= ModifierUsageLast;
				buf[cursor + 2] = isModifier ? (byte)(1 << (e.KeyCode - ModifierUsageFirst)) : e.KeyCode;

				var kind = isModifier ? ActionModifier : ActionNormalKey;
				buf[cursor + 3] = e.IsPress ? (byte)(kind | PressBit) : kind;
				cursor += EventSize;
			}
		}

		return new PackResult(buf, cursor, droppedMouseEvents, truncatedMacros, skippedMacros);
	}

	// Same filtering rules as the Raster path: mouse events are not encodable into a keyboard
	// macro on this family either, and the per-macro cap is the recorder's UX budget.
	private static List<MacroEvent> FilterAndCap(
		IReadOnlyList<MacroEvent> events, out int droppedMouseEvents, out bool truncated)
	{
		droppedMouseEvents = 0;
		truncated = false;

		if (events == null || events.Count == 0)
			return new List<MacroEvent>(0);

		var result = new List<MacroEvent>(Math.Min(events.Count, MaxEventsPerMacro));
		foreach (var e in events)
		{
			if (RasterMacroEncoder.IsMouseEvent(e.KeyCode))
			{
				droppedMouseEvents++;
				continue;
			}

			if (result.Count >= MaxEventsPerMacro)
			{
				truncated = true;
				break;
			}

			result.Add(e);
		}
		return result;
	}
}
