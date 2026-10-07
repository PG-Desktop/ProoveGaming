using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// RY5088 magnetic-axis trip (actuation) framing — §1/§2 of the vendor doc
// (docs/knowledge-base/ry5088-magnetic-axis-protocol.md). Unlike the key-matrix frames these are
// documented rather than captured, so the layout rules are pinned per the doc: value pages of 28
// two-byte keys (56 for one-byte options), FlashEnable only on the final packet of a batch, and
// the same silently-enforced one's-complement checksum every other command uses.
public class Ry5088ProtocolTripTests
{
	// ── Paging ───────────────────────────────────────────────────────────────────

	[Fact]
	public void TripPageCount_CoversAll128SlotsForBothOptionWidths()
	{
		Assert.Equal(5, Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageTwoByte));
		Assert.Equal(3, Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageOneByte));
	}

	// The page-selection rule of a per-key write: only pages holding a patched slot are
	// rewritten, so untouched keys' flash pages are never re-committed.

	[Fact]
	public void TripPagesContaining_SingleSlot_IsOnePage() =>
		Assert.Equal([0], Ry5088Protocol.TripPagesContaining([5], Ry5088Protocol.TripKeysPerPageTwoByte));

	[Fact]
	public void TripPagesContaining_SlotsStraddlingAPageBoundary_SplitAtTwoBytePagingOnly()
	{
		// Slots 27 and 28 sit on both sides of the 28-key boundary, but inside one 56-key page.
		Assert.Equal([0, 1], Ry5088Protocol.TripPagesContaining([27, 28], Ry5088Protocol.TripKeysPerPageTwoByte));
		Assert.Equal([0], Ry5088Protocol.TripPagesContaining([27, 28], Ry5088Protocol.TripKeysPerPageOneByte));
	}

	[Fact]
	public void TripPagesContaining_DeduplicatesAndSortsRegardlessOfInputOrder() =>
		Assert.Equal([0, 2, 4], Ry5088Protocol.TripPagesContaining(
			[127, 3, 60, 5, 58], Ry5088Protocol.TripKeysPerPageTwoByte));

	[Fact]
	public void TripPagesContaining_EmptyOrOutOfRangeSlots_YieldNoPages()
	{
		Assert.Empty(Ry5088Protocol.TripPagesContaining([], Ry5088Protocol.TripKeysPerPageTwoByte));
		Assert.Empty(Ry5088Protocol.TripPagesContaining([-1, 128, 500], Ry5088Protocol.TripKeysPerPageTwoByte));
	}

	// ── SET (0x65) ───────────────────────────────────────────────────────────────

	[Fact]
	public void SetTripPageTwoByte_LaysOutHeaderChecksumAndLittleEndianValues()
	{
		var values = new ushort[] { 200, 0x0158, 10 }; // 2.00 mm, 3.44 mm, 0.10 mm

		var p = Ry5088Protocol.BuildSetTripPageTwoByte(
			Ry5088Protocol.TripOptionPress, page: 2, values, flashEnable: false);

		Assert.Equal(Ry5088Protocol.ReportLength, p.Length);
		Assert.Equal(Ry5088Protocol.HidReportId, p[0]);
		Assert.Equal(0x65, p[1]);          // SET trip
		Assert.Equal(0x00, p[2]);          // Option 0 = trigger travel
		Assert.Equal(0x01, p[3]);          // Select 1 = bulk
		Assert.Equal(0x02, p[4]);          // Index = page
		Assert.Equal(0x00, p[5]);          // FlashEnable off
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
		// Values little-endian from protocol byte 8 (buffer index 9).
		Assert.Equal(200, p[9] | (p[10] << 8));
		Assert.Equal(0x0158, p[11] | (p[12] << 8));
		Assert.Equal(10, p[13] | (p[14] << 8));
	}

	[Fact]
	public void SetTripPage_FlashEnableRidesInByte5()
	{
		var p = Ry5088Protocol.BuildSetTripPageOneByte(
			Ry5088Protocol.TripOptionKeyMode, page: 2, new byte[] { 0x80, 0x80 }, flashEnable: true);

		Assert.Equal(0x65, p[1]);
		Assert.Equal(Ry5088Protocol.TripOptionKeyMode, p[2]);
		Assert.Equal(0x01, p[5]);
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
		// One-byte values from protocol byte 8.
		Assert.Equal(0x80, p[9]);
		Assert.Equal(0x80, p[10]);
	}

	[Fact]
	public void KeyModeRapidTrigger_IsNormalModeWithBit7()
	{
		Assert.Equal(0x80, Ry5088Protocol.KeyModeNormal | Ry5088Protocol.KeyModeRapidTriggerFlag);
	}

	// ── GET (0xE5) ───────────────────────────────────────────────────────────────

	[Fact]
	public void GetTripPage_LaysOutHeaderAndChecksum()
	{
		var p = Ry5088Protocol.BuildGetTripPage(Ry5088Protocol.TripOptionRtRelease, page: 4);

		Assert.Equal(0xE5, p[1]);
		Assert.Equal(Ry5088Protocol.TripOptionRtRelease, p[2]);
		Assert.Equal(0x01, p[3]);
		Assert.Equal(0x04, p[4]);
		Assert.Equal(0x00, p[5]);
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
	}

	// ── Function list (0xE6) precision ───────────────────────────────────────────

	[Fact]
	public void GetFunctionList_LaysOutHeaderAndChecksum()
	{
		var p = Ry5088Protocol.BuildGetFunctionList();

		Assert.Equal(0xE6, p[1]);
		Assert.Equal(Ry5088Protocol.Checksum(p, 1, 7), p[8]);
	}

	[Theory]
	[InlineData(0xAA, 0, 10)]  // 0.01 mm
	[InlineData(0xAA, 1, 5)]   // 0.005 mm
	[InlineData(0xAA, 2, 1)]   // 0.001 mm
	[InlineData(0xAA, 3, 0)]   // undefined precision -> unreadable
	[InlineData(0x00, 0, 0)]   // missing validity marker -> unreadable
	public void ParseTripPrecision_RequiresTheMarkerAndAKnownCode(byte marker, byte code, int expectedUm) =>
		Assert.Equal(expectedUm, Ry5088Protocol.ParseTripPrecisionMicrometres(marker, code));

	// ── Unit conversion ──────────────────────────────────────────────────────────
	// The one place a non-default precision changes the math — a wrong factor here is the
	// "values 10x off" failure mode the service clamps guard against.

	[Theory]
	[InlineData(200, 10, 200)]   // default precision: units ARE centi-mm (2.00 mm)
	[InlineData(400, 5, 200)]    // 0.005 mm units: 400 -> 2.00 mm
	[InlineData(401, 5, 201)]    // rounds to nearest: 2.005 mm -> 2.01 mm
	[InlineData(2000, 1, 200)]   // 0.001 mm units: 2000 -> 2.00 mm
	[InlineData(2005, 1, 201)]
	[InlineData(0, 10, 0)]
	public void TripUnitsToCentiMm_Converts(int units, int precisionUm, int expected) =>
		Assert.Equal(expected, Ry5088Protocol.TripUnitsToCentiMm(units, precisionUm));

	[Theory]
	[InlineData(200, 10, 200)]
	[InlineData(200, 5, 400)]
	[InlineData(200, 1, 2000)]
	[InlineData(1, 5, 2)]        // 0.01 mm at 0.005 mm precision -> 2 units
	[InlineData(1, 10, 1)]
	public void CentiMmToTripUnits_Converts(int centiMm, int precisionUm, int expected) =>
		Assert.Equal(expected, Ry5088Protocol.CentiMmToTripUnits(centiMm, precisionUm));

	[Theory]
	[InlineData(10)]
	[InlineData(5)]
	[InlineData(1)]
	public void TripUnitConversion_RoundTripsAtEveryDocumentedPrecision(int precisionUm)
	{
		foreach (int centiMm in new[] { 1, 10, 55, 199, 200, 201, 300, 400 })
			Assert.Equal(centiMm, Ry5088Protocol.TripUnitsToCentiMm(
				Ry5088Protocol.CentiMmToTripUnits(centiMm, precisionUm), precisionUm));
	}
}
