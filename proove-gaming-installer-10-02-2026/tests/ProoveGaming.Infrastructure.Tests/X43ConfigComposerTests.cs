using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Golden-image tests for the BLAZE/X43 unified-apply composition: DeviceConfiguration ->
// (block0, block1, block2, param0). This is where the session's real hardware incidents lived —
// a partial key-matrix write bricking buttons, and the DPI table clobbering the LED effect byte
// in param_0 — so these lock the exact byte layout instead of only being exercised on hardware.
public class X43ConfigComposerTests
{
	// A representative "current" cache: report rate 1 ms, active stage 2, effect Breathing
	// (proto 0x02), 50% brightness, medium speed, custom colour slot 0. Mirrors CreateDefaultParam0.
	private static byte[] BaselineParam0()
	{
		var p0 = new byte[64];
		p0[0] = 0x01; p0[1] = 0x02; p0[2] = 0x02; p0[3] = 0x80; p0[4] = 0x03; p0[5] = 0x00;
		return p0;
	}

	private static byte[] BaselineBlock0()
	{
		var b = new byte[64];
		b[0] = 8; b[1] = 8; b[3] = 2; b[4] = 7;
		return b;
	}

	private static X43ConfigComposer.Result ComposeBaseline(DeviceConfiguration config,
		byte cachedColorSlot = 0, byte cachedR = 0, byte cachedG = 0, byte cachedB = 0) =>
		X43ConfigComposer.Compose(config, BaselineBlock0(), BaselineParam0(), cachedColorSlot, cachedR, cachedG, cachedB);

	// ── Omitted members fall back to the current cache (documented "null = don't touch") ──────

	[Fact]
	public void Compose_AllMembersNull_Block0AndParam0MatchTheCurrentCache()
	{
		var result = ComposeBaseline(new DeviceConfiguration());

		Assert.Equal(BaselineBlock0(), result.Block0);
		Assert.Equal(BaselineParam0(), result.Param0);
		Assert.Equal(new byte[64], result.Block1); // no LED cache, no config.Led -> untouched colours
		Assert.Empty(result.MacroUploads);
	}

	[Fact]
	public void Compose_AllMembersNull_UsesFactoryKeyMatrix()
	{
		var result = ComposeBaseline(new DeviceConfiguration());
		Assert.Equal(X43KeyMatrix.CreateDefault(), result.Block2);
	}

	// ── DPI table (block 0) ─────────────────────────────────────────────────────────────────

	[Fact]
	public void Compose_DpiStages_WritesBlock0Table_ViaX43DpiTable()
	{
		var stages = new[] { new DpiStageParams(800, 0, 0, 0), new DpiStageParams(1600, 0, 0, 0) };
		var config = new DeviceConfiguration { DpiStages = stages };

		var result = ComposeBaseline(config);

		var expectedBlock0 = BaselineBlock0();
		X43DpiTable.ApplyStages(expectedBlock0, stages);
		Assert.Equal(expectedBlock0, result.Block0);
	}

	[Fact]
	public void Compose_Settings_WritesDebounceAndLiftOffIntoBlock0_WithoutTouchingDpiTable()
	{
		var config = new DeviceConfiguration { Settings = new MouseSettings(Debounce: 12, LiftOff: 3) };

		var result = ComposeBaseline(config);

		Assert.Equal(12, result.Block0[0]);
		Assert.Equal(3, result.Block0[3]);
		// Byte 4 (dpiStage_Max) and the DPI tables are untouched — same as the baseline.
		Assert.Equal(BaselineBlock0()[4], result.Block0[4]);
	}

	// ── LED (param_0 + block 1) ─────────────────────────────────────────────────────────────

