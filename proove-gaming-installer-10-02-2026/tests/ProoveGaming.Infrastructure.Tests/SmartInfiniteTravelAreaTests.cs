using System;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// The Smart Infinite key travel area codec (Cyrex / Aress actuation). The wire format comes from
// the vendor's Keyborder-v3 app, not a hardware capture — which is exactly why every encode /
// preserve / gate rule is pinned here: the service refuses to write anything this codec cannot
// round-trip. See docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md.
public class SmartInfiniteTravelAreaTests
{
	// A Cyrex-shaped Device Info block: 0xAA 0xBB marker, 75 travel slots (byte 7), step 0x01
	// (byte 16 -> 1 x 10 um = 0.01 mm), record size 10 (byte 44 = 0), press range 20..350 steps,
	// RT range 1..250, defaults press/release 200, RT off, quick 10.
	private static byte[] CyrexDeviceInfoBlock()
	{
		var block = new byte[64];
		block[0] = 0xAA;
		block[1] = 0xBB;
		block[2] = 0x72; block[3] = 0x32;   // productId 0x3272
		block[4] = 75;                      // matrix slots
		block[6] = 68;                      // key count
		block[7] = 75;                      // travel slots
		block[16] = 0x01;                   // step: 1 x 0.01 mm
		block[19] = 0x5E; block[20] = 0x01; // press max 350
		block[21] = 20;                     // press min
		block[23] = 0xFA; block[24] = 0x00; // rt max 250
		block[25] = 1;                      // rt min
		block[29] = 0xC8;                   // default press 200
		block[31] = 0xC8;                   // default release 200
		block[37] = 10;                     // default quick press
		block[39] = 10;                     // default quick release
		return block;
	}

	private static byte[] UniformArea(int slots, int recordBytes,
		int press, int release, int quickDownRaw, int quickUp, int bottomDeadZone, int topDeadZone = 0)
	{
		var area = new byte[slots * recordBytes];
		for (int slot = 0; slot < slots; slot++)
		{
			int o = slot * recordBytes;
			area[o] = (byte)press; area[o + 1] = (byte)(press >> 8);
			area[o + 2] = (byte)release; area[o + 3] = (byte)(release >> 8);
			area[o + 4] = (byte)quickDownRaw; area[o + 5] = (byte)(quickDownRaw >> 8);
			area[o + 6] = (byte)quickUp; area[o + 7] = (byte)(quickUp >> 8);
			area[o + 8] = (byte)bottomDeadZone; area[o + 9] = (byte)(bottomDeadZone >> 8);
			if (recordBytes >= 12)
			{
				area[o + 10] = (byte)topDeadZone; area[o + 11] = (byte)(topDeadZone >> 8);
			}
		}
		return area;
	}

	// ── Device Info magnetic fields ──────────────────────────────────────────────

	[Fact]
	public void DeviceInfo_ParsesMagneticFields()
	{
		var info = SmartInfiniteDeviceInfo.Parse(CyrexDeviceInfoBlock());

		Assert.True(info.IsValid);
		Assert.Equal(75, info.TravelSlots);
		Assert.Equal(10, info.TravelRecordBytes);       // byte 44 = 0 -> the 10-byte layout
		Assert.Equal(750, info.TravelAreaBytes);
		Assert.Equal(10, info.TravelStepMicrometres);   // 0x01 -> 1 x 0.01 mm
		Assert.Equal(350, info.PressMaxSteps);
		Assert.Equal(20, info.PressMinSteps);
		Assert.Equal(250, info.RtMaxSteps);
		Assert.Equal(1, info.RtMinSteps);
		Assert.Equal(200, info.DefaultPressSteps);
		Assert.False(info.DefaultRapidTrigger);
	}

	[Fact]
	public void DeviceInfo_HighPrecisionStepBit_SelectsTheMicrometreFactor()
	{
		var block = CyrexDeviceInfoBlock();
		block[16] = 0x85; // bit 7 set -> 5 x 0.001 mm = 5 um

		Assert.Equal(5, SmartInfiniteDeviceInfo.Parse(block).TravelStepMicrometres);
	}

	[Fact]
	public void DeviceInfo_RecordSize12_IsTakenAsReported()
	{
		var block = CyrexDeviceInfoBlock();
		block[44] = 12;

		Assert.Equal(12, SmartInfiniteDeviceInfo.Parse(block).TravelRecordBytes);
	}

	// ── Step conversion ──────────────────────────────────────────────────────────

