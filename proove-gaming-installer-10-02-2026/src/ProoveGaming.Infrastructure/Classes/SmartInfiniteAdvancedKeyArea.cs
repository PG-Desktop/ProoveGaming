using System.Buffers.Binary;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// One record found in the advanced-key storage area: its TYPE, where it sits, its exact bytes and
/// the matrix slots whose triplets point at it. Unmanaged records travel through a rebuild as these
/// bytes, verbatim.
/// </summary>
public sealed record AdvancedKeyRecord(byte Type, int Offset, byte[] Bytes, IReadOnlyList<int> Slots)
{
	public int Length => Bytes.Length;
}

/// <summary>A decoded Rapid Snap record — the two slots and each key's firmware <c>(codeType, code)</c>.</summary>
public readonly record struct RapidSnapRecord(int Slot1, byte CodeType1, byte Code1, int Slot2, byte CodeType2, byte Code2);

/// <summary>
/// A decoded Instant Release (SOCD) record — the Rapid Snap shape plus the priority nibble. <see cref="Priority"/>
/// is the raw high nibble of LEN (0..15): the codec does not judge it, the service does.
/// </summary>
public readonly record struct SocdRecord(int Slot1, byte CodeType1, byte Code1, int Slot2, byte CodeType2, byte Code2, int Priority);

/// <summary>
/// A decoded Mod-Tap record — the hold threshold in milliseconds and the firmware <c>(codeType, code)</c>
/// of the hold and tap outputs. The physical key is not in the record: it is whichever slot points at it.
/// </summary>
public readonly record struct ModTapRecord(int TimeMs, byte HoldCodeType, byte HoldCode, byte TapCodeType, byte TapCode);

/// <summary>
/// A decoded Toggle record — the threshold in milliseconds and the firmware <c>(codeType, code)</c> of the
/// toggled output. As for Mod-Tap, the physical key is whichever slot points at the record.
/// </summary>
public readonly record struct ToggleRecord(int TimeMs, byte CodeType, byte Code);

/// <summary>
/// Codec for the Smart Infinite advanced-key storage area (<see cref="SmartInfiniteProtocol.CmdReadAdvancedKeyArea"/>
/// / <see cref="SmartInfiniteProtocol.CmdWriteAdvancedKeyArea"/>) and the key-matrix pointers into it.
/// Pure functions only, so the wire format is unit-testable without hardware.
/// </summary>
/// <remarks>
/// <para>The area is a flat concatenation of <c>[TYPE, LEN, payload[LEN]]</c> records with no header
/// and no gaps. For SOCD (<c>0x83</c>) the LEN byte's low nibble is the length and the high nibble the
/// priority — parse with the mask or every record after it misaligns. A participating key's matrix
/// triplet is <c>{TYPE, off_lo, off_hi}</c>; both keys of a pair (RS, SOCD) point at one record.</para>
/// <para>The area is rebuilt whole on every save, in the vendor's group order (TGL, MT, SOCD, RS,
/// then the unmanaged DKS / END in their original order), and every affected pointer is re-aimed at
/// the new offset — offsets are not stable across saves, only the pointers give a record its identity.
/// Source: <c>docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md</c>, extracted
/// from the vendor's Keyborder-v3 app.</para>
/// <para><b>Extension point:</b> one <c>Encode*</c> / <c>TryDecode*</c> pair per managed mode. Rapid
/// Snap, SOCD, Mod-Tap and Toggle are here; <see cref="ParseRecords"/> and <see cref="BuildArea"/> are type-agnostic.</para>
/// </remarks>
public static class SmartInfiniteAdvancedKeyArea
{
	/// <summary>Bytes of a Rapid Snap record: TYPE, LEN and a 6-byte payload.</summary>
	public const int RapidSnapRecordBytes = 8;
	private const byte RapidSnapPayloadLength = 6;

	/// <summary>Bytes of an Instant Release (SOCD) record — the Rapid Snap shape with the priority in LEN's high nibble.</summary>
	public const int SocdRecordBytes = 8;
	private const byte SocdPayloadLength = 6;

	/// <summary>
	/// The SOCD priorities the firmware defines (<c>PrioritySetting</c> in the vendor app): 0 last input
	/// wins, 1 neither fires, 2 Key 1 (byte 2's slot) always wins, 3 Key 2 (byte 5's slot) always wins.
	/// </summary>
	public const int SocdPriorityLastInput = 0;
	public const int SocdPriorityMutualCancel = 1;
	public const int SocdPriorityKey1 = 2;
	public const int SocdPriorityKey2 = 3;
	public const int SocdPriorityMax = SocdPriorityKey2;

