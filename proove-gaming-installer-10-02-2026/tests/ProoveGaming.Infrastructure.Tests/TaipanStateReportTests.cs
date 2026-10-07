using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the parser for TAIPAN's device-pushed vendor reports. The golden frames are REAL
// captures from the hardware (2026-07-19): battery pushes at 2.4G link-up and DPI-state pushes
// from clicking the DPI button. These arrive unsolicited on a held-open pipe, so strict
// validation (shape + checksum) is what keeps garbage out of the battery cache.
public class TaipanStateReportTests
{
	private static byte[] Frame(params byte[] head)
	{
		var f = new byte[33];
		Array.Copy(head, f, head.Length);
		return f;
	}

	// ── The battery REQUEST ────────────────────────────────────────────────────────────────────
	//
	// Pins the exact bytes TaipanStateMonitor puts on the wire. This is not ceremony: the request
	// opcode was wrong for weeks. The vendor documents CMD_GetBATV as 0x08, and 0x08 gets no reply
	// over the 2.4G dongle — the protocol reuses ONE opcode for both directions, so the request is
	// 0x48, the same byte as the reply. That single byte had hardened into a documented belief that
	// the dongle ignored host->mouse traffic altogether, which was wrong about an entire transport.
	//
	// The rest of the suite only parses INBOUND frames, so nothing else here would catch the opcode
	// silently changing back.
	[Fact]
	public void BatteryRequest_IsTheReplyOpcode_NotTheDocumentedGetBatvOpcode()
	{
		Assert.Equal(0x48, TaipanStateReport.BatteryRequest);
		Assert.Equal(TaipanStateReport.BatteryReply, TaipanStateReport.BatteryRequest);
		Assert.NotEqual(0x08, TaipanStateReport.BatteryRequest);
	}

	[Fact]
	public void BatteryRequest_BuildsTheFrameSentOnTheWire()
	{
		var frame = TaipanFrame.Build(TaipanStateReport.BatteryRequest, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty);

		Assert.Equal(TaipanStateReport.Length, frame.Length);
		Assert.Equal(0x05, frame[0]);                              // report ID
		// Checksum: the sum is truncated to a byte BEFORE the XOR — 0xFA + 0x48 = 0x142 -> 0x42,
		// then 0x42 ^ 0x66 = 0x24. Written as the literal so the expected value is independent of
		// the implementation that produces it.
		Assert.Equal(0x24, frame[1]);
		Assert.Equal(0xFA, frame[2]);                              // driverID
		Assert.Equal(0x48, frame[3]);                              // driverCMD

		// All four parameters zero, and no payload — the request carries nothing. driverPara4 in
		// particular MUST stay 0: 0xD1 there is the flash-erase sequence header.
		Assert.All(frame[4..8], b => Assert.Equal(0, b));
		Assert.All(frame[8..], b => Assert.Equal(0, b));
	}

	// Real captured battery pushes: raw 1007 / 1004 / 1008 (~4.0 V on the quarter-mV scale).
	[Theory]
	[InlineData(0x52, 0xEF, 0x03, 1007)]
	[InlineData(0x57, 0xEC, 0x03, 1004)]
	[InlineData(0x53, 0xF0, 0x03, 1008)]
	public void TryParseBattery_ParsesRealCapturedFrames(byte dummy, byte lo, byte hi, int expectedRaw)
	{
		var report = Frame(0x05, dummy, 0xFA, 0x48, lo, hi, 0x00, 0x00, 0x00, 0x00, 0x01);

		Assert.True(TaipanStateReport.TryParseBattery(report, out int raw, out bool usb));
		Assert.Equal(expectedRaw, raw);
		Assert.False(usb);
	}

	[Fact]
	public void TryParseBattery_ReadsUsbConnectedFlag()
	{
		// Documented worked example: voltage 0x1068, USB=1, App=1.
		var report = Frame(0x05, 0xDA, 0xFA, 0x48, 0x68, 0x10, 0x01, 0x01);

		Assert.True(TaipanStateReport.TryParseBattery(report, out int raw, out bool usb));
		Assert.Equal(0x1068, raw);
		Assert.True(usb);
	}

	[Fact]
	public void TryParseBattery_RejectsBadChecksum()
	{
		var report = Frame(0x05, 0x00, 0xFA, 0x48, 0xEF, 0x03, 0x00, 0x00);
		Assert.False(TaipanStateReport.TryParseBattery(report, out _, out _));
	}

	[Fact]
	public void TryParseBattery_RejectsWrongCommand()
	{
		// A valid 0x49 state frame must not parse as battery.
		var report = Frame(0x05, 0x28, 0xFA, 0x49, 0x00, 0x04, 0x01, 0x06);
		Assert.False(TaipanStateReport.TryParseBattery(report, out _, out _));
	}