	[Theory]
	[InlineData(200, 10, 200)]  // 0.01 mm step: 200 steps = 2.00 mm
	[InlineData(200, 1, 20)]    // 0.001 mm step: 200 steps = 0.20 mm
	[InlineData(0, 10, 0)]
	public void StepsToCentiMm_Converts(int steps, int stepUm, int expectedCentiMm) =>
		Assert.Equal(expectedCentiMm, SmartInfiniteTravelArea.StepsToCentiMm(steps, stepUm));

	[Theory]
	[InlineData(200, 10, 200)]
	[InlineData(20, 1, 200)]
	public void CentiMmToSteps_IsTheInverse(int centiMm, int stepUm, int expectedSteps) =>
		Assert.Equal(expectedSteps, SmartInfiniteTravelArea.CentiMmToSteps(centiMm, stepUm));

	// ── Record fields and the RT flag ────────────────────────────────────────────

	[Fact]
	public void ReadSlot_DecodesFieldsAndRtFlag()
	{
		// QUICKLY_DOWN 0x400A = flag 0b01 + value 10.
		var area = UniformArea(2, 10, press: 200, release: 190, quickDownRaw: 0x400A, quickUp: 12, bottomDeadZone: 0x3014);

		var (press, release, rt, quickPress, quickRelease) = SmartInfiniteTravelArea.ReadSlot(area, 10, 1);

		Assert.Equal(200, press);
		Assert.Equal(190, release);
		Assert.True(rt);
		Assert.Equal(10, quickPress);
		Assert.Equal(12, quickRelease);
	}

	[Fact]
	public void ReadSlot_NoRtFlag_ReadsRapidTriggerOff()
	{
		var area = UniformArea(1, 10, press: 200, release: 200, quickDownRaw: 0x000A, quickUp: 10, bottomDeadZone: 0);

		Assert.False(SmartInfiniteTravelArea.ReadSlot(area, 10, 0).RapidTrigger);
	}

	[Fact]
	public void PatchAllSlots_EncodesTheRtFlagIntoQuicklyDown()
	{
		var area = UniformArea(3, 10, 200, 200, 0, 0, 0);

		SmartInfiniteTravelArea.PatchAllSlots(area, 10, 100, 100, rapidTrigger: true, 10, 12);

		var (_, _, rt, quickPress, _) = SmartInfiniteTravelArea.ReadSlot(area, 10, 2);
		Assert.True(rt);
		Assert.Equal(10, quickPress);
		// Raw wire value carries the 0b01 prefix in bits 15-14.
		Assert.Equal(0x40, area[2 * 10 + 5] & 0xC0);
	}

	[Fact]
	public void PatchAllSlots_RtOff_ClearsTheFlag()
	{
		var area = UniformArea(1, 10, 200, 200, 0x400A, 10, 0);

		SmartInfiniteTravelArea.PatchAllSlots(area, 10, 200, 200, rapidTrigger: false, 10, 10);

		Assert.False(SmartInfiniteTravelArea.ReadSlot(area, 10, 0).RapidTrigger);
	}

	// ── Preservation (the safety property of read-modify-write) ──────────────────

	[Fact]
	public void PatchAllSlots_PreservesAxisNibbleDeadZonesByteForByte()
	{
		// Axis nibble 0x3 in bits 15-12 of BOTTOM_DEAD_ZONE (value 0x014), top dead zone 30.
		var area = UniformArea(4, 12, 200, 200, 0x400A, 10, bottomDeadZone: 0x3014, topDeadZone: 30);
		var before = area.ToArray();

		SmartInfiniteTravelArea.PatchAllSlots(area, 12, 50, 60, rapidTrigger: false, 5, 6);

		for (int slot = 0; slot < 4; slot++)
		{
			int o = slot * 12;
			// BOTTOM_DEAD_ZONE (axis nibble + value) and TOP_DEAD_ZONE untouched.
			Assert.Equal(before[o + 8], area[o + 8]);
			Assert.Equal(before[o + 9], area[o + 9]);
			Assert.Equal(before[o + 10], area[o + 10]);
			Assert.Equal(before[o + 11], area[o + 11]);
		}
	}

	// ── Per-key patching ─────────────────────────────────────────────────────────

