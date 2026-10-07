namespace ProoveGaming.Infrastructure.Classes;

// Encodes the Raster keyboard's macro library into the vendor's packed 1096-byte buffer
// and slices it into 56-byte chunks for CMD_SET_MACRODATA (0x2D). Source of truth: the vendor
// webapp's setAllMacroDataV2, in
// docs/plans/raster-key-assignments/reference/vendor-webapp-protocol-chunk.js.
// Layout, in little-endian:
//
//   [0..1]                = 0xAA 0x55        magic
//   [2..3]                = dataLen          total occupied length (LE uint16)
//   [4..5]                = count            macro count (LE uint16, 0..16)
//   [6..7]                = 0 0              reserved
//   [8..8+2*count-1]      = offset table     2 bytes per macro; each = offset of that block
//   per-macro block at c:
//     [c..c+3]            = [len_lo, len_hi, 0, 0]      event count
//     per event 4 bytes   = [time_lo, time_hi, code, attr]
//                           time = ms delay BEFORE event, clamped [DelayMinMs, DelayMaxMs]
//                           attr = 0x81 keyboard press / 0x01 keyboard release
//
// Mouse events (KeyCode 0xF0..0xF4) are skipped per Decision 3 in the plan — Raster
// keyboards do not support mouse-in-macro in v1.
public static class RasterMacroEncoder
{
	// Vendor buffer size for the whole library.
	public const int BufferLength = 1096;

	// USB HID chunk size for CMD_SET_MACRODATA on Raster (deviceMode 0 = 56-byte chunks).
	public const int ChunkSize = 56;

	// Vendor UI shows 16 macro slots (KC_MACRO(N), N=0..15).
	public const int MaxMacros = 16;

	private const int HeaderSize = 8;         // magic (2) + dataLen (2) + count (2) + reserved (2)
	private const int OffsetEntrySize = 2;    // per macro
	private const int MacroBlockHeaderSize = 4;   // [len_lo, len_hi, 0, 0]
	public const int EventSize = 4;           // [time_lo, time_hi, code, attr]

	// Per-macro event budget, sized so a single macro can fill the pool budget minus the
	// worst-case fixed overhead (magic + count + reserved + 16 offset entries + 16 empty
	// per-macro headers). ((1096 - 8 - 2*16 - 4*16) / 4) = 248 events, but we cap much
	// lower to give the user headroom for other macros in the library and to match the UX
	// budgets on Blaze/Taipan. 60 lines up with the plan's stated per-macro budget.
	public const int MaxEventsPerMacro = 60;

	// Firmware clamps event delay to [10, 50000] ms; we cap much lower to match the
	// recorder UI-level 1s cap (matches Blaze) and stay well below vendor pathological ranges.
	public const int DelayMinMs = 10;
	public const int DelayMaxMs = 1000;

	// Key-matrix binding "type" byte for a macro slot. We always emit 0x60; the vendor decoder
	// renders both 0x60 and 0x61 as "M" + (code1 + 1), and how the macro plays is carried in
	// code2, not in the type byte:
	//
	//   [type=0x60, code1 = on-device macro slot, code2 = play mode]
	//     code2 0x00 = play through once
	//     code2 0x01 = repeat while the key is held
	//
	// Both confirmed on hardware: with code2=0x01 a tap of a six-key macro types two letters
	// and a longer hold types three, i.e. playback advances only while the key is down. The
	// record has no room for a repeat count — the vendor keeps `replayCnt` in a host-side
	// profile — so a repeat count cannot be expressed on this device.
	public const byte BindingTypePlay = 0x60;
	public const byte BindingTypeWhileHeld = 0x61;

	/// <summary>
	/// True for the key-matrix type bytes that mean "this slot plays a macro". The key-mapping
	/// service normalizes ordinary keyboard records to 0x10 but must pass these through, since
	/// they select a different record kind rather than a scancode. Accepts 0x61 as well as the
	/// 0x60 we emit, so a record written by the vendor tool still reads back as a macro.
	/// </summary>
	public static bool IsMacroBindingType(byte type) =>
		type == BindingTypePlay || type == BindingTypeWhileHeld;

