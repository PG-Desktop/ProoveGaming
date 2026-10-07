using System.Linq;
using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// RY5088 (T800) framing. Every expected value here is MEASURED against hardware, not derived from a
// vendor document — neither document defines the checksum or the key-record layout, and a wrong
// value produces no error at all: the firmware discards the packet silently and a following read
// returns the driver's cached copy of the request, which looks like a reply.
//
// The frames below are the exact bytes the device accepted during the write test recorded in
// docs/plans/add-t800-key-assignments/notes.md.
public class Ry5088ProtocolTests
{
	// ── Checksum ─────────────────────────────────────────────────────────────────

	// One's complement of the sum. A plain sum is off by exactly one and is silently discarded.
	[Fact]
	public void Checksum_IsOnesComplementOfTheSum()
	{
		var packet = new byte[] { 0x00, 0x07, 0x01, 0x00, 0x04, 0x07, 0xFF, 0x00, 0x00 };

		// 0x07+0x01+0x00+0x04+0x07+0xFF+0x00+0x00 = 0x112 -> 0xFF - 0x12 = 0xED
		Assert.Equal(0xED, Ry5088Protocol.Checksum(packet, 1, 8));
	}

	[Fact]
	public void Checksum_MatchesTheAcceptedGetKeyMatrixFrame()
	{
		var request = Ry5088Protocol.BuildGetKeyMatrixPage(0);

		// 0x8A + 0xFF = 0x189 -> 0xFF - 0x89 = 0x76
		Assert.Equal(0x76, request[8]);
	}

	// ── GET_KEYMATRIX ────────────────────────────────────────────────────────────

	[Fact]
	public void GetKeyMatrixPage_MatchesTheAcceptedFrame()
	{
		var p = Ry5088Protocol.BuildGetKeyMatrixPage(page: 3);

		Assert.Equal(Ry5088Protocol.ReportLength, p.Length);
		Assert.Equal(Ry5088Protocol.HidReportId, p[0]);
		Assert.Equal(0x8A, p[1]);                          // GET_KEYMATRIX
		Assert.Equal(0x00, p[2]);                          // Profile 0
		Assert.Equal(Ry5088Protocol.TypeWholeMatrix, p[3]); // 255 = whole table
		Assert.Equal(3, p[4]);                             // Page
		Assert.Equal(0x00, p[5]);                          // Index / layer 0
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
	}

	// ── SET_KEYMATRIX ────────────────────────────────────────────────────────────

	// The exact frame the hardware accepted, byte for byte:
	//   0A 00 47 00 04 01 00 A9 00 00 E6 00
	// It changed slot 71 (0x47) from RightCtrl to RightAlt and provably touched nothing else.
	[Fact]
	public void SetKeyRecord_ReproducesTheFrameTheHardwareAccepted()
	{
		var p = Ry5088Protocol.BuildSetKeyRecord(slot: 71, type: 0x00, code1: 0x00, code2: 0xE6);

		var wire = p.Skip(1).Take(12).ToArray();
		Assert.Equal(new byte[] { 0x0A, 0x00, 0x47, 0x00, 0x04, 0x01, 0x00, 0xA9, 0x00, 0x00, 0xE6, 0x00 }, wire);
	}

	// Type carries the SLOT, not 255. That is what makes a write surgical — it cannot disturb any
	// other key however wrong the rest of the framing is, which is why this family needs no
	// read-modify-write of the whole table.
	[Fact]
	public void SetKeyRecord_AddressesASingleSlotNotTheWholeTable()
	{
		var p = Ry5088Protocol.BuildSetKeyRecord(slot: 9, type: 0x00, code1: 0x00, code2: 0x04);

		Assert.Equal(9, p[3]);
		Assert.NotEqual(Ry5088Protocol.TypeWholeMatrix, p[3]);
		Assert.Equal(Ry5088Protocol.RecordBytes, p[5]);
	}

	// Without FlashEnable the firmware buffers the change and it is lost on replug.
	[Theory]
	[InlineData(true, 1)]
	[InlineData(false, 0)]
	public void SetKeyRecord_CarriesFlashEnable(bool flash, byte expected)
	{
		var p = Ry5088Protocol.BuildSetKeyRecord(1, 0x00, 0x00, 0x29, flashEnable: flash);

		Assert.Equal(expected, p[6]);
	}

	// The record's fourth byte is 0x00 throughout the factory table; the builder must keep it so,
	// because that is what lets these records ride the App's 3-byte KeyMatrixRecord.
	[Fact]
	public void SetKeyRecord_LeavesTheFourthRecordByteZero()
	{
		var p = Ry5088Protocol.BuildSetKeyRecord(1, 0x0A, 0x01, 0x00);

		Assert.Equal(0x00, p[12]);
	}

	// ── Matrix parsing ───────────────────────────────────────────────────────────

	[Fact]
	public void ParseMatrix_DecodesRecordsAndSkipsEmptySlots()
	{
		var matrix = new byte[Ry5088Protocol.MatrixSlots * Ry5088Protocol.RecordBytes];
		// slot 0 left empty, slot 1 = Escape, slot 65 = FN
		matrix[4] = 0x00; matrix[5] = 0x00; matrix[6] = 0x29; matrix[7] = 0x00;
		matrix[65 * 4] = 0x0A; matrix[65 * 4 + 1] = 0x01;

		var map = Ry5088Protocol.ParseMatrix(matrix);

		Assert.False(map.ContainsKey(0));
		Assert.Equal(new KeyMatrixRecord(0x00, 0x00, 0x29), map[1]);
		Assert.Equal(new KeyMatrixRecord(0x0A, 0x01, 0x00), map[65]);
	}

