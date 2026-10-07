using System.Linq;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the Raster feature-area mutation (CMD_SET_FUNCINFO 0x15). The bytes this does NOT touch
// matter as much as the ones it writes: the service does a read-modify-write precisely because a
// zero-filled block clobbers matrixScreenLightSwitch, sleep timers and user preferences, and the
// observable result of getting that wrong is a dark keyboard.
//
// This exists because a change that started writing lightColorIndex (byte 6) — an unverified
// guess that did not fix its target bug — shipped with nothing to catch it.
// Baseline: docs/plans/raster-rgb/raster-rgb-plan.md § Feature area.
public class RasterKeyboardRgbServiceTests
{
	// A block filled with a recognisable non-zero pattern, so "left alone" is distinguishable
	// from "written as zero".
	private static byte[] SeededBlock()
	{
		var block = new byte[64];
		for (int i = 0; i < block.Length; i++)
			block[i] = (byte)(0xC0 + i);
		return block;
	}

	[Fact]
	public void MutateLightFields_WritesMainChannel()
	{
		var block = SeededBlock();

		RasterKeyboardRgbService.MutateLightFields(
			block, lightSwitch: 0, lightMode: 4, brightness: 80, speed: 6, lightMix: 0,
			r: 0x20, g: 0xE0, b: 0xEE);

		Assert.Equal(0x00, block[1]);   // lightSwitch — 0 = ON, inverted on this family
		Assert.Equal(0x04, block[2]);   // lightMode = Steady
		Assert.Equal(80, block[3]);     // brightness
		Assert.Equal(0, block[4]);      // firmware stores 6 - speed
		Assert.Equal(0x00, block[5]);   // lightMix = mono
		Assert.Equal(0x20, block[7]);
		Assert.Equal(0xE0, block[8]);
		Assert.Equal(0xEE, block[9]);
	}

	// lightColorIndex is preserved, not written. Which value selects "use the R/G/B triplet" on
	// this firmware is unknown: writing 7 was tried and did not make the keys follow the picker,
	// and writing 0 is known to blank the keyboard. Until that is settled from hardware, the
	// value the keyboard powered up with is the only safe thing to send back.
	[Theory]
	[InlineData((byte)0)]
	[InlineData((byte)5)]
	[InlineData((byte)7)]
	public void MutateLightFields_PreservesColorIndexAndCustomIndex(byte existingColorIndex)
	{
		var block = SeededBlock();
		block[6] = existingColorIndex;
		var existingCustomIndex = block[10];

		RasterKeyboardRgbService.MutateLightFields(
			block, lightSwitch: 0, lightMode: 3, brightness: 50, speed: 3, lightMix: 0,
			r: 1, g: 2, b: 3);

		Assert.Equal(existingColorIndex, block[6]);
		Assert.Equal(existingCustomIndex, block[10]);
	}

	// LOGO (11..19) and side (20..28) mirror the main channel so the volume-button LED and any
	// case-edge strip follow the app instead of whatever the Fn shortcuts last set.
	[Fact]
	public void MutateLightFields_MirrorsMainChannelIntoLogoAndSide()
	{
		var block = SeededBlock();

		RasterKeyboardRgbService.MutateLightFields(
			block, lightSwitch: 0, lightMode: 2, brightness: 70, speed: 5, lightMix: 1,
			r: 0x11, g: 0x22, b: 0x33);

		foreach (var start in new[] { 11, 20 })
		{
			Assert.Equal(0x00, block[start]);        // switch
			Assert.Equal(0x02, block[start + 1]);    // mode
			Assert.Equal(70, block[start + 2]);      // brightness
			Assert.Equal(1, block[start + 3]);       // 6 - speed
			Assert.Equal(0x01, block[start + 4]);    // mix
			Assert.Equal(0x00, block[start + 5]);    // channel colorIndex
			Assert.Equal(0x11, block[start + 6]);
			Assert.Equal(0x22, block[start + 7]);
			Assert.Equal(0x33, block[start + 8]);
		}
	}

	// Everything outside the three light sub-blocks must survive untouched: byte 0 (profile) and
	// 29..63, which carry the matrix-screen / user-preference / sleep-timer state depending on
	// protocol version. Writing any of it is what made the keyboard go dark.
	[Fact]
	public void MutateLightFields_LeavesProfileAndTrailingStateUntouched()
	{
		var original = SeededBlock();
		var block = SeededBlock();

		RasterKeyboardRgbService.MutateLightFields(
			block, lightSwitch: 1, lightMode: 4, brightness: 0, speed: 0, lightMix: 0,
			r: 0, g: 0, b: 0);

		Assert.Equal(original[0], block[0]);

		var changed = Enumerable.Range(29, block.Length - 29)
			.Where(i => block[i] != original[i])
			.ToList();
		Assert.Empty(changed);
	}
}
