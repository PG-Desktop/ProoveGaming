namespace ProoveGaming.Infrastructure.Classes;

// Pure builder for the TAIPAN (Telink) 33-byte HID report. Kept device-free so the framing and
// the mandatory checksum can be snapshot-tested byte-for-byte against the vendor examples in
// docs/mouse/taipan/protocol.md — unlike BLAZE, whose firmware ignores its checksum, TAIPAN
// rejects a frame whose `dummy` byte is wrong, so this is the single most breakage-prone detail.
// TelinkMouseService sends exactly what this produces. See TaipanFrameTests.
public static class TaipanFrame
{
	// Whole HID report length including the report-id byte.
	public const int Length = 33;
	public const byte ReportId = 0x05;   // Byte0
	public const byte DriverId = 0xFA;   // Byte2
	public const byte ChecksumXor = 0x66;

	// FIFO payload occupies Byte8..32 (25 bytes).
	public const int PayloadOffset = 8;
	public const int MaxPayload = Length - PayloadOffset;

	// driverPara4 packet-sequencing values.
	public const byte SeqFirst = 0xD1;    // NOTE: erases the flash sector — see protocol.md
	public const byte SeqContinue = 0xD2;
	public const byte SeqCommit = 0xD3;

	// dummy = (driverID + cmd + p1 + p2 + p3 + p4) ^ 0x66, byte-wide (carry discarded).
	public static byte Checksum(byte cmd, byte p1, byte p2, byte p3, byte p4) =>
		(byte)((byte)(DriverId + cmd + p1 + p2 + p3 + p4) ^ ChecksumXor);

	// Builds one 33-byte report: [ReportId, dummy, driverID, cmd, p1, p2, p3, p4, payload…].
	// Throws if the payload would overrun the 25-byte FIFO region.
	public static byte[] Build(byte cmd, byte p1, byte p2, byte p3, byte p4, ReadOnlySpan<byte> payload)
	{
		if (payload.Length > MaxPayload)
			throw new ArgumentException($"Payload of {payload.Length} bytes exceeds the {MaxPayload}-byte FIFO", nameof(payload));

		var frame = new byte[Length];
		frame[0] = ReportId;
		frame[1] = Checksum(cmd, p1, p2, p3, p4);
		frame[2] = DriverId;
		frame[3] = cmd;
		frame[4] = p1;
		frame[5] = p2;
		frame[6] = p3;
		frame[7] = p4;
		payload.CopyTo(frame.AsSpan(PayloadOffset));
		return frame;
	}
}
