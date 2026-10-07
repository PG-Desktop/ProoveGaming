using System;
using System.Collections.Generic;
using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// The advanced-key area codec, pinned without hardware: the Rapid Snap, SOCD, Mod-Tap and Toggle wire
// formats, the pointer / record walk (including SOCD's length nibble, which misaligns everything after it when ignored),
// the rebuild's byte-for-byte preservation of records it does not understand, and every way a
// misread area must come back null rather than be rebuilt on top of.
public class SmartInfiniteAdvancedKeyAreaTests
{
	private const int MatrixSlots = 75;
	private const int AreaBytes = 256;

	// A plain 75-slot matrix: every slot {0x10, 0, usage}, usage = slot + 4 so no two collide.
	private static byte[] PlainMatrix()
	{
		var matrix = new byte[SmartInfiniteProtocol.MaxTableBytes];
		for (int slot = 0; slot < MatrixSlots; slot++)
			SmartInfiniteProtocol.PatchSlot(matrix, slot, new KeyMatrixRecord(0x10, 0x00, (byte)(slot + 4)));
		return matrix;
	}

	private static void Point(byte[] matrix, int slot, byte type, int offset) =>
		SmartInfiniteProtocol.PatchSlot(matrix, slot, SmartInfiniteAdvancedKeyArea.PointerRecord(type, offset));

	private static byte[] RsRecord(int slot1, int slot2) =>
		SmartInfiniteAdvancedKeyArea.EncodeRapidSnap(slot1, (1, (byte)(slot1 + 4)), slot2, (1, (byte)(slot2 + 4)));

	// ── Rapid Snap encode / decode ─────────────────────────────────────────────

