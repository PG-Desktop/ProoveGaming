using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── BLAZE — diff OUR composed LED frames against the OEM's captured ones ──────────────────────
//
// Every structural divergence between our write and the vendor's was tested on hardware and came
// back innocent (the extra 0x0D/0x80 drive, the blocks-before-param_0 ordering, the 0x07 status
// byte). So whatever still differs is in the frame CONTENT, and that can be compared offline.
//
// Reads nothing and writes nothing — pure composition, safe to run with the DeviceHost up.
internal static class BlazeFrameDiffProbe
{
	// Vendor param_0 payloads, bytes 0-5, from the two captures.
	private static readonly (string Name, byte AppEffect, string Vendor)[] Cases =
	[
		("Gradient breathing", 0x00, "01 07 02 FC 04 07"), // cap-06.08.26-for-blaze
		("Neon", 0x02, "01 07 03 BD 04 00"),               // cap-06.08.26-for-blaze-neon-color-cycle
		("Colour cycle", 0x03, "01 07 04 BD 04 00"),       // same capture
		("Breathing solid", 0x01, "(not captured)"),
	];

	// Vendor block 1 bytes 24-47 — byte-identical in every captured frame, both captures.
	private static readonly byte[] VendorPalette =
	[
		0x00, 0xB7, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 0x00,
		0x00, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0x80, 0x00, 0xFF, 0xFF, 0xFF,
	];

	public static void Run()
	{
		Console.WriteLine("BLAZE — our composed frames vs the OEM capture");
		Console.WriteLine();

		// Baseline the composer starts from: the device's current param_0 / block 0. Using the
		// vendor's own captured values so any difference below is ours, not the baseline's.
		var baselineParam0 = new byte[64];
		Fill(baselineParam0, "01 07 02 FC 04 07");
		var baselineBlock0 = new byte[64];

		foreach (var (name, appEffect, vendor) in Cases)
		{
			var (protocolEffect, colorSlot) = X43LedEncoder.MapEffect(appEffect);
			var brightness = appEffect is 0x02 or 0x03 ? (byte)0xBD : (byte)0xFC;

			var config = new DeviceConfiguration
			{
				Led = new LedConfig(0x00, 0x00, 0xFF, appEffect, brightness, Speed: 4),
			};

			var result = X43ConfigComposer.Compose(
				config, baselineBlock0, baselineParam0,
				cachedColorSlot: colorSlot, cachedLedR: 0x00, cachedLedG: 0x00, cachedLedB: 0xFF);

			var ours = Hex(result.Param0.AsSpan(0, 6));
			var match = vendor.StartsWith('(') ? "n/a" : (ours == vendor ? "MATCH" : "DIFFERS");

			Console.WriteLine($"{name}");
			Console.WriteLine($"  param_0  ours   {ours}");
			Console.WriteLine($"  param_0  vendor {vendor}   -> {match}");

			var palette = result.Block1.AsSpan(24, 24);
			var sweeps = X43LedEncoder.SweepsPalette(protocolEffect, colorSlot);
			var paletteMatch = palette.SequenceEqual(VendorPalette) ? "MATCH" : "DIFFERS";
			Console.WriteLine($"  block1[24..47] sweeps={sweeps} -> {paletteMatch}");
			Console.WriteLine($"    ours   {Hex(palette)}");
			if (paletteMatch == "DIFFERS")
				Console.WriteLine($"    vendor {Hex(VendorPalette)}");

			// The OEM leaves bytes 0-23 and 48-63 zeroed in every captured block 1.
			var head = result.Block1.AsSpan(0, 24).ToArray().Any(b => b != 0);
			var tail = result.Block1.AsSpan(48, 16).ToArray().Any(b => b != 0);
			if (head || tail)
				Console.WriteLine($"    !! non-zero outside the palette: head={head} tail={tail}");

			Console.WriteLine();
		}
	}

	private static void Fill(byte[] target, string hex)
	{
		var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(x => Convert.ToByte(x, 16)).ToArray();
		Buffer.BlockCopy(bytes, 0, target, 0, bytes.Length);
	}

	private static string Hex(ReadOnlySpan<byte> bytes)
	{
		var parts = new string[bytes.Length];
		for (var i = 0; i < bytes.Length; i++) parts[i] = bytes[i].ToString("X2");
		return string.Join(' ', parts);
	}
}
