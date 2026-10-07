using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── BLAZE — replay the OEM software's exact gradient-breathing write ──────────────────────────
//
// Why this exists: our own apply makes NEON light up but leaves BREATHING (protocol 0x02) dark,
// in both its solid and gradient forms. Since one effect works and another does not through the
// identical code path, the write mechanism is fine and something in the frame content is wrong.
//
// Diffing our frames against cap-06.08.26-for-blaze.pcapng leaves exactly three divergences:
//
//   1. param_1 status byte — vendor sends 0x02 ("block 1 changed"), we send 0x07 ("all blocks").
//   2. We additionally send 0x0D <effect> <brightness> <slot> then 0x80 to drive/commit the LED.
//      The OEM sequence contains neither.
//   3. Our block 0 / block 2 contents are our own defaults, not the device's current tables.
//
// This probe removes the guesswork by sending the OEM's six frames byte-for-byte. It is the
// safest possible write on this device: not a guess at a command, but a literal replay of what
// the vendor's own software did to this exact model.
//
//   - If the LED breathes through the gradient -> one of the three divergences is the bug, and
//     the next step is to bisect them.
//   - If it stays dark -> the effect needs something outside this captured window, and the
//     capture needs to be re-taken over a longer period.
//
// Run with ProoveGamingDeviceHost STOPPED.
internal static class BlazeVendorReplayProbe
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA245;

	// Verbatim from cap-06.08.26-for-blaze.pcapng, recorded while the OEM software's gradient
	// swatch was selected. Frame numbers kept so the capture can be re-read against this.
	private const string Param0Command = "02 00 00 00 00 00 01 00";            // frame 5049, SET_FEATURE
	// param_0 bytes 0-5 per effect, verbatim from the captures; the remaining 58 bytes are zero.
	//   gradient -> cap-06.08.26-for-blaze
	//   neon / colour cycle -> cap-06.08.26-for-blaze-neon-color-cycle
	private const string Param0Gradient = "01 07 02 FC 04 07";   // frame 5053
	private const string Param0Neon = "01 07 03 BD 04 00";       // frame 4517
	private const string Param0ColorCycle = "01 07 04 BD 04 00"; // frame 6347

	private const string Param1Command = "03 00 00 00 00 02 03 00";            // frame 5063, SET_FEATURE
	private const string Block0 =                                              // frame 5067
		"00 00 00 00 07 00 00 00 00 20 03 B0 04 60 09 10 0E C0 12 70 17 E0 2E D0 07 20 03 B0 04 60 09 10 " +
		"0E C0 12 70 17 E0 2E D0 07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";
	private const string Block1 =                                              // frame 5073
		"00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 B7 FF 00 00 FF 00 FF " +
		"00 FF FF 00 00 FF FF FF 00 FF FF 80 00 FF FF FF 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";
	private const string Block2 =                                              // frame 5081 (padded to 64)
		"F0 00 00 02 F1 00 00 02 F2 00 00 02 F4 00 00 02 F3 00 00 02 03 00 00 0B 03 00 00 06 03 00 00 0A " +
		"00 00 00 02 00 00 00 02 00 00 00 02 00 00 00 02 00 00 00 02 00 01 00 04 00 02 00 04 00 00 00 00";

	// Replays the OEM sequence, optionally with ONE of our divergences reintroduced, so each can be
	// tested in isolation against a sequence already proven to work on this device.
	public static async Task RunAsync(string variant = "")
	{
		Console.WriteLine("BLAZE — replaying the OEM gradient-breathing write, byte for byte");
		Console.WriteLine("Source: cap-06.08.26-for-blaze.pcapng frames 5049-5081");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(d, true) == 65 && Feat(d) == 9);

		if (device is null)
		{
			Console.WriteLine("Easen vendor interface (outLen=65 / featLen=9) not found.");
			Console.WriteLine("Is BLAZE plugged in, and is ProoveGamingDeviceHost stopped?");
			return;
		}

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the interface — the DeviceHost probably holds it.");
			return;
		}

		// Which effect to replay. Colour cycle is the case neon does not cover: same slot, same
		// palette, different protocol effect (0x04 vs 0x03).
		var (effectName, param0Header) =
			variant.Contains("cycle") ? ("colour cycle", Param0ColorCycle) :
			variant.Contains("neon") ? ("neon", Param0Neon) :
			("gradient breathing", Param0Gradient);
		// Divergence 5: our apply sends param_0 byte 1 as 0x02 where the OEM sends 0x07, on every
		// effect. It is the ONLY byte that differs between our frames and the vendor's.
		if (variant.Contains("stage2"))
		{
			var b = param0Header.Split(' ');
			b[1] = "02";
			param0Header = string.Join(' ', b);
			Console.WriteLine("  param_0 byte 1 forced to OUR value 0x02 (divergence 5)");
		}

		Console.WriteLine($"  EFFECT: {effectName}");

		using (stream)
		{
			// The OEM's two phases, as local steps so their ORDER can be varied.
			async Task Param0PhaseAsync()
			{
				SendFeature(stream, Param0Command, "param_0 command (frame 5049)");
				await WriteBlockAsync(stream, Pad(param0Header), $"param_0 data  — {effectName}: {param0Header}");
			}

			async Task BlocksPhaseAsync()
			{
				// Divergence 1: we declare all blocks changed (0x07) where the OEM declares block 1 only.
				var param1 = variant == "status7" ? "03 00 00 00 00 07 03 00" : Param1Command;
				SendFeature(stream, param1, variant == "status7"
					? "param_1 command with OUR status 0x07 (divergence 1)"
					: "param_1 command (frame 5063) — status 0x02, times 3");

				await WriteBlockAsync(stream, Block0, "block 0       (frame 5067) — DPI table");
				await WriteBlockAsync(stream, Block1, "block 1       (frame 5073) — LED palette");
				await WriteBlockAsync(stream, Block2, "block 2       (frame 5081) — key matrix");
			}

			// Divergence 4: EasenMouseService writes the blocks first and param_0 afterwards. The OEM
			// does param_0 first. Same bytes either way — only the sequence changes.
			if (variant == "ourorder")
			{
				Console.WriteLine("  ORDER: blocks first, then param_0 (OURS)");
				await BlocksPhaseAsync();
				await Param0PhaseAsync();
			}
			else
			{
				await Param0PhaseAsync();
				await BlocksPhaseAsync();
			}

			// Divergence 2: we follow the write with an LED drive/commit the OEM never sends.
			if (variant == "drive")
			{
				Console.WriteLine();
				Console.WriteLine("  Sequence sent. The LED should be breathing through the gradient NOW.");
				Console.WriteLine("  Waiting 8s so you can confirm that, then sending OUR extra drive...");
				await Task.Delay(TimeSpan.FromSeconds(8));

				// EasenMouseService: SendCommandViaStream(0x0D, effect, driveBrightness, colorSlot)
				var h = param0Header.Split(' ');
				SendFeature(stream, $"0D {h[2]} {h[3]} {h[5]} 00 00 00 00", "OUR LED drive 0x0D (divergence 2)");
				SendFeature(stream, "80 00 00 00 00 00 00 00", "OUR commit 0x80 (divergence 2)");
				Console.WriteLine();
				Console.WriteLine("  >>> If the LED just went DARK, the 0x0D/0x80 drive is the bug.");
			}
		}

		Console.WriteLine();
		Console.WriteLine("Replay complete. NOTE: no 0x0D/0x80 drive was sent — the OEM capture has none.");
		Console.WriteLine();
		Console.WriteLine("Watch the mouse for ~15s:");
		Console.WriteLine("  BREATHING THROUGH COLOURS -> the OEM sequence works, so one of our three");
		Console.WriteLine("     divergences is the bug (status 0x07 vs 0x02, or the extra 0x0D/0x80,");
		Console.WriteLine("     or our default block0/block2). Bisect from here.");
		Console.WriteLine("  STILL DARK -> the effect needs something outside the captured window;");
		Console.WriteLine("     re-take the capture over a longer period.");
	}

	private static void SendFeature(HidStream stream, string hex, string label)
	{
		var data = Parse(hex);
		var report = new byte[9];   // report id 0 + 8 payload bytes
		Buffer.BlockCopy(data, 0, report, 1, data.Length);
		stream.SetFeature(report);
		Console.WriteLine($"  SET_FEATURE  {hex}   {label}");
	}

	private static async Task WriteBlockAsync(HidStream stream, string hex, string label)
	{
		var data = Parse(hex);
		var report = new byte[65];  // report id 0 + 64 payload bytes
		Buffer.BlockCopy(data, 0, report, 1, data.Length);
		await stream.WriteAsync(report);
		Console.WriteLine($"  OUT 64B      {label}");
	}

	// Expands a 6-byte param_0 header into the full 64-byte payload the device expects.
	private static string Pad(string header)
		=> header + string.Concat(Enumerable.Repeat(" 00", 64 - header.Split(' ').Length));

	private static byte[] Parse(string hex)
	{
		var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(x => Convert.ToByte(x, 16)).ToArray();
		return bytes;
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return 0; }
	}

	private static int Feat(HidDevice d)
	{
		try { return d.GetMaxFeatureReportLength(); }
		catch { return 0; }
	}
}
