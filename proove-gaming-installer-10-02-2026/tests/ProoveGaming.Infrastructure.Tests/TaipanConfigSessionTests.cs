using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The configuration-mode framing, pinned byte-for-byte against the VENDOR'S OWN Bus Hound capture
// of 2026-09-02. Every expected frame below was read out of that capture, not computed from our
// own encoder — which is the point: this suite exists because our previous framing was
// self-consistent, passed its own tests, and was still wrong.
//
// What it was wrong about: the App had concluded that configuration writes were impossible over the
// 2.4G dongle, because the mouse answered every frame on the cable and nothing at all over the
// radio. That was recorded as a transport limitation for three weeks. It was not one — we were
// simply never entering configuration mode, and the cable had always tolerated its absence.
//
// Hardware-confirmed the same day: with this framing, every frame of a button-table write is
// acknowledged over the dongle and the commit answers driverPara4 = 0xEE.
public class TaipanConfigSessionTests
{
	private static string Hex(byte[] frame, int count) =>
		string.Join(' ', frame.Take(count).Select(b => b.ToString("X2")));

	// ---- Enter / exit ------------------------------------------------------------------------

	// Their capture opens with this frame, repeated until answered.
	[Fact]
	public void EnterFrameMatchesTheVendorCapture()
	{
		Assert.Equal("05 26 FA 46 00 00 00 00", Hex(TaipanConfigSession.EnterFrame(), 8));
	}

	// ...and closes with this one. If it is ever not sent, the mouse stays in configuration mode
	// and stops working as a mouse until it is replugged, so its exact bytes matter as much as the
	// enter frame's.
	[Fact]
	public void ExitFrameMatchesTheVendorCapture()
	{
		Assert.Equal("05 27 FA 46 01 00 00 00", Hex(TaipanConfigSession.ExitFrame(), 8));
	}

	// The two must differ ONLY in Para1 (and therefore the checksum). A copy-paste that left both
	// on the same Para1 would either never enter config mode or never leave it, and the second
	// failure mode is the one that strands a user's mouse.
	[Fact]
	public void EnterAndExitDifferOnlyInPara1()
	{
		var enter = TaipanConfigSession.EnterFrame();
		var exit = TaipanConfigSession.ExitFrame();

		Assert.NotEqual(enter[4], exit[4]);
		Assert.Equal(0x00, enter[4]);
		Assert.Equal(0x01, exit[4]);
		Assert.Equal(enter[3], exit[3]);
		// Everything from the payload on is identical.
		Assert.Equal(enter.Skip(8), exit.Skip(8));
	}

	// ---- The single-frame commit -------------------------------------------------------------

	// The change that matters most: ONE frame carrying both the ValidBankID and the commit
	// sequencing value, where we used to send a 0xD2 ValidBankID followed by an empty 0xD3.
	// All four table commands appear in their capture, so all four are pinned.
	[Theory]
	[InlineData(0x01, "05 28 FA 01 7F 00 01 D3")]   // MsConfig
	[InlineData(0x02, "05 29 FA 02 7F 00 01 D3")]   // DPI table
	[InlineData(0x03, "05 36 FA 03 7F 00 01 D3")]   // button map
	[InlineData(0x04, "05 37 FA 04 7F 00 01 D3")]   // LED table
	public void CommitFrameMatchesTheVendorCapture(byte cmd, string expected)
	{
		Assert.Equal(expected, Hex(TaipanConfigSession.CommitFrame(cmd, TaipanButtonMap.ValidBankIdOffset), 8));
	}

	[Fact]
	public void CommitFrameCarriesTheBankIdAsItsPayload()
	{
		var frame = TaipanConfigSession.CommitFrame(0x03, TaipanButtonMap.ValidBankIdOffset);

		Assert.Equal(TaipanButtonMap.ValidBankId, frame[TaipanFrame.PayloadOffset]);
		Assert.Equal(TaipanFrame.SeqCommit, frame[7]);
	}

	// MsConfig is the one table that takes NO ValidBankID. Writing one puts 0x1A far outside the
	// 19-byte block; the firmware rejects the transaction and leaves the sector erased
	// (hardware-confirmed 2026-07-20), which is the "800 DPI + polling change => max DPI" bug.
	[Fact]
	public void CommitWithoutBankIdSendsNoPayloadAndNoOffset()
	{
		var frame = TaipanConfigSession.CommitFrameWithoutBankId(TaipanMsConfig.ConfigCommand);

		Assert.Equal(0x00, frame[4]);
		Assert.Equal(0x00, frame[5]);
		Assert.Equal(0x00, frame[6]);
		Assert.Equal(TaipanFrame.SeqCommit, frame[7]);
		Assert.All(frame.Skip(TaipanFrame.PayloadOffset), b => Assert.Equal(0, b));
	}

	// ---- Apply -------------------------------------------------------------------------------

