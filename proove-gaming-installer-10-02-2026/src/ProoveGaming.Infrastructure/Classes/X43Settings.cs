namespace ProoveGaming.Infrastructure.Classes;

// Pure encoders for BLAZE / X43 device *settings* (report rate, …). Kept out of the device
// service so the non-obvious wire values can be unit-tested. See docs/mouse/blaze.
public static class X43Settings
{
	// param_0 byte 0 = report rate encoded as the raw polling period in ms (1/2/4/8), NOT an
	// index — confirmed from a VS750 USB capture (it sent 0x04→0x02→0x01 as the user cycled
	// 250→500→1000 Hz). Unknown rates fall back to 1000 Hz (0x01).
	public static byte ReportRateByte(int hz) => hz switch
	{
		1000 => 0x01, // 1 ms
		500 => 0x02,  // 2 ms
		250 => 0x04,  // 4 ms
		125 => 0x08,  // 8 ms
		_ => 0x01,
	};

	// Inverse of ReportRateByte: decodes the raw period byte the device reports back (param_0
	// byte 0 / the 0x05 state report's byte 2) into Hz. 0 = unknown (skip), so callers can tell
	// an absent/zero field from a real rate.
	public static int HzFromReportRateByte(byte raw) => raw switch
	{
		0x01 => 1000,
		0x02 => 500,
		0x04 => 250,
		0x08 => 125,
		_ => 0,
	};
}
