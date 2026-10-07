using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — can we drive the LED? Now with a way to TELL. ───────────────────────────────
//
// The 2026-08-07 round sent 12 command framings x 16 operation modes at report 0xFD and concluded
// "none changed the LED". That conclusion was weaker than it read, and the vendor letter says why:
//
//     "we cannot distinguish 'rejected' from 'ignored': 0xFD is a declared output report, so the
//      Windows HID stack accepts any write to it regardless of whether the firmware parses it."
//
// 2026-09-09 removed that blind spot. Report 0xFD implements the documented CM002X1 HID<->I2C
// bridge: send `FD 12 7A <map> <len>` and the device populates its input buffer with
// `FD 13 7A <map> <len> <data>`. See docs/headset/starlight/starlight-research-2026-09.md.
//
// THE ORACLE
//
//   1. Write a known marker into the input buffer with a documented 0x12 read.
//   2. Confirm the marker is there.
//   3. Send the candidate LED command.
//   4. Read the buffer again.
//
// If the buffer still holds our marker, the firmware did nothing observable with the command. If it
// changed, the command was PARSED and produced output. Either answer is worth more than "the LED
// did not change", because it separates "this framing is wrong" from "this device has no LED
// command set at all".
//
// A CAVEAT THAT LIMITS WHAT ANY NEGATIVE RESULT MEANS
//
// The LED is on the HEADSET; USB reaches only the DONGLE. The CM002X1 LED specification was written
// for a wired demo board, where the chip driving the LED is the chip on the USB bus. Here anything
// we send has to be relayed over 2.4G to the headset, and nothing documents that relay. So a
// command can be perfectly well-formed, parsed by the dongle, and still never reach the part with
// the LED on it. "No effect" cannot distinguish that from "wrong framing".
//
// SAFETY — what this probe will NOT do, and why
//
//   * NEVER sends 0x02 (LED Stop). The spec says firmware ERASES the flash LED table on Stop. If
//     the headset's own button-selected effect lives in that table, Stop destroys it.
//   * NEVER sends 0x11 (I2C write). That writes to the CM002X1 authentication coprocessor; a bad
//     write there risks the device's Xear licensing state, which we cannot restore.
//   * NEVER sweeps unknown opcodes. Only 0x01/0x03/0x04 (documented LED) and 0x12 (documented read).
//   * Sends 0x01 (Start) at most a handful of times — each Start is a flash write per the spec.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight rgb-oracle --i-mean-it
//       Add --colour RRGGBB to choose the target colour (default vivid green, so it is unmistakable
//       against the factory effects).
internal static class StarlightRgbOracleProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	private const byte VendorReportId = 0xFD;
	private const byte SpecLedReportId = 0xFF;
	private const int VendorReportLength = 32;

	// Documented CM002X1 LED commands. 0x02 (Stop) is deliberately absent — see the header.
	private const byte LedStart = 0x01;
	private const byte LedConfiguration = 0x03;
	private const byte LedColorArray = 0x04;
	private const byte ModeStatic = 0x00;

	// Documented I2C bridge read, used only to plant the oracle marker.
	private const byte CmdI2cRead = 0x12;
	private const byte CmdI2cReturn = 0x13;
	private const byte CoprocessorSlaveAddress = 0x7A;

	// A sub-address chosen only because it is memorable in a hex dump.
	private const byte MarkerMap = 0xA5;
	private const byte MarkerLen = 0x10;

	public static void Run(bool allowWrites, byte r = 0x00, byte g = 0xFF, byte b = 0x00)
	{
		Console.WriteLine("PG Starlight — LED command probe, with a response oracle");
		Console.WriteLine(new string('=', 88));

		if (!allowWrites)
		{
			Console.WriteLine("REFUSED: this arm WRITES to the vendor channel.");
			Console.WriteLine();
			Console.WriteLine("  It sends only documented CM002X1 LED commands (0x03 Configuration, 0x04 Colour");
			Console.WriteLine("  Array, 0x01 Start) plus the documented 0x12 read used to plant the oracle marker.");
			Console.WriteLine("  It NEVER sends 0x02 (LED Stop erases the flash LED table) or 0x11 (I2C write to");
			Console.WriteLine("  the authentication coprocessor), and never sweeps unknown opcodes.");
			Console.WriteLine();
			Console.WriteLine("  Re-run with --i-mean-it to proceed.");
			return;
		}

		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(() => d.GetMaxOutputReportLength(), 0) == VendorReportLength);
		if (device is null)
		{
			Console.WriteLine("Vendor interface not found — plug the dongle in, and stop ProoveGamingDeviceHost.");
			return;
		}

		var path = device.DevicePath;
		Console.WriteLine($"target colour  R={r:X2} G={g:X2} B={b:X2}");
		Console.WriteLine();

		if (!CalibrateOracle(path))
		{
			return;
		}

		var oracleTrusted = PositiveControl(path);

		// Both plausible framings on the report this device actually declares. "direct" puts the
		// spec's frame at byte 1 (where 0x12 lives, so it is where the firmware's command dispatch
		// demonstrably reads); "envelope" wraps it with the spec's own report ID first, in case
		// 0xFD is a tunnel rather than the command channel itself.
		// LOOK AT THE HEADSET between each step, not at the end. The whole sequence takes a couple
		// of seconds, so running it straight through would let a brief or reverted change go by
		// unseen — and "did the LED move" is half the evidence the oracle cannot supply.
		// Skipped when input is redirected so a piped/automated run still works unattended.
		var interactive = !Console.IsInputRedirected;
		if (interactive)
		{
			Console.WriteLine("Interactive: it pauses after each step so you can watch the headset.");
			Console.WriteLine("Have the headset ON and in view, and note its CURRENT colour/effect now.");
			Pause("Press Enter when you are looking at it");
		}

		foreach (var framing in new[] { "direct", "envelope" })
		{
			Console.WriteLine();
			Console.WriteLine(new string('-', 88));
			Console.WriteLine($"FRAMING: {framing}   {(framing == "direct" ? "FD <cmd> <params…>" : "FD FF <cmd> <params…>")}");
			Console.WriteLine(new string('-', 88));
			ProbeSequence(path, framing, r, g, b, interactive);

			if (interactive)
			{
				Pause($"  End of '{framing}'. Did the LED change at ANY point? Press Enter");
			}
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 88));
		Console.WriteLine("LOOK AT THE HEADSET NOW. Did anything change — colour, effect, brightness?");
		Console.WriteLine();
		Console.WriteLine("Reading the result:");
		Console.WriteLine("  LED changed                  -> we have lighting control. Note which framing ran last.");
		Console.WriteLine("  LED same, oracle CHANGED     -> the command was parsed. Framing is close; the payload");
		Console.WriteLine("                                  or the relay to the headset is what is wrong.");
		Console.WriteLine("  LED same, oracle UNCHANGED   -> the dongle does not implement an LED command set on");
		Console.WriteLine("                                  0xFD at all. That is a real negative, and it is the");
		Console.WriteLine("                                  finding to send the vendor.");
		Console.WriteLine();
		Console.WriteLine(oracleTrusted
			? "  The positive control passed, so the negatives above are trustworthy."
			: "  THE POSITIVE CONTROL FAILED — treat every result above as inconclusive.");
		Console.WriteLine();
		Console.WriteLine("  One limit worth stating: the oracle proves these commands produce no RESPONSE.");
		Console.WriteLine("  A fire-and-forget command handled silently would look the same. The LED spec's");
		Console.WriteLine("  commands are not documented to reply, so pair this with what the LED actually did.");
	}

	// ── The oracle ────────────────────────────────────────────────────────────────────────────

	// Establishes how the input buffer actually behaves before trusting it as an oracle.
	//
	// Measured 2026-09-10: the buffer is CONSUME-ON-READ. Planting a marker and then reading twice
	// gives the marker once and zeros after. So the oracle has to be plant -> command -> ONE read,
	// and every read below is deliberately the only one after its plant.
	private static bool CalibrateOracle(string path)
	{
		Console.WriteLine("ORACLE CALIBRATION");

		if (!PlantMarker(path))
		{
			Console.WriteLine("  FAILED to send the 0x12 request — the vendor interface is not accepting writes.");
			return false;
		}

		var first = Read(path);
		Console.WriteLine($"  plant, read #1     {Dump(first)}   {(first is not null && IsMarker(first) ? "marker present" : "NO MARKER")}");
		if (first is null || !IsMarker(first))
		{
			Console.WriteLine("  The 0x12 bridge is not answering. Run `starlight console` option 1 first;");
			Console.WriteLine("  without the bridge there is no oracle.");
			return false;
		}

		// Second read with no new plant: shows whether the buffer is one-shot or holds its value.
		var second = Read(path);
		var oneShot = second is null || second.All(x => x == 0);
		Console.WriteLine($"  read #2, no plant  {Dump(second)}   {(oneShot ? "consumed — buffer is ONE-SHOT" : "still held")}");

		// Plant again and let it sit: the marker must survive the delay a command will occupy,
		// or a "changed" result could just be the buffer ageing out.
		if (!PlantMarker(path)) return false;
		Thread.Sleep(300);
		var aged = Read(path);
		var survives = aged is not null && IsMarker(aged);
		Console.WriteLine($"  plant, +300 ms     {Dump(aged)}   {(survives ? "SURVIVES — oracle is usable" : "decayed — oracle unusable")}");

		if (!survives)
		{
			Console.WriteLine("  The marker does not outlive the gap a command needs, so a change cannot be");
			Console.WriteLine("  attributed to the command rather than to the buffer clearing itself.");
		}

		return survives;
	}

	// Sends the 0x12 request and does NOT read. The read is the consumption, so it belongs to
	// whoever is running the test, not to the planting.
	private static bool PlantMarker(string path)
	{
		var request = new byte[VendorReportLength];
		request[0] = VendorReportId;
		request[1] = CmdI2cRead;
		request[2] = CoprocessorSlaveAddress;
		request[3] = MarkerMap;
		request[4] = MarkerLen;

		if (!Write(path, request)) return false;
		Thread.Sleep(40);
		return true;
	}

	private static bool IsMarker(byte[] frame) =>
		frame.Length >= 5
		&& frame[1] == CmdI2cReturn
		&& frame[2] == CoprocessorSlaveAddress
		&& frame[3] == MarkerMap
		&& frame[4] == MarkerLen;

	// ── The sequence ──────────────────────────────────────────────────────────────────────────

	// POSITIVE CONTROL. Without this, "marker intact" is worthless: it would look identical if the
	// write path were broken, the timing wrong, or the report malformed. So push a command we KNOW
	// the firmware handles — another 0x12, at a different sub-address — through the exact same
	// plant/send/read code the LED commands use. It must come back showing the NEW sub-address.
	private static bool PositiveControl(string path)
	{
		Console.WriteLine();
		Console.WriteLine("POSITIVE CONTROL — a known-handled command through the same path");

		const byte otherMap = 0x5A;

		if (!PlantMarker(path))
		{
			Console.WriteLine("  plant failed");
			return false;
		}

		var request = new byte[VendorReportLength];
		request[0] = VendorReportId;
		request[1] = CmdI2cRead;
		request[2] = CoprocessorSlaveAddress;
		request[3] = otherMap;
		request[4] = MarkerLen;

		var sent = Write(path, request);
		Thread.Sleep(60);
		var after = Read(path);

		var detected = after is { Length: >= 5 } && after[1] == CmdI2cReturn && after[3] == otherMap;
		Console.WriteLine($"    sent   {BitConverter.ToString(request, 0, 12)}…  write={(sent ? "ok" : "FAILED")}");
		Console.WriteLine($"    oracle {Dump(after)}   {(detected ? "DETECTED — the oracle works end to end" : "NOT detected — the oracle cannot be trusted")}");

		if (!detected)
		{
			Console.WriteLine("  Every negative below would be meaningless. Fix this before reading anything into them.");
		}

		return detected;
	}

	private static void ProbeSequence(string path, string framing, byte r, byte g, byte b, bool interactive)
	{
		// Documented order, Stop omitted on purpose. Static mode fixes array size at 1 and interval
		// at 0. Multi-byte fields little-endian (spec v0.2).
		(string Label, byte[] Body)[] sequence =
		[
			("0x03 Configuration (Static, 1 colour, 0 ms)",
				[LedConfiguration, ModeStatic, 0x01, 0x00, 0x00, 0x00]),
			($"0x04 Colour Array (offset 0, RGB {r:X2} {g:X2} {b:X2})",
				[LedColorArray, 0x00, 0x00, r, g, b]),
			("0x01 Start (applies AND saves the table to flash)",
				[LedStart]),
		];

		foreach (var (label, body) in sequence)
		{
			if (!PlantMarker(path))
			{
				Console.WriteLine($"  {label,-48} marker failed, skipping");
				continue;
			}

			var report = Frame(framing, body);
			var sent = Write(path, report);
			Thread.Sleep(60);

			// The ONLY read after this plant — the buffer is consume-on-read.
			var after = Read(path);

			var verdict = after is null ? "read FAILED"
				: after.All(x => x == 0) ? "CLEARED — the command consumed or reset the buffer"
				: IsMarker(after) ? "marker intact — no observable handling"
				: "CHANGED — the command wrote its own response";

			Console.WriteLine($"  {label}");
			Console.WriteLine($"    sent   {BitConverter.ToString(report, 0, 12)}…  write={(sent ? "ok" : "FAILED")}");
			Console.WriteLine($"    oracle {Dump(after)}   {verdict}");

			// The spec requires Configuration and Colour Array to arrive before Start; a pause
			// between them costs nothing and mirrors how the vendor's own tool would behave.
			if (interactive)
			{
				Pause("    ^ look at the headset, then press Enter for the next command");
			}
			else
			{
				Thread.Sleep(200);
			}
		}
	}

	private static byte[] Frame(string framing, byte[] body)
	{
		var report = new byte[VendorReportLength];
		report[0] = VendorReportId;

		if (framing == "envelope")
		{
			report[1] = SpecLedReportId;
			Array.Copy(body, 0, report, 2, Math.Min(body.Length, report.Length - 2));
		}
		else
		{
			Array.Copy(body, 0, report, 1, Math.Min(body.Length, report.Length - 1));
		}

		return report;
	}

	// ── HID plumbing ──────────────────────────────────────────────────────────────────────────

	private static void Pause(string prompt)
	{
		Console.Write($"{prompt}… ");
		Console.ReadLine();
	}

	private static string Dump(byte[]? frame) =>
		frame is null ? "read FAILED"
		: frame.All(x => x == 0) ? "all zeros"
		: BitConverter.ToString(frame, 0, 10) + "…";

	private static int Len(Func<int> get, int fallback)
	{
		try { return get(); }
		catch { return fallback; }
	}

	private static byte[]? Read(string path)
	{
		if (!OperatingSystem.IsWindows()) return null;
		using var handle = Open(path);
		if (handle is null || handle.IsInvalid) return null;

		var buffer = new byte[VendorReportLength];
		buffer[0] = VendorReportId;
		return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
	}

	private static bool Write(string path, byte[] report)
	{
		if (!OperatingSystem.IsWindows()) return false;
		using var handle = Open(path);
		return handle is not null && !handle.IsInvalid
			&& HidD_SetOutputReport(handle, report, report.Length);
	}

	private static SafeFileHandle? Open(string path)
	{
		try
		{
			return CreateFileW(path, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
		}
		catch
		{
			return null;
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
