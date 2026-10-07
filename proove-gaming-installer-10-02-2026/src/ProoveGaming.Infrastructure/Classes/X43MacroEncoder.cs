namespace ProoveGaming.Infrastructure.Classes;

// Encodes a macro into the BLAZE / X43 128-byte device format:
//   [repeat_lo, repeat_hi] (little-endian) then [attr, code] events then [00, 00], zero-padded.
//   attr bit 7 = press(0)/release(1); low 7 bits = delay in DelayUnitMs units.
//   codes are USB HID keyboard usages or mouse buttons (F0–F4).
//
// Pure/testable — the encoding had several hardware-driven bugs (delay scale, min-1, cap), so
// the byte layout is locked down by X43MacroEncoderTests. See docs/mouse/blaze/macros.md.
public static class X43MacroEncoder
{
	// Device flash buffer size for one macro.
	public const int BufferLength = 128;

	// Maximum make/break events one macro can hold: the 2-byte repeat header and the 2-byte
	// 00 00 terminator leave 124 bytes = 62 two-byte [attr, code] events. Events beyond this
	// are silently dropped by the device, so the recorder enforces the same ceiling. (docs
	// say "~63"; 62 is the exact count that survives the round-trip with the terminator.)
	public const int MaxEvents = (BufferLength - 2 - 2) / 2;

	// Firmware delay unit is 10 ms, but its top-of-range values misbehave (a step at the max
	// 127 units played back as ~63 s), so delays are capped at MaxDelayUnits (≈1 s). Minimum
	// 1 unit so a 0-delay make/break isn't played too fast for the target to register.
	public const int DelayUnitMs = 10;
	public const int MaxDelayUnits = 100;

	public static byte[] Encode(MacroDefinition macro)
	{
		var buf = new byte[BufferLength];
		var repeat = Math.Clamp(macro.RepeatCount <= 0 ? 1 : macro.RepeatCount, 1, 65535);
		buf[0] = (byte)(repeat & 0xFF);
		buf[1] = (byte)(repeat >> 8);

		var pos = 2;
		var count = 0;
		foreach (var e in macro.Events)
		{
			if (count >= MaxEvents) break; // buffer full; leaves room for the 00 00 terminator
			count++;
			var units = (int)Math.Round(e.DelayMs / (double)DelayUnitMs, MidpointRounding.AwayFromZero);
			var delay = (byte)Math.Clamp(units, 1, MaxDelayUnits);
			buf[pos++] = (byte)((e.IsPress ? 0x00 : 0x80) | delay);
			buf[pos++] = e.KeyCode;
		}
		// Remaining bytes (incl. the 00 00 terminator) are already zero.
		return buf;
	}

	// Key-matrix macro "type" byte (chosen at bind time, mode 0x05). The only hardware-confirmed
	// macro bound with 0x01 and played correctly; 0x00 ("play N times; any button press stops it")
	// self-cancels on the trigger press. 0x02 = repeat while the button is held.
	public static byte TypeByte(MacroRepeatMode mode) => mode switch
	{
		MacroRepeatMode.PlayCount => 0x01,
		MacroRepeatMode.LoopForever => 0x01,
		MacroRepeatMode.WhileHeld => 0x02,
		_ => 0x01,
	};
}