	[Fact]
	public void TryParseBattery_RejectsShortReport()
	{
		Assert.False(TaipanStateReport.TryParseBattery(new byte[] { 0x05, 0x52, 0xFA, 0x48 }, out _, out _));
	}

	[Fact]
	public void TryParseBattery_RejectsZeroReading()
	{
		// raw=0 (e.g. a blank relay frame) must not enter the cache. dummy for all-zero params:
		// (0xFA + 0x48) ^ 0x66 = 0x24.
		var report = Frame(0x05, 0x24, 0xFA, 0x48, 0x00, 0x00, 0x00, 0x00);
		Assert.False(TaipanStateReport.TryParseBattery(report, out _, out _));
	}

	// ── Mouse presence behind the dongle ───────────────────────────────────────────────────────
	//
	// The dongle is its own USB device and stays enumerated with no mouse behind it, so it answers a
	// battery request either way. Measured 2026-08-14, 8 requests per state on one unit:
	//
	//     mouse ON   ->  05-73-FA-48-D0-03-...   raw 976, 8/8
	//     mouse OFF  ->  05-24-FA-48-00-00-...   raw 0,   8/8
	//
	// So a zero reading — not silence — is the "no mouse" signal, and it is what lets the App stop
	// showing the last known percentage for a mouse that is switched off.
	[Fact]
	public void IsMouseAbsentReply_TrueForTheDonglesEmptyReply()
	{
		// The exact frame captured with the mouse switched off; checksum 0x24 is valid.
		var report = Frame(0x05, 0x24, 0xFA, 0x48, 0x00, 0x00, 0x00, 0x00);

		Assert.True(TaipanStateReport.IsMouseAbsentReply(report));
		// And the ordinary parse still rejects it, so no zero can reach the battery cache.
		Assert.False(TaipanStateReport.TryParseBattery(report, out _, out _));
	}

	[Fact]
	public void IsMouseAbsentReply_FalseWhenTheMouseAnswers()
	{
		// The exact frame captured with the mouse switched on: raw 976 (0x03D0).
		var report = Frame(0x05, 0x73, 0xFA, 0x48, 0xD0, 0x03, 0x00, 0x00);

		Assert.False(TaipanStateReport.IsMouseAbsentReply(report));
		Assert.True(TaipanStateReport.TryParseBattery(report, out var raw, out _));
		Assert.Equal(976, raw);
	}

	// Garbage must not be read as "mouse absent" — that would clear a good reading on a corrupt
	// frame. Only a WELL-FORMED battery reply carrying zero counts.
	[Fact]
	public void IsMouseAbsentReply_FalseForABadChecksum()
	{
		var report = Frame(0x05, 0x00, 0xFA, 0x48, 0x00, 0x00, 0x00, 0x00);

		Assert.False(TaipanStateReport.IsMouseAbsentReply(report));
	}

	[Fact]
	public void IsMouseAbsentReply_FalseForAStateReport()
	{
		// A valid 0x49 state push, not a battery reply.
		var report = Frame(0x05, 0x28, 0xFA, 0x49, 0x00, 0x04, 0x01, 0x06);

		Assert.False(TaipanStateReport.IsMouseAbsentReply(report));
	}

	// Real captured state pushes from cycling the DPI button: stage 4, 5, then wrap to 0.
	[Theory]
	[InlineData(0x28, 0x04, 4)]
	[InlineData(0x29, 0x05, 5)]
	[InlineData(0x2C, 0x00, 0)]
	public void TryParseState_ParsesRealCapturedFrames(byte dummy, byte stageByte, int expectedStage)
	{
		var report = Frame(0x05, dummy, 0xFA, 0x49, 0x00, stageByte, 0x01, 0x06);

		Assert.True(TaipanStateReport.TryParseState(report, out int profile, out int stage, out byte rate));
		Assert.Equal(0, profile);
		Assert.Equal(expectedStage, stage);
		Assert.Equal(0x01, rate);
	}

	// The vendor's conversion shape (fabric-info T6) — (100 * (raw - RawEmpty)) / (RawFull - RawEmpty),
	// clamped — but ROUNDED rather than truncated (see PercentFromRaw), and with RawFull corrected
	// from the vendor's 985 to the measured 987; see
	// PercentFromRaw_MatchesTheDevicesOwnPercentage below for the evidence. The vendor's worked
	// example therefore lands at 97 % here rather than their stated 98 %: their arithmetic is
	// self-consistent, their full point is not what the hardware reports.
	//
	// Before either, the implementation read raw as quarter-millivolts over 3300-4200 mV, which
	// under-reported across the whole scale — a full battery could never read above ~78 %.
	[Theory]
	[InlineData(981, 97)]    // the vendor's worked example, on the corrected span
	[InlineData(750, 0)]     // their empty point
	[InlineData(1008, 100)]  // real captured level, above full -> clamps
	[InlineData(700, 0)]     // below range clamps
	[InlineData(1200, 100)]  // above range clamps
	[InlineData(867, 49)]    // mid-scale ((867-750)*100/237 = 49.37 -> 49)
	public void PercentFromRaw_UsesTheVendorCurveShape(int raw, int expectedPercent)
	{
		Assert.Equal(expectedPercent, TaipanStateReport.PercentFromRaw(raw));
	}