	[Theory]
	[InlineData(0x01, 0x02, 0x00)] // Breathing        -> proto 0x02, custom colour
	[InlineData(0x00, 0x02, 0x07)] // RainbowBreathing -> proto 0x02, all colours
	[InlineData(0x02, 0x03, 0x00)] // Neon             -> proto 0x03
	[InlineData(0x03, 0x04, 0x00)] // ColorCycle       -> proto 0x04, slot 0 (the OEM never sends 7 here)
	[InlineData(0xED, 0x02, 0x00)] // Off              -> proto 0x02
	public void Compose_EachLedEffect_MapsToDocumentedProtocolAndColourSlot(byte appEffect, byte expectedProto, byte expectedSlot)
	{
		var led = new LedConfig(R: 10, G: 20, B: 30, Effect: appEffect, Brightness: 200, Speed: 4);
		var config = new DeviceConfiguration { Led = led };

		var result = ComposeBaseline(config);

		Assert.Equal(expectedProto, result.ProtocolEffect);
		Assert.Equal(expectedSlot, result.ColorSlot);
		Assert.Equal(expectedProto, result.Param0[2]);
		Assert.Equal(4, result.Param0[4]);
		Assert.Equal(expectedSlot, result.Param0[5]);

		// Byte 3 is the requested brightness for every effect EXCEPT colour cycle, where it is not a
		// level at all — it gates whether the effect renders, so the composer pins it to the OEM's
		// value. See X43LedEncoder.ResolveBrightness.
		var expectedBrightness = expectedProto == 0x04 ? X43LedEncoder.ColorCycleBrightness : (byte)200;
		Assert.Equal(expectedBrightness, result.Param0[3]);
	}

	[Fact]
	public void Compose_LedWithCustomColourSlot_WritesColourIntoAllEightBlock1Slots()
	{
		var led = new LedConfig(R: 0x11, G: 0x22, B: 0x33, Effect: 0x01 /* Breathing, slot 0 */, Brightness: 255, Speed: 3);
		var result = ComposeBaseline(new DeviceConfiguration { Led = led });

		for (var slot = 0; slot < 8; slot++)
		{
			Assert.Equal(0x11, result.Block1[24 + slot * 3]);
			Assert.Equal(0x22, result.Block1[24 + slot * 3 + 1]);
			Assert.Equal(0x33, result.Block1[24 + slot * 3 + 2]);
		}
		// DPI-indicator colours (bytes 0-23) stay zero — writing them disturbs the effect.
		Assert.All(result.Block1[0..24], b => Assert.Equal(0, b));
	}

	[Fact]
	public void Compose_LedWithAllColoursSlot_WritesTheOemRainbowPaletteIntoBlock1()
	{
		// Regression: this previously asserted block 1 stayed ZEROED for slot 7, on the assumption
		// that "all colours" meant the firmware supplied its own. It does not — slot 7 sweeps the
		// eight colours in THIS palette, so zeroing it cycled eight blacks and the gradient/rainbow
		// breathing effect looked exactly like the LED switching off.
		//
		// The expected bytes are the OEM software's own, read out of block 1 bytes 24-47 of
		// cap-06.08.26-for-blaze.pcapng while the gradient swatch was selected.
		// App effect 0x00 = RainbowBreathing -> protocol 0x02 with colour slot 7.
		var led = new LedConfig(R: 0x11, G: 0x22, B: 0x33, Effect: 0x00, Brightness: 0xFC, Speed: 4);
		var result = ComposeBaseline(new DeviceConfiguration { Led = led });

		(byte R, byte G, byte B)[] expected =
		[
			(0x00, 0xB7, 0xFF), (0x00, 0x00, 0xFF), (0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00),
			(0x00, 0xFF, 0xFF), (0xFF, 0x00, 0xFF), (0xFF, 0x80, 0x00), (0xFF, 0xFF, 0xFF),
		];

		for (int slot = 0; slot < 8; slot++)
		{
			Assert.Equal(expected[slot].R, result.Block1[24 + slot * 3]);
			Assert.Equal(expected[slot].G, result.Block1[24 + slot * 3 + 1]);
			Assert.Equal(expected[slot].B, result.Block1[24 + slot * 3 + 2]);
		}

		// The custom colour (0x11/0x22/0x33) must NOT leak into the palette in this mode.
		Assert.DoesNotContain((byte)0x11, result.Block1[24..48]);

		// DPI-indicator colours stay zero, matching the OEM capture.
		Assert.All(result.Block1[0..24], b => Assert.Equal(0, b));
		Assert.All(result.Block1[48..64], b => Assert.Equal(0, b));
	}

