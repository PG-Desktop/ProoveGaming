using System;
using System.Collections.Generic;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// The Smart Infinite macro buffer (Cyrex / Aress / Rampage), transcribed from the vendor
// Keyborder app's formatHongRect serializer — the writer its own driver ships to this firmware
// with sethongMessage (0x1C). The reference vectors below are hand-derived from that function,
// so a regression here is a divergence from the only known-good writer for these boards. See
// docs/knowledge-base/smart-infinite-protocol/macro-format-gamingkeyboard2.md.
public class SmartInfiniteMacroEncoderTests
{
	// Cyrex's macro region: Device Info byte 5 = 4 -> 4 x 256.
	private const int CyrexRegionSize = 1024;

	private static MacroLibraryEntry Entry(int index, params MacroEvent[] events) =>
		new(index, MacroRepeatMode.PlayCount, events);

	// ── Header ───────────────────────────────────────────────────────────────────

	[Fact]
	public void Header_CarriesMagicRegionLengthAndSlotCount()
	{
		var packed = SmartInfiniteMacroEncoder.EncodePacked([], CyrexRegionSize);

		Assert.Equal(CyrexRegionSize, packed.Buffer.Length);
		Assert.Equal(0xAA, packed.Buffer[0]);
		Assert.Equal(0x55, packed.Buffer[1]);
		// Bytes 2-3 are the WHOLE region length (formatHongRect writes hong_Rect.length), not the
		// used length — 1024 = 0x0400 little-endian.
		Assert.Equal(0x00, packed.Buffer[2]);
		Assert.Equal(0x04, packed.Buffer[3]);
		// Slot count is the firmware's fixed 20, empty slots included.
		Assert.Equal(20, packed.Buffer[4]);
		Assert.Equal(0, packed.Buffer[5]);
	}

	[Fact]
	public void EmptyLibrary_OccupiesHeaderAndOffsetTableOnly()
	{
		var packed = SmartInfiniteMacroEncoder.EncodePacked([], CyrexRegionSize);

		Assert.Equal(SmartInfiniteMacroEncoder.BlocksStart, packed.DataLen);   // 8 + 20*2 = 48
		// Everything past the slot-count byte is zero: reserved header bytes, every offset entry
		// (= empty slot), and the whole tail.
		for (int i = 5; i < CyrexRegionSize; i++)
			Assert.Equal(0, packed.Buffer[i]);
	}

