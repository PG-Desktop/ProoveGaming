namespace ProoveGaming.Infrastructure.Classes;

// Parses the PG Cosmo headset's vendor status payload (HID report 0xBC, usage page 0xFF02).
//
// How this is obtained matters: the dongle pushes NOTHING on 0xBC — a full guided capture across
// mic mute, power off/on and volume produced zero frames. The payload is only reachable by a
// GET_REPORT control-transfer READ (HidD_GetInputReport). No host->device write is involved.
//
// Payload window, measured 2026-08-05 (32-byte buffer; everything outside offsets 16-23 is zero):
//
//   offset 16   0x01    constant
//   offset 17   0x57    constant  — NOT the battery percentage, see below
//   offset 18   0x01    constant
//   offset 19   0x03 = headset linked / ON,  0x0F = not linked / OFF
//   offset 20   0xB4 \  battery, millivolts, little-endian  (0x0EB4 = 3764 mV)
//   offset 21   0x0E /
//   offset 22   0x01    constant
//   offset 23   0x06    constant
//
// ── Byte 19 is the link state ────────────────────────────────────────────────────────────────
// It was the ONLY offset that moved during a guided power-off / power-on run, and it moved exactly
// with the switch. It flickered 0x0F -> 0x03 -> 0x0F once while off (the dongle retrying its link),
// so only 0x03 counts as on and any other value fails safe to off.
//
// ── Byte 17 was surfaced as the percentage, and is now DISPROVEN ─────────────────────────────
// From 2026-08-05 this parser published byte 17 (0x57 = 87) as the battery percentage. It was
// shipped as an explicit bet, on the reasoning that "when a device publishes a percentage, that is
// the number to trust", with the caveat that it had never been seen to change and had to be
// withdrawn if it never tracked a real discharge.
//
// It never did. Measured 2026-09-03, the deciding evidence the earlier work could not get: with the
// SAME headset reporting 40 % over Bluetooth (its own GATT battery service), report 0xBC still read
// byte 17 = 0x57 = 87. A device cannot be 87 % and 40 % at once, so byte 17 is a constant and the
// App was showing a fabricated level over the dongle. That is worse than showing nothing, because
// it looks authoritative.
//
// ── The millivolts are ALSO a constant — report 0xBC carries no battery ──────────────────────
// Bytes 20-21 read 3764 mV, which is a plausible single-cell Li-ion voltage and sat at ~40 % on a
// standard curve — matching the headset's own Bluetooth reading at that moment. On 2026-09-03 that
// was briefly shipped as the derived percentage. It was wrong, and was withdrawn the same day.
//
// The deciding measurement, which four hours on a desk could never produce: the headset discharged
// from 40 % to 30 % (its own Bluetooth report), and bytes 20-21 stayed at exactly 0x0EB4. Not a
// single bit moved anywhere in the window — 01 57 01 03 B4 0E 01 06, byte-identical before and
// after a real ten-point discharge.
//
// So BOTH candidate fields are constants:
//   * byte 17  (0x57 = 87)   — disproven by 87 vs a real 40 %
//   * bytes 20-21 (3764 mV)  — disproven by no movement across 40 % -> 30 %
//
// The lesson this file has now taught twice: a value that is individually plausible, sits in a
// vendor status report, and never changes is NOT evidence of a stable reading. Only a correlated
// change against an independent source counts. Both wrong answers here looked convincing, and both
// reached users as a confident, fabricated level.
//
// Battery over the dongle therefore does not exist today. Cosmo's `Dongle2_4G` transport carries
// `SupportsBattery: false` so the indicator is hidden rather than filled with a guess; the headset
// still reports a real level over BLUETOOTH (GATT), which is where the 40 % and 30 % above came
// from. Reviving this needs the vendor: the layout of 0xBC, or a battery field added to it.
//
// BatteryMillivolts is kept as a parsed field for diagnostics only — it is a constant, not a level,
// and must never be converted to a percentage again without a correlated two-point measurement.
public readonly record struct CosmoStateReport(bool IsHeadsetOn, int? BatteryPercent, int BatteryMillivolts, byte[] RawWindow)
{
	internal const int OffsetWindowStart = 16;
	// Kept as a named offset even though it is no longer read: it names what byte 17 was believed
	// to be, so the next person diffing a capture is not tempted to re-adopt it.
	internal const int OffsetDisprovenPercent = 17;
	internal const int OffsetLinkState = 19;
	internal const int OffsetMillivoltsLow = 20;
	internal const int OffsetMillivoltsHigh = 21;
	internal const int OffsetWindowEnd = 23;

	// The only value of byte 19 seen while the headset was switched on.
	internal const byte LinkStateOn = 0x03;

	internal const int MinimumLength = OffsetWindowEnd + 1;

	public static CosmoStateReport? TryParse(ReadOnlySpan<byte> payload)
	{
		if (payload.Length < MinimumLength)
			return null;

		var isOn = payload[OffsetLinkState] == LinkStateOn;
		var window = payload[OffsetWindowStart..(OffsetWindowEnd + 1)].ToArray();
		var millivolts = payload[OffsetMillivoltsLow] | (payload[OffsetMillivoltsHigh] << 8);

		// BatteryPercent is ALWAYS null. Report 0xBC carries no battery — see the header. The field
		// stays on the record because DeviceService reads it and answers Success = false, so a
		// fabricated level cannot escape even if the catalog flag is ever lost.
		return new CosmoStateReport(isOn, BatteryPercent: null, millivolts, window);
	}
}
