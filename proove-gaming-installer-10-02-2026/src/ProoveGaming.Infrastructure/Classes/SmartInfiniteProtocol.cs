using System.Buffers.Binary;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Framing for the Smart Infinite ("智慧无限") vendor HID protocol used by the Rampage / Aress / Cyrex
/// keyboards. Pure functions only — no device access — so the wire format is unit-testable without
/// hardware.
/// </summary>
/// <remarks>
/// <para>Report layout (64 bytes; the magic byte is part of the payload, not an OS report-ID prefix):</para>
/// <code>
/// byte 0      0x06        magic
/// byte 1      cmd         see the Cmd* constants
/// byte 2..3   addr        little-endian byte offset into the addressed table
/// byte 4      len         payload bytes carried (write) or requested (read); max 56
/// byte 5..6   checksum    little-endian additive sum, see <see cref="ComputeChecksum"/>
/// byte 7      0x00 in a request; 0x55 in a reply = success
/// byte 8..    payload
/// </code>
/// <para>
/// Two behaviours are not obvious from the vendor documentation and were established on hardware:
/// every reply arrives <b>one command behind</b> its request, so replies must be matched by the
/// address they echo back rather than by arrival order; and the Start ack must never be drained
/// before the first real request, because reading the input pipe that early leaves the HidSharp
/// Windows stream in a state where subsequent writes fail.
/// </para>
/// <para>
/// Sources, in descending order of trust: the vendor packet captures under
/// <c>docs/knowledge-base/rampage-keyboard-protocol/images/</c>, then hardware measurements, then
/// the written vendor documents — which contain real errors (a transposed read/write command pair,
/// a wrong packet layout, and a CRC that is not a CRC).
/// </para>
/// </remarks>
public static class SmartInfiniteProtocol
{
	public const int ReportLength = 64;
	public const byte MagicByte = 0x06;

	/// <summary>Header size; payload starts here.</summary>
	public const int HeaderLength = 8;

	/// <summary>Largest payload one 64-byte report can carry.</summary>
	public const int MaxPayloadLength = ReportLength - HeaderLength;

	/// <summary>
	/// Table-transfer payload per packet over the 2.4G receiver (factory clarification C12,
	/// hardware-confirmed on the Rampage receiver 2026-08-13): the receiver relays table transfers
	/// only in 24-byte payload packets — 56-byte reads arrive misaligned and 56-byte-chunked
	/// writes are discarded. Identity/config blocks (<see cref="CmdReadFuncInfo"/> etc.) are
	/// unaffected and keep their 56+8 shape on every transport.
	/// </summary>
	public const int Receiver24GPayloadLength = 24;

	/// <summary>Each key occupies {TYPE, CODE_L, CODE_H} in every matrix table.</summary>
	public const int MatrixSlotSize = 3;

	/// <summary>
	/// Largest table reserve seen on this family (128 slots), used to size receive buffers.
	/// <b>Read</b> transfers are sized from the matrix the device reports in Device Info, which
	/// differs per model — Cyrex declares 75 slots (225 bytes), Rampage 128 (384).
	/// </summary>
	public const int MaxTableBytes = 384;

	/// <summary>
	/// Full key-matrix table reserve — <c>KEY_MATRIX_TAB_SIZE</c> per the factory's 2026-08
	/// clarifications (C8). A <b>write</b> must transfer exactly this many bytes regardless of how
	/// many slots the model populates; shorter transfers are acknowledged and discarded, which is
	/// what kept Rampage key remapping dead until the RM1 update spelled it out. The tail past the
	/// populated slots is zero, matching the firmware's own empty reserve.
	/// </summary>
	public const int KeyMatrixTableSize = 384;

	/// <summary>
	/// Full macro table reserve — <c>MACRO_TAB_SIZE</c> per the factory's 2026-08 clarifications
	/// (C8). The <see cref="CmdSetMacroData"/> transfer length, on the same full-table rule as
	/// <see cref="KeyMatrixTableSize"/>. Note Cyrex firmware 0x0002 under-reports 1024 in Device
	/// Info byte 5 — the C8 constant outranks that field for transfer sizing, the same way the
	/// key table is 384 while Cyrex reports a 225-byte matrix.
	/// </summary>
	public const int MacroTableSize = 2048;

	// Session bracket. The factory's 2026-08 clarifications (C1) state these are NOT used — for
	// the key write or any other command; a bracketed short transfer was the acknowledge-and-
	// discard cause. Kept only so captures and older logs can be decoded.
	public const byte CmdStartCommunication = 0x10;
	public const byte CmdEndCommunication = 0x11;

	// Identity and configuration blocks — 64 bytes each, so two packets (offset 0, then 56).
	public const byte CmdReadDeviceInfo = 0x12;
	public const byte CmdReadFuncInfo = 0x14;
	public const byte CmdSetFuncInfo = 0x15;

