using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the App-effect -> (protocol effect, colour slot) mapping. Several UI effects collapse
// onto the same protocol effect, distinguished only by colour slot (0x00 custom, 0x07 all
// colours), so a wrong slot silently turns e.g. RainbowBreathing into a single-colour Breathing.
// Baseline reverse-engineered from captures (see docs/mouse/blaze).
public class X43LedEncoderTests
{
	[Theory]
	[InlineData(0x01, 0x02, 0x00)] // Breathing        -> proto 0x02, custom colour
	[InlineData(0x00, 0x02, 0x07)] // RainbowBreathing -> proto 0x02, all colours
	[InlineData(0x02, 0x03, 0x00)] // Neon             -> proto 0x03
	[InlineData(0x03, 0x04, 0x00)] // ColorCycle       -> proto 0x04, slot 0 (the OEM never sends 7 here)
	[InlineData(0xED, 0x02, 0x00)] // Off              -> proto 0x02 (brightness=0 elsewhere)
	[InlineData(0x7F, 0x02, 0x00)] // unknown          -> Breathing custom-colour fallback
	public void MapEffect_ProducesExpectedProtocolAndSlot(byte appEffect, byte proto, byte slot)
	{
		var (protocolEffect, colorSlot) = X43LedEncoder.MapEffect(appEffect);
		Assert.Equal(proto, protocolEffect);
		Assert.Equal(slot, colorSlot);
	}

	// Slot 7 is the gradient/rainbow BREATHING marker only. Colour cycle is a distinct protocol
	// effect (0x04) and the OEM drives it on slot 0 — sending slot 7 there left the LED dark on
	// hardware. Pinned because "both cycle colours, so both must be slot 7" is the intuitive
	// (and wrong) reading. See cap-06.08.26-for-blaze-neon-color-cycle.pcapng.
	[Fact]
	public void MapEffect_OnlyRainbowBreathingUsesSlot7()
	{
		Assert.Equal(X43LedEncoder.ColorSlotAllColors, X43LedEncoder.MapEffect(0x00).colorSlot); // RainbowBreathing
		Assert.Equal(X43LedEncoder.ColorSlotCustom, X43LedEncoder.MapEffect(0x03).colorSlot);    // ColorCycle
	}

	// Which effects read the block 1 palette. Neon and colour cycle do so on slot 0, which is the
	// distinction that broke them: the slot alone does not decide whether the palette is used.
	[Theory]
	[InlineData(0x02, 0x00, false)] // breathing, custom colour -> shows that one colour
	[InlineData(0x02, 0x07, true)]  // gradient breathing       -> sweeps the palette
	[InlineData(0x03, 0x00, true)]  // neon                     -> sweeps it despite slot 0
	[InlineData(0x04, 0x00, true)]  // colour cycle             -> sweeps it despite slot 0
	public void SweepsPalette_MatchesTheOemBehaviour(byte protocolEffect, byte colorSlot, bool expected)
	{
		Assert.Equal(expected, X43LedEncoder.SweepsPalette(protocolEffect, colorSlot));
	}

	// ── Colour cycle's byte 3 ──────────────────────────────────────────────────────────────────
	//
	// For protocol 0x04 byte 3 is not a brightness level — it decides whether the effect renders.
	// The OEM sends 0xBD; driving it at 0xFC produced no cycle. The App maps its slider linearly to
	// 0-255, so near full it sent 0xFF and the cycle collapsed into something indistinguishable from
	// neon on hardware.
	// ── Wave is the mouse's protocol-0x04 effect ───────────────────────────────────────────────
	//
	// Mice stopped offering "ColorCycle" on 2026-08-17 — on BLAZE's single LED that effect is a
	// travelling gradient, which the App calls Wave. The App's byte for Wave is 0x05, and it has to
	// land on the same protocol effect the old name did, or picking Wave silently gives Breathing
	// (0x05 used to fall through to the default).
	[Fact]
	public void MapEffect_Wave_IsTheColourCycleProtocolEffect()
	{
		Assert.Equal((0x04, X43LedEncoder.ColorSlotCustom), X43LedEncoder.MapEffect(0x05));
	}

	// RGB state persisted before the rename still names ColorCycle, and a saved effect must keep
	// lighting the mouse the way it did.
	[Fact]
	public void MapEffect_TheLegacyColorCycleByte_StillMapsToTheSameEffect()
	{
		Assert.Equal(X43LedEncoder.MapEffect(0x05), X43LedEncoder.MapEffect(0x03));
	}

	// Wave inherits the byte-3 gate automatically, because ResolveBrightness keys off the PROTOCOL
	// effect rather than the App's name for it.
	[Fact]
	public void ResolveBrightness_AppliesToWave_ViaItsProtocolEffect()
	{
		var (protocolEffect, _) = X43LedEncoder.MapEffect(0x05);

		Assert.Equal(X43LedEncoder.ColorCycleBrightness, X43LedEncoder.ResolveBrightness(protocolEffect, 0xFF));
	}

	// The captured value itself, as a literal. Every other assertion here compares against the
	// constant, so without this one the constant could be changed to anything and the whole suite
	// would still pass while colour cycle stopped rendering on hardware. 0xBD is what the OEM sends
	// in cap-06.08.26-for-blaze-neon-color-cycle.pcapng — it is a measurement, not a choice.
	[Fact]
	public void ColorCycleBrightness_IsTheByteTheOemCaptureSends()
	{
		Assert.Equal(0xBD, X43LedEncoder.ColorCycleBrightness);
	}

	[Theory]
	[InlineData(0xFF)]
	[InlineData(0xFC)]
	[InlineData(0x00)]
	[InlineData(0x99)]
	public void ResolveBrightness_ColourCycle_AlwaysUsesTheOemValue(byte requested)
	{
		Assert.Equal(X43LedEncoder.ColorCycleBrightness, X43LedEncoder.ResolveBrightness(0x04, requested));
	}

	// Every other effect keeps the user's brightness — only 0x04 has evidence of the byte gating it.
	[Theory]
	[InlineData(0x02, 0xFC)]   // breathing
	[InlineData(0x03, 0xFF)]   // neon — renders fine across the slider
	[InlineData(0x06, 0x80)]
	public void ResolveBrightness_OtherEffects_PassTheRequestedValueThrough(byte effect, byte requested)
	{
		Assert.Equal(requested, X43LedEncoder.ResolveBrightness(effect, requested));
	}
}