	/// <summary>Bytes of a Mod-Tap record: TYPE, LEN, a 16-bit time and the two <c>(codeType, code)</c> outputs.</summary>
	public const int ModTapRecordBytes = 8;
	private const byte ModTapPayloadLength = 6;

	/// <summary>Bytes of a Toggle record: TYPE, LEN, a 16-bit time and one <c>(codeType, code)</c> output.</summary>
	public const int ToggleRecordBytes = 6;
	private const byte TogglePayloadLength = 4;

	/// <summary>
	/// The MT / TGL hold threshold range in milliseconds — the vendor slider's 0..1000 (step 1,
	/// default 100). The wire field is a uint16, so the cap is a product rule, not a codec limit.
	/// </summary>
	public const int TimeMsMin = 0;
	public const int TimeMsMax = 1000;

	/// <summary>Firmware code types carried in a pair record (and in the MT / TGL outputs).</summary>
	public const byte CodeTypeKeyboardUsage = 1;
	public const byte CodeTypeModifierMask = 2;

	/// <summary>Smallest record the area can hold — TYPE and LEN with an empty payload.</summary>
	private const int RecordHeaderBytes = 2;

	/// <summary>
	/// Walks the first <paramref name="matrixSlots"/> slots of <paramref name="matrix"/> and collects
	/// every record an advanced-key pointer aims at, in offset order. Returns <c>null</c> when the
	/// area cannot be trusted — a pointer past the area, a record that runs past its end, a TYPE at
	/// the offset that does not match the pointer, or two records that overlap. A null result must
	/// be treated as "unsupported": rebuilding an area that was not read correctly would destroy
	/// records the App never understood.
	/// </summary>
	public static IReadOnlyList<AdvancedKeyRecord>? ParseRecords(ReadOnlySpan<byte> matrix, int matrixSlots, ReadOnlySpan<byte> area)
	{
		var byOffset = new SortedDictionary<int, (byte Type, int Length, List<int> Slots)>();

		for (int slot = 0; slot < matrixSlots && (slot + 1) * SmartInfiniteProtocol.MatrixSlotSize <= matrix.Length; slot++)
		{
			var record = SmartInfiniteProtocol.ReadSlot(matrix, slot);
			if (!SmartInfiniteProtocol.IsExtendedKeyType(record.Type)) continue;

			int offset = record.CodeLow | (record.CodeHigh << 8);
			if (offset + RecordHeaderBytes > area.Length) return null;
			if (area[offset] != record.Type) return null;

			int length = RecordLength(area, offset);
			if (offset + length > area.Length) return null;

			if (byOffset.TryGetValue(offset, out var existing))
			{
				if (existing.Type != record.Type) return null;
				existing.Slots.Add(slot);
			}
			else
			{
				byOffset[offset] = (record.Type, length, [slot]);
			}
		}

		var records = new List<AdvancedKeyRecord>(byOffset.Count);
		int previousEnd = 0;
		foreach (var (offset, (type, length, slots)) in byOffset)
		{
			if (offset < previousEnd) return null;
			previousEnd = offset + length;
			records.Add(new AdvancedKeyRecord(type, offset, area.Slice(offset, length).ToArray(), slots));
		}

		return records;
	}

	/// <summary>Total bytes of the record at <paramref name="offset"/> (TYPE + LEN + payload), honouring SOCD's length nibble.</summary>
	public static int RecordLength(ReadOnlySpan<byte> area, int offset)
	{
		byte len = area[offset + 1];
		if (area[offset] == SmartInfiniteProtocol.TypeExtendedSocd)
			len &= 0x0F;
		return RecordHeaderBytes + len;
	}

	/// <summary>
	/// Lays <paramref name="records"/> out as one compact area of exactly <paramref name="areaBytes"/>
	/// bytes (zero-padded — the vendor always writes the full area), in the vendor's group order.
	/// Returns the area and, aligned with the <b>input</b> order, each record's new offset; <c>null</c>
	/// when the records do not fit. Record bytes are copied verbatim, so an unmanaged record survives
	/// byte-for-byte.
	/// </summary>
	public static (byte[] Area, int[] Offsets)? BuildArea(IReadOnlyList<byte[]> records, int areaBytes)
	{
		var order = new List<int>(records.Count);
		for (int i = 0; i < records.Count; i++) order.Add(i);
		// Stable: within a group the input order is kept, which is what keeps DKS / END where the
		// vendor left them relative to each other.
		order.Sort((a, b) =>
		{
			int byGroup = GroupRank(records[a]).CompareTo(GroupRank(records[b]));
			return byGroup != 0 ? byGroup : a.CompareTo(b);
		});

		var area = new byte[areaBytes];
		var offsets = new int[records.Count];
		int cursor = 0;
		foreach (int index in order)
		{
			var bytes = records[index];
			if (cursor + bytes.Length > areaBytes) return null;
			bytes.CopyTo(area, cursor);
			offsets[index] = cursor;
			cursor += bytes.Length;
		}

		return (area, offsets);
	}

