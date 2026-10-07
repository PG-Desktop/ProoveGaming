using System;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the MsConfig patch helpers. MsConfig holds the polling rate, the active DPI stage and
// the profile state together and its write erases the sector, so every patch must be applied to a
// block READ BACK from the device and must touch nothing but its own field — that "leaves every
// other byte intact" property is the point of these tests.
public class TaipanMsConfigTests
{
	// The vendor's worked full-write block, used as a realistic read-back baseline.
	private static byte[] VendorBlock() =>
	[
		0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x03, 0x01,
		0xFF, 0x01, 0x02, 0x04, 0x08, 0x01, 0x00, 0x00, 0x00,
	];

	[Theory]
	[InlineData(1000, 0x01)]
	[InlineData(500, 0x02)]
	[InlineData(250, 0x04)]
	[InlineData(125, 0x08)]
	public void PollingRateByte_EncodesRawPeriod(int hz, byte expected)
	{
		Assert.Equal(expected, TaipanMsConfig.PollingRateByte(hz));
	}

	[Fact]
	public void PollingRateByte_UnknownRateFallsBackTo1000Hz()
	{
		Assert.Equal(0x01, TaipanMsConfig.PollingRateByte(333));
	}

	[Theory]
	[InlineData(0x01, 1000)]
	[InlineData(0x02, 500)]
	[InlineData(0x04, 250)]
	[InlineData(0x08, 125)]
	[InlineData(0x00, 0)] // unknown → 0 so callers can skip it
	[InlineData(0x7F, 0)]
	public void HzFromPollingRateByte_DecodesRawPeriod(byte raw, int expected)
	{
		Assert.Equal(expected, TaipanMsConfig.HzFromPollingRateByte(raw));
	}

	[Fact]
	public void PatchPollingRate_SetsEverySlotAndLeavesTheRestIntact()
	{
		var block = VendorBlock();
		var original = VendorBlock();

		TaipanMsConfig.PatchPollingRate(block, 500);

		for (var i = 0; i < TaipanMsConfig.PollingRateSlots; i++)
			Assert.Equal(0x02, block[TaipanMsConfig.PollingRateOffset + i]);

		AssertUntouchedOutside(original, block,
			TaipanMsConfig.PollingRateOffset,
			TaipanMsConfig.PollingRateOffset + TaipanMsConfig.PollingRateSlots - 1);
	}

	[Fact]
	public void PatchActiveDpiStage_SetsOnlyDpiSwitch()
	{
		var block = VendorBlock();
		var original = VendorBlock();

		TaipanMsConfig.PatchActiveDpiStage(block, 5);

		Assert.Equal(5, block[TaipanMsConfig.DpiSwitchOffset]);
		AssertUntouchedOutside(original, block, TaipanMsConfig.DpiSwitchOffset, TaipanMsConfig.DpiSwitchOffset);
	}

	[Theory]
	[InlineData(-1, 0)]
	[InlineData(0, 0)]
	[InlineData(7, 7)]
	[InlineData(99, 7)]
	public void PatchActiveDpiStage_ClampsToDeviceRange(int requested, byte expected)
	{
		var block = VendorBlock();
		TaipanMsConfig.PatchActiveDpiStage(block, requested);
		Assert.Equal(expected, block[TaipanMsConfig.DpiSwitchOffset]);
	}

	[Fact]
	public void PatchPollingRate_AndActiveStage_Compose()
	{
		var block = VendorBlock();

		TaipanMsConfig.PatchPollingRate(block, 125);
		TaipanMsConfig.PatchActiveDpiStage(block, 3);

		// Neither patch clobbered the other — the whole point of the read-modify-write.
		Assert.Equal(125, TaipanMsConfig.PollingRateHz(block));
		Assert.Equal(3, TaipanMsConfig.ActiveDpiStage(block));
	}

	// The stored stage trails the live one by exactly one button press (hardware-observed: the LED
	// shows green while the block still reads purple), so LiveDpiStage advances it and wraps on the
	// device's own stage count at 0x12.
	[Theory]
	[InlineData(0x00, 1)] // stored red    -> live blue
	[InlineData(0x02, 3)] // stored purple -> live green
	[InlineData(0x05, 6)] // stored aqua   -> live yellow-green (top stage)
	[InlineData(0x06, 0)] // stored top    -> wraps to the first stage
	public void LiveDpiStage_AdvancesStoredStageAndWraps(byte stored, int expected)
	{
		var block = VendorBlock();
		block[TaipanMsConfig.DpiSwitchOffset] = stored;

		Assert.Equal(expected, TaipanMsConfig.LiveDpiStage(block, stageCount: 7));
	}