	// Base key matrix.
	public const byte CmdReadDefaultKeyInfo = 0x16; // factory table
	public const byte CmdReadKeyInfo = 0x17;        // current table
	public const byte CmdSetKeyInfo = 0x18;         // write current table

	/// <summary>Per-key custom lighting — same 3-bytes-per-key table shape, but R/G/B.</summary>
	public const byte CmdCustomLighting = 0x19;

	/// <summary>
	/// Reads the macro library region — <c>gethongMessage</c> (读取宏信息) in the vendor Keyborder
	/// app's command table for this firmware line.
	/// </summary>
	public const byte CmdReadMacroInfo = 0x1B;

	/// <summary>
	/// Writes the macro library region — <c>sethongMessage</c> (设定宏信息): the whole formatted
	/// buffer (sized to Device Info's macro space), chunk-addressed like every other table.
	/// </summary>
	/// <remarks>
	/// The counterpart read is <see cref="CmdReadMacroInfo"/>. See
	/// <c>docs/knowledge-base/smart-infinite-protocol/macro-format-gamingkeyboard2.md</c> for the
	/// buffer layout (<c>formatHongRect</c>): 20-slot offset table, blocks of
	/// <c>{count, reserved, 4-byte actions}</c>, modifiers as hot-key events.
	/// </remarks>
	public const byte CmdWriteMacroInfo = 0x1C;

	/// <summary>
	/// Superseded — kept only so old logs and captures can be decoded. <c>0x2D</c>/<c>0x2C</c> come
	/// from the V3.1 workbook, whose command numbering belongs to a different firmware line; the
	/// vendor's Keyborder app never sends either on our boards. Every "0x2D confirmed working"
	/// observation was ack-only: the all-zero read-back in
	/// <c>docs/plans/add-cyrex-aress-parity/macro-investigation-result.md</c> is what a
	/// wrong-command write looks like. The live path is <see cref="CmdWriteMacroInfo"/>.
	/// </summary>
	public const byte CmdSetMacroData = 0x2D;

	/// <summary>Superseded counterpart of <see cref="CmdSetMacroData"/> — see its remarks.</summary>
	public const byte CmdReadMacroData = 0x2C;

	// Magnetic-axis commands (Cyrex / Aress). Extracted from the vendor's Keyborder-v3 driver app,
	// whose command table matches this firmware's measured numbering (0x17/0x18 matrix, 0x20 reset)
	// where the V3.1 workbook did not. Not yet exercised on our units — the actuation service reads
	// and validates before it ever writes. See
	// docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md.
	public const byte CmdEnterExitTravelTest = 0x36;
	public const byte CmdEnterExitCalibration = 0x37;
	public const byte CmdReadTravelArea = 0x38;
	public const byte CmdWriteTravelArea = 0x39;
	public const byte CmdReadAdvancedKeyArea = 0x3A;
	public const byte CmdWriteAdvancedKeyArea = 0x3B;

	/// <summary>
	/// Key-record TYPE that plays a macro with a stop mode — <c>MACRO_NO_COUNT</c> in this
	/// firmware's own <c>KeyType</c> enum (from the vendor's Keyborder app). <c>CODE_L</c> is the
	/// macro's slot in the library, <c>CODE_H</c> the stop mode: <c>0x00</c> play through once,
	/// <c>0x01</c> play while held, <c>0x02</c> press-and-release starts / press stops,
	/// <c>0x03</c> press to start, press again to stop.
	/// </summary>
	/// <remarks>
	/// <b>This byte has now flipped three times; here is the per-family truth and its source, so a
	/// fourth flip requires new hardware evidence, not another document.</b> The Raster/T-Racer
	/// family uses <c>0x60</c>/<c>0x61</c> (hardware-verified). The V3.1 workbook's enum
	/// (<c>0x60</c> = macro, <c>0x50</c> = FUNC) describes a <i>different</i> firmware line — the
	/// same sheet whose command numbering our boards contradict. This firmware's enum, extracted
	/// from the vendor app that drives our Cyrex (<c>index-CDyQl7nP.js</c>, <c>KeyType</c>):
	/// <c>0x50</c>/<c>0x51</c> macro, <b><c>0x60</c> = FUNCTION</b>, <c>0x61</c> = TEXT_OUTPUT —
	/// which is why a <c>{0x60, slot, mode}</c> binding is stored, verifies, and plays nothing.
	/// </remarks>
	public const byte TypeMacroStopMode = 0x50;

	/// <summary>
	/// Key-record TYPE that plays a macro a fixed number of times — <c>MACRO_WITH_COUNT</c>.
	/// <c>CODE_L</c> is the macro slot, <c>CODE_H</c> the play count (1..255). The only place a
	/// repeat count fits — <see cref="TypeMacroStopMode"/> spends <c>CODE_H</c> on the stop mode.
	/// </summary>
	public const byte TypeMacroPlayCount = 0x51;