	// Attribute (event-type) bytes for a keyboard event; low 7 bits of the attribute could
	// carry a delay in the raw vendor spec but our path stores the delay in the [time_lo,
	// time_hi] word and uses fixed press/release attributes as the vendor webapp does.
	//
	// The high bit marks a key-DOWN, not a key-up. Both vendor writers (setAllMacroData and
	// setAllMacroDataV2) emit `1 === o.type ? 129 : 1`, where type 1 is the down event — V1
	// builds it from `down_delay`, V2 from `hasDownArrow`. Emitting these the other way round
	// makes the firmware read every press as a release and vice versa, so a macro ends with
	// its keys still held: the key behaves as stuck until the keyboard is reset.
	private const byte AttrKeyboardPress = 0x81;
	private const byte AttrKeyboardRelease = 0x01;

	/// <summary>
	/// True when a macro event's KeyCode is a mouse-button code (0xF0..0xF4). Such events are
	/// dropped by <see cref="EncodePacked"/> per Decision 3 and counted in
	/// <see cref="PackResult.DroppedMouseEvents"/>.
	/// </summary>
	public static bool IsMouseEvent(byte keyCode) => keyCode >= 0xF0 && keyCode <= 0xF4;

	/// <summary>
	/// Outcome of packing the library: the buffer, how many bytes of it are occupied, and what
	/// had to be discarded to make the library fit. The counts exist because the encoder is a
	/// pure static with no logger, and quietly shortening a macro the user recorded is not an
	/// acceptable outcome — the caller logs whatever was dropped.
	/// </summary>
	/// <param name="Buffer">The full <see cref="BufferLength"/>-byte buffer.</param>
	/// <param name="DataLen">Occupied byte count, the value written to the header.</param>
	/// <param name="DroppedMouseEvents">Mouse events removed across the whole library.</param>
	/// <param name="TruncatedMacros">Macros that had events past <see cref="MaxEventsPerMacro"/> cut.</param>
	/// <param name="SkippedMacros">Macros left out entirely because the buffer had no room.</param>
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
	/// Builds the 1096-byte packed buffer for the whole library. Empty macros consume an
	/// offset-table entry but their entry is zeroed to signal "no block". Anything that could
	/// not be encoded is counted on the returned <see cref="PackResult"/> so the caller can
	/// report it rather than shortening the user's macro in silence.
	/// </summary>
	public static PackResult EncodePacked(IReadOnlyList<MacroLibraryEntry> entries)
	{
		if (entries == null) throw new ArgumentNullException(nameof(entries));

		var buf = new byte[BufferLength];
		buf[0] = 0xAA;
		buf[1] = 0x55;

		var count = Math.Min(entries.Count, MaxMacros);
		buf[4] = (byte)(count & 0xFF);
		buf[5] = (byte)(count >> 8);

		// Offset table starts at byte 8; per-macro data blocks start after the whole table.
		var offsetTableStart = HeaderSize;
		var cursor = offsetTableStart + OffsetEntrySize * count;

		var droppedMouseEvents = 0;
		var truncatedMacros = 0;
		// Entries past the device's slot count never reach the layout loop below, so they count
		// as skipped here — otherwise a caller handed 20 macros would silently ship 16 and see
		// HasDiscards report that everything fit.
		var skippedMacros = Math.Max(0, entries.Count - MaxMacros);

		for (int i = 0; i < count; i++)
		{
			var entry = entries[i];
			var offsetSlot = offsetTableStart + OffsetEntrySize * i;

			// Filter out mouse events (Decision 3 — Raster keyboards do not encode mouse
			// events into macros) and cap at MaxEventsPerMacro.
			var events = FilterAndCap(entry.Events, out var droppedHere, out var truncatedHere);
			droppedMouseEvents += droppedHere;
			if (truncatedHere) truncatedMacros++;

			if (events.Count == 0)
			{
				// Empty macro: table entry stays 0 to signal "no block".
				buf[offsetSlot] = 0;
				buf[offsetSlot + 1] = 0;
				continue;
			}

			// Not enough space for this macro's block: leave it out. Later, smaller macros can
			// still fit, so this keeps going rather than breaking out of the loop.
			var blockSize = MacroBlockHeaderSize + events.Count * EventSize;
			if (cursor + blockSize > BufferLength)
			{
				buf[offsetSlot] = 0;
				buf[offsetSlot + 1] = 0;
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
				buf[cursor + 2] = e.KeyCode;
				buf[cursor + 3] = e.IsPress ? AttrKeyboardPress : AttrKeyboardRelease;
				cursor += EventSize;
			}
		}

		// dataLen is the position after the last written byte; if no macros hold events
		// dataLen is the header + empty offset table.
		buf[2] = (byte)(cursor & 0xFF);
		buf[3] = (byte)((cursor >> 8) & 0xFF);

		return new PackResult(buf, cursor, droppedMouseEvents, truncatedMacros, skippedMacros);
	}

