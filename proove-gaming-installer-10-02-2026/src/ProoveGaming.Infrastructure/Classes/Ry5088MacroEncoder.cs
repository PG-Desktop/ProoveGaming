namespace ProoveGaming.Infrastructure.Classes;

// Encodes one macro library entry into the RY5088 (T800) 256-byte macro blob uploaded with
// FEA_CMD_SET_MACRO (0x0B). Source of truth: the "Macro Table memory information" section of the
// vendor's YC3123 Gaming SPEC, translated in
// docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md. Layout:
//
//   [0..1]              = repeat count, uint16 LE (how the bound key plays it is NOT here —
//                         it rides in the key-matrix binding record [0x09, playType, slot])
//   per event, one 2-byte word:
//     [code, attr]        attr bit7 = make(1)/break(0); attr bits 0-6 = delay after this
//                         action in ms (1..127). Bits 0-6 of ZERO mean the NEXT word is a
//                         16-bit LE delay instead — the form every delay over 127 ms takes.
//   [0x00 0x00]         = end-of-macro marker
//
// This deliberately shares nothing with the other two keyboard encoders: Raster and Smart
// Infinite pack the whole library into one offset-table region with 4-byte events, while this
// family stores each macro as its own per-slot blob with 2-byte words and the repeat count in
// the blob header.
public static class Ry5088MacroEncoder
{
	/// <summary>
	/// Slots the App manages (its library cap — <c>MacroLibrarySlots</c> in
	/// <c>DeviceDetailsViewModel.KeyAssignments</c>). The device stores
	/// <see cref="Ry5088Protocol.MacroSlots"/> (64), but an upload rewrites exactly this many:
	/// content for provided entries, the empty blob for the rest, so a deleted macro cannot
	/// linger on the board within the range key bindings can reference.
	/// </summary>
	public const int LibrarySlots = 16;

	// Same per-macro and delay budgets as the other keyboard encoders — recorder UX limits,
	// not device properties. The event cap also guarantees a blob always fits: worst case is
	// 59 events in the extended-delay form plus the always-inline last one,
	// 2 + 59*4 + 2 + 2 = 242 <= 256.
	public const int MaxEventsPerMacro = 60;
	public const int DelayMinMs = 10;
	public const int DelayMaxMs = 1000;

	public const int MaxRepeatCount = 65535;

	/// <summary>Largest delay the attribute byte carries inline; anything above takes the extended word.</summary>
	public const int MaxInlineDelayMs = 127;

	private const byte MakeBit = 0x80;
	private const int RepeatHeaderSize = 2;
	private const int TerminatorSize = 2;

	/// <summary>
	/// The 4-byte blob an unused slot gets: repeat 1, immediate end marker. Written to every
	/// slot the library does not occupy so stale macros cannot survive a delete.
	/// </summary>
	public static byte[] EmptyBlob() => new byte[] { 0x01, 0x00, 0x00, 0x00 };

	// The doc's keyboard-code range. Outside it, a code is not merely unsupported — 0x00 in the
	// code position IS the end-of-macro marker, 0xF9/0xFA are motion/jump commands, and
	// 0xF0-0xF8 are mouse codes we do not emit — so any of them reaching the blob would corrupt
	// or truncate playback rather than "type the wrong key".
	private const byte KeyboardCodeFirst = 0x04;
	private const byte KeyboardCodeLast = 0xEF;

	/// <summary>
	/// Outcome of encoding one entry — same contract as the other encoders'
	/// <c>PackResult</c>: the encoder is a pure static with no logger, so anything it had to
	/// discard is counted for the caller to report.
	/// </summary>
	/// <param name="Blob">The exact-length blob, never longer than <see cref="Ry5088Protocol.MacroBytes"/>.</param>
	/// <param name="DroppedEvents">Events removed from this macro: mouse codes and anything else outside the keyboard-code range.</param>
	/// <param name="Truncated">True when events past <see cref="MaxEventsPerMacro"/> were cut.</param>
	public readonly record struct PackResult(byte[] Blob, int DroppedEvents, bool Truncated)
	{
		/// <summary>True when any macro content did not make it into the blob.</summary>
		public bool HasDiscards => DroppedEvents > 0 || Truncated;
	}

