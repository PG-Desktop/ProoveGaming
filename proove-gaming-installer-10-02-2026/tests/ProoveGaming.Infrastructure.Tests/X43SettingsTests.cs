using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the report-rate wire values. These are a raw ms period (1/2/4/8), not an index, and
// were capture-confirmed — a wrong byte sets the wrong polling rate. Baseline: docs/mouse/blaze.
public class X43SettingsTests
{
	[Theory]
	[InlineData(1000, 0x01)]
	[InlineData(500, 0x02)]
	[InlineData(250, 0x04)]
	[InlineData(125, 0x08)]
	public void ReportRateByte_MapsKnownRates(int hz, byte expected)
	{
		Assert.Equal(expected, X43Settings.ReportRateByte(hz));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2000)]
	[InlineData(333)]
	public void ReportRateByte_UnknownRate_FallsBackTo1000Hz(int hz)
	{
		// 0x01 == 1 ms == 1000 Hz, the safe default.
		Assert.Equal(0x01, X43Settings.ReportRateByte(hz));
	}

	[Theory]
	[InlineData(0x01, 1000)]
	[InlineData(0x02, 500)]
	[InlineData(0x04, 250)]
	[InlineData(0x08, 125)]
	public void HzFromReportRateByte_DecodesKnownBytes(byte raw, int expectedHz)
	{
		Assert.Equal(expectedHz, X43Settings.HzFromReportRateByte(raw));
	}

	[Theory]
	[InlineData(0x00)]
	[InlineData(0x03)]
	[InlineData(0xFF)]
	public void HzFromReportRateByte_UnknownByte_IsZero(byte raw)
	{
		// 0 signals "unknown" so callers skip it rather than defaulting to a real rate.
		Assert.Equal(0, X43Settings.HzFromReportRateByte(raw));
	}

	// The encode/decode pair must round-trip for every supported rate.
	[Theory]
	[InlineData(1000)]
	[InlineData(500)]
	[InlineData(250)]
	[InlineData(125)]
	public void ReportRate_RoundTrips(int hz)
	{
		Assert.Equal(hz, X43Settings.HzFromReportRateByte(X43Settings.ReportRateByte(hz)));
	}
}
