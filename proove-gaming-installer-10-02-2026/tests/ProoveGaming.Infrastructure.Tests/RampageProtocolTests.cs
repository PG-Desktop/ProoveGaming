using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the SmartInfinite framing shared by Rampage / Aress / Cyrex RGB and by Rampage key
// remap. Every golden vector below is taken from a vendor packet capture in
// docs/knowledge-base/rampage-keyboard-protocol/images/ — image4 (default-matrix read), image5
// (a complete working key remap) and image6 (lighting read). They are NOT taken from that
// directory's README.md, which has documented errors in its command table, packet layout and both
// of its worked examples.
public class RampageProtocolTests
{
	[Fact]
	public void BuildCommand_ReturnsExactly64Bytes()
	{
		var report = RampageProtocol.BuildCommand(RampageProtocol.CmdStartCommunication);
		Assert.Equal(64, report.Length);
	}

	[Fact]
	public void BuildCommand_SetsMagicByteAndCommand()
	{
		var report = RampageProtocol.BuildCommand(RampageProtocol.CmdSetKeyInfo);

		Assert.Equal(0x06, report[0]);
		Assert.Equal(0x18, report[1]);
	}

	[Fact]
	public void BuildCommand_WritesAddressLittleEndian()
	{
		var report = RampageProtocol.BuildCommand(RampageProtocol.CmdSetKeyInfo, addr: 0x0134);

		Assert.Equal(0x34, report[2]);
		Assert.Equal(0x01, report[3]);
	}

	[Fact]
	public void BuildCommand_PutsPayloadLengthInByte4AndPayloadAtByte8()
	{
		byte[] payload = [0x10, 0x00, 0x29];

		var report = RampageProtocol.BuildCommand(RampageProtocol.CmdSetKeyInfo, addr: 0, payload: payload);

		Assert.Equal(3, report[4]);
		Assert.Equal(0x10, report[8]);
		Assert.Equal(0x00, report[9]);
		Assert.Equal(0x29, report[10]);
	}