	/// <summary>
	/// Builds the blob for one entry. Events outside the keyboard-code range are dropped and
	/// counted — the format documents mouse codes, but T800 mirrors the other keyboards until
	/// playback is hardware-verified — and the entry's repeat count is clamped to
	/// 1..<see cref="MaxRepeatCount"/>.
	/// </summary>
	public static PackResult Encode(MacroLibraryEntry entry)
	{
		if (entry == null) throw new ArgumentNullException(nameof(entry));

		var events = FilterAndCap(entry.Events, out var droppedEvents, out var truncated);
		if (events.Count == 0)
			return new PackResult(EmptyBlob(), droppedEvents, truncated);

		// Exact size first: 2-byte word per event, plus 2 more when the delay needs the
		// extended form. Delays are resolved once here so the sizing and writing loops agree.
		//
		// Shifted by one on purpose: MacroEvent.DelayMs is the pause BEFORE the event (the
		// recorder stopwatches the gap since the previous step), while this format's attribute
		// delay plays AFTER its action. So action i carries event i+1's leading pause, and the
		// last action gets the minimum — its trailing delay has nothing left to wait for.
		var delays = new int[events.Count];
		var size = RepeatHeaderSize + TerminatorSize;
		for (int i = 0; i < events.Count; i++)
		{
			var beforeNext = i + 1 < events.Count ? events[i + 1].DelayMs : 0;
			delays[i] = Math.Clamp(beforeNext <= 0 ? DelayMinMs : beforeNext, DelayMinMs, DelayMaxMs);
			size += delays[i] <= MaxInlineDelayMs ? 2 : 4;
		}

		var blob = new byte[size];
		var repeat = Math.Clamp(entry.RepeatCount, 1, MaxRepeatCount);
		blob[0] = (byte)(repeat & 0xFF);
		blob[1] = (byte)(repeat >> 8);

		var cursor = RepeatHeaderSize;
		for (int i = 0; i < events.Count; i++)
		{
			var attr = events[i].IsPress ? MakeBit : (byte)0;
			blob[cursor] = events[i].KeyCode;
			if (delays[i] <= MaxInlineDelayMs)
			{
				blob[cursor + 1] = (byte)(attr | delays[i]);
				cursor += 2;
			}
			else
			{
				// Attribute bits 0-6 of zero switch the firmware to the extended form: the next
				// word is the delay as uint16 LE.
				blob[cursor + 1] = attr;
				blob[cursor + 2] = (byte)(delays[i] & 0xFF);
				blob[cursor + 3] = (byte)(delays[i] >> 8);
				cursor += 4;
			}
		}

		// The terminator bytes are already zero; cursor + TerminatorSize == size.
		return new PackResult(blob, droppedEvents, truncated);
	}

	// Same filtering rules as the other keyboard encoders — mouse events dropped and counted,
	// events past the recorder's per-macro budget cut — except the drop criterion is the whole
	// keyboard-code range, not just mouse codes, per the constants' remarks above.
	private static List<MacroEvent> FilterAndCap(
		IReadOnlyList<MacroEvent> events, out int droppedEvents, out bool truncated)
	{
		droppedEvents = 0;
		truncated = false;

		if (events == null || events.Count == 0)
			return new List<MacroEvent>(0);

		var result = new List<MacroEvent>(Math.Min(events.Count, MaxEventsPerMacro));
		foreach (var e in events)
		{
			if (e.KeyCode < KeyboardCodeFirst || e.KeyCode > KeyboardCodeLast)
			{
				droppedEvents++;
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