	/// <summary>
	/// Legacy macro type bytes some boards of this family carry in factory tables; the vendor app
	/// recognises them on read (<c>LEGACY_MACRO_KEY_TYPES</c>) but never writes them.
	/// </summary>
	public const byte TypeMacroLegacyStopMode = 0xC0;
	public const byte TypeMacroLegacyPlayCount = 0xC1;

	// Advanced-key ("Extended Keys") record TYPEs. A key-matrix triplet carrying one of these is a
	// pointer {TYPE, off_lo, off_hi} into the advanced-key storage area (CmdReadAdvancedKeyArea /
	// CmdWriteAdvancedKeyArea), where the record itself starts with the same TYPE byte. Values from
	// the vendor's Keyborder-v3 app (ADV_DKS .. ADV_END) — see
	// docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md.
	public const byte TypeExtendedDks = 0x80;
	public const byte TypeExtendedModTap = 0x81;
	public const byte TypeExtendedToggle = 0x82;
	public const byte TypeExtendedSocd = 0x83;
	public const byte TypeExtendedRapidSnap = 0x84;
	public const byte TypeExtendedEnd = 0x85;

	/// <summary>True for every advanced-key TYPE byte (<c>0x80</c>..<c>0x85</c>) — a matrix pointer, not a key.</summary>
	public static bool IsExtendedKeyType(byte type) => type >= TypeExtendedDks && type <= TypeExtendedEnd;

	// 0x20 is "Reset" in the vendor sheet. Behavioural testing proved it is a FULL FACTORY RESET
	// that wipes user config and leaves the board in pairing mode needing vendor recovery.
	//   DO NOT SEND 0x20.

	/// <summary>Status byte the firmware acks a received packet with. Does not imply it was applied.</summary>
	public const byte ResponseSuccess = 0x55;

	/// <summary>Offset of the status byte in a reply.</summary>
	public const int ResponseStatusOffset = 7;

	/// <summary>Offset the payload starts at in a reply.</summary>
	public const int ResponsePayloadOffset = 8;

	/// <summary>Builds one 64-byte report. Payload may be empty for start / end / sync commands.</summary>
	public static byte[] BuildCommand(byte command, ushort addr = 0, ReadOnlySpan<byte> payload = default)
	{
		if (payload.Length > MaxPayloadLength)
			throw new ArgumentOutOfRangeException(nameof(payload),
				$"Payload of {payload.Length} bytes exceeds the {MaxPayloadLength}-byte maximum for one report.");

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
	/// Builds a read request: the length byte states how many bytes the caller wants back, and the
	/// report carries no payload.
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

	/// <summary>
	/// Plain 16-bit additive sum of <c>cmd + addr_lo + addr_hi + len</c> plus every payload byte,
	/// stored little-endian at bytes 5-6. <b>Not a CRC</b>, despite the vendor sheet labelling the
	/// field CRC16 — and the sheet also notes the field is reserved and unverified by the firmware.
	/// </summary>
	/// <remarks>
	/// Verified byte-for-byte against every packet of two vendor captures: the lighting read
	/// (cmd <c>0x19</c>) yields <c>51, 89, C1, F9, 131, 6A</c> across its six offsets, including the
	/// little-endian carry on <c>0x0131</c>; and the key write (cmd <c>0x18</c>, first packet
	/// <c>06 18 00 00 38 5c 05 00</c>) leaves <c>0x055C − 0x50 = 0x050C</c> as the payload sum.
	/// </remarks>
	public static ushort ComputeChecksum(ReadOnlySpan<byte> report)
	{
		int payloadLength = report[4];
		int sum = report[1] + report[2] + report[3] + report[4];
		for (int i = 0; i < payloadLength && HeaderLength + i < report.Length; i++)
			sum += report[HeaderLength + i];
		return (ushort)sum;
	}

	/// <summary>
	/// Splits a transfer of exactly <paramref name="dataSize"/> bytes into (addr, length) packets.
	/// The final packet is trimmed so a transfer never runs past the matrix the device declares:
	/// Cyrex reserves 225 bytes, so five full 56-byte packets would write 55 bytes beyond it.
	/// </summary>
	public static IReadOnlyList<(ushort Addr, int Length)> EnumerateChunks(int dataSize, int maxPayload = MaxPayloadLength)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(dataSize);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxPayload, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayload, MaxPayloadLength);

