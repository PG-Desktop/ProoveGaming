namespace ProoveGaming.Infrastructure.Classes;

// Encodes a macro into the TAIPAN (Telink) 200-byte macro-slot format (command 0x05):
//   one action per 3 bytes [Action_Type, Data_L, Data_H]; Action_Type low nibble = type,
//   bit7 = 1 Make (press) / 0 Break (release); slot ends at a 0x00 action (zero padding).
//   Mouse buttons are type 0x01 with a Data_L bitmask; keyboard keys are type 0x04 with the
//   HID usage in Data_L; a delay is its OWN 0x06 action ((Data_H<<8|Data_L) × 10 ms).
//
// Pure/testable like X43MacroEncoder — the byte layout is locked down by
// TaipanMacroEncoderTests. Source: docs/mouse/taipan/protocol.md §Macros.
public static class TaipanMacroEncoder
{
	// One device macro slot: 200 bytes, 20 slots on the device.
	public const int SlotLength = 200;
	public const int MaxSlots = 20;

	public const int ActionLength = 3;

	// Maximum recorder events per macro. Unlike BLAZE — where the delay rides inside the event's
	// attr byte — a TAIPAN delay costs a separate action, so the worst case (every event timed)
	// spends 2 actions per event: (200 / 3 = 66 actions, minus the end marker) / 2 = 32.
	public const int MaxEvents = (SlotLength / ActionLength - 1) / 2;

	public const int DelayUnitMs = 10;
	private const int MaxDelayUnits = 0xFFFF;

	private const byte ActionMouse = 0x01;
	private const byte ActionKeyboard = 0x04;
	private const byte ActionDelay = 0x06;
	private const byte MakeBit = 0x80;

	// Builds one 200-byte slot image: per event an optional delay action followed by the
	// make/break action. An event's DelayMs is the time SINCE THE PREVIOUS event (the recorder
	// times each step from the one before), so the delay must elapse BEFORE its action —
	// emitting it after shifts every gap onto the wrong key and plays inter-key pauses as
	// held-down keys (OS autorepeat then types repeats; seen on hardware as "123" → "1222333").
	// Events beyond MaxEvents are dropped (the recorder enforces the same ceiling; this guard
	// is defence in depth). The remaining bytes stay zero — the 0x00 end marker.
	public static byte[] Encode(MacroDefinition macro)
	{
		var slot = new byte[SlotLength];
		var pos = 0;
		var count = 0;

		foreach (var e in macro.Events)
		{
			if (count >= MaxEvents) break;
			count++;

			var units = (int)Math.Round(e.DelayMs / (double)DelayUnitMs, MidpointRounding.AwayFromZero);
			if (units > 0)
			{
				units = Math.Min(units, MaxDelayUnits);
				slot[pos++] = ActionDelay;
				slot[pos++] = (byte)(units & 0xFF);
				slot[pos++] = (byte)(units >> 8);
			}

			if (e.KeyCode is >= 0xF0 and <= 0xF4)
			{
				slot[pos++] = (byte)(ActionMouse | (e.IsPress ? MakeBit : 0));
				slot[pos++] = MouseBitmask(e.KeyCode);
			}
			else
			{
				slot[pos++] = (byte)(ActionKeyboard | (e.IsPress ? MakeBit : 0));
				slot[pos++] = e.KeyCode;
			}
			pos++; // Data_H stays 0 for mouse/keyboard actions
		}

		return slot;
	}

	// Recorder mouse codes (F0–F4) → the TAIPAN mouse-action bitmask.
	private static byte MouseBitmask(byte keyCode) => keyCode switch
	{
		0xF0 => 0x01, // Left
		0xF1 => 0x02, // Right
		0xF2 => 0x04, // Middle
		0xF3 => 0x08, // B4 / Back
		0xF4 => 0x10, // B5 / Forward
		_ => 0x00,
	};
}
