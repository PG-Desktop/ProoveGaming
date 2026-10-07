using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the 0x05 vendor state-report decode — the critical DPI *read* path. The active
// stage is at offset 3 and only 0..7 is valid; a wrong offset or a missing bounds check would
// drive the UI to the wrong (or a non-existent) DPI stage. Baseline: docs/mouse/blaze/dpi-stage.md.
public class X43StateReportTests
{
	[Fact]
	public void TryParse_ValidReport_ExtractsStageRateAndLed()
	{
		// [0x05, profile, rate=0x02(500Hz), stage=3, led=0x0A, 0, 0]
		var report = new byte[] { 0x05, 0x00, 0x02, 0x03, 0x0A, 0x00, 0x00 };

		var ok = X43StateReport.TryParse(report, out var stage, out var hz, out var led);

		Assert.True(ok);
		Assert.Equal(3, stage);
		Assert.Equal(500, hz);
		Assert.Equal(0x0A, led);
	}

	[Fact]
	public void TryParse_NotAStateReport_ReturnsFalseWithSentinels()
	{
		var report = new byte[] { 0x06, 0x00, 0x02, 0x03, 0x0A };

		var ok = X43StateReport.TryParse(report, out var stage, out var hz, out var led);

		Assert.False(ok);
		Assert.Equal(-1, stage);
		Assert.Equal(0, hz);
		Assert.Equal(-1, led);
	}

	[Fact]
	public void TryParse_TooShort_ReturnsFalse()
	{
		var report = new byte[] { 0x05, 0x00, 0x02 }; // no stage byte

		Assert.False(X43StateReport.TryParse(report, out _, out _, out _));
	}

	[Theory]
	[InlineData(0x08)] // just above the top stage
	[InlineData(0xFF)] // garbage
	public void TryParse_StageOutOfRange_ParsesButMarksStageUnknown(byte stageByte)
	{
		// A 0x05 report is still a valid report (rate/led usable) even if the stage is bogus,
		// so TryParse returns true but stage is the -1 "unknown" sentinel.
		var report = new byte[] { 0x05, 0x00, 0x01, stageByte, 0x02 };

		var ok = X43StateReport.TryParse(report, out var stage, out var hz, out var led);

		Assert.True(ok);
		Assert.Equal(-1, stage);
		Assert.Equal(1000, hz);
		Assert.Equal(0x02, led);
	}

	[Fact]
	public void TryParse_NoLedByte_LeavesLedUnknown()
	{
		var report = new byte[] { 0x05, 0x00, 0x04, 0x02 }; // exactly 4 bytes, no led

		var ok = X43StateReport.TryParse(report, out var stage, out var hz, out var led);

		Assert.True(ok);
		Assert.Equal(2, stage);
		Assert.Equal(250, hz);
		Assert.Equal(-1, led);
	}

	[Fact]
	public void TryParse_UnknownRateByte_YieldsZeroHz()
	{
		var report = new byte[] { 0x05, 0x00, 0x03, 0x01, 0x02 }; // 0x03 is not a valid rate

		X43StateReport.TryParse(report, out _, out var hz, out _);

		Assert.Equal(0, hz);
	}
}