	// Neon and colour cycle sweep the same palette, but the OEM drives BOTH on colour slot 0 —
	// so the slot cannot be what decides whether the palette is written. Filling all eight entries
	// with the single custom colour (what the slot-0 path does for breathing) turned both effects
	// into a "cycle" through eight copies of one colour, which reads on hardware as the effect not
	// working. Source: cap-06.08.26-for-blaze-neon-color-cycle.pcapng, where block 1 is byte-identical
	// to the gradient capture across every neon and colour-cycle frame.
	[Theory]
	[InlineData((byte)0x02)] // Neon         -> protocol 0x03
	[InlineData((byte)0x03)] // ColorCycle   -> protocol 0x04
	public void Compose_NeonAndColourCycle_WriteTheOemPaletteDespiteUsingSlotZero(byte appEffect)
	{
		var led = new LedConfig(R: 0x11, G: 0x22, B: 0x33, Effect: appEffect, Brightness: 0xBD, Speed: 4);
		var result = ComposeBaseline(new DeviceConfiguration { Led = led });

		Assert.Equal(X43LedEncoder.ColorSlotCustom, result.ColorSlot);

		for (int slot = 0; slot < 8; slot++)
		{
			var (r, g, b) = X43LedEncoder.AllColorsPalette[slot];
			Assert.Equal(r, result.Block1[24 + slot * 3]);
			Assert.Equal(g, result.Block1[24 + slot * 3 + 1]);
			Assert.Equal(b, result.Block1[24 + slot * 3 + 2]);
		}

		// The custom colour must not overwrite the palette these effects sweep.
		Assert.DoesNotContain((byte)0x11, result.Block1[24..48]);
	}

	[Fact]
	public void Compose_LedOff_DriveBrightnessIsZero_EvenWithNonZeroBrightnessField()
	{
		var led = new LedConfig(R: 0, G: 0, B: 0, Effect: 0xED, Brightness: 128, Speed: 3);
		var result = ComposeBaseline(new DeviceConfiguration { Led = led });

		Assert.Equal(0, result.DriveBrightness);
	}

	[Fact]
	public void Compose_LedNull_KeepsCachedColour_AndBaselineEffectFromParam0()
	{
		var result = ComposeBaseline(new DeviceConfiguration(), cachedColorSlot: 3, cachedR: 5, cachedG: 6, cachedB: 7);

		// No config.Led -> param_0's effect/brightness/speed/slot come straight from the cache,
		// and param_0 bytes 2-5 are NOT overwritten (config.Led is null).
		Assert.Equal(BaselineParam0()[2], result.ProtocolEffect);
		Assert.Equal(BaselineParam0(), result.Param0);
		// Colour cache passes through untouched.
		Assert.Equal((byte)3, result.ColorSlot);
		Assert.Equal((byte)5, result.LedR);
		Assert.Equal((byte)6, result.LedG);
		Assert.Equal((byte)7, result.LedB);
	}

	// ── Buttons / macros (block 2) ──────────────────────────────────────────────────────────

	[Fact]
	public void Compose_RemappedButton_OverwritesOnlyThatSlot()
	{
		var config = new DeviceConfiguration
		{
			Buttons = [new ButtonBinding(MouseButton.Right, MouseButtonAction.VolumeUp)],
		};

		var result = ComposeBaseline(config);

		var expected = X43KeyMatrix.CreateDefault();
		var entry = X43KeyMatrix.EncodeAction(MouseButtonAction.VolumeUp)!;
		Buffer.BlockCopy(entry, 0, expected, X43KeyMatrix.ButtonToSlot(MouseButton.Right) * 4, 4);
		Assert.Equal(expected, result.Block2);
	}

