using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the TAIPAN (Telink) 33-byte frame builder. TAIPAN firmware REJECTS a frame whose
// `dummy` checksum is wrong (unlike BLAZE, which ignores its checksum), so the checksum is the
// single most breakage-prone detail — the golden cases below are real vendor frames from
// docs/mouse/taipan/protocol.md's worked "write the default map" example.
public class TaipanFrameTests
{
	[Fact]
	public void Build_ReturnsExactly33Bytes()
	{
		var f = TaipanFrame.Build(0x03, 0x00, 0x00, 0x02, TaipanFrame.SeqFirst, [0x0A, 0x00]);
		Assert.Equal(33, f.Length);
	}

	[Fact]
	public void Build_SetsReportIdAndDriverId()
	{
		var f = TaipanFrame.Build(0x03, 0x00, 0x00, 0x00, TaipanFrame.SeqCommit, ReadOnlySpan<byte>.Empty);
		Assert.Equal(0x05, f[0]); // ReportID
		Assert.Equal(0xFA, f[2]); // driverID
		Assert.Equal(0x03, f[3]); // cmd echoed
	}

	// dummy = (driverID + cmd + p1 + p2 + p3 + p4) ^ 0x66, byte-wide. Each row is a real frame
	// header from the vendor example; the expected value is its published `dummy` byte.
	[Theory]
	[InlineData(0x03, 0x00, 0x00, 0x02, 0xD1, 0xB6)] // TX(D1) header
	[InlineData(0x03, 0x02, 0x00, 0x18, 0xD2, 0x8F)] // TX(D2) buttons 1-6
	[InlineData(0x03, 0x1A, 0x00, 0x18, 0xD2, 0x67)] // TX(D2) button 7
	[InlineData(0x03, 0x7F, 0x00, 0x01, 0xD2, 0x29)] // TX(BankID) ValidBankID @ 0x007F
	[InlineData(0x03, 0x00, 0x00, 0x00, 0xD3, 0xB6)] // TX(D3) commit
	public void Checksum_MatchesVendorFrames(byte cmd, byte p1, byte p2, byte p3, byte p4, byte expected)
	{
		Assert.Equal(expected, TaipanFrame.Checksum(cmd, p1, p2, p3, p4));
		var f = TaipanFrame.Build(cmd, p1, p2, p3, p4, ReadOnlySpan<byte>.Empty);
		Assert.Equal(expected, f[1]); // dummy sits at Byte1
	}

	[Fact]
	public void Build_PlacesPayloadAtByte8()
	{
		byte[] payload = [0x81, 0x00, 0x00, 0x00, 0x82];
		var f = TaipanFrame.Build(0x03, 0x02, 0x00, 0x05, TaipanFrame.SeqContinue, payload);
		for (int i = 0; i < payload.Length; i++)
			Assert.Equal(payload[i], f[TaipanFrame.PayloadOffset + i]);
	}

	[Fact]
	public void Build_RejectsPayloadLongerThanFifo()
	{
		var tooLong = new byte[TaipanFrame.MaxPayload + 1];
		Assert.Throws<ArgumentException>(() =>
			TaipanFrame.Build(0x03, 0x00, 0x00, 0x00, TaipanFrame.SeqContinue, tooLong));
	}

	[Fact]
	public void Build_AcceptsPayloadExactlyFifoLength()
	{
		var exact = new byte[TaipanFrame.MaxPayload];
		var f = TaipanFrame.Build(0x03, 0x00, 0x00, (byte)exact.Length, TaipanFrame.SeqContinue, exact);
		Assert.Equal(33, f.Length);
	}
}
