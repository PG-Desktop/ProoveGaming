namespace ProoveGaming.Infrastructure.Classes;

// Maps the App's RGB "effect" byte onto the device protocol's (effect, colour-slot) pair.
// The BLAZE / X43 firmware has fewer native effects than the UI exposes, so several UI
// effects collapse onto the same protocol effect distinguished only by the colour slot:
// slot 0x00 = a single custom colour, slot 0x07 = "all colours" (the rainbow/cycle behaviour).
//
// Pure/testable — this mapping was reverse-engineered from captures (RainbowBreathing is just
// Breathing with colour slot 7; see the gradient-color capture note in docs/mouse/blaze).
public static class X43LedEncoder
{
	// Colour slot meaning custom single colour vs. the firmware's built-in all-colours sweep.
	public const byte ColorSlotCustom = 0x00;
	public const byte ColorSlotAllColors = 0x07;

	// True when the firmware sweeps the block 1 palette instead of showing one colour.
	//
	// Slot 7 sweeps it on any effect. Neon (0x03) and colour cycle (0x04) sweep it too, even though
	// the OEM sends them on slot 0 — cap-06.08.26-for-blaze-neon-color-cycle.pcapng shows the OEM
	// writing the identical palette for both. We used to fill all eight entries with the single
	// custom colour on slot 0, which made a "cycle" through eight copies of the same colour and read
	// on hardware as the effect not working at all.
	public static bool SweepsPalette(byte protocolEffect, byte colorSlot)
		=> colorSlot == ColorSlotAllColors || protocolEffect is 0x03 or 0x04;

	// Byte 3 of param_0 is nominally brightness, but for the COLOUR CYCLE effect (protocol 0x04)
	// it is not a level the firmware honours — it decides whether the effect renders at all.
	//
	// Evidence: the OEM sends 0xBD for it in cap-06.08.26-for-blaze-neon-color-cycle.pcapng, and
	// driving the same effect at 0xFC through our own path produced no cycle while the byte-for-byte
	// replay at 0xBD did. The App maps its brightness slider linearly onto 0-255, so at anything near
	// full it sends 0xFF and the cycle degenerates — on hardware it becomes indistinguishable from
	// neon, which is exactly how this surfaced (2026-08-17).
	//
	// Only 0x04 is pinned. Neon (0x03) uses 0xBD in the same capture but renders correctly at the
	// slider's values, so its brightness stays user-controlled rather than being clamped on a
	// symmetry argument the hardware has not been asked about.
	public const byte ColorCycleBrightness = 0xBD;

	public static byte ResolveBrightness(byte protocolEffect, byte requested) =>
		protocolEffect == 0x04 ? ColorCycleBrightness : requested;

	// The palette the firmware sweeps through in slot 7 ("all colours").
	//
	// Slot 7 does NOT mean "the firmware supplies its own colours" — it means "cycle the eight
	// colours in the block 1 palette". Leaving that palette zeroed therefore cycles eight blacks,
	// which is why the gradient/rainbow breathing effect looked like the LED simply switching off.
	//
	// Taken verbatim from the OEM software: capture cap-06.08.26-for-blaze.pcapng, block 1 bytes
	// 24-47, recorded while selecting the gradient swatch. Byte-for-byte the same eight slots this
	// encoder's custom-colour path writes.
	public static readonly (byte R, byte G, byte B)[] AllColorsPalette =
	[
		(0x00, 0xB7, 0xFF), // light blue
		(0x00, 0x00, 0xFF), // blue
		(0x00, 0xFF, 0x00), // green
		(0xFF, 0xFF, 0x00), // yellow
		(0x00, 0xFF, 0xFF), // cyan
		(0xFF, 0x00, 0xFF), // magenta
		(0xFF, 0x80, 0x00), // orange
		(0xFF, 0xFF, 0xFF), // white
	];

	public static (byte protocolEffect, byte colorSlot) MapEffect(byte appEffect) => appEffect switch
	{
		0x01 => (0x02, ColorSlotCustom),    // Breathing        -> proto 0x02, custom colour
		0x00 => (0x02, ColorSlotAllColors), // RainbowBreathing -> proto 0x02, all colours
		0x02 => (0x03, ColorSlotCustom),    // Neon             -> proto 0x03
		// Wave is what this effect is CALLED on a mouse — on BLAZE's single LED protocol 0x04 is a
		// travelling gradient, not a cycle of solid colours, and the UI presents it accordingly. The
		// App's own byte for Wave is 0x05.
		0x05 => (0x04, ColorSlotCustom),    // Wave             -> proto 0x04, slot 0 (see below)
		// The same protocol effect under its old name. Mice no longer offer "ColorCycle" (it is
		// keyboard-only since 2026-08-17), but RGB state persisted before then still names it, and a
		// saved effect must keep lighting the mouse the way it did. Keyboards never reach this
		// encoder at all — they have their own services.
		0x03 => (0x04, ColorSlotCustom),    // ColorCycle       -> proto 0x04, slot 0 (see below)
		0xED => (0x02, ColorSlotCustom),    // Off              -> proto 0x02, brightness=0 persists
		_    => (0x02, ColorSlotCustom),    // default          -> Breathing custom colour
	};
}