	[Fact]
	public void RegionSmallerThanTheTable_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteMacroEncoder.EncodePacked([], SmartInfiniteMacroEncoder.BlocksStart - 1));
	}

	// ── Reference vector (hand-derived from formatHongRect) ──────────────────────
	// Macro 0: A down (delay 0 -> clamped to 10), A up (20 ms).
	// Macro 1: LShift down (15), C down (20), C up (20), LShift up (20) — the modifier travels as
	// the hot-key form: code = bitmask 0x02, attr low bits = 2.

	private static SmartInfiniteMacroEncoder.PackResult ReferenceLibrary() =>
		SmartInfiniteMacroEncoder.EncodePacked([
			Entry(0,
				new MacroEvent(0x04, true, 0), new MacroEvent(0x04, false, 20)),
			Entry(1,
				new MacroEvent(0xE1, true, 15),
				new MacroEvent(0x06, true, 20), new MacroEvent(0x06, false, 20),
				new MacroEvent(0xE1, false, 20)),
		], CyrexRegionSize);

	[Fact]
	public void ReferenceLibrary_MatchesTheHandDerivedBytes()
	{
		var packed = ReferenceLibrary();

		// Offset table: slot 0 -> 48, slot 1 -> 60 (48 + 4 + 2*4), slots 2..19 empty.
		Assert.Equal(48, packed.Buffer[8] | (packed.Buffer[9] << 8));
		Assert.Equal(60, packed.Buffer[10] | (packed.Buffer[11] << 8));
		for (int slot = 2; slot < SmartInfiniteMacroEncoder.SlotCount; slot++)
			Assert.Equal(0, packed.Buffer[8 + slot * 2] | (packed.Buffer[9 + slot * 2] << 8));

		// Macro 0 block: count 2, reserved, then the two events (delay 0 clamps up to 10).
		byte[] expectedBlock0 =
		[
			2, 0, 0, 0,
			10, 0, 0x04, 0x81,   // A down: normal key, press bit
			20, 0, 0x04, 0x01,   // A up
		];
		Assert.Equal(expectedBlock0, packed.Buffer.AsSpan(48, expectedBlock0.Length).ToArray());

		// Macro 1 block: the modifier is the hot-key form — bitmask 0x02, attr 0x82/0x02.
		byte[] expectedBlock1 =
		[
			4, 0, 0, 0,
			15, 0, 0x02, 0x82,   // LShift down: bitmask + modifier kind + press bit
			20, 0, 0x06, 0x81,   // C down
			20, 0, 0x06, 0x01,   // C up
			20, 0, 0x02, 0x02,   // LShift up: bitmask + modifier kind
		];
		Assert.Equal(expectedBlock1, packed.Buffer.AsSpan(60, expectedBlock1.Length).ToArray());

		Assert.Equal(80, packed.DataLen);
		Assert.False(packed.HasDiscards);
		// The tail past the last block is the "rest is empty" state the firmware's own region holds.
		for (int i = packed.DataLen; i < CyrexRegionSize; i++)
			Assert.Equal(0, packed.Buffer[i]);
	}

	[Fact]
	public void EveryModifierUsage_TranslatesToItsBitmask()
	{
		// LCtrl 0xE0 -> 0x01 … RWin 0xE7 -> 0x80: the 1 << (usage - 0xE0) ladder.
		for (byte usage = 0xE0; usage <= 0xE7; usage++)
		{
			var packed = SmartInfiniteMacroEncoder.EncodePacked(
				[Entry(0, new MacroEvent(usage, true, 20))], CyrexRegionSize);

			Assert.Equal(1 << (usage - 0xE0), packed.Buffer[48 + 4 + 2]);
			Assert.Equal(0x82, packed.Buffer[48 + 4 + 3]);
		}
	}

	// ── Empty slots and capacity ─────────────────────────────────────────────────

	[Fact]
	public void EmptyMacro_LeavesItsOffsetEntryZero()
	{
		var packed = SmartInfiniteMacroEncoder.EncodePacked([
			Entry(0),
			Entry(1, new MacroEvent(0x04, true, 20), new MacroEvent(0x04, false, 20)),
		], CyrexRegionSize);

		Assert.Equal(0, packed.Buffer[8] | (packed.Buffer[9] << 8));
		// The first real block still starts at BlocksStart — empty slots reserve no space.
		Assert.Equal(48, packed.Buffer[10] | (packed.Buffer[11] << 8));
	}

	[Fact]
	public void EntriesPastTheSlotCount_AreCountedAsSkipped()
	{
		var entries = Enumerable.Range(0, SmartInfiniteMacroEncoder.SlotCount + 3)
			.Select(i => Entry(i, new MacroEvent(0x04, true, 20)))
			.ToList();

		var packed = SmartInfiniteMacroEncoder.EncodePacked(entries, CyrexRegionSize);

		Assert.Equal(3, packed.SkippedMacros);
		Assert.True(packed.HasDiscards);
	}

	[Fact]
	public void MacroThatDoesNotFitTheRegion_IsSkippedNotTruncatedMidBlock()
	{
		// A region with room for the table plus one 12-byte block only.
		var regionSize = SmartInfiniteMacroEncoder.BlocksStart + 12;
		var packed = SmartInfiniteMacroEncoder.EncodePacked([
			Entry(0, new MacroEvent(0x04, true, 20), new MacroEvent(0x04, false, 20)),
			Entry(1, new MacroEvent(0x05, true, 20), new MacroEvent(0x05, false, 20)),
		], regionSize);

		Assert.Equal(48, packed.Buffer[8] | (packed.Buffer[9] << 8));
		Assert.Equal(0, packed.Buffer[10] | (packed.Buffer[11] << 8));   // no half-written block
		Assert.Equal(1, packed.SkippedMacros);
	}

	[Fact]
	public void MouseEvents_AreDroppedAndCounted()
	{
		var packed = SmartInfiniteMacroEncoder.EncodePacked([
			Entry(0,
				new MacroEvent(0xF0, true, 20),                          // mouse — not encodable
				new MacroEvent(0x04, true, 20), new MacroEvent(0x04, false, 20)),
		], CyrexRegionSize);

		Assert.Equal(1, packed.DroppedMouseEvents);
		Assert.Equal(2, packed.Buffer[48] | (packed.Buffer[49] << 8));   // only the keyboard events
	}

	[Fact]
	public void EventsPastThePerMacroCap_AreTruncatedAndCounted()
	{
		var events = new List<MacroEvent>();
		for (int i = 0; i < SmartInfiniteMacroEncoder.MaxEventsPerMacro + 5; i++)
			events.Add(new MacroEvent(0x04, i % 2 == 0, 20));

		var packed = SmartInfiniteMacroEncoder.EncodePacked([new MacroLibraryEntry(0, MacroRepeatMode.PlayCount, events)], CyrexRegionSize);

		Assert.Equal(1, packed.TruncatedMacros);
		Assert.Equal(SmartInfiniteMacroEncoder.MaxEventsPerMacro, packed.Buffer[48] | (packed.Buffer[49] << 8));
	}

	[Fact]
	public void Delays_AreClampedToTheRecorderBudget()
	{
		var packed = SmartInfiniteMacroEncoder.EncodePacked([
			Entry(0, new MacroEvent(0x04, true, 5000), new MacroEvent(0x04, false, 3)),
		], CyrexRegionSize);

		Assert.Equal(SmartInfiniteMacroEncoder.DelayMaxMs, packed.Buffer[52] | (packed.Buffer[53] << 8));
		Assert.Equal(SmartInfiniteMacroEncoder.DelayMinMs, packed.Buffer[56] | (packed.Buffer[57] << 8));
	}
}