	// The stage count comes from the DPI table, not MsConfig — a nonsense value must not divide by
	// zero or wrap early.
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(99)]
	public void LiveDpiStage_FallsBackWhenStageCountIsUnusable(int stageCount)
	{
		var block = VendorBlock();
		block[TaipanMsConfig.DpiSwitchOffset] = 0x02;

		Assert.Equal(3, TaipanMsConfig.LiveDpiStage(block, stageCount));
	}

	[Fact]
	public void ActiveDpiStage_StillReportsTheRawStoredByte()
	{
		var block = VendorBlock();
		block[TaipanMsConfig.DpiSwitchOffset] = 0x02;

		Assert.Equal(2, TaipanMsConfig.ActiveDpiStage(block));                    // raw
		Assert.Equal(3, TaipanMsConfig.LiveDpiStage(block, stageCount: 7));       // corrected
	}

	[Fact]
	public void PatchLiftOffDistance_SetsOnlySensorLiftValue()
	{
		var block = VendorBlock();
		var original = VendorBlock();

		TaipanMsConfig.PatchLiftOffDistance(block, 0x03);

		Assert.Equal(0x03, block[TaipanMsConfig.SensorLiftValueOffset]);
		Assert.Equal(0x03, TaipanMsConfig.LiftOffDistance(block));
		AssertUntouchedOutside(original, block,
			TaipanMsConfig.SensorLiftValueOffset, TaipanMsConfig.SensorLiftValueOffset);
	}

	[Fact]
	public void PatchLiftOffDistance_ComposesWithTheOtherPatches()
	{
		var block = VendorBlock();

		TaipanMsConfig.PatchPollingRate(block, 500);
		TaipanMsConfig.PatchActiveDpiStage(block, 4);
		TaipanMsConfig.PatchLiftOffDistance(block, 2);

		Assert.Equal(500, TaipanMsConfig.PollingRateHz(block));
		Assert.Equal(4, TaipanMsConfig.ActiveDpiStage(block));
		Assert.Equal(2, TaipanMsConfig.LiftOffDistance(block));
	}

	[Fact]
	public void Accessors_ReadBackFromABlock()
	{
		var block = VendorBlock();
		Assert.Equal(1000, TaipanMsConfig.PollingRateHz(block)); // 0x01 at 0x0B
		Assert.Equal(0, TaipanMsConfig.ActiveDpiStage(block));
	}

	// The erased-sector guard: a read taken too soon after a commit comes back all-0x00 / all-0xFF,
	// and writing that back resets every setting at once (DPI to an extreme, rate to default).
	[Fact]
	public void IsPlausible_AcceptsARealBlock()
	{
		Assert.True(TaipanMsConfig.IsPlausible(VendorBlock()));
	}

	[Fact]
	public void IsPlausible_RejectsErasedSectorSignatures()
	{
		Assert.False(TaipanMsConfig.IsPlausible(new byte[TaipanMsConfig.Length]));                              // all 0x00
		Assert.False(TaipanMsConfig.IsPlausible(Enumerable.Repeat((byte)0xFF, TaipanMsConfig.Length).ToArray())); // all 0xFF
	}

	[Fact]
	public void IsPlausible_RejectsShortBlock()
	{
		Assert.False(TaipanMsConfig.IsPlausible(new byte[TaipanMsConfig.Length - 1]));
	}

	[Fact]
	public void IsPlausible_AcceptsAMostlyBlankBlockWithRealData()
	{
		// Only the erase signatures are rejected — a sparse but genuine block must still pass.
		var block = new byte[TaipanMsConfig.Length];
		block[TaipanMsConfig.PollingRateOffset] = 0x02;
		Assert.True(TaipanMsConfig.IsPlausible(block));
	}

	[Fact]
	public void Patch_RejectsUndersizedBlock()
	{
		var tooShort = new byte[TaipanMsConfig.Length - 1];
		Assert.Throws<ArgumentException>(() => TaipanMsConfig.PatchPollingRate(tooShort, 1000));
		Assert.Throws<ArgumentException>(() => TaipanMsConfig.PatchActiveDpiStage(tooShort, 0));
	}

	// ── GetFactoryDefaultBlock — the cold-start (no read, no cache) write fallback ──────────────

	[Fact]
	public void GetFactoryDefaultBlock_MatchesTheVendorWorkedExample()
	{
		Assert.Equal(VendorBlock(), TaipanMsConfig.GetFactoryDefaultBlock());
	}

	[Fact]
	public void GetFactoryDefaultBlock_IsExactlyOneBlockLong()
	{
		Assert.Equal(TaipanMsConfig.Length, TaipanMsConfig.GetFactoryDefaultBlock().Length);
	}

	[Fact]
	public void GetFactoryDefaultBlock_IsPlausible()
	{
		// It must clear the same erased-sector guard PatchMsConfigAsync applies to a real read —
		// otherwise falling back to it would immediately look like a bad block and defeat the point.
		Assert.True(TaipanMsConfig.IsPlausible(TaipanMsConfig.GetFactoryDefaultBlock()));
	}

	[Fact]
	public void GetFactoryDefaultBlock_PatchesTheSameAsAnyOtherBlock()
	{
		// Confirms it's usable as a real patch source, not just inert bytes — same contract as the
		// other Patch_* tests, applied to this specific block.
		var patched = TaipanMsConfig.GetFactoryDefaultBlock();
		TaipanMsConfig.PatchActiveDpiStage(patched, 3);

		Assert.Equal(3, patched[TaipanMsConfig.DpiSwitchOffset]);
		AssertUntouchedOutside(TaipanMsConfig.GetFactoryDefaultBlock(), patched, TaipanMsConfig.DpiSwitchOffset, TaipanMsConfig.DpiSwitchOffset);
	}

	[Fact]
	public void GetFactoryDefaultBlock_ReturnsAFreshArrayEachCall()
	{
		// The whole point of making this a method instead of a public byte[] field: mutating the
		// result must never corrupt what the next caller gets back.
		var first = TaipanMsConfig.GetFactoryDefaultBlock();
		first[TaipanMsConfig.DpiSwitchOffset] = 0xFF;

		var second = TaipanMsConfig.GetFactoryDefaultBlock();

		Assert.NotSame(first, second);
		Assert.Equal(0x00, second[TaipanMsConfig.DpiSwitchOffset]);
	}

	private static void AssertUntouchedOutside(byte[] original, byte[] patched, int fromInclusive, int toInclusive)
	{
		for (var i = 0; i < original.Length; i++)
		{
			if (i >= fromInclusive && i <= toInclusive) continue;
			Assert.Equal(original[i], patched[i]);
		}
	}
}
