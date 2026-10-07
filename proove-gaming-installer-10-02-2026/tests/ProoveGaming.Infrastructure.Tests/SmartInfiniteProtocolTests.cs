using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the Smart Infinite framing (Rampage / Aress / Cyrex). Every golden vector comes from a
// vendor packet capture or from a measurement on real hardware — not from the vendor's written
// documents, which contain a transposed read/write command pair, a wrong packet layout, and a
// "CRC16" field that is not a CRC.
public class SmartInfiniteProtocolTests
{
	[Fact]
	public void BuildCommand_ReturnsExactly64Bytes()
	{
		Assert.Equal(64, SmartInfiniteProtocol.BuildCommand(SmartInfiniteProtocol.CmdStartCommunication).Length);
	}

	[Fact]
	public void BuildCommand_SetsMagicCommandAndLittleEndianAddress()
	{
		var report = SmartInfiniteProtocol.BuildCommand(SmartInfiniteProtocol.CmdSetKeyInfo, addr: 0x0134);

		Assert.Equal(0x06, report[0]);
		Assert.Equal(0x18, report[1]);
		Assert.Equal(0x34, report[2]);
		Assert.Equal(0x01, report[3]);
	}

	[Fact]
	public void BuildCommand_PutsLengthInByte4AndPayloadAtByte8()
	{
		byte[] payload = [0x10, 0x00, 0x29];

		var report = SmartInfiniteProtocol.BuildCommand(SmartInfiniteProtocol.CmdSetKeyInfo, 0, payload);

		Assert.Equal(3, report[4]);
		Assert.Equal(new byte[] { 0x10, 0x00, 0x29 }, report[8..11]);
	}