	private static int GroupRank(byte[] record) => record.Length == 0 ? 4 : record[0] switch
	{
		SmartInfiniteProtocol.TypeExtendedToggle => 0,
		SmartInfiniteProtocol.TypeExtendedModTap => 1,
		SmartInfiniteProtocol.TypeExtendedSocd => 2,
		SmartInfiniteProtocol.TypeExtendedRapidSnap => 3,
		_ => 4,
	};

	/// <summary>The matrix triplet that points a key at the record of <paramref name="type"/> at <paramref name="offset"/>.</summary>
	public static KeyMatrixRecord PointerRecord(byte type, int offset) =>
		new(type, (byte)(offset & 0xFF), (byte)((offset >> 8) & 0xFF));

	/// <summary>
	/// The firmware's <c>(codeType, code)</c> for a key from its <c>{0x10, mask, usage}</c> record —
	/// <c>getKeyCodeAndType</c> in the vendor app: a usage wins, a modifier-only key is carried as its
	/// bitmask. <c>null</c> for anything that is not a plain keyboard record or names no key at all
	/// (an unused placeholder), neither of which can join an advanced key.
	/// </summary>
	public static (byte CodeType, byte Code)? KeyCodeFromRecord(KeyMatrixRecord record)
	{
		if (record.Type != KeyMatrixRecord.TypeKeyboardKey) return null;
		if (record.CodeHigh != 0) return (CodeTypeKeyboardUsage, record.CodeHigh);
		if (record.CodeLow != 0) return (CodeTypeModifierMask, record.CodeLow);
		return null;
	}

	/// <summary>
	/// The inverse of <see cref="KeyCodeFromRecord"/>: the <c>{0x10, mask, usage}</c> record a firmware
	/// <c>(codeType, code)</c> output stands for — what an MT / TGL output reads back as. <c>null</c>
	/// for a code type this build cannot express as a keyboard record (mouse buttons) or a zero code.
	/// </summary>
	public static KeyMatrixRecord? KeyCodeToRecord(byte codeType, byte code)
	{
		if (code == 0) return null;
		return codeType switch
		{
			CodeTypeKeyboardUsage => new KeyMatrixRecord(KeyMatrixRecord.TypeKeyboardKey, 0, code),
			CodeTypeModifierMask => new KeyMatrixRecord(KeyMatrixRecord.TypeKeyboardKey, code, 0),
			_ => null,
		};
	}

	/// <summary><c>[0x84, 6, slot1, codeType1, code1, slot2, codeType2, code2]</c>.</summary>
	public static byte[] EncodeRapidSnap(int slot1, (byte CodeType, byte Code) key1, int slot2, (byte CodeType, byte Code) key2) =>
	[
		SmartInfiniteProtocol.TypeExtendedRapidSnap, RapidSnapPayloadLength,
		(byte)slot1, key1.CodeType, key1.Code,
		(byte)slot2, key2.CodeType, key2.Code,
	];

	/// <summary>Decodes a Rapid Snap record; false when the bytes are not one (wrong TYPE or LEN).</summary>
	public static bool TryDecodeRapidSnap(ReadOnlySpan<byte> record, out RapidSnapRecord decoded)
	{
		decoded = default;
		if (record.Length != RapidSnapRecordBytes) return false;
		if (record[0] != SmartInfiniteProtocol.TypeExtendedRapidSnap || record[1] != RapidSnapPayloadLength) return false;

		decoded = new RapidSnapRecord(record[2], record[3], record[4], record[5], record[6], record[7]);
		return true;
	}

