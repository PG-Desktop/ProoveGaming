namespace ProoveGaming.Infrastructure.Classes;

// Pure encoder for the TAIPAN (Telink) button map table (CMD 0x03 / 0x43), the analogue of
// X43KeyMatrix for the Easen mice. Kept side-effect-free so it can be unit-tested byte-for-byte
// against docs/mouse/taipan/protocol.md without a device. TelinkMouseService packetises what
// BuildTable produces and sends it via TaipanFrame. See TaipanButtonMapTests.
//
// Table layout (table-relative offsets):
//   0x0000  Max button-event count, low byte
//   0x0001  Max button-event count, high byte
//   0x0002  Button 1 (4 bytes) … Button N at 0x0002 + (N-1)*4 … Button 25 at 0x0062
// Each entry is [ BtFuncNum, BtModify, BtAlpha1, BtAlpha2 ]; BtFuncNum bit7 = active,
// function = BtFuncNum & 0x7F.
public static class TaipanButtonMap
{
	public const int HeaderLength = 2;
	// 25 physical slots exist; the factory map populates 1–7 and the wheel pair 23/24.
	public const int MaxButtons = 25;
	public const int EntryLength = 4;
	public const int TableLength = HeaderLength + MaxButtons * EntryLength; // 102

	// Slot 23 carries wheel DOWN and slot 24 wheel UP — the reverse of the vendor address table,
	// which the protocol doc records as a convention ("Buttons 23 and 24 are reserved by convention
	// for Wheel Up / Wheel Down") rather than a hardware-verified fact. Confirmed by hand on
	// 2026-08-12: writing the documented order inverts scroll direction, writing this order
	// restores it.
	//
	// This matters far beyond the wheel itself, because CreateDefault populates both slots and
	// BuildTable starts from CreateDefault — so EVERY button write rewrites the wheel pair, even a
	// remap of right-click. With the slots reversed, any binding change silently inverted the
	// user's scrolling.
	public const int WheelUpIndex = 24;
	public const int WheelDownIndex = 23;

	// Factory "max button-event count" from the vendor's worked full-write example.
	public const byte MaxButtonEventCount = 0x0A;

	// Commit sequence (see protocol.md): ValidBankID 0x1A written to table offset 0x007F, then a
	// 0xD3 packet triggers Update_Para(). Exposed for TelinkMouseService.
	public const byte ButtonCommand = 0x03;
	public const byte ValidBankId = 0x1A;
	public const int ValidBankIdOffset = 0x007F;

	// Macro table (command 0x05) commits its ValidBankID at 0x0FFF instead.
	public const byte MacroCommand = 0x05;
	public const int MacroValidBankIdOffset = 0x0FFF;

	// BtFuncNum values (bit7 set = active).
	private const byte FnLeft = 0x81;
	private const byte FnRight = 0x82;
	private const byte FnMiddle = 0x83;
	private const byte FnBack = 0x84;      // B4
	private const byte FnForward = 0x85;   // B5
	private const byte FnDpiCycle = 0x88;
	private const byte FnWheelUp = 0x8B;
	private const byte FnWheelDown = 0x8C;
	private const byte FnConsumer = 0x8E;  // media/consumer key: BtAlpha1/2 = usage LE
	private const byte FnGeneralKey = 0x8F; // combo: BtModify = HID modifier, BtAlpha1 = key code
	private const byte FnMacro = 0x91;     // BtModify: bit7 infinite, bit6 interrupt, bits0-4 slot
	private const byte FnPollRateCycle = 0x97;

	private const byte MacroInfiniteBit = 0x80;
	private const byte MacroInterruptBit = 0x40;

	// HID modifier bit for the Win key (Left GUI), used by the Win+E / Win+D system combos.
	private const byte ModLeftGui = 0x08;
	// HID keyboard usage codes for the combo actions.
	private const byte KeyE = 0x08;
	private const byte KeyD = 0x07;

	// Maps a device-agnostic button to THIS device's table index (1-based). The five shared
	// physical buttons only; returns -1 for a button TAIPAN doesn't expose as remappable (skip it).
	public static int ButtonToIndex(MouseButton button) => button switch
	{
		MouseButton.Left => 1,
		MouseButton.Right => 2,
		MouseButton.Wheel => 3,
		MouseButton.Forward => 4,
		MouseButton.Back => 5,
		_ => -1,
	};

