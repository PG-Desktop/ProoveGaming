namespace ProoveGaming.Infrastructure.Classes;

// The framing around a TAIPAN (Telink) flash-table write: enter configuration mode, commit, apply,
// exit. Pure and side-effect-free like the other Taipan encoders, so the byte layout can be pinned
// byte-for-byte against the vendor's own capture without a device. See TaipanConfigSessionTests.
//
// WHY THIS EXISTS
//
// Until 2026-09-02 the App believed configuration writes were impossible over the 2.4G dongle: the
// mouse answered every `0x03` frame on the cable and nothing at all over the radio, which was
// recorded as a transport limitation (docs/mouse/taipan/taipan-dongle-ack-evidence-2026-08-18.md).
//
// That conclusion was wrong. The vendor's Bus Hound capture of 2026-09-02 shows their driver
// bracketing every write with a handshake we had never sent:
//
//   1. `0x46` with Para1 = 0x00 — ENTER configuration mode, retried until acknowledged.
//   2. …the table write…
//   3. commit: ValidBankID at the table's bank offset, in ONE frame carrying `driverPara4 = 0xD3`
//      (we had been sending two: a 0xD2 ValidBankID, then an empty 0xD3).
//   4. `0x40` with Para1 = 0xF3 and Para2 = a per-table bitmask — APPLY.
//   5. `0x46` with Para1 = 0x01 — EXIT configuration mode.
//
// Verified on hardware the same day: with the handshake, every frame of a button-table write is
// acknowledged OVER THE DONGLE, the commit answers `driverPara4 = 0xEE`, and the remap takes
// effect. The cable had always tolerated the missing handshake, which is why this went unnoticed.
//
// SAFETY — `0x06` / `0x46` is `CMD_SetMouseState`, and Para1 = 0x00 suppresses the mouse's own
// reporting. A device left in configuration mode stops working as a mouse until it is replugged,
// so ExitFrame must be sent on EVERY path out of a write, exceptions included.
public static class TaipanConfigSession
{
	// CMD_SetMouseState. The vendor sends the READ/ACK form (0x46), not the write form (0x06) —
	// the same pattern the battery request already needed over 2.4G, where `0x48` answers and
	// `0x08` does not.
	public const byte SetMouseStateAckCommand = 0x46;
	public const byte EnterConfigPara1 = 0x00;
	public const byte ExitConfigPara1 = 0x01;

	// CMD_Reset's read/ACK form, used here as "apply the table(s) named by the mask".
	public const byte ApplyCommand = 0x40;
	public const byte ApplyPara1 = 0xF3;

	// Per-table bits, in the order the vendor's driver applies them.
	public const byte ApplyMaskConfig = 0x01;   // MsConfig   (cmd 0x01)
	public const byte ApplyMaskDpi = 0x02;      // DPI table  (cmd 0x02)
	public const byte ApplyMaskButton = 0x04;   // button map (cmd 0x03)
	public const byte ApplyMaskLight = 0x08;    // LED table  (cmd 0x04)
	public const byte ApplyMaskMacro = 0x10;    // macros     (cmd 0x05)

	// `driverPara4` on a reply, meaning the firmware accepted and committed the frame. Distinct
	// from the plain per-frame ACK, which echoes the frame with driverPara4 = 0x00.
	public const byte CommittedPara4 = 0xEE;

	// How many times to ask for configuration mode before giving up. The vendor's capture needed
	// five; ours was acknowledged on the second. Cheap either way — this runs once per user-
	// initiated write, never on a hot path.
	public const int EnterAttempts = 5;

	// Spacing between attempts, from the vendor capture (~170 ms).
	public const int EnterGapMs = 170;

	/// <summary>Enter configuration mode. Retry until acknowledged; see <see cref="EnterAttempts"/>.</summary>
	public static byte[] EnterFrame() =>
		TaipanFrame.Build(SetMouseStateAckCommand, EnterConfigPara1, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty);

	/// <summary>Leave configuration mode and restore normal mouse reporting. Must always be sent.</summary>
	public static byte[] ExitFrame() =>
		TaipanFrame.Build(SetMouseStateAckCommand, ExitConfigPara1, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty);

	/// <summary>
	/// The single-frame commit: ValidBankID written at <paramref name="bankIdOffset"/> with the
	/// commit sequencing value, replacing the two-frame form (0xD2 ValidBankID + empty 0xD3).
	/// </summary>
	public static byte[] CommitFrame(byte cmd, int bankIdOffset) =>
		TaipanFrame.Build(
			cmd,
			(byte)(bankIdOffset & 0xFF),
			(byte)(bankIdOffset >> 8),
			0x01,
			TaipanFrame.SeqCommit,
			[TaipanButtonMap.ValidBankId]);

	/// <summary>
	/// The commit for a table that takes NO ValidBankID — MsConfig is the only one. Writing a bank
	/// id for it puts 0x1A far outside the 19-byte block and the firmware rejects the whole
	/// transaction, leaving the sector erased (hardware-confirmed 2026-07-20).
	/// </summary>
	public static byte[] CommitFrameWithoutBankId(byte cmd) =>
		TaipanFrame.Build(cmd, 0x00, 0x00, 0x00, TaipanFrame.SeqCommit, ReadOnlySpan<byte>.Empty);

	/// <summary>Apply the table(s) named by <paramref name="mask"/>, e.g. <see cref="ApplyMaskButton"/>.</summary>
	public static byte[] ApplyFrame(byte mask) =>
		TaipanFrame.Build(ApplyCommand, ApplyPara1, mask, 0x00, 0x00, ReadOnlySpan<byte>.Empty);

	/// <summary>True when a reply frame carries the firmware's "committed" marker.</summary>
	public static bool IsCommitted(ReadOnlySpan<byte> reply) =>
		reply.Length > 7 &&
		reply[0] == TaipanFrame.ReportId &&
		reply[2] == TaipanFrame.DriverId &&
		reply[7] == CommittedPara4;

	/// <summary>The apply mask for a table command, or null for a command that needs no apply.</summary>
	public static byte? ApplyMaskFor(byte cmd) => cmd switch
	{
		0x01 => ApplyMaskConfig,
		0x02 => ApplyMaskDpi,
		0x03 => ApplyMaskButton,
		0x04 => ApplyMaskLight,
		0x05 => ApplyMaskMacro,
		_ => null,
	};
}