	// The fourth wire byte is dropped, not folded into the 3-byte record.
	[Fact]
	public void ParseMatrix_DropsTheFourthRecordByte()
	{
		var matrix = new byte[Ry5088Protocol.MatrixSlots * Ry5088Protocol.RecordBytes];
		matrix[0] = 0x00; matrix[1] = 0x00; matrix[2] = 0x29; matrix[3] = 0xAB;

		Assert.Equal(new KeyMatrixRecord(0x00, 0x00, 0x29), Ry5088Protocol.ParseMatrix(matrix)[0]);
	}

	// A rejected read leaves the caller holding its own request, which parses into a couple of
	// nonsense slots. That must read as "unknown", never as "the board has no keys" — writing a UI
	// state from it would show every key as unassigned.
	[Fact]
	public void IsPlausibleKeyTable_RejectsAnEchoedRequest()
	{
		var echo = new byte[Ry5088Protocol.MatrixSlots * Ry5088Protocol.RecordBytes];
		echo[0] = 0x8A; echo[2] = 0xFF; echo[7] = 0x76;

		Assert.False(Ry5088Protocol.IsPlausibleKeyTable(Ry5088Protocol.ParseMatrix(echo)));
	}

	[Fact]
	public void IsPlausibleKeyTable_AcceptsAFullBoard()
	{
		var matrix = new byte[Ry5088Protocol.MatrixSlots * Ry5088Protocol.RecordBytes];
		for (int slot = 1; slot <= 68; slot++)
			matrix[slot * 4 + 2] = 0x04;

		Assert.True(Ry5088Protocol.IsPlausibleKeyTable(Ry5088Protocol.ParseMatrix(matrix)));
	}

	[Fact]
	public void MatrixGeometry_Is512BytesOver8Pages()
	{
		Assert.Equal(512, Ry5088Protocol.MatrixPages * Ry5088Protocol.PageBytes);
		Assert.Equal(512, Ry5088Protocol.MatrixSlots * Ry5088Protocol.RecordBytes);
	}

	// ── SET_MACRO / GET_MACRO ────────────────────────────────────────────────────
	// Framing per the YC3123 doc's Set macro / Read macro sections:
	//   0x0B | Macro | Page | Len | End | 00 | 00 | CS | payload
	//   0x8B | Macro | Page | 00  | 00  | 00 | 00 | CS

	[Fact]
	public void SetMacroPage_LaysOutHeaderChecksumAndPayload()
	{
		var payload = new byte[] { 0x01, 0x00, 0x04, 0x8A, 0x04, 0x0A, 0x00, 0x00 };
		var p = Ry5088Protocol.BuildSetMacroPage(macroIndex: 5, page: 0, payload, end: true);

		Assert.Equal(Ry5088Protocol.ReportLength, p.Length);
		Assert.Equal(Ry5088Protocol.HidReportId, p[0]);
		Assert.Equal(Ry5088Protocol.CmdSetMacro, p[1]);
		Assert.Equal(5, p[2]);                              // Macro slot
		Assert.Equal(0, p[3]);                              // Page
		Assert.Equal(payload.Length, p[4]);                 // Len = effective payload bytes
		Assert.Equal(1, p[5]);                              // End = macro end
		Assert.Equal(0x00, p[6]);
		Assert.Equal(0x00, p[7]);
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
		Assert.Equal(payload, p.Skip(9).Take(payload.Length).ToArray());
	}

	// End marks the LAST page of a macro only — a non-final page must leave it clear, or the
	// firmware treats a partial blob as complete.
	[Theory]
	[InlineData(true, 1)]
	[InlineData(false, 0)]
	public void SetMacroPage_CarriesTheEndFlag(bool end, byte expected)
	{
		var p = Ry5088Protocol.BuildSetMacroPage(0, 0, new byte[4], end);

		Assert.Equal(expected, p[5]);
	}

	[Fact]
	public void GetMacroPage_MatchesTheDocumentedFrame()
	{
		var p = Ry5088Protocol.BuildGetMacroPage(macroIndex: 7, page: 3);

		Assert.Equal(Ry5088Protocol.ReportLength, p.Length);
		Assert.Equal(Ry5088Protocol.CmdGetMacro, p[1]);
		Assert.Equal(7, p[2]);
		Assert.Equal(3, p[3]);
		Assert.Equal(0x00, p[4]);
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
	}

	// Write pages carry 56 bytes, read replies 64 — the asymmetry is the vendor doc's.
	[Fact]
	public void MacroGeometry_CoversTheBlobOnBothPaths()
	{
		Assert.True(5 * Ry5088Protocol.MacroWritePageBytes >= Ry5088Protocol.MacroBytes);
		Assert.Equal(Ry5088Protocol.MacroBytes,
			Ry5088Protocol.MacroReadPages * Ry5088Protocol.MacroReadPageBytes);
	}
}