	[Fact]
	public void PatchSlots_PatchesExactlyTheListedSlotsAndNothingElse()
	{
		var area = UniformArea(6, 10, press: 200, release: 190, quickDownRaw: 0x000A, quickUp: 10, bottomDeadZone: 0x3014);
		var before = area.ToArray();

		SmartInfiniteTravelArea.PatchSlots(area, 10, [1, 4], 100, 90, rapidTrigger: true, 5, 6);

		foreach (int slot in new[] { 1, 4 })
		{
			var (press, release, rt, quickPress, quickRelease) = SmartInfiniteTravelArea.ReadSlot(area, 10, slot);
			Assert.Equal(100, press);
			Assert.Equal(90, release);
			Assert.True(rt);
			Assert.Equal(5, quickPress);
			Assert.Equal(6, quickRelease);
			// The unmodelled field of a PATCHED slot stays byte-identical too.
			Assert.Equal(before[slot * 10 + 8], area[slot * 10 + 8]);
			Assert.Equal(before[slot * 10 + 9], area[slot * 10 + 9]);
		}

		// Every unlisted slot is byte-identical across the patch.
		foreach (int slot in new[] { 0, 2, 3, 5 })
			Assert.True(before.AsSpan(slot * 10, 10).SequenceEqual(area.AsSpan(slot * 10, 10)));
	}

	[Fact]
	public void PatchSlots_IgnoresSlotsOutsideTheArea()
	{
		var area = UniformArea(3, 10, 200, 200, 0x000A, 10, 0);
		var before = area.ToArray();

		SmartInfiniteTravelArea.PatchSlots(area, 10, [-1, 3, 99], 100, 100, rapidTrigger: false, 5, 5);

		Assert.True(before.AsSpan().SequenceEqual(area));
	}

	[Theory]
	[InlineData(10)]
	[InlineData(12)]
	public void ReadAllSlots_RoundTripsAMixedTableAtBothRecordSizes(int recordBytes)
	{
		var area = UniformArea(4, recordBytes, press: 200, release: 190, quickDownRaw: 0x400A, quickUp: 12,
			bottomDeadZone: 0x3014, topDeadZone: 30);
		// One vendor-configured key: press 50, RT off.
		SmartInfiniteTravelArea.PatchSlots(area, recordBytes, [2], 50, 45, rapidTrigger: false, 3, 4);

		var map = SmartInfiniteTravelArea.ReadAllSlots(area, recordBytes);

		Assert.Equal(4, map.Count);
		Assert.Equal((200, 190, true, 10, 12), map[0]);
		Assert.Equal((50, 45, false, 3, 4), map[2]);
	}

	// ── Majority reduction ───────────────────────────────────────────────────────

	[Fact]
	public void ReduceMajority_PicksThePerFieldMajorityOnAMixedTable()
	{
		var area = UniformArea(5, 10, 200, 200, 0x400A, 10, 0);
		// One key configured per-key by the vendor app: press 50, RT off.
		area[0] = 50; area[1] = 0;
		area[4] = 0x0A; area[5] = 0x00;

		var (press, _, rt, _, _) = SmartInfiniteTravelArea.ReduceMajority(area, 10);

		Assert.Equal(200, press);
		Assert.True(rt);
	}

	// ── Geometry gate ────────────────────────────────────────────────────────────

	[Fact]
	public void ValidateGeometry_AcceptsACoherentCyrexArea()
	{
		var info = SmartInfiniteDeviceInfo.Parse(CyrexDeviceInfoBlock());

		Assert.True(SmartInfiniteTravelArea.ValidateGeometry(info, areaLength: 750));
	}

	[Theory]
	[InlineData(7, 0)]    // no travel slots -> mechanical board
	[InlineData(16, 0)]   // step 0 -> unit unknown, values would be garbage
	public void ValidateGeometry_RejectsMissingMagneticFields(int offset, byte value)
	{
		var block = CyrexDeviceInfoBlock();
		block[offset] = value;
		var info = SmartInfiniteDeviceInfo.Parse(block);

		Assert.False(SmartInfiniteTravelArea.ValidateGeometry(info, info.TravelAreaBytes));
	}

	[Fact]
	public void ValidateGeometry_RejectsAnAreaSizeMismatch() =>
		Assert.False(SmartInfiniteTravelArea.ValidateGeometry(
			SmartInfiniteDeviceInfo.Parse(CyrexDeviceInfoBlock()), areaLength: 384));

	[Fact]
	public void ValidateGeometry_RejectsInvertedLimits()
	{
		var block = CyrexDeviceInfoBlock();
		block[19] = 10; block[20] = 0; // press max 10 < min 20

		var info = SmartInfiniteDeviceInfo.Parse(block);
		Assert.False(SmartInfiniteTravelArea.ValidateGeometry(info, info.TravelAreaBytes));
	}
}
