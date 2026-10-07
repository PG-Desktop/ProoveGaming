using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the T800 (Rongyuan RY5088) lighting packets against the vendor spec's LED Param Table
// (docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md).
//
// This exists because the first attempt at this device shipped with the effect codes, the 0..4
// firmware scales and the Option byte all guessed, and the only signal that they were wrong was a
// keyboard that stayed dark. Every value asserted here is quoted from the spec, so a future edit
// that "simplifies" one of them back to a plausible-looking guess fails instead of shipping.
//
// The checksum values below are MEASURED, not derived from a document: neither vendor spec defines
// the algorithm, and the hardware confirmed `0xFF - (sum & 0xFF)` by answering GET_LEDPARAM under
// that rule and only that rule (every other candidate got our own request echoed back). A plain
// sum is off by exactly one and is silently discarded by the firmware, which is what sank the two
// previous attempts at this device — so these are regression tests in the strongest sense.
public class T800KeyboardRgbServiceTests
{
	private const byte AppRainbowBreathing = 0x00;
	private const byte AppBreathing = 0x01;
	private const byte AppNeon = 0x02;
	private const byte AppColorCycle = 0x03;
	private const byte AppStatic = 0x04;
	private const byte AppWave = 0x05;
	private const byte AppOff = 0xED;

	// ── Packet framing ───────────────────────────────────────────────────────────

	[Fact]
	public void LedParamPacket_MatchesSpecLayout()
	{
		var p = T800KeyboardRgbService.BuildLedParamPacket(
			effect: 0x01, speed: 2, brightness: 4, option: 0x07, r: 0x20, g: 0xE0, b: 0xEE);

		Assert.Equal(65, p.Length);
		Assert.Equal(0x00, p[0]);   // HID report ID
		Assert.Equal(0x07, p[1]);   // FEA_CMD_SET_LEDPARAM
		Assert.Equal(0x01, p[2]);   // Effect#
		Assert.Equal(2, p[3]);      // Speed
		Assert.Equal(4, p[4]);      // Bri
		Assert.Equal(0x07, p[5]);   // Option
		Assert.Equal(0x20, p[6]);   // R
		Assert.Equal(0xE0, p[7]);   // G
		Assert.Equal(0xEE, p[8]);   // B
		// CS at protocol byte 8 = buffer index 9: this command takes SEVEN parameters, so it sits
		// one later than every six-parameter command in the spec.
		Assert.Equal(T800KeyboardRgbService.Checksum(p, 1, 8), p[9]);
	}

	[Fact]
	public void LedParamPacket_TailIsZero()
	{
		var p = T800KeyboardRgbService.BuildLedParamPacket(1, 2, 4, 7, 1, 2, 3);

		Assert.All(p[10..], b => Assert.Equal(0, b));
	}

	// 0 lights the board and 1 darkens it — inverted against the intuitive reading, and the same
	// trap that cost the Raster service a long debugging session.
	[Theory]
	[InlineData(false, 0x00)]
	[InlineData(true, 0x01)]
	public void OnOffPacket_UsesInvertedPolarity(bool off, byte expected)
	{
		var p = T800KeyboardRgbService.BuildOnOffPacket(mainOff: off, sideOff: off);

		Assert.Equal(0x05, p[1]);   // FEA_CMD_SET_LEDONOFF
		Assert.Equal(expected, p[2]);  // main light
		Assert.Equal(expected, p[3]);  // side light
		// CS at protocol byte 7 = buffer index 8 (six-parameter command).
		Assert.Equal(T800KeyboardRgbService.Checksum(p, 1, 7), p[8]);
	}

	// ── Effect mapping ───────────────────────────────────────────────────────────

	// Codes straight out of the LED Param Table: Static 0x01, Breathe 0x02, Neon 0x03, Wave 0x04.
	// Not a sequential 1..6 matching the app's effect list, which is what the first attempt used.
	[Theory]
	[InlineData(AppStatic, 0x01)]
	[InlineData(AppBreathing, 0x02)]
	[InlineData(AppRainbowBreathing, 0x02)]
	[InlineData(AppNeon, 0x03)]
	[InlineData(AppWave, 0x04)]
	[InlineData(AppColorCycle, 0x04)]
	[InlineData(AppOff, 0x00)]
	public void EffectMapping_UsesSpecCodes(byte appEffect, byte expected)
	{
		Assert.Equal(expected, T800KeyboardRgbService.MapAppEffectToEffect(appEffect));
	}

	// ── Option byte ──────────────────────────────────────────────────────────────

	// The single most load-bearing assertion in this file. Option's low nibble selects the colour
	// SOURCE, and 0..6 are preset colours for which the firmware ignores R/G/B entirely. Any effect
	// the user picks a colour for must therefore use slot 7 (AP colour) — the first attempt sent 0,
	// i.e. "preset red", which is why the picker had no effect on the board.
	[Theory]
	[InlineData(AppStatic)]
	[InlineData(AppBreathing)]
	[InlineData(AppWave)]
	public void ColourFollowingEffects_UseApColourSlot(byte appEffect)
	{
		var option = T800KeyboardRgbService.MapAppEffectToOption(appEffect);

		Assert.Equal(7, option & 0x0F);
	}

	// Multicolour effects run the firmware's own spectrum, so they take slot 8 and their R/G/B is
	// ignored by design.
	[Theory]
	[InlineData(AppRainbowBreathing)]
	[InlineData(AppNeon)]
	[InlineData(AppColorCycle)]
	public void MulticolourEffects_UseFullColourSlot(byte appEffect)
	{
		var option = T800KeyboardRgbService.MapAppEffectToOption(appEffect);

		Assert.Equal(8, option & 0x0F);
	}

