using System.Buffers.Binary;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Framing for the Smart Infinite ("智慧无限") vendor HID protocol used by the Rampage keyboard
/// family (Rampage, Aress, Cyrex). Pure functions only — no device access — so the wire format is
/// unit-testable without hardware.
/// </summary>
/// <remarks>
/// <para>
/// Report layout (64 bytes, report id is the first byte of the payload itself, not an OS prefix):
/// </para>
/// <code>
/// byte 0      0x06        magic / report id
/// byte 1      cmd         see the Cmd* constants
/// byte 2..3   addr        little-endian byte offset into the addressed table
/// byte 4      len         payload length carried by this packet (always 56 for table transfers)
/// byte 5..6   checksum    little-endian additive sum, see <see cref="ComputeChecksum"/>
/// byte 7      0x00 in a request; 0x55 in a reply = success
/// byte 8..    payload
/// </code>
/// <para>
/// Sources, in descending order of trust: the vendor packet captures in
/// <c>docs/knowledge-base/rampage-keyboard-protocol/images/</c> (image5 is a complete working key
/// remap, image6 a complete lighting read), then PR #45's hardware testing, then the
/// knowledge-base README — which has real errors and must not be used as the primary reference.
/// </para>
/// </remarks>
public static class RampageProtocol
{
	public const int ReportLength = 64;
	public const byte MagicByte = 0x06;

	/// <summary>Header size; payload starts here.</summary>
	public const int HeaderLength = 8;

	/// <summary>Largest payload a single 64-byte report can carry.</summary>
	public const int MaxPayloadLength = ReportLength - HeaderLength;

	/// <summary>Each key occupies {TYPE, CODE_L, CODE_H} in every matrix table.</summary>
	public const int MatrixSlotSize = 3;

	/// <summary>
	/// Firmware reserve for a table (KEY_MATRIX_TAB_SIZE / FN_KEY_MATRIX_TAB_SIZE /
	/// LED_DEFINE_TAB_SIZE), i.e. 128 addressable slots. Only the first
	/// <see cref="KeyDataSize"/> bytes carry real keys; the rest is unused reserve that the vendor
	/// driver never transfers.
	/// </summary>
	public const int KeyMatrixTableSize = 384;

	/// <summary>Keys the Rampage firmware actually populates.</summary>
	public const int KeyCount = 96;

	/// <summary>
	/// Meaningful bytes in a key table: 96 keys × 3 = 288. Transfers are sized from this, not from
	/// <see cref="KeyMatrixTableSize"/> — image5 (a working remap) and image6 (a lighting read)
	/// both show the vendor driver sending exactly <c>ceil(288 / 56) = 6</c> packets, covering
	/// offsets 0x00…0x118.
	/// </summary>
	public const int KeyDataSize = KeyCount * MatrixSlotSize;

	/// <summary>Addressable slots in a 384-byte table (128), not the 96 keys the vendor populates.</summary>
	public const int MatrixSlotCount = KeyMatrixTableSize / MatrixSlotSize;

	// Session bracket. The factory update of 2026-08 (RM1) states these must NOT be sent — key
	// writes are a bare 0x18 transfer, and no other command needs the bracket either. Kept only so
	// captures and older logs can be decoded.
	public const byte CmdStartCommunication = 0x10;
	public const byte CmdEndCommunication = 0x11;

	// Device identity and configuration blocks. 64 bytes each, so two packets (offset 0 and 56).
	public const byte CmdReadDeviceInfo = 0x12;
	public const byte CmdReadFuncInfo = 0x14;

	// Base key-matrix table.
	public const byte CmdReadDefaultKeyInfo = 0x16; // factory table
	public const byte CmdReadKeyInfo = 0x17;        // current table
	public const byte CmdSetKeyInfo = 0x18;         // write current table

	/// <summary>
	/// Per-key custom lighting: same 3-bytes-per-key table layout as the key matrix, but R/G/B.
	/// Used as a control — if this table accepts a write and the key table does not, the transport
	/// is fine and the key table is specifically protected.
	/// </summary>
	public const byte CmdCustomLighting = 0x19;

