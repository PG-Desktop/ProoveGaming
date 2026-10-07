using ProoveGaming.Infrastructure.Services;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the Rampage brightness mapping (PR #101 m6): light_level is the family's discrete
// 0-4 scale — the factory-confirmed Rampage max (C9) — not the workbook's 0-99, and any
// non-zero slider value must keep the backlight on. Pure arithmetic, so it is tested
// directly; the HID write path around it stays hardware-QA territory.
public class RampageKeyboardRgbServiceTests
{
	private const byte EffectStatic = 0x04;
	private const byte EffectOff = 0xED;

	[Theory]
	[InlineData(255, 4)]   // 100% slider → the board's max level
	[InlineData(153, 2)]   // 60% (the catalog default brightness)
	[InlineData(128, 2)]
	[InlineData(64, 1)]
	[InlineData(32, 1)]    // rounds to 0 but clamps up — low slider must not read as "off"
	[InlineData(1, 1)]
	[InlineData(0, 0)]     // 0% genuinely off
	public void ToKeyboardBrightness_MapsTheSliderOntoTheDiscreteScale(byte appBrightness, byte expectedLevel)
	{
		Assert.Equal(expectedLevel,
			RampageKeyboardRgbService.ToKeyboardBrightness(appBrightness, EffectStatic));
	}

	[Fact]
	public void ToKeyboardBrightness_OffEffect_ForcesZero_RegardlessOfSlider()
	{
		Assert.Equal(0, RampageKeyboardRgbService.ToKeyboardBrightness(255, EffectOff));
	}
}