	[Fact]
	public void Compose_MacroBoundButton_WritesMacroEntry_AndReturnsUploadForThatSlot()
	{
		var macro = new MacroDefinition("M1", 1, MacroRepeatMode.PlayCount, [new MacroEvent(0xF0, true, 0)]);
		var config = new DeviceConfiguration
		{
			Buttons = [new ButtonBinding(MouseButton.Wheel, MouseButtonAction.Macro, macro)],
		};

		var result = ComposeBaseline(config);

		var upload = Assert.Single(result.MacroUploads);
		Assert.Equal((byte)0, upload.Slot); // first (and only) macro-bound button -> slot 0
		Assert.Same(macro, upload.Macro);

		var slot = X43KeyMatrix.ButtonToSlot(MouseButton.Wheel);
		byte[] expectedEntry = [0, 0x00, X43MacroEncoder.TypeByte(macro.Mode), 0x05];
		Assert.Equal(expectedEntry, result.Block2[(slot * 4)..(slot * 4 + 4)]);
	}

	[Fact]
	public void Compose_MultipleMacroBoundButtons_AssignSequentialSlots()
	{
		var macroA = new MacroDefinition("A", 1, MacroRepeatMode.PlayCount, []);
		var macroB = new MacroDefinition("B", 1, MacroRepeatMode.LoopForever, []);
		var config = new DeviceConfiguration
		{
			Buttons =
			[
				new ButtonBinding(MouseButton.Right, MouseButtonAction.Macro, macroA),
				new ButtonBinding(MouseButton.Wheel, MouseButtonAction.Macro, macroB),
			],
		};

		var result = ComposeBaseline(config);

		Assert.Equal(2, result.MacroUploads.Count);
		Assert.Equal((byte)0, result.MacroUploads[0].Slot);
		Assert.Same(macroA, result.MacroUploads[0].Macro);
		Assert.Equal((byte)1, result.MacroUploads[1].Slot);
		Assert.Same(macroB, result.MacroUploads[1].Macro);
	}

	[Fact]
	public void Compose_ButtonNotOnThisDevice_IsSkipped_MatrixUnchanged()
	{
		// MouseButton.Unspecified has no matrix slot (ButtonToSlot returns -1) — must be a no-op.
		var config = new DeviceConfiguration
		{
			Buttons = [new ButtonBinding(MouseButton.Unspecified, MouseButtonAction.VolumeUp)],
		};

		var result = ComposeBaseline(config);
		Assert.Equal(X43KeyMatrix.CreateDefault(), result.Block2);
	}

	// ── param_0 (stage / report rate) ───────────────────────────────────────────────────────

	[Fact]
	public void Compose_ActiveStage_WritesParam0Byte1_WithoutTouchingEffectBytes()
	{
		// The historic bug: rewriting param_0 for a stage change also clobbered the LED effect
		// byte. A DPI-only apply (no config.Led) must leave bytes 2-5 exactly as cached.
		var config = new DeviceConfiguration { ActiveStage = 5 };

		var result = ComposeBaseline(config);

		Assert.Equal(5, result.Param0[1]);
		Assert.Equal(BaselineParam0()[2], result.Param0[2]);
		Assert.Equal(BaselineParam0()[3], result.Param0[3]);
		Assert.Equal(BaselineParam0()[4], result.Param0[4]);
		Assert.Equal(BaselineParam0()[5], result.Param0[5]);
	}

	[Fact]
	public void Compose_ReportRateHz_WritesParam0Byte0_ViaX43Settings()
	{
		var config = new DeviceConfiguration { ReportRateHz = 500 };
		var result = ComposeBaseline(config);
		Assert.Equal(X43Settings.ReportRateByte(500), result.Param0[0]);
	}

	[Fact]
	public void Compose_StageAndLedTogether_BothLandInTheSameParam0Write()
	{
		var led = new LedConfig(R: 1, G: 2, B: 3, Effect: 0x02 /* Neon -> proto 0x03 */, Brightness: 99, Speed: 5);
		var config = new DeviceConfiguration { ActiveStage = 6, Led = led };

		var result = ComposeBaseline(config);

		Assert.Equal(6, result.Param0[1]);
		Assert.Equal(0x03, result.Param0[2]);
		Assert.Equal(99, result.Param0[3]);
	}
}