	// FN-combo (layer 2) table. UNVERIFIED — these opcodes come from PR #45 and are not confirmed
	// by any capture in the vendor drop. All that is actually known about the FN layer is that the
	// firmware reserves a second table of the same size (FN_KEY_MATRIX_TAB_SIZE = 384) and defines
	// a cell type FN_COM_TYPE = 0x70. Nothing uses these yet; confirm against a capture before
	// relying on them.
	public const byte CmdReadFnDefaultKeyInfo = 0x32;
	public const byte CmdReadFnKeyInfo = 0x33;
	public const byte CmdSetFnKeyInfo = 0x34;

	// Profile. Do NOT use a 0->1->0 round trip as a reload trigger: PR #45 confirmed it freezes the
	// firmware until the keyboard is physically replugged.
	public const byte CmdReadCurrentProfile = 0x30;
	public const byte CmdSetProfile = 0x31;

	// 0x20 is "Reset" in the vendor sheet. Behavioural testing in PR #45 proved it is a FULL
	// FACTORY RESET: it wipes user config and leaves the board in rainbow/pairing mode needing
	// vendor recovery. It is deliberately not defined as a usable constant.
	//   DO NOT SEND 0x20.

	/// <summary>Status byte the firmware acks a successful command with.</summary>
	public const byte ResponseSuccess = 0x55;

