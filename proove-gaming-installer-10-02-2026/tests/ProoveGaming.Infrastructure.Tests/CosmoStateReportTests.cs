using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the PG Cosmo vendor status payload (HID report 0xBC), decoded from hardware on 2026-08-05.
//
// The frames below are verbatim captures. The dongle pushes NOTHING on this report — a full guided
// capture produced zero frames — so it is only reachable via a GET_REPORT control-transfer read.
//
// Byte 19 was the ONLY offset that ever moved: it tracked power off/on exactly. Every other byte in
// the window held constant across ~50 samples spanning more than an hour, which is what disproved
// the initial reading of bytes 17 and 20-21 as battery percent and millivolts.
public class CosmoStateReportTests
{
	// Captured with the headset switched ON.
	private static byte[] HeadsetOn() =>
	[
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x01, 0x57, 0x01, 0x03, 0xB4, 0x0E, 0x01, 0x06,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	];

	// Same capture with the headset switched OFF: byte 19 flips 0x03 -> 0x0F, nothing else moves.
	private static byte[] HeadsetOff()
	{
		var frame = HeadsetOn();
		frame[19] = 0x0F;
		return frame;
	}

	[Fact]
	public void ReadsHeadsetOn()
	{
		Assert.True(CosmoStateReport.TryParse(HeadsetOn())!.Value.IsHeadsetOn);
	}

	[Fact]
	public void ReadsHeadsetOff()
	{
		Assert.False(CosmoStateReport.TryParse(HeadsetOff())!.Value.IsHeadsetOn);
	}

	// Any value other than the observed 0x03 counts as off. The capture showed 0x0F while off, but
	// byte 19 also flickered mid-phase (the dongle retrying its link), so an unseen intermediate
	// value must fail safe rather than claim a headset is connected when it is not.
	[Theory]
	[InlineData((byte)0x0F)]
	[InlineData((byte)0x07)]
	[InlineData((byte)0x00)]
	[InlineData((byte)0xFF)]
	public void TreatsAnyNonLinkedValueAsOff(byte linkByte)
	{
		var frame = HeadsetOn();
		frame[19] = linkByte;

		Assert.False(CosmoStateReport.TryParse(frame)!.Value.IsHeadsetOn);
	}

	// Report 0xBC carries NO battery. Two fields were published as one and both were wrong:
	//
	//   byte 17    (0x57 = 87)   shipped 2026-08-05, disproven 2026-09-03 — the same headset
	//                            reported 40 % over Bluetooth while byte 17 still read 87.
	//   bytes 20-21 (3764 mV)    shipped 2026-09-03, disproven the same day — the headset
	//                            discharged 40 % -> 30 % (its own Bluetooth report) and the value
	//                            did not move a single bit.
	//
	// Both looked convincing: plausible magnitudes, sitting in a vendor status report, stable across
	// long sampling runs. Stability was mistaken for a good reading twice. Only a CORRELATED change
	// against an independent source settles a field like this, and neither field survived one.
	//
	// Battery over the dongle is therefore hidden by `SupportsBattery: false` on Cosmo's
	// Dongle2_4G transport; Bluetooth still reports a real level. These tests pin that no level can
	// escape this parser regardless of the catalog.
	[Fact]
	public void PublishesNoBatteryLevelAtAll()
	{
		Assert.Null(CosmoStateReport.TryParse(HeadsetOn())!.Value.BatteryPercent);
	}

	// The two captures that settled it, transcribed separately rather than derived from one another
	// — comparing a frame to itself would assert nothing. These are what the hardware returned when
	// the headset's own Bluetooth reading was 40 %, and again when it had fallen to 30 %.
	private static byte[] CapturedAtForty() =>
	[
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x01, 0x57, 0x01, 0x03, 0xB4, 0x0E, 0x01, 0x06,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	];

	private static byte[] CapturedAtThirty() =>
	[
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x01, 0x57, 0x01, 0x03, 0xB4, 0x0E, 0x01, 0x06,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	];

	// A real ten-point discharge moved nothing. That is the whole case against both candidate
	// fields, and it is why neither may be published as a level.
	[Fact]
	public void TheWindowIsUnchangedAcrossARealDischarge()
	{
		var atForty = CosmoStateReport.TryParse(CapturedAtForty())!.Value;
		var atThirty = CosmoStateReport.TryParse(CapturedAtThirty())!.Value;

		Assert.Equal(atForty.RawWindow, atThirty.RawWindow);
		Assert.Equal(atForty.BatteryMillivolts, atThirty.BatteryMillivolts);
		Assert.Null(atForty.BatteryPercent);
		Assert.Null(atThirty.BatteryPercent);
	}

	// Neither disproven field may be republished as a level, whatever it holds.
	[Theory]
	[InlineData(17, (byte)0x57)]   // the first wrong answer
	[InlineData(17, (byte)40)]     // even a value that happens to look right
	[InlineData(20, (byte)0x00)]
	[InlineData(21, (byte)0x00)]
	public void NoByteInTheWindowCanProduceALevel(int offset, byte value)
	{
		var frame = HeadsetOn();
		frame[offset] = value;

		var state = CosmoStateReport.TryParse(frame)!.Value;

		Assert.Null(state.BatteryPercent);
		// The link state is the one field that does work, and stays parsed.
		Assert.True(state.IsHeadsetOn);
	}

	[Fact]
	public void ReadsBatteryMillivoltsLittleEndian()
	{
		// 0xB4 0x0E -> 0x0EB4 -> 3764. Parsed for diagnostics only: it is a CONSTANT, not a level,
		// and must never be converted to a percentage again without a correlated two-point sample.
		Assert.Equal(3764, CosmoStateReport.TryParse(HeadsetOn())!.Value.BatteryMillivolts);
	}

	// The undecoded constants are preserved so a future vendor reply or USB capture has the
	// measured bytes to work from.
	[Fact]
	public void KeepsTheRawWindowForFutureDecoding()
	{
		var state = CosmoStateReport.TryParse(HeadsetOn());

		Assert.Equal([0x01, 0x57, 0x01, 0x03, 0xB4, 0x0E, 0x01, 0x06], state!.Value.RawWindow);
	}

	[Fact]
	public void RejectsATruncatedPayload()
	{
		Assert.Null(CosmoStateReport.TryParse(new byte[8]));
		Assert.Null(CosmoStateReport.TryParse([]));
	}
}