	// Rounding, not truncation. The vendor's integer division reads systematically low by up to a
	// point, and this is the case that showed it: the dongle reported 93 % where the mouse's own
	// GATT percentage said 94 %, from a value that works out at 93.67.
	[Theory]
	[InlineData(972, 94)]    // 93.67 — truncation gave 93, GATT said 94
	[InlineData(969, 92)]    // 92.41 — rounds down, as it should
	[InlineData(973, 94)]    // 94.09 — rounds down
	public void PercentFromRaw_RoundsRatherThanTruncating(int raw, int expected)
	{
		Assert.Equal(expected, TaipanStateReport.PercentFromRaw(raw));
	}

	// The two readings that set the full point, each captured alongside the mouse's OWN percentage
	// from the GATT Battery Service over Bluetooth (2026-08-14). These are the calibration itself:
	// the vendor's 985 full point reported 100 % for a battery the device called 99 %, which is what
	// made the dongle read a permanent 100 % while Windows' tray showed 99 %.
	[Theory]
	[InlineData(987, 100)]   // GATT reported 100 %
	[InlineData(985, 99)]    // GATT reported 99 % — the vendor's constant said 100 here
	public void PercentFromRaw_MatchesTheDevicesOwnPercentage(int raw, int devicePercent)
	{
		Assert.Equal(devicePercent, TaipanStateReport.PercentFromRaw(raw));
	}

	// ---- The app flag (Para4) and the battery checksum ---------------------------------------

	// The regression that took the battery indicator out on 2026-09-02.
	//
	// Para4 of an 0x48 reply is an "app flag", and the SHIPPED firmware sets it after computing the
	// checksum — so a reply with the flag set carries the checksum of the same frame with Para4 = 0.
	// This parser validated strictly with Para4, so every such reply was rejected as corrupt and the
	// battery silently went blank. Latent from the day the parser was written; nothing had raised
	// that flag until the configuration-mode session landed.
	//
	// Note the vendor's DOCUMENTED example does the opposite and includes Para4 — see
	// TryParseBattery_ReadsUsbConnectedFlag, which still passes. Both forms are accepted, because
	// both are real and picking one discards genuine readings from a device sending the other.
	[Fact]
	public void BatteryReplyParsesWithTheAppFlagSet()
	{
		// Measured on our unit over the dongle, 2026-09-02. 0xEB is the Para4 = 0 checksum.
		byte[] report = [0x05, 0xEB, 0xFA, 0x48, 0x48, 0x03, 0x00, 0x01];

		Assert.True(TaipanStateReport.TryParseBattery(report, out var raw, out var usb));
		Assert.Equal(0x0348, raw);
		Assert.False(usb);
	}

	// The two consecutive replies from the vendor capture: identical but for Para4, identical
	// checksum. Both must parse, to the same reading.
	[Theory]
	[InlineData((byte)0x00)]
	[InlineData((byte)0x01)]
	public void TheSameBatteryReadingParsesWhicheverWayTheAppFlagSits(byte appFlag)
	{
		byte[] report = [0x05, 0x49, 0xFA, 0x48, 0xEA, 0x03, 0x00, appFlag];

		Assert.True(TaipanStateReport.TryParseBattery(report, out var raw, out _));
		Assert.Equal(0x03EA, raw);
	}

	// The mouse-absent reply travels the same path, so it must tolerate the flag too — otherwise a
	// switched-off mouse would look like a garbage frame instead of a clear "no reading".
	[Fact]
	public void MouseAbsentReplyIsRecognisedWithTheAppFlagSet()
	{
		// Zero reading, app flag set; checksum computed with Para4 = 0.
		var report = new byte[] { 0x05, 0x00, 0xFA, 0x48, 0x00, 0x00, 0x00, 0x01 };
		report[1] = TaipanFrame.Checksum(0x48, 0x00, 0x00, 0x00, 0x00);

		Assert.True(TaipanStateReport.IsMouseAbsentReply(report));
	}

	// Para4 is excluded from the BATTERY checksum only. Config-write ACKs include it, and the
	// commit reply below validates solely with Para4 = 0xEE — so the relaxation must not leak into
	// a blanket "ignore Para4", which would let genuinely corrupt frames through.
	[Fact]
	public void AGenuinelyCorruptBatteryFrameIsStillRejected()
	{
		// Right shape, wrong checksum under either rule.
		byte[] report = [0x05, 0x00, 0xFA, 0x48, 0x48, 0x03, 0x00, 0x01];

		Assert.False(TaipanStateReport.TryParseBattery(report, out _, out _));
	}
}