	/// <summary>
	/// Builds one 64-byte report. <paramref name="payload"/> may be empty for commands that carry
	/// no data (start / end / sync).
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">Payload exceeds what one report can carry.</exception>
	public static byte[] BuildCommand(byte command, ushort addr = 0, ReadOnlySpan<byte> payload = default)
	{
		if (payload.Length > MaxPayloadLength)
			throw new ArgumentOutOfRangeException(nameof(payload),
				$"Payload of {payload.Length} bytes exceeds the {MaxPayloadLength}-byte maximum for a single report.");

		var report = new byte[ReportLength];
		report[0] = MagicByte;
		report[1] = command;
		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2, 2), addr);
		report[4] = (byte)payload.Length;
		report[7] = 0;
		payload.CopyTo(report.AsSpan(HeaderLength));

		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(5, 2), ComputeChecksum(report));

		return report;
	}

	/// <summary>
	/// Builds a read request. Unlike a write, the length byte states how many bytes the caller
	/// wants back rather than how many it is sending, and the report carries no payload.
	/// </summary>
	public static byte[] BuildReadCommand(byte command, ushort addr, byte length)
	{
		var report = new byte[ReportLength];
		report[0] = MagicByte;
		report[1] = command;
		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2, 2), addr);
		report[4] = length;
		report[7] = 0;

		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(5, 2), ComputeChecksum(report));

		return report;
	}

	/// <summary>Offset of the status byte in a reply; <see cref="ResponseSuccess"/> on success.</summary>
	public const int ResponseStatusOffset = 7;

	/// <summary>Offset the payload starts at in a reply.</summary>
	public const int ResponsePayloadOffset = 8;

	/// <summary>
	/// Plain 16-bit additive sum of <c>cmd + addr_lo + addr_hi + len</c> plus every payload byte,
	/// stored little-endian at bytes 5-6. Not a CRC.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Verified against every packet of two independent vendor captures. image6 (lighting read,
	/// cmd <c>0x19</c>, six packets) gives <c>0x19+lo+hi+0x38</c> = <c>51, 89, C1, F9, 131, 6A</c>,
	/// matching the capture byte for byte including the little-endian carry on <c>0x0131</c>.
	/// image5 (key write, cmd <c>0x18</c>, first packet <c>06 18 00 00 38 5c 05 00</c>) gives a
	/// header sum of <c>0x50</c>, leaving <c>0x055C − 0x50 = 0x050C</c> as the sum of the 56
	/// payload bytes.
	/// </para>
	/// <para>
	/// Earlier revisions of this code used CRC16/Modbus, inherited from
	/// <c>RampageKeyboardRgbService</c>. That was wrong. It also explains the anomaly this repo
	/// previously recorded as unexplainable — the README's sample <c>06 16 00 00 38 4a 00 00</c>
	/// carries <c>0x4A</c>, which is the correct checksum for command <c>0x12</c>
	/// (<c>0x12+0+0+0x38</c>), not for <c>0x16</c> (<c>0x4E</c>): the doc's author copied the
	/// packet from a neighbouring screenshot and changed the command without recomputing the sum.
	/// </para>
	/// </remarks>
	public static ushort ComputeChecksum(ReadOnlySpan<byte> report)
	{
		int payloadLength = report[4];
		int sum = report[1] + report[2] + report[3] + report[4];
		for (int i = 0; i < payloadLength; i++)
			sum += report[HeaderLength + i];

		return (ushort)sum;
	}

	/// <summary>
	/// Table-transfer payload per packet over the 2.4G receiver. Per the factory clarification C12
	/// and confirmed on hardware (2026-08-13): the receiver relays table transfers only in 24-byte
	/// payload packets — a 56-byte read comes back misaligned and a 56-byte-chunked write is
	/// discarded. The wired transport keeps <see cref="MaxPayloadLength"/>.
	/// </summary>
	public const int Receiver24GPayloadLength = 24;

	/// <summary>
	/// The (addr, length) packets a table <b>read</b> is split into:
	/// <c>ceil(dataSize / maxPayload)</c> packets, each a <b>full</b> <paramref name="maxPayload"/>
	/// bytes. For the default 288 bytes of key data over the cable that is 6 packets at offsets
	/// 0x00, 0x38, 0x70, 0xA8, 0xE0, 0x118 — covering 336 bytes, i.e. the last packet runs past
	/// the meaningful data into the table reserve.
	/// </summary>
	/// <remarks>
	/// Packets are never truncated to the exact data size: image5 and image6 both show the final
	/// packet carrying len = 0x38 like all the others. Writes use
	/// <see cref="EnumerateWriteChunks"/> instead — the factory update of 2026-08 confirmed the
	/// docx line "you must send a full 384 bytes" for the key write, so the two paths differ.
	/// </remarks>
	public static IReadOnlyList<(ushort Addr, int Length)> EnumerateChunks(
		int dataSize = KeyDataSize, int maxPayload = MaxPayloadLength)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(dataSize);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxPayload, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayload, MaxPayloadLength);

		int packets = (dataSize + maxPayload - 1) / maxPayload;
		var chunks = new List<(ushort, int)>(packets);
		for (int i = 0; i < packets; i++)
			chunks.Add(((ushort)(i * maxPayload), maxPayload));

		return chunks;
	}

	/// <summary>
	/// The (addr, length) packets a key-table <b>write</b> is split into, totalling exactly
	/// <see cref="KeyMatrixTableSize"/>: over the cable 7 packets — six of 56 bytes plus a final
	/// 48 at offset 0x150 — and over the 2.4G receiver 16 packets of 24.
	/// </summary>
	/// <remarks>
	/// Per the factory update of 2026-08 (RM1): a key write is a bare <see cref="CmdSetKeyInfo"/>
	/// transfer of exactly 384 bytes, with no 0x10/0x11 session bracket. The 336-byte shape the
	/// vendor capture (image5) shows is what the firmware silently discards — every earlier attempt
	/// acked 0x55 and never persisted.
	/// </remarks>
	public static IReadOnlyList<(ushort Addr, int Length)> EnumerateWriteChunks(int maxPayload = MaxPayloadLength)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxPayload, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayload, MaxPayloadLength);

		var chunks = new List<(ushort, int)>((KeyMatrixTableSize + maxPayload - 1) / maxPayload);
		for (int off = 0; off < KeyMatrixTableSize; off += maxPayload)
			chunks.Add(((ushort)off, Math.Min(maxPayload, KeyMatrixTableSize - off)));

		return chunks;
	}

	/// <summary>
	/// Reads the {TYPE, CODE_L, CODE_H} record at <paramref name="slot"/> out of a full table.
	/// </summary>
	public static KeyMatrixRecord ReadSlot(ReadOnlySpan<byte> table, int slot)
	{
		ValidateTableAndSlot(table.Length, slot);
		int offset = slot * MatrixSlotSize;
		return new KeyMatrixRecord(table[offset], table[offset + 1], table[offset + 2]);
	}

	/// <summary>
	/// Overwrites the record at <paramref name="slot"/> in place, leaving every other byte of the
	/// table untouched. The caller is expected to have read the real table first — this is the
	/// modify step of a read-modify-write.
	/// </summary>
	public static void PatchSlot(Span<byte> table, int slot, KeyMatrixRecord record)
	{
		ValidateTableAndSlot(table.Length, slot);
		int offset = slot * MatrixSlotSize;
		table[offset] = record.Type;
		table[offset + 1] = record.CodeLow;
		table[offset + 2] = record.CodeHigh;
	}

	/// <summary>
	/// Expands a full table into <c>slot -&gt; record</c> for every slot up to
	/// <paramref name="maxSlot"/>, skipping slots that are entirely zero (unused padding).
	/// </summary>
	public static IReadOnlyDictionary<byte, KeyMatrixRecord> ParseTable(ReadOnlySpan<byte> table, int maxSlot = MatrixSlotCount)
	{
		if (table.Length != KeyMatrixTableSize)
			throw new ArgumentException(
				$"Expected a {KeyMatrixTableSize}-byte table, got {table.Length}.", nameof(table));

		int limit = Math.Min(maxSlot, MatrixSlotCount);
		var map = new Dictionary<byte, KeyMatrixRecord>(limit);
		for (int slot = 0; slot < limit; slot++)
		{
			var record = ReadSlot(table, slot);
			if (record.IsZero) continue;
			map[(byte)slot] = record;
		}
		return map;
	}

	/// <summary>
	/// Every TYPE byte the firmware defines (keyboard_key_define.h). Anything else in a type
	/// position means the table is not aligned to 3-byte records.
	/// </summary>
	private static readonly HashSet<byte> KnownTypes =
	[
		0x10, 0x11,                               // keyboard, repeat
		0x20, 0x30,                               // consumer, system
		0x40, 0x41, 0x42, 0x43, 0x44, 0x45,       // mouse
		0x50, 0x51, 0x52,                         // macro
		0x60, 0x61, 0x70,                         // custom fn, text, fn-combo
		0x80, 0x81, 0x82, 0x83, 0x84,             // advanced keys
	];

	/// <summary>
	/// Cheap sanity check that a table really is a 3-byte-aligned key matrix, used to refuse a
	/// misaligned read before it can be written back and scramble the keyboard.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A transfer that lands one 56-byte chunk out of place shifts the whole table by 56. Since
	/// 56 ≡ 2 (mod 3), record boundaries break and the TYPE column is fed from what was really the
	/// modifier column.
	/// </para>
	/// <para>
	/// That is exactly why <c>0x00</c> is <b>not</b> accepted as a valid type here: the modifier
	/// byte is <c>0x00</c> for every non-modifier key, so a shifted table otherwise reads as a
	/// plausible page of "unwritten slots" and sails through. The vendor factory table populates
	/// all 96 slots with a non-zero type, so requiring that is both safe and decisive.
	/// </para>
	/// </remarks>
	public static bool IsPlausibleKeyTable(ReadOnlySpan<byte> table, out int validSlots)
	{
		validSlots = 0;
		if (table.Length != KeyMatrixTableSize) return false;

		for (int slot = 0; slot < KeyCount; slot++)
		{
			if (KnownTypes.Contains(table[slot * MatrixSlotSize]))
				validSlots++;
		}

		// A correctly aligned table scores 96/96. Allow a small margin for firmware revisions that
		// introduce a type we don't know yet, but nothing close to what a misalignment produces.
		return validSlots >= KeyCount - 6;
	}

	private static void ValidateTableAndSlot(int tableLength, int slot)
	{
		if (tableLength != KeyMatrixTableSize)
			throw new ArgumentException(
				$"Expected a {KeyMatrixTableSize}-byte table, got {tableLength}.", nameof(tableLength));

		if (slot < 0 || slot >= MatrixSlotCount)
			throw new ArgumentOutOfRangeException(nameof(slot),
				$"Slot {slot} is outside the addressable range 0..{MatrixSlotCount - 1}.");
	}
}
