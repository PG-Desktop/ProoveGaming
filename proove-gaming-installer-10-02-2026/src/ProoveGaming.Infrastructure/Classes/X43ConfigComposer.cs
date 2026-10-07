namespace ProoveGaming.Infrastructure.Classes;

// Pure DeviceConfiguration -> byte-image composer for the BLAZE/X43 all-blocks write. Mirrors
// exactly what EasenMouseService.ApplyConfigurationAsync used to build inline, so the composed
// layout can be locked by golden-image tests (X43ConfigComposerTests) instead of only being
// exercised on hardware. No I/O here — HID writes, including macro slot uploads, stay in
// EasenMouseService; Compose only tells the caller which (slot, macro) pairs to upload.
public static class X43ConfigComposer
{
	// Block0/Block1/Block2 + Param0 are the byte images to write (param_1 = Block0+Block1+Block2,
	// 192 bytes total, then Param0 separately). ProtocolEffect/ColorSlot/DriveBrightness/LedR/G/B
	// are what the caller needs to drive the LED (0x0D/0x80) when config.Led is set, and to update
	// its own colour cache. MacroUploads lists the (slot, macro) pairs to upload BEFORE writing
	// Block2 — slot assignment is deterministic (sequential over config.Buttons), so it can be
	// computed here even though the actual flash upload is I/O and stays in EasenMouseService.
	public readonly record struct Result(
		byte[] Block0, byte[] Block1, byte[] Block2, byte[] Param0,
		byte ProtocolEffect, byte ColorSlot, byte DriveBrightness,
		byte LedR, byte LedG, byte LedB,
		IReadOnlyList<(byte Slot, MacroDefinition Macro)> MacroUploads);

	// currentBlock0/currentParam0: the service's cached baseline (block0 = DPI table +
	// debounce/lift; param0 = report rate/active stage/effect/brightness/speed) — cloned, never
	// mutated. cachedColorSlot/R/G/B: the LED colour cache carried across calls when config.Led is
	// null, so an apply that doesn't touch RGB keeps showing the last colour.
	public static Result Compose(
		DeviceConfiguration config,
		byte[] currentBlock0, byte[] currentParam0,
		byte cachedColorSlot, byte cachedLedR, byte cachedLedG, byte cachedLedB)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(currentBlock0);
		ArgumentNullException.ThrowIfNull(currentParam0);

		// Block 0 — start from the cache, overlay debounce/lift-off and the DPI table.
		var block0 = (byte[])currentBlock0.Clone();
		if (config.Settings is { } s)
		{
			block0[0] = s.Debounce;
			block0[3] = s.LiftOff;
		}
		if (config.DpiStages is { Length: > 0 } stages)
			X43DpiTable.ApplyStages(block0, stages);

		// LED — resolve the effect/slot/colour for param_0 and block 1. Falls back to the current
		// param_0's effect/brightness/speed and the colour cache when config.Led is null.
		byte protocolEffect = currentParam0[2], brightness = currentParam0[3], speed = currentParam0[4];
		byte colorSlot = cachedColorSlot, ledR = cachedLedR, ledG = cachedLedG, ledB = cachedLedB;
		if (config.Led is { } led)
		{
			(protocolEffect, colorSlot) = X43LedEncoder.MapEffect(led.Effect);
			// Colour cycle ignores the requested level and takes the OEM's byte-3 value — see
			// X43LedEncoder.ResolveBrightness: for that effect the byte gates whether it renders.
			brightness = X43LedEncoder.ResolveBrightness(protocolEffect, led.Brightness);
			speed = (byte)Math.Clamp((int)(led.Speed > 0 ? led.Speed : 3), 1, 7);
			ledR = led.R; ledG = led.G; ledB = led.B;
		}

		// Block 1 — LED-effect colours (bytes 24-47); DPI-indicator colours (0-23) stay zero,
		// matching the OEM capture (writing them disturbs the effect).
		var block1 = new byte[64];
		if (X43LedEncoder.SweepsPalette(protocolEffect, colorSlot))
		{
			// Sweeping effects read their colours IN THIS TABLE — they do not generate their own.
			// Leaving it zeroed cycled eight blacks, which is exactly what "gradient breathing just
			// turns the LED off" was; filling it with eight copies of the custom colour is what made
			// neon and colour cycle look dead. Palette captured from the OEM software; see
			// X43LedEncoder.AllColorsPalette.
			for (int i = 0; i < 8; i++)
			{
				var (pr, pg, pb) = X43LedEncoder.AllColorsPalette[i];
				block1[24 + i * 3] = pr;
				block1[24 + i * 3 + 1] = pg;
				block1[24 + i * 3 + 2] = pb;
			}
		}
		else
		{
			for (int i = 0; i < 8; i++)
			{
				block1[24 + i * 3] = ledR;
				block1[24 + i * 3 + 1] = ledG;
				block1[24 + i * 3 + 2] = ledB;
			}
		}

		// Block 2 — full key matrix from the factory baseline + the caller's remaps (slots 1-4).
		// Macro-bound buttons get sequential slots (0, 1, 2, ...) in binding order — deterministic,
		// so the entry bytes here are final even though the actual flash upload happens later.
		var block2 = X43KeyMatrix.CreateDefault();
		var macroUploads = new List<(byte Slot, MacroDefinition Macro)>();
		if (config.Buttons is { Count: > 0 } bindings)
		{
			byte macroSlot = 0;
			foreach (var b in bindings)
			{
				var slot = X43KeyMatrix.ButtonToSlot(b.Button);
				if (slot < 1 || slot > 4) continue;

				byte[]? entry;
				if (b.Action == MouseButtonAction.Macro && b.Macro != null)
				{
					macroUploads.Add((macroSlot, b.Macro));
					entry = [macroSlot, 0x00, X43MacroEncoder.TypeByte(b.Macro.Mode), 0x05];
					macroSlot++;
				}
				else
				{
					entry = X43KeyMatrix.EncodeAction(b.Action);
				}
				if (entry == null) continue;
				Buffer.BlockCopy(entry, 0, block2, slot * 4, 4);
			}
		}

		// param_0 — report rate + active stage + LED effect, composed once so the stage and effect
		// bytes never clobber each other.
		var param0 = (byte[])currentParam0.Clone();
		if (config.ReportRateHz is { } hz)
			param0[0] = X43Settings.ReportRateByte(hz);
		if (config.ActiveStage is { } stageIndex)
			param0[1] = stageIndex;
		if (config.Led is not null)
		{
			param0[2] = protocolEffect; param0[3] = brightness; param0[4] = speed; param0[5] = colorSlot;
		}

		// Kept for the 0x0D/0x80 LED-drive command, which this service deliberately does NOT send
		// (see EasenMouseService.ApplyConfigurationAsync — the OEM sends neither and the sequence
		// applies the effect on its own). So nothing in production reads this today; only the
		// composer tests do. It stays as the composed value rather than being deleted so the drive
		// command has a correct input if it is ever needed again.
		//
		// Note it takes the RESOLVED brightness, so for colour cycle it is the OEM's byte, not the
		// slider — consistent with what param_0 actually carries.
		byte driveBrightness = 0;
		if (config.Led is { } drive)
			driveBrightness = (drive.Effect != 0xED && brightness > 0) ? brightness : (byte)0;

		return new Result(block0, block1, block2, param0, protocolEffect, colorSlot, driveBrightness,
			ledR, ledG, ledB, macroUploads);
	}
}