	[Fact]
	public void EncodeRapidSnap_PlainKeys_ProducesTheVendorLayoutAndRoundTrips()
	{
		var bytes = SmartInfiniteAdvancedKeyArea.EncodeRapidSnap(31, (1, 0x04), 33, (1, 0x07));

		Assert.Equal(new byte[] { 0x84, 0x06, 31, 1, 0x04, 33, 1, 0x07 }, bytes);
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeRapidSnap(bytes, out var decoded));
		Assert.Equal(new RapidSnapRecord(31, 1, 0x04, 33, 1, 0x07), decoded);
	}

	[Fact]
	public void EncodeRapidSnap_ModifierKey_CarriesTheBitmaskWithCodeType2()
	{
		var shift = SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x10, 0x02, 0x00));
		Assert.Equal(((byte)2, (byte)0x02), shift);

		var bytes = SmartInfiniteAdvancedKeyArea.EncodeRapidSnap(45, shift!.Value, 47, (1, 0x1D));
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeRapidSnap(bytes, out var decoded));
		Assert.Equal(2, decoded.CodeType1);
		Assert.Equal(0x02, decoded.Code1);
	}

	[Theory]
	[InlineData(new byte[] { 0x83, 0x06, 1, 1, 4, 2, 1, 7 })]       // SOCD, not RS
	[InlineData(new byte[] { 0x84, 0x05, 1, 1, 4, 2, 1 })]          // wrong LEN
	[InlineData(new byte[] { 0x84, 0x06, 1, 1, 4, 2, 1, 7, 0 })]    // too long
	public void TryDecodeRapidSnap_RejectsAnythingThatIsNotAnEightByteRsRecord(byte[] bytes)
	{
		Assert.False(SmartInfiniteAdvancedKeyArea.TryDecodeRapidSnap(bytes, out _));
	}

	// ── SOCD encode / decode ──────────────────────────────────────────────────

	[Theory]
	[InlineData(0, 0x06)]
	[InlineData(1, 0x16)]
	[InlineData(2, 0x26)]
	[InlineData(3, 0x36)]
	public void EncodeSocd_PutsThePriorityInTheLengthHighNibble_AndRoundTrips(int priority, byte expectedLen)
	{
		var bytes = SmartInfiniteAdvancedKeyArea.EncodeSocd(18, (1, 0x08), 19, (1, 0x15), priority);

		Assert.Equal(new byte[] { 0x83, expectedLen, 18, 1, 0x08, 19, 1, 0x15 }, bytes);
		Assert.Equal(expectedLen, SmartInfiniteAdvancedKeyArea.SocdLength(priority));
		Assert.Equal(priority, SmartInfiniteAdvancedKeyArea.SocdPriorityFromLength(expectedLen));
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeSocd(bytes, out var decoded));
		Assert.Equal(new SocdRecord(18, 1, 0x08, 19, 1, 0x15, priority), decoded);
		// The walk sees 8 bytes whatever the nibble says.
		Assert.Equal(8, SmartInfiniteAdvancedKeyArea.RecordLength(bytes, 0));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(4)]
	[InlineData(16)]
	public void EncodeSocd_RefusesAPriorityTheFirmwareDoesNotDefine(int priority)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteAdvancedKeyArea.EncodeSocd(18, (1, 0x08), 19, (1, 0x15), priority));
	}

	[Fact]
	public void TryDecodeSocd_AcceptsAForeignPriorityNibble_AndReportsItAsFound()
	{
		// Priority 9 is nothing this build writes, but the record is well-formed and must parse.
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeSocd([0x83, 0x96, 18, 1, 0x08, 19, 1, 0x15], out var decoded));
		Assert.Equal(9, decoded.Priority);
	}

	[Theory]
	[InlineData(new byte[] { 0x84, 0x06, 1, 1, 4, 2, 1, 7 })]       // RS, not SOCD
	[InlineData(new byte[] { 0x83, 0x27, 1, 1, 4, 2, 1, 7 })]       // length nibble is not 6
	[InlineData(new byte[] { 0x83, 0x26, 1, 1, 4, 2, 1 })]          // too short
	[InlineData(new byte[] { 0x83, 0x26, 1, 1, 4, 2, 1, 7, 0 })]    // too long
	public void TryDecodeSocd_RejectsAnythingThatIsNotAnEightByteSocdRecord(byte[] bytes)
	{
		Assert.False(SmartInfiniteAdvancedKeyArea.TryDecodeSocd(bytes, out _));
	}

	// ── Mod-Tap encode / decode ───────────────────────────────────────────────

	[Fact]
	public void EncodeModTap_CapsLockExample_ProducesTheVendorBytesAndRoundTrips()
	{
		// Caps Lock: hold → Left Ctrl (modifier mask 0x01), tap → Esc (usage 0x29), 150 ms.
		var bytes = SmartInfiniteAdvancedKeyArea.EncodeModTap(150, (2, 0x01), (1, 0x29));

		Assert.Equal(new byte[] { 0x81, 0x06, 0x96, 0x00, 0x02, 0x01, 0x01, 0x29 }, bytes);
		Assert.Equal(8, SmartInfiniteAdvancedKeyArea.RecordLength(bytes, 0));
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeModTap(bytes, out var decoded));
		Assert.Equal(new ModTapRecord(150, 2, 0x01, 1, 0x29), decoded);
	}

	[Fact]
	public void TryDecodeModTap_ReadsTheTimeAsLittleEndianUInt16()
	{
		// 261 ms = 0x0105: the vendor's own read-back concatenates un-padded hex and shows "15" —
		// the case the knowledge base pins so this decoder is never "fixed" to match it.
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeModTap([0x81, 0x06, 0x05, 0x01, 0x01, 0x04, 0x01, 0x05], out var decoded));
		Assert.Equal(261, decoded.TimeMs);
		Assert.Equal(new byte[] { 0x81, 0x06, 0x05, 0x01, 0x01, 0x04, 0x01, 0x05 },
			SmartInfiniteAdvancedKeyArea.EncodeModTap(261, (1, 0x04), (1, 0x05)));

		// The slider's ends.
		Assert.Equal(new byte[] { 0xE8, 0x03 }, SmartInfiniteAdvancedKeyArea.EncodeModTap(1000, (1, 4), (1, 5))[2..4]);
		Assert.Equal(new byte[] { 0x00, 0x00 }, SmartInfiniteAdvancedKeyArea.EncodeModTap(0, (1, 4), (1, 5))[2..4]);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1001)]
	public void EncodeModTap_RefusesATimeOutsideTheSlider(int timeMs)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteAdvancedKeyArea.EncodeModTap(timeMs, (1, 0x04), (1, 0x05)));
	}

	[Theory]
	[InlineData(new byte[] { 0x82, 0x04, 0x64, 0x00, 1, 4 })]              // TGL, not MT
	[InlineData(new byte[] { 0x84, 0x06, 1, 1, 4, 2, 1, 7 })]              // RS, not MT
	[InlineData(new byte[] { 0x81, 0x05, 0x64, 0x00, 1, 4, 1 })]           // wrong LEN
	[InlineData(new byte[] { 0x81, 0x06, 0x64, 0x00, 1, 4, 1, 5, 0 })]     // too long
	public void TryDecodeModTap_RejectsAnythingThatIsNotAnEightByteMtRecord(byte[] bytes)
	{
		Assert.False(SmartInfiniteAdvancedKeyArea.TryDecodeModTap(bytes, out _));
	}

	// ── Toggle encode / decode ────────────────────────────────────────────────

	[Fact]
	public void EncodeToggle_TToggledW_ProducesTheVendorBytesAndRoundTrips()
	{
		// T toggles W (usage 0x1A) at 100 ms — the acceptance criterion's record.
		var bytes = SmartInfiniteAdvancedKeyArea.EncodeToggle(100, (1, 0x1A));

		Assert.Equal(new byte[] { 0x82, 0x04, 0x64, 0x00, 0x01, 0x1A }, bytes);
		Assert.Equal(6, SmartInfiniteAdvancedKeyArea.RecordLength(bytes, 0));
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeToggle(bytes, out var decoded));
		Assert.Equal(new ToggleRecord(100, 1, 0x1A), decoded);
	}

	[Fact]
	public void EncodeToggle_ModifierOutput_CarriesTheBitmaskWithCodeType2()
	{
		// Left Shift as the toggled output: {0x10, 0x02, 0} → (2, 0x02), and back.
		var shift = SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x10, 0x02, 0x00));
		var bytes = SmartInfiniteAdvancedKeyArea.EncodeToggle(100, shift!.Value);

		Assert.Equal(new byte[] { 0x82, 0x04, 0x64, 0x00, 0x02, 0x02 }, bytes);
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeToggle(bytes, out var decoded));
		Assert.Equal(new KeyMatrixRecord(0x10, 0x02, 0x00), SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(decoded.CodeType, decoded.Code));
	}

	[Fact]
	public void TryDecodeToggle_ReadsTheTimeAsLittleEndianUInt16()
	{
		// 261 ms = 0x0105: the vendor's handleEchoTGLMode concatenates un-padded hex and shows "15".
		Assert.True(SmartInfiniteAdvancedKeyArea.TryDecodeToggle([0x82, 0x04, 0x05, 0x01, 0x01, 0x1A], out var decoded));
		Assert.Equal(261, decoded.TimeMs);
		Assert.Equal(new byte[] { 0x82, 0x04, 0x05, 0x01, 0x01, 0x1A }, SmartInfiniteAdvancedKeyArea.EncodeToggle(261, (1, 0x1A)));

		// The slider's ends.
		Assert.Equal(new byte[] { 0xE8, 0x03 }, SmartInfiniteAdvancedKeyArea.EncodeToggle(1000, (1, 0x1A))[2..4]);
		Assert.Equal(new byte[] { 0x00, 0x00 }, SmartInfiniteAdvancedKeyArea.EncodeToggle(0, (1, 0x1A))[2..4]);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(1001)]
	public void EncodeToggle_RefusesATimeOutsideTheSlider(int timeMs)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => SmartInfiniteAdvancedKeyArea.EncodeToggle(timeMs, (1, 0x1A)));
	}

	[Theory]
	[InlineData(new byte[] { 0x81, 0x06, 0x64, 0x00, 1, 4, 1, 5 })]     // MT, not TGL
	[InlineData(new byte[] { 0x82, 0x06, 0x64, 0x00, 1, 4, 1, 5 })]     // wrong LEN
	[InlineData(new byte[] { 0x82, 0x04, 0x64, 0x00, 1 })]              // too short
	[InlineData(new byte[] { 0x82, 0x04, 0x64, 0x00, 1, 4, 0 })]        // too long
	public void TryDecodeToggle_RejectsAnythingThatIsNotASixByteTglRecord(byte[] bytes)
	{
		Assert.False(SmartInfiniteAdvancedKeyArea.TryDecodeToggle(bytes, out _));
	}

	[Fact]
	public void KeyCodeToRecord_IsTheInverseOfKeyCodeFromRecord_AndNullForWhatItCannotExpress()
	{
		Assert.Equal(new KeyMatrixRecord(0x10, 0x00, 0x29), SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(1, 0x29));
		Assert.Equal(new KeyMatrixRecord(0x10, 0x01, 0x00), SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(2, 0x01));
		Assert.Equal(((byte)1, (byte)0x29), SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(1, 0x29)!.Value));
		Assert.Equal(((byte)2, (byte)0x01), SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(2, 0x01)!.Value));
		// Mouse buttons (codeType 3) and a zero code have no keyboard-record form.
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(3, 0x01));
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(1, 0x00));
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(2, 0x00));
	}

	[Fact]
	public void KeyCodeFromRecord_UsageWins_ModifierFallsBack_OthersAreNull()
	{
		Assert.Equal(((byte)1, (byte)0x04), SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x10, 0x00, 0x04)));
		Assert.Equal(((byte)2, (byte)0x20), SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x10, 0x20, 0x00)));
		// FN, a system command and an unused placeholder cannot join an advanced key.
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x60, 0x01, 0x00)));
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x20, 0x00, 0x04)));
		Assert.Null(SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(new KeyMatrixRecord(0x10, 0x00, 0x00)));
	}

	[Fact]
	public void PointerRecord_IsLittleEndian()
	{
		Assert.Equal(new KeyMatrixRecord(0x84, 0x34, 0x02), SmartInfiniteAdvancedKeyArea.PointerRecord(0x84, 0x0234));
	}

	// ── ParseRecords ──────────────────────────────────────────────────────────

	[Fact]
	public void ParseRecords_FindsAnRsRecordOwnedByBothSlots_AndIgnoresPlainKeys()
	{
		var matrix = PlainMatrix();
		var area = new byte[AreaBytes];
		RsRecord(31, 33).CopyTo(area, 0);
		Point(matrix, 31, 0x84, 0);
		Point(matrix, 33, 0x84, 0);

		var records = SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, area);

		Assert.NotNull(records);
		var record = Assert.Single(records!);
		Assert.Equal(0x84, record.Type);
		Assert.Equal(0, record.Offset);
		Assert.Equal(8, record.Length);
		Assert.Equal(new[] { 31, 33 }, record.Slots);
	}

	[Fact]
	public void ParseRecords_HonoursTheSocdLengthNibble_SoTheNextRecordStaysAligned()
	{
		var matrix = PlainMatrix();
		var area = new byte[AreaBytes];
		// SOCD with priority 1: LEN byte 0x16, payload still 6 → 8 bytes; the RS record follows at 8.
		new byte[] { 0x83, 0x16, 16, 1, 0x14, 17, 1, 0x1A }.CopyTo(area, 0);
		RsRecord(31, 33).CopyTo(area, 8);
		Point(matrix, 16, 0x83, 0);
		Point(matrix, 17, 0x83, 0);
		Point(matrix, 31, 0x84, 8);
		Point(matrix, 33, 0x84, 8);

		var records = SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, area);

		Assert.NotNull(records);
		Assert.Equal(2, records!.Count);
		Assert.Equal(8, records[0].Length);
		Assert.Equal(0x83, records[0].Type);
		Assert.Equal(8, records[1].Offset);
		Assert.Equal(0x84, records[1].Type);
	}

	[Fact]
	public void ParseRecords_PointerPastTheArea_IsNull()
	{
		var matrix = PlainMatrix();
		Point(matrix, 31, 0x84, AreaBytes - 1);

		Assert.Null(SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, new byte[AreaBytes]));
	}

	[Fact]
	public void ParseRecords_RecordRunningPastTheArea_IsNull()
	{
		var matrix = PlainMatrix();
		var area = new byte[AreaBytes];
		// A DKS record at the very end claiming 40 payload bytes it does not have.
		area[AreaBytes - 4] = 0x80;
		area[AreaBytes - 3] = 40;
		Point(matrix, 10, 0x80, AreaBytes - 4);

		Assert.Null(SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, area));
	}

	[Fact]
	public void ParseRecords_TypeMismatchBetweenPointerAndRecord_IsNull()
	{
		var matrix = PlainMatrix();
		var area = new byte[AreaBytes];
		RsRecord(31, 33).CopyTo(area, 0);
		Point(matrix, 31, 0x83, 0); // the slot says SOCD, the record says RS

		Assert.Null(SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, area));
	}

	[Fact]
	public void ParseRecords_OverlappingRecords_IsNull()
	{
		var matrix = PlainMatrix();
		var area = new byte[AreaBytes];
		RsRecord(31, 33).CopyTo(area, 0);
		// A second pointer lands inside the first record's bytes, where 0x84 happens to sit... make
		// it a genuine record header so the type check passes and only the overlap can fail it.
		area[4] = 0x84; area[5] = 0x06;
		Point(matrix, 31, 0x84, 0);
		Point(matrix, 33, 0x84, 0);
		Point(matrix, 40, 0x84, 4);

		Assert.Null(SmartInfiniteAdvancedKeyArea.ParseRecords(matrix, MatrixSlots, area));
	}

	[Fact]
	public void ParseRecords_NoPointers_IsEmptyNotNull()
	{
		var records = SmartInfiniteAdvancedKeyArea.ParseRecords(PlainMatrix(), MatrixSlots, new byte[AreaBytes]);

		Assert.NotNull(records);
		Assert.Empty(records!);
	}

	// ── BuildArea ─────────────────────────────────────────────────────────────

	[Fact]
	public void BuildArea_KeepsUnmanagedBytesVerbatim_AndReturnsOffsetsInInputOrder()
	{
		var dks = new byte[] { 0x80, 0x0A, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44 };
		var rs = RsRecord(31, 33);
		var tgl = new byte[] { 0x82, 0x04, 0x64, 0x00, 0x01, 0x04 };

		var built = SmartInfiniteAdvancedKeyArea.BuildArea([dks, rs, tgl], AreaBytes);

		Assert.NotNull(built);
		var (area, offsets) = built!.Value;
		Assert.Equal(AreaBytes, area.Length);
		// Vendor group order: TGL, then RS, then the unmanaged DKS — offsets aligned with the INPUT.
		Assert.Equal(0, offsets[2]);
		Assert.Equal(tgl.Length, offsets[1]);
		Assert.Equal(tgl.Length + rs.Length, offsets[0]);
		Assert.Equal(dks, area.AsSpan(offsets[0], dks.Length).ToArray());
		Assert.Equal(rs, area.AsSpan(offsets[1], rs.Length).ToArray());
		// Zero-padded past the last record.
		Assert.All(area.AsSpan(offsets[0] + dks.Length).ToArray(), b => Assert.Equal(0, b));
	}

	[Fact]
	public void BuildArea_ReportsOverflowAtAreaBytesPlusOne()
	{
		var records = new List<byte[]>();
		for (int i = 0; i < 32; i++) records.Add(RsRecord(i, i + 32));   // exactly 256 bytes
		Assert.NotNull(SmartInfiniteAdvancedKeyArea.BuildArea(records, AreaBytes));

		records.Add([0x82, 0x00]);                                          // 257 bytes
		Assert.Null(SmartInfiniteAdvancedKeyArea.BuildArea(records, AreaBytes));
	}
}