		var chunks = new List<(ushort, int)>((dataSize + maxPayload - 1) / maxPayload);
		for (int offset = 0; offset < dataSize; offset += maxPayload)
			chunks.Add(((ushort)offset, Math.Min(maxPayload, dataSize - offset)));
		return chunks;
	}

	/// <summary>Reads the {TYPE, CODE_L, CODE_H} record at <paramref name="slot"/>.</summary>
	public static KeyMatrixRecord ReadSlot(ReadOnlySpan<byte> table, int slot)
	{
		int offset = slot * MatrixSlotSize;
		if (offset + MatrixSlotSize > table.Length)
			throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} is past the end of a {table.Length}-byte table.");
		return new KeyMatrixRecord(table[offset], table[offset + 1], table[offset + 2]);
	}

	/// <summary>
	/// Overwrites the record at <paramref name="slot"/> in place, leaving every other byte untouched.
	/// The caller is expected to have read the real table first — this is the modify step of a
	/// read-modify-write.
	/// </summary>
	public static void PatchSlot(Span<byte> table, int slot, KeyMatrixRecord record)
	{
		int offset = slot * MatrixSlotSize;
		if (offset + MatrixSlotSize > table.Length)
			throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} is past the end of a {table.Length}-byte table.");
		table[offset] = record.Type;
		table[offset + 1] = record.CodeLow;
		table[offset + 2] = record.CodeHigh;
	}

	/// <summary>
	/// Expands a table into <c>slot -&gt; record</c> for the first <paramref name="slots"/> slots,
	/// skipping all-zero entries. Unused placeholders (<c>{0x10,0x00,0x00}</c>) are kept: they are
	/// real records and dropping them would misalign a caller counting positions.
	/// </summary>
	public static IReadOnlyDictionary<byte, KeyMatrixRecord> ParseTable(ReadOnlySpan<byte> table, int slots)
	{
		var map = new Dictionary<byte, KeyMatrixRecord>(slots);
		for (int slot = 0; slot < slots && (slot + 1) * MatrixSlotSize <= table.Length; slot++)
		{
			var record = ReadSlot(table, slot);
			if (record.IsZero) continue;
			map[(byte)slot] = record;
		}
		return map;
	}

	/// <summary>
	/// Every TYPE byte the firmware defines (keyboard_key_define.h). Used to sanity-check a table
	/// before writing it back.
	/// </summary>
	private static readonly HashSet<byte> KnownTypes =
	[
		// Names from the vendor's Keyboard_Key_Type enum. The set is deliberately WIDER than that
		// enum: this is only a "does this look like a 3-byte-aligned table" sanity check, and real
		// Cyrex tables carry types the enum omits (0x84 = Rapid Snap pointer, read off the hardware).
		// Rejecting a type the firmware actually uses would refuse a legitimate write.
		0x10, 0x11, 0x12,                         // KB_TYPE / KB_TYPE1 / KB_TYPE2 — basic keys
		0x20, 0x30,                               // SYSTEM_TYPE, CONSUMER_TYPE
		0x40, 0x41, 0x42, 0x43, 0x44, 0x45,       // MS_TYPE — mouse
		// 0x52 is not in this firmware's KeyType enum; kept because this is a wider-than-the-enum
		// sanity set (see the remark above) and it was in the measured pre-fix table acceptance.
		0x50, 0x51, 0x52,                         // MACRO_NO_COUNT, MACRO_WITH_COUNT (+0x52 margin)
		0x60, 0x61,                               // FUNCTION, TEXT_OUTPUT
		0x70,                                     // FN_COMBO
		TypeExtendedDks, TypeExtendedModTap, TypeExtendedToggle, TypeExtendedSocd, TypeExtendedRapidSnap,
		TypeExtendedEnd,                          // advanced-key pointers (DKS/MT/TGL/SOCD/RS/END); 0x84 read off hardware
		TypeMacroLegacyStopMode, TypeMacroLegacyPlayCount,
	];

	/// <summary>
	/// Cheap check that a table really is a 3-byte-aligned key matrix, so a misread is never written
	/// back and used to scramble the keyboard.
	/// </summary>
	/// <remarks>
	/// A transfer that lands one 56-byte packet out of place shifts the table by 56, and since
	/// 56 ≡ 2 (mod 3) the record boundaries break and the TYPE column is fed from what was really the
	/// modifier column. <c>0x00</c> is deliberately <b>not</b> accepted as a valid type: the modifier
	/// byte is <c>0x00</c> for every non-modifier key, so a shifted table would otherwise read as a
	/// plausible page of blank slots and pass. Every slot on a real table carries a non-zero type.
	/// </remarks>
	public static bool IsPlausibleKeyTable(ReadOnlySpan<byte> table, int slots, out int validSlots)
	{
		validSlots = 0;
		if (slots <= 0 || slots * MatrixSlotSize > table.Length) return false;

		for (int slot = 0; slot < slots; slot++)
			if (KnownTypes.Contains(table[slot * MatrixSlotSize])) validSlots++;

		// Allow a small margin for a firmware revision introducing a type we don't know yet —
		// nothing close to what a misalignment produces.
		return validSlots >= slots - 4;
	}
}
