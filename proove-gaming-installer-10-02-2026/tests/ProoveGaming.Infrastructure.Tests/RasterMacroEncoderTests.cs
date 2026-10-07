using System.Linq;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the Raster packed macro library (CMD_SET_MACRODATA 0x2D): 1096-byte buffer,
// [0xAA 0x55][dataLen][count][reserved] header, a 2-byte-per-macro offset table, and per-macro
// blocks of [len_lo, len_hi, 0, 0] followed by 4-byte [time_lo, time_hi, code, attr] events.
// Baseline: docs/plans/raster-key-assignments/reference/vendor-webapp-protocol-chunk.js
// (setAllMacroData / setAllMacroDataV2).
public class RasterMacroEncoderTests
{
	private static MacroLibraryEntry Macro(params MacroEvent[] events) =>
		new(0, MacroRepeatMode.PlayCount, events);

	private static MacroEvent[] Events(int count) =>
		Enumerable.Range(0, count).Select(i => new MacroEvent(0x04, i % 2 == 0, 10)).ToArray();

	[Fact]
	public void EncodePacked_KeyboardMacro_MatchesGoldenBytes()
	{
		// Press A after 50 ms, release it 50 ms later. DelayMs is the pause BEFORE its event,
		// which is what the vendor writes into the [time_lo, time_hi] word.
		var packed = RasterMacroEncoder.EncodePacked([Macro(
			new MacroEvent(0x04, true, 50),
			new MacroEvent(0x04, false, 50))]);

		byte[] expected =
		[
			0xAA, 0x55,       // magic
			0x16, 0x00,       // dataLen = 22
			0x01, 0x00,       // count = 1
			0x00, 0x00,       // reserved
			0x0A, 0x00,       // offset table: macro 0 block starts at byte 10
			0x02, 0x00, 0x00, 0x00, // block header: 2 events
			0x32, 0x00, 0x04, 0x81, // 50 ms, A, press
			0x32, 0x00, 0x04, 0x01, // 50 ms, A, release
		];
		Assert.Equal(22, packed.DataLen);
		Assert.Equal(RasterMacroEncoder.BufferLength, packed.Buffer.Length);
		Assert.Equal(expected, packed.Buffer.Take(expected.Length).ToArray());
		Assert.False(packed.HasDiscards);
	}

	// The high bit marks key-DOWN. Both vendor writers emit `1 === o.type ? 129 : 1` with type 1
	// as the down event; swapping these leaves every macro key held after playback, which the
	// hardware surfaces as a stuck key that survives until the keyboard is reset.
	[Theory]
	[InlineData(true, 0x81)]
	[InlineData(false, 0x01)]
	public void EncodePacked_EventAttribute_SetsHighBitOnPress(bool isPress, byte expectedAttr)
	{
		var packed = RasterMacroEncoder.EncodePacked([Macro(new MacroEvent(0x14, isPress, 25))]);
		Assert.Equal(expectedAttr, packed.Buffer[17]);
	}

	[Fact]
	public void EncodePacked_ClampsDelayToVendorRange()
	{
		var packed = RasterMacroEncoder.EncodePacked([Macro(
			new MacroEvent(0x04, true, 0),
			new MacroEvent(0x04, false, 999999))]);

		Assert.Equal(RasterMacroEncoder.DelayMinMs, packed.Buffer[14] | (packed.Buffer[15] << 8));
		Assert.Equal(RasterMacroEncoder.DelayMaxMs, packed.Buffer[18] | (packed.Buffer[19] << 8));
	}

	// Mouse codes are dropped rather than encoded — Raster keyboards do not play mouse events —
	// and the drop is reported so the caller can tell the user their macro was shortened.
	[Fact]
	public void EncodePacked_SkipsMouseEvents_AndReportsTheDrop()
	{
		var packed = RasterMacroEncoder.EncodePacked([Macro(
			new MacroEvent(0xF0, true, 10),
			new MacroEvent(0x04, true, 10))]);

		Assert.Equal(18, packed.DataLen);   // header + table + block header + one event
		Assert.Equal(1, packed.Buffer[10]); // one event survived
		Assert.Equal(0x04, packed.Buffer[16]);
		Assert.Equal(1, packed.DroppedMouseEvents);
		Assert.True(packed.HasDiscards);
	}

	// An empty macro claims no block; its offset-table entry stays zero so the firmware skips it.
	[Fact]
	public void EncodePacked_EmptyMacro_LeavesOffsetEntryZero()
	{
		var packed = RasterMacroEncoder.EncodePacked([Macro()]);

		Assert.Equal(0, packed.Buffer[8]);
		Assert.Equal(0, packed.Buffer[9]);
		Assert.Equal(10, packed.DataLen);
		Assert.False(packed.HasDiscards);
	}