	// Sent after every table in their capture and never by us before. Each is answered 0xEE.
	[Theory]
	[InlineData(TaipanConfigSession.ApplyMaskConfig, "05 48 FA 40 F3 01 00 00")]
	[InlineData(TaipanConfigSession.ApplyMaskDpi, "05 49 FA 40 F3 02 00 00")]
	[InlineData(TaipanConfigSession.ApplyMaskButton, "05 57 FA 40 F3 04 00 00")]
	[InlineData(TaipanConfigSession.ApplyMaskLight, "05 53 FA 40 F3 08 00 00")]
	[InlineData(TaipanConfigSession.ApplyMaskMacro, "05 5B FA 40 F3 10 00 00")]
	public void ApplyFrameMatchesTheVendorCapture(byte mask, string expected)
	{
		Assert.Equal(expected, Hex(TaipanConfigSession.ApplyFrame(mask), 8));
	}

	// The mask must be the table's own bit — sending the wrong one would apply a table we did not
	// write while leaving the one we did write uncommitted, which no reply would reveal.
	[Theory]
	[InlineData(0x01, TaipanConfigSession.ApplyMaskConfig)]
	[InlineData(0x02, TaipanConfigSession.ApplyMaskDpi)]
	[InlineData(0x03, TaipanConfigSession.ApplyMaskButton)]
	[InlineData(0x04, TaipanConfigSession.ApplyMaskLight)]
	[InlineData(0x05, TaipanConfigSession.ApplyMaskMacro)]
	public void EveryTableCommandMapsToItsOwnApplyMask(byte cmd, byte expected)
	{
		Assert.Equal(expected, TaipanConfigSession.ApplyMaskFor(cmd));
	}

	// The masks are single, distinct bits. A duplicate would silently apply the wrong table.
	[Fact]
	public void TheApplyMasksAreDistinctSingleBits()
	{
		byte[] masks =
		[
			TaipanConfigSession.ApplyMaskConfig,
			TaipanConfigSession.ApplyMaskDpi,
			TaipanConfigSession.ApplyMaskButton,
			TaipanConfigSession.ApplyMaskLight,
			TaipanConfigSession.ApplyMaskMacro,
		];

		Assert.Equal(masks.Length, masks.Distinct().Count());
		Assert.All(masks, m => Assert.Equal(1, System.Numerics.BitOperations.PopCount(m)));
	}

	// A command with no table of its own must not be given an apply frame.
	[Theory]
	[InlineData(0x00)]
	[InlineData(0x06)]
	[InlineData(0x48)]
	public void ACommandWithNoTableGetsNoApplyMask(byte cmd)
	{
		Assert.Null(TaipanConfigSession.ApplyMaskFor(cmd));
	}

	// ---- The committed marker ----------------------------------------------------------------

	// 0xEE in driverPara4 is how the firmware says it accepted a frame, as opposed to the plain
	// per-frame echo which carries 0x00. Both appear in their capture; only one means committed.
	[Fact]
	public void IsCommittedRecognisesTheFirmwareMarker()
	{
		// Straight from the capture: the reply to the button-map commit.
		byte[] committed = [0x05, 0x0D, 0xFA, 0x03, 0x7F, 0x00, 0x01, 0xEE, 0x1A];
		Assert.True(TaipanConfigSession.IsCommitted(committed));
	}

	[Fact]
	public void IsCommittedRejectsAPlainEcho()
	{
		// The ordinary per-frame ACK: same frame, driverPara4 zeroed.
		byte[] echoed = [0x05, 0x99, 0xFA, 0x03, 0x00, 0x00, 0x02, 0x00, 0x0A];
		Assert.False(TaipanConfigSession.IsCommitted(echoed));
	}

	// Guards against reading a marker out of a short or foreign buffer.
	[Theory]
	[InlineData(new byte[] { 0x05, 0x0D, 0xFA })]
	[InlineData(new byte[] { 0x00, 0x0D, 0xFA, 0x03, 0x7F, 0x00, 0x01, 0xEE })]  // wrong report id
	[InlineData(new byte[] { 0x05, 0x0D, 0x00, 0x03, 0x7F, 0x00, 0x01, 0xEE })]  // wrong driver id
	public void IsCommittedRejectsMalformedFrames(byte[] frame)
	{
		Assert.False(TaipanConfigSession.IsCommitted(frame));
	}

	// ---- The retry budget --------------------------------------------------------------------

	// Their driver needed five attempts; ours was acknowledged on the second. The budget must stay
	// generous enough to cover both, and the gap must not collapse to zero (the device needs time
	// between attempts).
	[Fact]
	public void TheHandshakeBudgetCoversTheVendorsObservedWorstCase()
	{
		Assert.True(TaipanConfigSession.EnterAttempts >= 5);
		Assert.InRange(TaipanConfigSession.EnterGapMs, 100, 500);
	}
}
