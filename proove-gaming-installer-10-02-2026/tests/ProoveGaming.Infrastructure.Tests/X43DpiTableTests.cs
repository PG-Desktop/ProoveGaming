using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the DPI stage table written into param_1 block 0: stage count at byte 4, each
// CPI as a little-endian ushort stored in a primary table (bytes 9..) and a mirror (bytes 25..).
// A wrong offset, byte order or a disturbed neighbour byte corrupts the DPI stages or the LED
// state that shares the block. Baseline: docs/mouse/blaze/dpi-stage.md.
public class X43DpiTableTests
{
	private static DpiStageParams Stage(ushort dpi) => new(dpi, 0, 0, 0);

	[Fact]
	public void ApplyStages_WritesCountAndLittleEndianValuesInBothTables()
	{
		var block0 = new byte[64];
		// 800=0x0320, 1600=0x0640, 3200=0x0C80
		X43DpiTable.ApplyStages(block0, new[] { Stage(800), Stage(1600), Stage(3200) });

		Assert.Equal(3, block0[X43DpiTable.StageCountOffset]);

		// primary table @9, little-endian
		Assert.Equal(new byte[] { 0x20, 0x03, 0x40, 0x06, 0x80, 0x0C }, block0[9..15]);
		// mirror table @25, identical
		Assert.Equal(new byte[] { 0x20, 0x03, 0x40, 0x06, 0x80, 0x0C }, block0[25..31]);
	}

	// Regression: this used to assert the trailing slots were ZEROED. A zero DPI entry makes the
	// firmware reject the param_1 write, and block 1 (the LED) is in that same write — so RGB
	// effects applied only intermittently, and colour cycle never at all. The OEM writes 2000 in
	// its unused slot; hardware traces showed ours writing 00 00 in exactly that position.
	[Fact]
	public void ApplyStages_FillsTrailingUnusedStagesWithAValidDpi()
	{
		var block0 = new byte[64];
		X43DpiTable.ApplyStages(block0, new[] { Stage(800), Stage(1600) });

		var lo = (byte)(X43DpiTable.UnusedStageDpi & 0xFF);
		var hi = (byte)(X43DpiTable.UnusedStageDpi >> 8);

		for (var i = 2; i < X43DpiTable.MaxStages; i++)
		{
			Assert.Equal(lo, block0[X43DpiTable.PrimaryTableOffset + i * 2]);
			Assert.Equal(hi, block0[X43DpiTable.PrimaryTableOffset + i * 2 + 1]);
			Assert.Equal(lo, block0[X43DpiTable.MirrorTableOffset + i * 2]);
			Assert.Equal(hi, block0[X43DpiTable.MirrorTableOffset + i * 2 + 1]);
		}

		Assert.NotEqual(0, X43DpiTable.UnusedStageDpi);
	}

	[Fact]
	public void ApplyStages_ClampsToEightStages()
	{
		var block0 = new byte[64];
		var stages = new DpiStageParams[10];
		for (var i = 0; i < stages.Length; i++)
			stages[i] = Stage((ushort)(100 * (i + 1)));

		X43DpiTable.ApplyStages(block0, stages);

		Assert.Equal(8, block0[X43DpiTable.StageCountOffset]);
		// 8th stage (index 7) value 800=0x0320 present in both tables; nothing written past it
		Assert.Equal(0x20, block0[X43DpiTable.PrimaryTableOffset + 7 * 2]);
		Assert.Equal(0x03, block0[X43DpiTable.PrimaryTableOffset + 7 * 2 + 1]);
		Assert.Equal(0x20, block0[X43DpiTable.MirrorTableOffset + 7 * 2]);
		Assert.Equal(0x03, block0[X43DpiTable.MirrorTableOffset + 7 * 2 + 1]);
	}

	// The ladder is user-defined now (1-8 points), and the count byte is what the firmware reads to
	// know how far the table goes. The trailing slots still hold UnusedStageDpi rather than zero —
	// see the regression note above; a shorter ladder must not reintroduce a zero entry.
	// See docs/plans/add-custom-dpi-values/.
	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	[InlineData(5)]
	[InlineData(8)]
	public void ApplyStages_WritesTheCountOfAUserDefinedLadderAndFillsTheRest(int count)
	{
		var block0 = new byte[64];
		var stages = new DpiStageParams[count];
		for (var i = 0; i < count; i++)
			stages[i] = Stage((ushort)(800 + i * 400));

		X43DpiTable.ApplyStages(block0, stages);

		Assert.Equal(count, block0[X43DpiTable.StageCountOffset]);

		var lo = (byte)(X43DpiTable.UnusedStageDpi & 0xFF);
		var hi = (byte)(X43DpiTable.UnusedStageDpi >> 8);
		for (var i = count; i < X43DpiTable.MaxStages; i++)
		{
			Assert.Equal(lo, block0[X43DpiTable.PrimaryTableOffset + i * 2]);
			Assert.Equal(hi, block0[X43DpiTable.PrimaryTableOffset + i * 2 + 1]);
			Assert.Equal(lo, block0[X43DpiTable.MirrorTableOffset + i * 2]);
			Assert.Equal(hi, block0[X43DpiTable.MirrorTableOffset + i * 2 + 1]);
		}
	}

	[Fact]
	public void ApplyStages_LeavesUnrelatedBytesUntouched()
	{
		// Fill with a sentinel; only byte 4, the primary table [9..24] and mirror [25..40]
		// may change — everything else (incl. the LED bytes elsewhere in the block) must survive.
		var block0 = new byte[64];
		Array.Fill(block0, (byte)0xAA);

		X43DpiTable.ApplyStages(block0, new[] { Stage(1200) });

		for (var i = 0; i < 64; i++)
		{
			var touched = i == X43DpiTable.StageCountOffset
				|| (i >= X43DpiTable.PrimaryTableOffset && i < X43DpiTable.PrimaryTableOffset + X43DpiTable.MaxStages * 2)
				|| (i >= X43DpiTable.MirrorTableOffset && i < X43DpiTable.MirrorTableOffset + X43DpiTable.MaxStages * 2);
			if (!touched)
				Assert.Equal(0xAA, block0[i]);
		}
	}
}