	[Fact]
	public void BuildCommand_RejectsPayloadLargerThanOneReport()
	{
		var tooBig = new byte[RampageProtocol.MaxPayloadLength + 1];

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RampageProtocol.BuildCommand(RampageProtocol.CmdSetKeyInfo, 0, tooBig));
	}

	// image4's real first OUT packet for a default-matrix read. Note the checksum is 0x4E, not the
	// 0x4A the README prints — the README copied the packet from image3's 0x12 command and changed
	// the command byte without recomputing the sum.
	[Fact]
	public void BuildReadCommand_MatchesTheVendorCapturedReadRequest()
	{
		var report = RampageProtocol.BuildReadCommand(RampageProtocol.CmdReadDefaultKeyInfo, addr: 0, length: 56);

		Assert.Equal(new byte[] { 0x06, 0x16, 0x00, 0x00, 0x38, 0x4E, 0x00, 0x00 }, report[..8]);
	}

	// Every packet of image6 (lighting read, cmd 0x19), byte for byte. Six independent confirmations
	// of the checksum formula, including the little-endian carry on the fifth.
	[Theory]
	[InlineData(0x0000, 0x51, 0x00)]
	[InlineData(0x0038, 0x89, 0x00)]
	[InlineData(0x0070, 0xC1, 0x00)]
	[InlineData(0x00A8, 0xF9, 0x00)]
	[InlineData(0x00E0, 0x31, 0x01)]
	[InlineData(0x0118, 0x6A, 0x00)]
	public void ComputeChecksum_MatchesTheVendorLightingReadCapture(int addr, byte expectedLo, byte expectedHi)
	{
		var report = RampageProtocol.BuildReadCommand(0x19, (ushort)addr, length: 56);

		Assert.Equal(expectedLo, report[5]);
		Assert.Equal(expectedHi, report[6]);
	}

	// image5's first write packet: 06 18 00 00 38 5c 05 00. The header contributes
	// 0x18+0+0+0x38 = 0x50, so the payload must account for the remaining 0x050C.
	[Fact]
	public void ComputeChecksum_IncludesThePayloadOnWrites()
	{
		var payload = new byte[56];
		// Contrive a payload summing to 0x050C so the whole packet reproduces the captured checksum.
		int remaining = 0x050C;
		for (int i = 0; i < payload.Length && remaining > 0; i++)
		{
			payload[i] = (byte)Math.Min(0xFF, remaining);
			remaining -= payload[i];
		}
		Assert.Equal(0, remaining);

		var report = RampageProtocol.BuildCommand(RampageProtocol.CmdSetKeyInfo, addr: 0, payload: payload);

		Assert.Equal(0x5C, report[5]);
		Assert.Equal(0x05, report[6]);
	}

	// Characterization of the RGB wire change (PR #67 review, M4): the three RGB services now
	// frame through BuildCommand, whose bytes 5-6 are the additive sum — NOT the CRC16/Modbus the
	// inline copies used to compute. For this 0x15 Set General Info frame (the payload
	// hardware-findings.md shows persisting: mode=10, brightness=59, speed=3, R=118 G=32 B=238)
	// the additive sum is 0x01F2; the old CRC16 produced different bytes. The frame on the wire
	// CHANGED for shipped devices — QA step 11 (RGB smoke on all three boards) gates the merge.
	[Fact]
	public void BuildCommand_RgbSetGeneralInfoFrame_CarriesTheAdditiveSumNotCrc16()
	{
		byte[] payload = [0x00, 0x0A, 0x3B, 0x03, 0x00, 0x00, 0x76, 0x20, 0xEE, 0x07];

		var report = RampageProtocol.BuildCommand(0x15, addr: 0, payload: payload);

		Assert.Equal(0x15, report[1]);
		Assert.Equal(10, report[4]);
		Assert.Equal(0xF2, report[5]); // 0x15 + 0x0A + Σpayload = 0x01F2, little-endian
		Assert.Equal(0x01, report[6]);
	}

	[Fact]
	public void ComputeChecksum_IsASumNotACrc()
	{
		// A trivially checkable case: cmd 0x12, addr 0, len 0x38 -> 0x4A. This is the value the
		// README misattributes to command 0x16.
		var report = RampageProtocol.BuildReadCommand(0x12, addr: 0, length: 56);

		Assert.Equal(0x4A, report[5]);
		Assert.Equal(0x00, report[6]);
	}

	// The exact offsets both vendor captures show: 0x00, 0x38, 0x70, 0xA8, 0xE0, 0x118. Six
	// packets, every one a full 56 bytes. This is the READ shape; writes use EnumerateWriteChunks,
	// which covers the full 384-byte reserve per the factory update of 2026-08.
	[Fact]
	public void EnumerateChunks_MatchesTheVendorCapturedSixPacketTransfer()
	{
		var chunks = RampageProtocol.EnumerateChunks();

		Assert.Equal(6, chunks.Count);
		Assert.Equal<(ushort, int)>((0x0000, 56), chunks[0]);
		Assert.Equal<(ushort, int)>((0x0038, 56), chunks[1]);
		Assert.Equal<(ushort, int)>((0x0070, 56), chunks[2]);
		Assert.Equal<(ushort, int)>((0x00A8, 56), chunks[3]);
		Assert.Equal<(ushort, int)>((0x00E0, 56), chunks[4]);
		Assert.Equal<(ushort, int)>((0x0118, 56), chunks[5]);
	}

	[Fact]
	public void EnumerateChunks_CoversAllKeyDataContiguously()
	{
		var chunks = RampageProtocol.EnumerateChunks();

		// Covers 336 bytes: past the 288 bytes of real key data, into the table reserve.
		Assert.Equal(336, chunks.Sum(c => c.Length));
		Assert.True(chunks.Sum(c => c.Length) >= RampageProtocol.KeyDataSize);
		Assert.True(chunks.Sum(c => c.Length) < RampageProtocol.KeyMatrixTableSize);

		int expectedOffset = 0;
		foreach (var (addr, length) in chunks)
		{
			Assert.Equal(expectedOffset, addr);
			expectedOffset += length;
		}
	}

	[Fact]
	public void EnumerateChunks_NeverEmitsAPartialFinalPacket()
	{
		Assert.All(RampageProtocol.EnumerateChunks(), c => Assert.Equal(56, c.Length));
	}

	// The factory update for RM1 (2026-08): a key write must total exactly 384 bytes — the whole
	// KEY_MATRIX_TAB_SIZE reserve — so the final packet carries 48 bytes, not a full 56.
	[Fact]
	public void EnumerateWriteChunks_CoversTheFull384ByteTableInSevenPackets()
	{
		var chunks = RampageProtocol.EnumerateWriteChunks();

		Assert.Equal(7, chunks.Count);
		Assert.Equal<(ushort, int)>((0x0000, 56), chunks[0]);
		Assert.Equal<(ushort, int)>((0x0038, 56), chunks[1]);
		Assert.Equal<(ushort, int)>((0x0070, 56), chunks[2]);
		Assert.Equal<(ushort, int)>((0x00A8, 56), chunks[3]);
		Assert.Equal<(ushort, int)>((0x00E0, 56), chunks[4]);
		Assert.Equal<(ushort, int)>((0x0118, 56), chunks[5]);
		Assert.Equal<(ushort, int)>((0x0150, 48), chunks[6]);
	}

	[Fact]
	public void EnumerateWriteChunks_TotalsExactlyTheTableSizeContiguously()
	{
		var chunks = RampageProtocol.EnumerateWriteChunks();

		Assert.Equal(RampageProtocol.KeyMatrixTableSize, chunks.Sum(c => c.Length));

		int expectedOffset = 0;
		foreach (var (addr, length) in chunks)
		{
			Assert.Equal(expectedOffset, addr);
			expectedOffset += length;
		}
	}

	// The 2.4G receiver relays table transfers only in 24-byte payload packets (factory
	// clarification C12; hardware-confirmed 2026-08-13 — a 56-byte read arrives misaligned, a
	// 56-byte-chunked write is acked and discarded, and this exact 12-packet read / 16-packet
	// write shape is what persisted on the real receiver).
	[Fact]
	public void EnumerateChunks_Receiver24GShape_TwelveFullPacketsCoveringTheKeyData()
	{
		var chunks = RampageProtocol.EnumerateChunks(maxPayload: RampageProtocol.Receiver24GPayloadLength);

		Assert.Equal(12, chunks.Count);
		Assert.All(chunks, c => Assert.Equal(24, c.Length));
		Assert.Equal(RampageProtocol.KeyDataSize, chunks.Sum(c => c.Length));

		int expectedOffset = 0;
		foreach (var (addr, length) in chunks)
		{
			Assert.Equal(expectedOffset, addr);
			expectedOffset += length;
		}
	}

	[Fact]
	public void EnumerateWriteChunks_Receiver24GShape_SixteenPacketsTotallingExactly384()
	{
		var chunks = RampageProtocol.EnumerateWriteChunks(RampageProtocol.Receiver24GPayloadLength);

		Assert.Equal(16, chunks.Count);
		Assert.All(chunks, c => Assert.Equal(24, c.Length));
		Assert.Equal(RampageProtocol.KeyMatrixTableSize, chunks.Sum(c => c.Length));
		Assert.Equal<(ushort, int)>((0x0168, 24), chunks[^1]);
	}

	// The default overloads must keep producing the wired shapes byte for byte — the cable path
	// is proven working and every existing caller relies on the parameter defaulting to 56.
	[Fact]
	public void ChunkEnumerators_DefaultToTheWiredShapes()
	{
		Assert.Equal(6, RampageProtocol.EnumerateChunks().Count);
		Assert.Equal(7, RampageProtocol.EnumerateWriteChunks().Count);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(57)]
	public void ChunkEnumerators_RejectPayloadsOutsideOnePacket(int maxPayload)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => RampageProtocol.EnumerateChunks(maxPayload: maxPayload));
		Assert.Throws<ArgumentOutOfRangeException>(() => RampageProtocol.EnumerateWriteChunks(maxPayload));
	}

	// Regression test for a real field failure. A read whose chunks landed one 56-byte packet out
	// of place made the app report slot 33 (Q) as {0x00, 0x47, 0x10} — which is the byte triple
	// that really lives at offset 43. Because 56 is not a multiple of 3, the shift destroys record
	// alignment, so the TYPE positions fill with what were actually key codes. Had that table been
	// written back it would have scrambled the whole keyboard.
	[Fact]
	public void IsPlausibleKeyTable_RejectsATableShiftedByOneChunk()
	{
		var good = FactoryLikeTable();
		var shifted = new byte[RampageProtocol.KeyMatrixTableSize];
		Array.Copy(good, 0, shifted, RampageProtocol.MaxPayloadLength,
			good.Length - RampageProtocol.MaxPayloadLength);

		Assert.True(RampageProtocol.IsPlausibleKeyTable(good, out var okSlots));
		Assert.Equal(RampageProtocol.KeyCount, okSlots);

		Assert.False(RampageProtocol.IsPlausibleKeyTable(shifted, out _));

		// And the exact symptom from the log is reproduced by the shift.
		Assert.Equal(new KeyMatrixRecord(0x00, 0x47, 0x10), RampageProtocol.ReadSlot(shifted, 33));
	}

	[Fact]
	public void IsPlausibleKeyTable_AcceptsUserRemappedSlots()
	{
		var table = FactoryLikeTable();
		// A user remap only changes code bytes, never the TYPE, so plausibility must survive it.
		RampageProtocol.PatchSlot(table, 33, new KeyMatrixRecord(0x10, 0x00, 0x1A)); // Q -> W
		RampageProtocol.PatchSlot(table, 35, new KeyMatrixRecord(0x20, 0xE9, 0x00)); // media target

		Assert.True(RampageProtocol.IsPlausibleKeyTable(table, out _));
	}

	// A stand-in for the vendor factory table: every slot a well-formed keyboard record, with FN
	// at slot 90 exactly as keyboard_user_flash.c has it.
	private static byte[] FactoryLikeTable()
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		for (int slot = 0; slot < RampageProtocol.KeyCount; slot++)
			RampageProtocol.PatchSlot(table, slot, new KeyMatrixRecord(0x10, 0x00, (byte)(0x04 + slot % 0x60)));

		RampageProtocol.PatchSlot(table, 14, new KeyMatrixRecord(0x10, 0x00, 0x47)); // ScrollLock
		RampageProtocol.PatchSlot(table, 15, new KeyMatrixRecord(0x10, 0x00, 0x48)); // Pause
		RampageProtocol.PatchSlot(table, 33, new KeyMatrixRecord(0x10, 0x00, 0x14)); // Q
		RampageProtocol.PatchSlot(table, 90, new KeyMatrixRecord(0x60, 0x01, 0x00)); // FN
		return table;
	}

	[Fact]
	public void ReadSlot_DecodesTheThreeByteRecordAtSlotTimesThree()
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		// Slot 35 (E in the factory table) at offset 105.
		table[105] = 0x10;
		table[106] = 0x00;
		table[107] = 0x08;

		var record = RampageProtocol.ReadSlot(table, 35);

		Assert.Equal(new KeyMatrixRecord(0x10, 0x00, 0x08), record);
	}

	// The guard that matters most: a read-modify-write must not disturb any other key on the board.
	[Fact]
	public void PatchSlot_ChangesOnlyThatSlotsThreeBytes()
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		for (int i = 0; i < table.Length; i++) table[i] = (byte)(i % 251);
		var original = (byte[])table.Clone();

		RampageProtocol.PatchSlot(table, 35, new KeyMatrixRecord(0x10, 0x00, 0x04));

		Assert.Equal(0x10, table[105]);
		Assert.Equal(0x00, table[106]);
		Assert.Equal(0x04, table[107]);

		for (int i = 0; i < table.Length; i++)
		{
			if (i is >= 105 and <= 107) continue;
			Assert.Equal(original[i], table[i]);
		}
	}

	[Fact]
	public void PatchSlot_RejectsAShortTable()
	{
		var shortTable = new byte[RampageProtocol.KeyMatrixTableSize - 1];

		Assert.Throws<ArgumentException>(() =>
			RampageProtocol.PatchSlot(shortTable, 0, new KeyMatrixRecord(0x10, 0, 0x04)));
	}

	[Fact]
	public void PatchSlot_RejectsAnOutOfRangeSlot()
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			RampageProtocol.PatchSlot(table, RampageProtocol.MatrixSlotCount, new KeyMatrixRecord(0x10, 0, 0x04)));
	}

	[Fact]
	public void ParseTable_SkipsEmptySlotsButKeepsModifierOnlyRecords()
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		RampageProtocol.PatchSlot(table, 0, new KeyMatrixRecord(0x10, 0x00, 0x29));  // Esc
		RampageProtocol.PatchSlot(table, 64, new KeyMatrixRecord(0x10, 0x02, 0x00)); // Left Shift

		var map = RampageProtocol.ParseTable(table);

		Assert.Equal(2, map.Count);
		Assert.Equal(new KeyMatrixRecord(0x10, 0x00, 0x29), map[0]);
		// A modifier carries its identity in code1 with a 0x00 usage code — it must survive parsing.
		Assert.Equal(new KeyMatrixRecord(0x10, 0x02, 0x00), map[64]);
	}

	[Fact]
	public void ParseTable_RejectsATableOfTheWrongSize()
	{
		Assert.Throws<ArgumentException>(() => RampageProtocol.ParseTable(new byte[100]));
	}

}