	// Exactly at the cap is not a truncation — only an event actually turned away counts.
	[Fact]
	public void EncodePacked_AtEventCap_EncodesAllAndReportsNoTruncation()
	{
		var packed = RasterMacroEncoder.EncodePacked(
			[Macro(Events(RasterMacroEncoder.MaxEventsPerMacro))]);

		Assert.Equal(RasterMacroEncoder.MaxEventsPerMacro, packed.Buffer[10]);
		Assert.Equal(0, packed.TruncatedMacros);
		Assert.False(packed.HasDiscards);
	}

	[Fact]
	public void EncodePacked_PastEventCap_TruncatesAndReportsIt()
	{
		var packed = RasterMacroEncoder.EncodePacked(
			[Macro(Events(RasterMacroEncoder.MaxEventsPerMacro + 5))]);

		Assert.Equal(RasterMacroEncoder.MaxEventsPerMacro, packed.Buffer[10]);
		Assert.Equal(1, packed.TruncatedMacros);
		Assert.True(packed.HasDiscards);
	}

	// A library too large for the 1096-byte buffer drops whole macros rather than corrupting the
	// ones that fit: the skipped macro's offset entry is zeroed and earlier blocks stay intact.
	[Fact]
	public void EncodePacked_LibraryOverflow_SkipsMacrosAndKeepsEarlierOnesIntact()
	{
		// 16 macros × 60 events = 16 × 244 bytes of blocks, far past BufferLength.
		var entries = Enumerable.Range(0, RasterMacroEncoder.MaxMacros)
			.Select(i => new MacroLibraryEntry(i, MacroRepeatMode.PlayCount,
				Events(RasterMacroEncoder.MaxEventsPerMacro)))
			.ToList();

		var packed = RasterMacroEncoder.EncodePacked(entries);

		Assert.True(packed.SkippedMacros > 0);
		Assert.True(packed.HasDiscards);
		Assert.True(packed.DataLen <= RasterMacroEncoder.BufferLength);

		// Macro 0 still points at the first block after the offset table and still declares a
		// full event count — the overflow did not corrupt what already fit.
		var tableStart = 8;
		var firstOffset = packed.Buffer[tableStart] | (packed.Buffer[tableStart + 1] << 8);
		Assert.Equal(tableStart + 2 * RasterMacroEncoder.MaxMacros, firstOffset);
		Assert.Equal(RasterMacroEncoder.MaxEventsPerMacro, packed.Buffer[firstOffset]);

		// At least one macro was left out, and a skipped macro reads back as offset 0.
		var skipped = Enumerable.Range(0, RasterMacroEncoder.MaxMacros)
			.Count(i => (packed.Buffer[tableStart + 2 * i] | (packed.Buffer[tableStart + 2 * i + 1] << 8)) == 0);
		Assert.Equal(packed.SkippedMacros, skipped);
	}

	// Entries past the device's 16 slots never reach the layout loop, so they have to be counted
	// separately — otherwise a caller handed more than MaxMacros ships 16 and is told everything
	// fit. Two small macros so the buffer itself is nowhere near full: the only thing dropping
	// them is the slot ceiling.
	[Fact]
	public void EncodePacked_MoreEntriesThanSlots_ReportsTheOverflowAsSkipped()
	{
		var entries = Enumerable.Range(0, RasterMacroEncoder.MaxMacros + 3)
			.Select(i => new MacroLibraryEntry(i, MacroRepeatMode.PlayCount, Events(2)))
			.ToList();

		var packed = RasterMacroEncoder.EncodePacked(entries);

		Assert.Equal(RasterMacroEncoder.MaxMacros, packed.Buffer[4] | (packed.Buffer[5] << 8));
		Assert.Equal(3, packed.SkippedMacros);
		Assert.True(packed.HasDiscards);
	}

	[Fact]
	public void SliceChunks_LastChunkCarriesRemainderLength()
	{
		var packed = RasterMacroEncoder.EncodePacked([Macro(Events(20))]);

		var chunks = RasterMacroEncoder.SliceChunks(packed.Buffer, packed.DataLen).ToList();

		Assert.Equal(2, chunks.Count);
		Assert.Equal(RasterMacroEncoder.ChunkSize, chunks[0].ChunkLen);
		Assert.Equal(packed.DataLen - RasterMacroEncoder.ChunkSize, chunks[1].ChunkLen);
		// The full 56-byte window is always written even when ChunkLen is short.
		Assert.All(chunks, c => Assert.Equal(RasterMacroEncoder.ChunkSize, c.ChunkBytes.Length));
	}

	// The type byte we emit is always 0x60 (play mode rides in code2), but a record written by
	// the vendor tool can carry 0x61 and must still read back as a macro rather than a keycode.
	[Theory]
	[InlineData(RasterMacroEncoder.BindingTypePlay)]
	[InlineData(RasterMacroEncoder.BindingTypeWhileHeld)]
	public void IsMacroBindingType_AcceptsBothMacroRecordKinds(byte type)
	{
		Assert.True(RasterMacroEncoder.IsMacroBindingType(type));
	}

	[Fact]
	public void IsMacroBindingType_RejectsKeyboardRecordType()
	{
		Assert.False(RasterMacroEncoder.IsMacroBindingType(0x10));
	}
}