	// The high nibble is the per-effect Type variant. The app exposes no direction control, so Wave
	// pins to 0 (right) — asserted so a later direction feature has to change this deliberately.
	[Theory]
	[InlineData(AppWave)]
	[InlineData(AppColorCycle)]
	public void WaveEffects_FlowRight(byte appEffect)
	{
		var option = T800KeyboardRgbService.MapAppEffectToOption(appEffect);

		Assert.Equal(0, option >> 4);
	}

	// ── Firmware scales ──────────────────────────────────────────────────────────

	// Brightness is a 0..4 enum, NOT 0..100 (Raster's scale) and not the app's 0..255. Sending 100
	// here is far outside the enum and is what the first attempt did.
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 1)]      // any non-zero brightness must stay lit
	[InlineData(64, 1)]
	[InlineData(128, 2)]
	[InlineData(255, 4)]
	public void Brightness_ScalesToFirmwareEnum(byte appBrightness, byte expected)
	{
		Assert.Equal(expected, T800KeyboardRgbService.ToKeyboardBrightness(appBrightness, AppStatic));
	}

	[Fact]
	public void Brightness_IsZeroForOff()
	{
		Assert.Equal(0, T800KeyboardRgbService.ToKeyboardBrightness(255, AppOff));
	}

	// Speed is INVERTED on the wire: high app speed must send a LOW byte. Getting this backwards is
	// a silent, plausible-looking bug — the board still animates, just at the wrong rate — so both
	// ends are pinned.
	[Theory]
	[InlineData(1, 5)]  // app slowest -> wire slowest
	[InlineData(4, 3)]
	[InlineData(7, 1)]  // app fastest -> fastest value the vendor tool emits
	public void Speed_ScalesAndInvertsToWire(byte appSpeed, byte expected)
	{
		Assert.Equal(expected, T800KeyboardRgbService.ToKeyboardSpeed(appSpeed));
	}

	// 0 is not a value the app sends (its range starts at 1); fall back to mid-range.
	[Fact]
	public void Speed_ZeroFallsBackToMidRange()
	{
		Assert.Equal(3, T800KeyboardRgbService.ToKeyboardSpeed(0));
	}

	// Wire 0 is outside the range the vendor tool uses. The firmware stores it without complaint,
	// so nothing at runtime would catch it being emitted; an invalid speed that freezes the
	// animation is indistinguishable from a dead slider. Keep it out until hardware says otherwise.
	[Fact]
	public void Scales_StayInVendorProvenRange()
	{
		for (int i = 0; i <= 255; i++)
		{
			Assert.InRange(T800KeyboardRgbService.ToKeyboardBrightness((byte)i, AppStatic), 0, 4);
			Assert.InRange(T800KeyboardRgbService.ToKeyboardSpeed((byte)i), 1, 5);
		}
	}

	// ── Checksum ─────────────────────────────────────────────────────────────────

	// Hand-computed against the exact frame the hardware accepted:
	//   07 01 00 04 07 FF 00 00 -> sum 0x112, 0xFF - 0x12 = 0xED
	// which is the byte the device answered to. A plain sum would put 0x12 here and be discarded.
	[Fact]
	public void Checksum_IsOnesComplementOfTheSum()
	{
		var p = new byte[] { 0x00, 0x07, 0x01, 0x00, 0x04, 0x07, 0xFF, 0x00, 0x00 };

		Assert.Equal(0xED, T800KeyboardRgbService.Checksum(p, 1, 8));
	}

	[Fact]
	public void Checksum_MatchesTheProbedGetLedParamFrame()
	{
		// 87 00 00 00 00 00 00 -> sum 0x87, 0xFF - 0x87 = 0x78. This is the frame the firmware
		// replied to with real state; under any other rule it echoed the request back.
		var p = new byte[T800KeyboardRgbService.ReportLen];
		p[1] = 0x87;

		Assert.Equal(0x78, T800KeyboardRgbService.Checksum(p, 1, 7));
	}

	// ── Colour clamping ──────────────────────────────────────────────────────────

	// The white quirk is scoped to white. Hardware read-back confirms a plain FF 00 00 is stored
	// unchanged, so saturated colours must pass through untouched.
	[Theory]
	[InlineData(0xFF, 0x00, 0x00)]
	[InlineData(0x76, 0x20, 0xEE)]
	[InlineData(0x00, 0x00, 0x00)]
	public void ClampColour_LeavesOrdinaryColoursAlone(byte r, byte g, byte b)
	{
		Assert.Equal((r, g, b), T800KeyboardRgbService.ClampColour(r, g, b));
	}

	[Fact]
	public void ClampColour_SendsWhiteAsFa()
	{
		Assert.Equal(((byte)0xFA, (byte)0xFA, (byte)0xFA), T800KeyboardRgbService.ClampColour(0xFF, 0xFF, 0xFF));
	}

	// Below 0x08 every channel renders unlit, which would look like a broken board rather than a
	// dark colour. Lifted to the visible floor, hue preserved.
	[Fact]
	public void ClampColour_LiftsInvisibleColoursToTheFloor()
	{
		var (r, g, b) = T800KeyboardRgbService.ClampColour(0x04, 0x02, 0x00);

		Assert.Equal(0x08, r);
		Assert.Equal(0x04, g);
		Assert.Equal(0x00, b);
	}
}