	// Encodes a button action into its 4-byte entry [BtFuncNum, BtModify, BtAlpha1, BtAlpha2].
	// Returns null for actions that are NOT a plain table entry: Unspecified (revert to factory
	// default for that slot) and Macro (bound with a device macro-slot at write time, function
	// 0x91). Consumer usages are little-endian into BtAlpha1(low)/BtAlpha2(high).
	// Source: docs/mouse/taipan/protocol.md §Button map.
	public static byte[]? EncodeAction(MouseButtonAction action) => action switch
	{
		// Standard mouse buttons.
		MouseButtonAction.LeftClick => [FnLeft, 0x00, 0x00, 0x00],
		MouseButtonAction.RightClick => [FnRight, 0x00, 0x00, 0x00],
		MouseButtonAction.WheelClick => [FnMiddle, 0x00, 0x00, 0x00],
		MouseButtonAction.Forward => [FnForward, 0x00, 0x00, 0x00], // B5
		MouseButtonAction.Back => [FnBack, 0x00, 0x00, 0x00],       // B4
		// Recommended — media / consumer keys (function 0x8E, usage LE).
		MouseButtonAction.PlayPause => Consumer(0x00CD),
		MouseButtonAction.NextTrack => Consumer(0x00B5),
		MouseButtonAction.PreviousTrack => Consumer(0x00B6),
		MouseButtonAction.Stop => Consumer(0x00B7),
		MouseButtonAction.Mute => Consumer(0x00E2),
		MouseButtonAction.VolumeUp => Consumer(0x00E9),
		MouseButtonAction.VolumeDown => Consumer(0x00EA),
		MouseButtonAction.MediaPlayer => Consumer(0x0183),
		MouseButtonAction.WwwHome => Consumer(0x0223), // AC Home
		MouseButtonAction.WwwSearch => Consumer(0x0221), // AC Search
		// System. My PC and Desktop are Win-key combos (function 0x8F) rather than consumer
		// application-launch usages, which the firmware/OS pairing does not honour reliably —
		// mirrors the same choice made for BLAZE. Win+E = This PC, Win+D = show desktop.
		MouseButtonAction.MyComputer => [FnGeneralKey, ModLeftGui, KeyE, 0x00],
		MouseButtonAction.Desktop => [FnGeneralKey, ModLeftGui, KeyD, 0x00],
		_ => null,
	};

	// Builds the full 102-byte button table from the factory default map with the requested
	// bindings overlaid. Because the write's first (0xD1) packet erases the whole flash sector,
	// every retained byte — the wheel pair and Buttons 6/7 included — must be re-sent, so this
	// always emits the complete table, not just the changed slots.
	public static byte[] BuildTable(IReadOnlyList<ButtonBinding> bindings)
	{
		var table = CreateDefault();
		if (bindings != null)
		{
			foreach (var b in bindings)
			{
				int index = ButtonToIndex(b.Button);
				if (index < 1 || index > MaxButtons) continue;

				var entry = EncodeAction(b.Action);
				if (entry == null) continue; // Unspecified / Macro → keep factory default

				System.Buffer.BlockCopy(entry, 0, table, EntryOffset(index), EntryLength);
			}
		}
		return table;
	}

	// Encodes a macro-bound button entry pointing at a device macro slot (0-19). Repeat modes:
	// PlayCount plays BtAlpha1 times; LoopForever runs infinite + interruptible (press again to
	// stop). TAIPAN documents no hold-to-repeat, so WhileHeld maps to the same toggle — the
	// closest firmware behaviour (called out in the plan's QA notes).
	public static byte[] EncodeMacroEntry(int slot, MacroRepeatMode mode, int repeatCount)
	{
		if (slot is < 0 or >= TaipanMacroEncoder.MaxSlots)
			throw new ArgumentOutOfRangeException(nameof(slot), slot, $"TAIPAN has {TaipanMacroEncoder.MaxSlots} macro slots");

		return mode switch
		{
			MacroRepeatMode.PlayCount => [FnMacro, (byte)slot, (byte)Math.Clamp(repeatCount, 1, 255), 0x00],
			_ => [FnMacro, (byte)(slot | MacroInfiniteBit | MacroInterruptBit), 0x00, 0x00],
		};
	}

	// Table offset of Button N's first byte (N is 1-based).
	public static int EntryOffset(int index) => HeaderLength + (index - 1) * EntryLength;

	// Fresh factory default table (Profile 1), byte-exact with docs/mouse/taipan/protocol.md.
	public static byte[] CreateDefault()
	{
		var t = new byte[TableLength];
		t[0] = MaxButtonEventCount; // 0x0A
		t[1] = 0x00;
		SetEntry(t, 1, FnLeft, 0x00, 0x00, 0x00);
		SetEntry(t, 2, FnRight, 0x00, 0x00, 0x00);
		SetEntry(t, 3, FnMiddle, 0x00, 0x00, 0x00);
		SetEntry(t, 4, FnForward, 0x00, 0x00, 0x00); // B5
		SetEntry(t, 5, FnBack, 0x00, 0x00, 0x00);    // B4
		SetEntry(t, 6, FnDpiCycle, 0x00, 0x00, 0x00);
		SetEntry(t, 7, FnPollRateCycle, 0x01, 0x00, 0x00); // BtModify 0x01 → cycle 1/2/4/8
		SetEntry(t, WheelUpIndex, FnWheelUp, 0x00, 0x00, 0x00);
		SetEntry(t, WheelDownIndex, FnWheelDown, 0x00, 0x00, 0x00);
		return t;
	}

	private static void SetEntry(byte[] table, int index, byte fn, byte modify, byte alpha1, byte alpha2)
	{
		int o = EntryOffset(index);
		table[o] = fn;
		table[o + 1] = modify;
		table[o + 2] = alpha1;
		table[o + 3] = alpha2;
	}

	private static byte[] Consumer(ushort usage) =>
		[FnConsumer, 0x00, (byte)(usage & 0xFF), (byte)(usage >> 8)];
}