	[Fact]
	public void BuildCommand_RejectsPayloadLargerThanOneReport()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteProtocol.BuildCommand(SmartInfiniteProtocol.CmdSetKeyInfo, 0,
				new byte[SmartInfiniteProtocol.MaxPayloadLength + 1]));
	}

	// Every packet of the vendor lighting-read capture (cmd 0x19) — six independent confirmations
	// that the checksum is an additive sum, including the little-endian carry on the fifth.
	[Theory]
	[InlineData(0x0000, 0x51, 0x00)]
	[InlineData(0x0038, 0x89, 0x00)]
	[InlineData(0x0070, 0xC1, 0x00)]
	[InlineData(0x00A8, 0xF9, 0x00)]
	[InlineData(0x00E0, 0x31, 0x01)]
	[InlineData(0x0118, 0x6A, 0x00)]
	public void Checksum_MatchesTheVendorLightingReadCapture(int addr, byte expectedLo, byte expectedHi)
	{
		var report = SmartInfiniteProtocol.BuildReadCommand(
			SmartInfiniteProtocol.CmdCustomLighting, (ushort)addr, length: 56);

		Assert.Equal(expectedLo, report[5]);
		Assert.Equal(expectedHi, report[6]);
	}

	// The vendor write capture's first packet is 06 18 00 00 38 5c 05 00: the header contributes
	// 0x18+0+0+0x38 = 0x50, leaving 0x050C for the payload.
	[Fact]
	public void Checksum_IncludesThePayloadOnWrites()
	{
		var payload = new byte[56];
		int remaining = 0x050C;
		for (int i = 0; i < payload.Length && remaining > 0; i++)
		{
			payload[i] = (byte)Math.Min(0xFF, remaining);
			remaining -= payload[i];
		}
		Assert.Equal(0, remaining);

		var report = SmartInfiniteProtocol.BuildCommand(SmartInfiniteProtocol.CmdSetKeyInfo, 0, payload);

		Assert.Equal(0x5C, report[5]);
		Assert.Equal(0x05, report[6]);
	}

	[Fact]
	public void Checksum_IsASumNotACrc()
	{
		// cmd 0x12, addr 0, len 0x38 -> 0x4A. Trivially checkable by hand.
		var report = SmartInfiniteProtocol.BuildReadCommand(SmartInfiniteProtocol.CmdReadDeviceInfo, 0, 56);

		Assert.Equal(0x4A, report[5]);
		Assert.Equal(0x00, report[6]);
	}

	// Cyrex declares a 225-byte matrix. Sending five full 56-byte packets would write 55 bytes past
	// it, so the final packet must be trimmed.
	[Fact]
	public void EnumerateChunks_TrimsTheFinalPacketToTheDeclaredSize()
	{
		var chunks = SmartInfiniteProtocol.EnumerateChunks(225);

		Assert.Equal(5, chunks.Count);
		Assert.Equal<(ushort, int)>((0, 56), chunks[0]);
		Assert.Equal<(ushort, int)>((56, 56), chunks[1]);
		Assert.Equal<(ushort, int)>((112, 56), chunks[2]);
		Assert.Equal<(ushort, int)>((168, 56), chunks[3]);
		Assert.Equal<(ushort, int)>((224, 1), chunks[4]);
		Assert.Equal(225, chunks.Sum(c => c.Length));
	}

	[Fact]
	public void EnumerateChunks_IsContiguousWithNoGapsOrOverlap()
	{
		int expected = 0;
		foreach (var (addr, length) in SmartInfiniteProtocol.EnumerateChunks(384))
		{
			Assert.Equal(expected, addr);
			expected += length;
		}
		Assert.Equal(384, expected);
	}

	// The 2.4G receiver relays table transfers only in 24-byte payload packets (C12,
	// hardware-confirmed on the Rampage receiver 2026-08-13). The transfer still totals exactly
	// the declared size — the full-length rule is the firmware's, not the transport's.
	[Fact]
	public void EnumerateChunks_Receiver24G_KeyMatrixIsSixteenPacketsTotalling384()
	{
		var chunks = SmartInfiniteProtocol.EnumerateChunks(
			SmartInfiniteProtocol.KeyMatrixTableSize, SmartInfiniteProtocol.Receiver24GPayloadLength);

		Assert.Equal(16, chunks.Count);
		Assert.All(chunks, c => Assert.Equal(24, c.Length));
		Assert.Equal(SmartInfiniteProtocol.KeyMatrixTableSize, chunks.Sum(c => c.Length));
	}

	[Fact]
	public void EnumerateChunks_Receiver24G_MacroTableKeepsTheExactTotalWithAnEightByteTail()
	{
		var chunks = SmartInfiniteProtocol.EnumerateChunks(
			SmartInfiniteProtocol.MacroTableSize, SmartInfiniteProtocol.Receiver24GPayloadLength);

		Assert.Equal(86, chunks.Count);
		Assert.All(chunks.Take(85), c => Assert.Equal(24, c.Length));
		Assert.Equal<(ushort, int)>((0x07F8, 8), chunks[^1]);
		Assert.Equal(SmartInfiniteProtocol.MacroTableSize, chunks.Sum(c => c.Length));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(57)]
	public void EnumerateChunks_RejectsPayloadsOutsideOnePacket(int maxPayload)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteProtocol.EnumerateChunks(384, maxPayload));
	}

	// The factory-confirmed key-write transfer (C1/C8): the full KEY_MATRIX_TAB_SIZE, exactly —
	// the shape hardware-proven on Rampage. Shorter transfers are acked and discarded.
	[Fact]
	public void EnumerateChunks_KeyMatrixTableIsSevenPacketsTotalling384()
	{
		Assert.Equal(384, SmartInfiniteProtocol.KeyMatrixTableSize);

		var chunks = SmartInfiniteProtocol.EnumerateChunks(SmartInfiniteProtocol.KeyMatrixTableSize);

		Assert.Equal(7, chunks.Count);
		Assert.Equal<(ushort, int)>((0x0150, 48), chunks[6]);
		Assert.All(chunks.Take(6), c => Assert.Equal(56, c.Length));
		Assert.Equal(SmartInfiniteProtocol.KeyMatrixTableSize, chunks.Sum(c => c.Length));
	}

	// The macro-write transfer (C8's MACRO_TAB_SIZE): full 2048 bytes in 37 packets, final 32.
	[Fact]
	public void EnumerateChunks_MacroTableIsThirtySevenPacketsTotalling2048()
	{
		Assert.Equal(2048, SmartInfiniteProtocol.MacroTableSize);

		var chunks = SmartInfiniteProtocol.EnumerateChunks(SmartInfiniteProtocol.MacroTableSize);

		Assert.Equal(37, chunks.Count);
		Assert.Equal<(ushort, int)>((0x07E0, 32), chunks[36]);
		Assert.All(chunks.Take(36), c => Assert.Equal(56, c.Length));
		Assert.Equal(SmartInfiniteProtocol.MacroTableSize, chunks.Sum(c => c.Length));

		int expected = 0;
		foreach (var (addr, length) in chunks)
		{
			Assert.Equal(expected, addr);
			expected += length;
		}
	}

	// The guard that matters most: a read-modify-write must not disturb any other key.
	[Fact]
	public void PatchSlot_ChangesOnlyThatSlotsThreeBytes()
	{
		var table = new byte[225];
		for (int i = 0; i < table.Length; i++) table[i] = (byte)(i % 251);
		var original = (byte[])table.Clone();

		SmartInfiniteProtocol.PatchSlot(table, 18, new KeyMatrixRecord(0x10, 0x00, 0x1A));

		Assert.Equal(new byte[] { 0x10, 0x00, 0x1A }, table[54..57]);
		for (int i = 0; i < table.Length; i++)
		{
			if (i is >= 54 and <= 56) continue;
			Assert.Equal(original[i], table[i]);
		}
	}

	[Fact]
	public void PatchSlot_RejectsASlotPastTheEndOfTheTable()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			SmartInfiniteProtocol.PatchSlot(new byte[225], 75, new KeyMatrixRecord(0x10, 0, 0x04)));
	}

	// 56 is not a multiple of 3, so a one-packet misalignment shifts the table and feeds the TYPE
	// column from what was really the modifier column — which is 0x00 for every ordinary key. That
	// is why 0x00 must not count as a valid type: otherwise a shifted table reads as a plausible
	// page of blank slots and sails through.
	[Fact]
	public void IsPlausibleKeyTable_RejectsATableShiftedByOnePacket()
	{
		var good = CyrexFactoryTable();
		var shifted = new byte[good.Length];
		Array.Copy(good, 0, shifted, SmartInfiniteProtocol.MaxPayloadLength,
			good.Length - SmartInfiniteProtocol.MaxPayloadLength);

		Assert.True(SmartInfiniteProtocol.IsPlausibleKeyTable(good, 75, out var okSlots));
		Assert.Equal(75, okSlots);

		Assert.False(SmartInfiniteProtocol.IsPlausibleKeyTable(shifted, 75, out _));
	}

	[Fact]
	public void IsPlausibleKeyTable_AcceptsUserRemappedSlots()
	{
		var table = CyrexFactoryTable();
		SmartInfiniteProtocol.PatchSlot(table, 18, new KeyMatrixRecord(0x10, 0x00, 0x1A)); // E -> W
		SmartInfiniteProtocol.PatchSlot(table, 33, new KeyMatrixRecord(0x84, 0x20, 0x10)); // Rapid Trigger

		Assert.True(SmartInfiniteProtocol.IsPlausibleKeyTable(table, 75, out _));
	}

	// ── Macro upload ──────────────────────────────────────────────────────────
	// The two values SmartInfiniteMacroService needs are inferred rather than captured, so these
	// tests do not claim they are right — they pin them so a later "tidy-up" cannot quietly change
	// what the hardware test was run against, and so the two dangerous mistakes are caught.

	// 0x20 is a FULL FACTORY RESET on this family. The live macro-write command is 0x1C — and if
	// anyone ever re-derives either macro command, landing on 0x20 wipes the user's whole board.
	[Fact]
	public void MacroCommands_AreNotTheFactoryResetCommand()
	{
		Assert.NotEqual(0x20, SmartInfiniteProtocol.CmdWriteMacroInfo);
		Assert.Equal(0x1C, SmartInfiniteProtocol.CmdWriteMacroInfo);
		Assert.Equal(0x1B, SmartInfiniteProtocol.CmdReadMacroInfo);
	}

	// This byte has flipped three times; the pin now carries the per-family truth. On THIS firmware
	// line (per its own KeyType enum, extracted from the vendor's Keyborder app) 0x50/0x51 are the
	// macro types and 0x60 is FUNCTION — binding {0x60, slot, mode} is stored, verifies on
	// read-back, and plays nothing, which is exactly the broken state this fix removes. The
	// V3.1 workbook says the opposite because it describes a different firmware line; Raster keeps
	// 0x60 because that IS hardware-verified there (RasterMacroEncoder.BindingTypePlay).
	// Flipping this again requires new hardware evidence, not another document.
	[Fact]
	public void TypeMacro_IsThisFirmwaresMacroTypeNotTheFunctionType()
	{
		Assert.Equal(0x50, SmartInfiniteProtocol.TypeMacroStopMode);
		Assert.NotEqual(0x60, SmartInfiniteProtocol.TypeMacroStopMode);
	}

	// The only place a repeat count fits: type 0x50 spends code2 on the stop mode.
	[Fact]
	public void TypeMacroPlayCount_IsTheCountedMacroType()
	{
		Assert.Equal(0x51, SmartInfiniteProtocol.TypeMacroPlayCount);
	}

	// A key bound to the counted type must also survive the table sanity check.
	[Fact]
	public void IsPlausibleKeyTable_AcceptsACountedMacroBoundSlot()
	{
		var table = CyrexFactoryTable();
		SmartInfiniteProtocol.PatchSlot(table, 20,
			new KeyMatrixRecord(SmartInfiniteProtocol.TypeMacroPlayCount, 0x02, 0x05)); // macro 2, x5

		Assert.True(SmartInfiniteProtocol.IsPlausibleKeyTable(table, 75, out _));
	}

	// A macro-bound slot must survive the table sanity check, or SetKeyInfo refuses to write it.
	[Fact]
	public void IsPlausibleKeyTable_AcceptsAMacroBoundSlot()
	{
		var table = CyrexFactoryTable();
		SmartInfiniteProtocol.PatchSlot(table, 18,
			new KeyMatrixRecord(SmartInfiniteProtocol.TypeMacroStopMode, 0x03, 0x00)); // E -> library macro 3

		Assert.True(SmartInfiniteProtocol.IsPlausibleKeyTable(table, 75, out _));
	}

	// The macro service ships RasterMacroEncoder's buffer through this framing, so the chunking has
	// to carry the packed library across byte-for-byte — a dropped or duplicated packet is a library
	// the device holds differently from what the key slots point into.
	[Fact]
	public void EnumerateChunks_CarriesThePackedMacroLibraryByteForByte()
	{
		var packed = RasterMacroEncoder.EncodePacked([
			new MacroLibraryEntry(0, MacroRepeatMode.PlayCount, [
				new MacroEvent(0x14, true, 0), new MacroEvent(0x14, false, 20),
				new MacroEvent(0x1A, true, 20), new MacroEvent(0x1A, false, 20),
			]),
		]);

		var reassembled = new byte[packed.DataLen];
		foreach (var (addr, len) in SmartInfiniteProtocol.EnumerateChunks(packed.DataLen))
		{
			var report = SmartInfiniteProtocol.BuildCommand(
				SmartInfiniteProtocol.CmdSetMacroData, addr, packed.Buffer.AsSpan(addr, len));

			Assert.Equal(len, report[4]);
			report.AsSpan(SmartInfiniteProtocol.ResponsePayloadOffset, len).CopyTo(reassembled.AsSpan(addr));
		}

		Assert.Equal(packed.Buffer[..packed.DataLen], reassembled);
	}

	// Cyrex's real factory table, read from the device with command 0x16.
	internal static byte[] CyrexFactoryTable() => Convert.FromHexString(
		"10002910001E10001F10002010002110002210002310002410002510002610002710002D10002E10002A10004C" +
		"10002B10001410001A10000810001510001710001C10001810000C10001210001310002F10003010003110004B" +
		"10003910000410001610000710000910000A10000B10000D10000E10000F10003310003410000010002810004E" +
		"10020010000010001D10001B10000610001910000510001110001010003610003710003810200010005210004D" +
		"10010010080010040010000010000010000010002C10000010000010400060010010100010005010005110004F");
}