	/// <summary>
	/// Slices a packed library buffer into 56-byte chunks for CMD_SET_MACRODATA. Yields
	/// the memory offset (into the buffer), the chunk's data length (last chunk is
	/// <c>dataLen % ChunkSize</c>), the checksum (low byte of the sum of the chunk's
	/// data bytes), and the chunk data. The final chunk carries a length equal to the
	/// remainder even when the whole 56-byte window is written on the wire.
	/// <para>
	/// Deliberate divergence from the vendor: it computes the last chunk's length as
	/// <c>dataLen % ChunkSize</c>, which is 0 when dataLen is an exact multiple of the chunk
	/// size. We send the true remaining length (<c>ChunkSize</c>) in that case, since a
	/// declared length of 0 for a full chunk is nonsensical. Not known to matter — the vendor's
	/// own tool sends 0 there, so firmware evidently tolerates both.
	/// </para>
	/// </summary>
	public static IEnumerable<(ushort AddrOffset, byte ChunkLen, byte Checksum, byte[] ChunkBytes)>
		SliceChunks(byte[] packed, int dataLen)
	{
		if (packed == null) throw new ArgumentNullException(nameof(packed));
		if (dataLen <= 0) yield break;
		if (dataLen > packed.Length) throw new ArgumentOutOfRangeException(nameof(dataLen));

		var chunkCount = (dataLen + ChunkSize - 1) / ChunkSize;
		for (int i = 0; i < chunkCount; i++)
		{
			var offset = i * ChunkSize;
			var isLast = i == chunkCount - 1;
			var chunkLen = isLast ? (byte)(dataLen - offset) : (byte)ChunkSize;
			// Full 56-byte window is always written; only chunkLen (in the header) shrinks.
			var chunk = new byte[ChunkSize];
			Array.Copy(packed, offset, chunk, 0, Math.Min(ChunkSize, packed.Length - offset));

			int sum = 0;
			for (int b = 0; b < ChunkSize; b++)
				sum += chunk[b];
			var checksum = (byte)(sum & 0xFF);

			yield return ((ushort)offset, chunkLen, checksum, chunk);
		}
	}

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
			if (IsMouseEvent(e.KeyCode))
			{
				droppedMouseEvents++;   // Decision 3
				continue;
			}

			// Capacity is checked before adding, so `truncated` is only set when an event that
			// could have been encoded is actually turned away — a macro of exactly
			// MaxEventsPerMacro events is not reported as truncated.
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

// One macro library entry as seen by the encoders. Index is the on-device slot. Mode is carried
// for reference only: how the macro plays is written in the key-matrix binding's code2, not
// into the packed buffer, and the type byte we emit is always BindingTypePlay. RepeatCount is
// used by the T800 encoder only — that family stores the count in the blob header
// (Ry5088MacroEncoder) because its binding record has no room for one; Raster and Smart
// Infinite carry theirs in the binding and ignore this field.
public sealed record MacroLibraryEntry(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount = 1);
