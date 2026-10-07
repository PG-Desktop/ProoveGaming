namespace ProoveGaming.Infrastructure.Classes;

// The BLAZE / X43 factory key matrix (param_1 block 2): 16 slots × 4 bytes = 64 bytes,
// each slot [Code, b1, b2, Mode]. This is the capture-confirmed baseline that every button
// write rebuilds from — a remap only overrides the remappable slots (1–4) and must never
// disturb the FIXED buttons: DPI+ (slot 5), report-rate (slot 6), LED effect (slot 7) and
// the ACPI slots (14/15). If any of those bytes drift, those hardware buttons break.
//
// Kept as a pure, side-effect-free builder so it can be snapshot-tested byte-for-byte against
// docs/mouse/blaze/key-matrix.md. See X43KeyMatrixTests.
public static class X43KeyMatrix
{
	// Total matrix length in bytes (16 slots × 4).
	public const int Length = 64;

	// Byte offset of a slot's first byte.
	public static int SlotOffset(int slot) => slot * 4;

	// Builds a fresh 64-byte factory key matrix. Byte-exact with docs/mouse/blaze/key-matrix.md.
	public static byte[] CreateDefault()
	{
		var m = new byte[Length];
		m[0] = 0xF0; m[3] = 0x02;   // KEY0 Left click
		m[4] = 0xF1; m[7] = 0x02;   // KEY1 Right click
		m[8] = 0xF2; m[11] = 0x02;  // KEY2 Wheel click
		m[12] = 0xF4; m[15] = 0x02; // KEY3 Forward (B5)
		m[16] = 0xF3; m[19] = 0x02; // KEY4 Back (B4)
		m[20] = 0x03; m[23] = 0x0B; // KEY5 DPI+ loop      (mode 0x0B) — FIXED
		m[24] = 0x03; m[27] = 0x06; // KEY6 report-rate loop            — FIXED
		// KEY7 = the physical LED slide switch. Mode 0x0E, deliberately NOT the OEM's 0x0A.
		//
		// 0x0A (code 0x03, "LED effect cycle") is what VS750 writes — byte-exact in the 2026-07-10
		// capture — and it hands the LED to the switch, which then cycles it in firmware. The cost
		// only shows on RECONNECT: the firmware takes the effect back and the colour the app set is
		// simply off. That is the long-standing "RGB does not survive a replug" behaviour, and it
		// turns out never to have been a firmware limitation — it was this slot.
		//
		// Mode 0x0E came from the vendor (2026-08-17) and is NOT in the protocol spec they supplied
		// (which documents 01/02/03/04/05/0A/0B/0C/F0). Confirmed on hardware the same day: with
		// 0x0E the colour survives a reconnect; with 0x0A it does not.
		m[28] = 0x00; m[31] = 0x0E; // KEY7 LED slide switch — leaves the LED under software control
		// KEY8-13: null mouse buttons (mode 0x02, code 0x00)
		m[35] = 0x02; m[39] = 0x02; m[43] = 0x02; m[47] = 0x02; m[51] = 0x02; m[55] = 0x02;
		// KEY14/15: ACPI (mode 0x04) — FIXED
		m[57] = 0x01; m[59] = 0x04;
		m[61] = 0x02; m[63] = 0x04;
		return m;
	}

	// Encodes a button action into its 4-byte key-matrix slot entry [Code, b1, b2, Mode].
	// Returns null for actions that are NOT a plain matrix entry: Unspecified (no binding)
	// and Macro (uploaded + bound separately, mode 0x05). Mode byte by family: standard
	// mouse = 0x02, multimedia = 0x03, Win+E/Win+D system combos = 0x01.
	// Source: docs/x43_protocol_904a4f8.md §V.
	public static byte[]? EncodeAction(MouseButtonAction action) => action switch
	{
		// Standard mouse buttons (mode 0x02)
		MouseButtonAction.LeftClick   => [0xF0, 0x00, 0x00, 0x02],
		MouseButtonAction.RightClick  => [0xF1, 0x00, 0x00, 0x02],
		MouseButtonAction.WheelClick  => [0xF2, 0x00, 0x00, 0x02],
		MouseButtonAction.Forward     => [0xF4, 0x00, 0x00, 0x02], // B5
		MouseButtonAction.Back        => [0xF3, 0x00, 0x00, 0x02], // B4
		// Recommended — multimedia keys (mode 0x03)
		MouseButtonAction.PlayPause     => [0xCD, 0x00, 0x00, 0x03],
		MouseButtonAction.NextTrack     => [0xB5, 0x00, 0x00, 0x03],
		MouseButtonAction.PreviousTrack => [0xB6, 0x00, 0x00, 0x03],
		MouseButtonAction.Stop          => [0xB7, 0x00, 0x00, 0x03],
		MouseButtonAction.Mute          => [0xE2, 0x00, 0x00, 0x03],
		MouseButtonAction.VolumeUp      => [0xE9, 0x00, 0x00, 0x03],
		MouseButtonAction.VolumeDown    => [0xEA, 0x00, 0x00, 0x03],
		MouseButtonAction.MediaPlayer   => [0x83, 0x02, 0x00, 0x03], // Media Select
		MouseButtonAction.WwwHome       => [0x23, 0x02, 0x00, 0x03],
		// System. My PC and Desktop use keyboard combos (mode 0x01) rather than consumer
		// application-launch usages (AL Local Machine Browser etc.), which this firmware
		// does not honour — the OS ignores them. Win+E = File Explorer/This PC, Win+D = show
		// desktop. Search stays a consumer usage (WWW Search 0x221 is a declared range).
		MouseButtonAction.MyComputer  => [0x08, 0xE3, 0x00, 0x01], // E + Left GUI (Win+E)
		MouseButtonAction.Desktop     => [0x07, 0xE3, 0x00, 0x01], // D + Left GUI (Win+D)
		MouseButtonAction.WwwSearch   => [0x21, 0x02, 0x00, 0x03],
		_ => null,
	};

	// Maps a device-agnostic button to THIS unit's key-matrix slot index. Left=0 (fixed primary);
	// only slots 1–4 are writable, and the DPI/rate/LED/ACPI buttons are not exposed as remappable
	// MouseButtons. Returns -1 for a button this device doesn't expose (skip it — don't write).
	public static int ButtonToSlot(MouseButton button) => button switch
	{
		MouseButton.Left => 0,
		MouseButton.Right => 1,
		MouseButton.Wheel => 2,
		MouseButton.Forward => 3,
		MouseButton.Back => 4,
		_ => -1,
	};
}