	/// <summary>The SOCD LEN byte: <c>priority &lt;&lt; 4 | 6</c> — <c>0x06</c>, <c>0x16</c>, <c>0x26</c>, <c>0x36</c> for the four priorities.</summary>
	public static byte SocdLength(int priority)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(priority);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, SocdPriorityMax);
		return (byte)((priority << 4) | SocdPayloadLength);
	}

	/// <summary>The priority nibble of a SOCD LEN byte, unjudged (a foreign record may carry 4..15).</summary>
	public static int SocdPriorityFromLength(byte length) => length >> 4;

	/// <summary><c>[0x83, priority &lt;&lt; 4 | 6, slot1, codeType1, code1, slot2, codeType2, code2]</c>.</summary>
	public static byte[] EncodeSocd(int slot1, (byte CodeType, byte Code) key1, int slot2, (byte CodeType, byte Code) key2, int priority) =>
	[
		SmartInfiniteProtocol.TypeExtendedSocd, SocdLength(priority),
		(byte)slot1, key1.CodeType, key1.Code,
		(byte)slot2, key2.CodeType, key2.Code,
	];

	/// <summary>
	/// Decodes a SOCD record; false when the bytes are not one (wrong TYPE or a length nibble other
	/// than 6). The priority nibble is returned as found — the caller decides whether it is one of the
	/// four it manages.
	/// </summary>
	public static bool TryDecodeSocd(ReadOnlySpan<byte> record, out SocdRecord decoded)
	{
		decoded = default;
		if (record.Length != SocdRecordBytes) return false;
		if (record[0] != SmartInfiniteProtocol.TypeExtendedSocd || (record[1] & 0x0F) != SocdPayloadLength) return false;

		decoded = new SocdRecord(record[2], record[3], record[4], record[5], record[6], record[7], SocdPriorityFromLength(record[1]));
		return true;
	}

	/// <summary>
	/// <c>[0x81, 6, time_lo, time_hi, hold_codeType, hold_code, tap_codeType, tap_code]</c> — the time
	/// in milliseconds, little-endian. Throws when <paramref name="timeMs"/> is outside
	/// <see cref="TimeMsMin"/>..<see cref="TimeMsMax"/>; the service range-checks first and refuses.
	/// </summary>
	public static byte[] EncodeModTap(int timeMs, (byte CodeType, byte Code) hold, (byte CodeType, byte Code) tap)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(timeMs, TimeMsMin);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(timeMs, TimeMsMax);

		var bytes = new byte[ModTapRecordBytes];
		bytes[0] = SmartInfiniteProtocol.TypeExtendedModTap;
		bytes[1] = ModTapPayloadLength;
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)timeMs);
		bytes[4] = hold.CodeType;
		bytes[5] = hold.Code;
		bytes[6] = tap.CodeType;
		bytes[7] = tap.Code;
		return bytes;
	}

	/// <summary>
	/// Decodes a Mod-Tap record; false when the bytes are not one (wrong TYPE or LEN). The time is read
	/// as a proper little-endian uint16 (the vendor app's own read-back concatenates un-padded hex
	/// digits and misreads e.g. <c>0x0105</c>) and is returned as found — the caller decides whether a
	/// value past <see cref="TimeMsMax"/> is one it manages.
	/// </summary>
	public static bool TryDecodeModTap(ReadOnlySpan<byte> record, out ModTapRecord decoded)
	{
		decoded = default;
		if (record.Length != ModTapRecordBytes) return false;
		if (record[0] != SmartInfiniteProtocol.TypeExtendedModTap || record[1] != ModTapPayloadLength) return false;

		decoded = new ModTapRecord(BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(2)), record[4], record[5], record[6], record[7]);
		return true;
	}

	/// <summary>
	/// <c>[0x82, 4, time_lo, time_hi, codeType, code]</c> — the time in milliseconds, little-endian, and
	/// the toggled output. Throws when <paramref name="timeMs"/> is outside <see cref="TimeMsMin"/>..
	/// <see cref="TimeMsMax"/>; the service range-checks first and refuses.
	/// </summary>
	public static byte[] EncodeToggle(int timeMs, (byte CodeType, byte Code) output)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(timeMs, TimeMsMin);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(timeMs, TimeMsMax);

		var bytes = new byte[ToggleRecordBytes];
		bytes[0] = SmartInfiniteProtocol.TypeExtendedToggle;
		bytes[1] = TogglePayloadLength;
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)timeMs);
		bytes[4] = output.CodeType;
		bytes[5] = output.Code;
		return bytes;
	}

	/// <summary>
	/// Decodes a Toggle record; false when the bytes are not one (wrong TYPE or LEN). The time is read as
	/// a proper little-endian uint16 (the vendor's <c>handleEchoTGLMode</c> has the same un-padded-hex
	/// misread as MT) and returned as found — the caller decides whether a value past
	/// <see cref="TimeMsMax"/> is one it manages.
	/// </summary>
	public static bool TryDecodeToggle(ReadOnlySpan<byte> record, out ToggleRecord decoded)
	{
		decoded = default;
		if (record.Length != ToggleRecordBytes) return false;
		if (record[0] != SmartInfiniteProtocol.TypeExtendedToggle || record[1] != TogglePayloadLength) return false;

		decoded = new ToggleRecord(BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(2)), record[4], record[5]);
		return true;
	}
}
