using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — try to change the EFFECT rather than the colour ─────────────────────────────
//
// Button 2 cycles the headset through a fixed set of effects, so the firmware owns an effect table.
// That suggests what is settable is an effect SELECTION, not arbitrary RGB — and every attempt so
// far has been trying to push colours the device may never accept.
//
// It also exposes a likely mistake in those attempts. The specification says LED Stop **erases the
// stored LED table**, and every sequence sent so far opened with Stop. If the device's built-in
// effects live in that table, we have been wiping it and then asking the device to Start with
// nothing left to display — which is a very good explanation for the LED sitting on a default green
// no matter what colour we asked for.
//
// So this probe deliberately does NOT send Stop. It varies one documented field — the operation
// mode byte of command 0x03 — and lets the device keep whatever colours it already has. The
// specification explicitly allows this: "The device should keep former LED Configuration and Color
// Array if the application doesn't send them."
//
// Still no unknown opcodes: only commands 0x03 (Configuration) and 0x01 (Start), which are
// documented. The one thing pushed past the specification is the RANGE of the mode byte — the spec
// names 0x00-0x03, and this tries 0x00-0x07 in case the firmware exposes more.
//
// WRITES. Requires the explicit opt-in.
internal static class StarlightEffectProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	private const byte VendorReportId = 0xFD;
	private const int VendorReportLength = 32;

	private const byte LedStart = 0x01;
	private const byte LedConfiguration = 0x03;

	// The spec documents 0x00-0x03. Anything above that is a guess at an extended table, which is
	// why the run reports where documented values end.
	private const byte MaxMode = 0x07;

	private static readonly string[] DocumentedModes =
	[
		"Static", "Repeat Forward", "Back And Forth", "Lookup Table (audio reactive)",
	];

	public static void Run(bool allowWrites, int holdSeconds = 6)
	{
		Console.WriteLine("PG Starlight — effect (operation mode) probe");
		Console.WriteLine();

		if (!allowWrites)
		{
			Console.WriteLine("REFUSED: this arm WRITES to an undocumented vendor channel.");
			Console.WriteLine();
			Console.WriteLine("  It sends only documented commands 0x03 (Configuration) and 0x01 (Start),");
			Console.WriteLine("  varying the operation-mode byte. It deliberately does NOT send Stop, because");
			Console.WriteLine("  Stop erases the device's stored LED table.");
			Console.WriteLine();
			Console.WriteLine("  Re-run with the explicit opt-in:");
			Console.WriteLine("      dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight effects --i-mean-it");
			return;
		}

		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(() => d.GetMaxOutputReportLength()) == VendorReportLength);

		if (device is null)
		{
			Console.WriteLine("Vendor interface not found. Is the headset connected and the DeviceHost stopped?");
			return;
		}

		Console.WriteLine("NO Stop command is sent. The device keeps its own colours; only the mode changes.");
		Console.WriteLine("Note the headset's CURRENT effect before starting, so a change is obvious.");
		Console.WriteLine();

		// Pass 1: Configuration then Start — the documented way to select a mode.
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("PASS 1 — Configuration(mode) then Start");
		Console.WriteLine();

		for (byte mode = 0; mode <= MaxMode; mode++)
			TryMode(device, mode, withStart: true, holdSeconds);

		// Pass 2: Configuration alone. If the firmware applies a mode immediately, Start is
		// redundant — and Start is also the command that writes flash, so knowing whether it is
		// needed matters for how often we would wear the flash in production.
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("PASS 2 — Configuration(mode) alone, no Start");
		Console.WriteLine();

		for (byte mode = 0; mode <= MaxMode; mode++)
			TryMode(device, mode, withStart: false, holdSeconds);

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("Done.");
		Console.WriteLine();
		Console.WriteLine("  EFFECT CHANGED at some mode -> tell me the PASS and MODE number. That is the");
		Console.WriteLine("     control surface, and an RGB section can be built on it even if arbitrary");
		Console.WriteLine("     colours are never settable — a fixed effect list is still a real feature.");
		Console.WriteLine("  NOTHING CHANGED             -> the effect table is not reachable either, and the");
		Console.WriteLine("     device accepts no lighting control at all on this channel.");
		Console.WriteLine();
		Console.WriteLine("If the LED is now on an effect you did not choose, press Button 2 to cycle it back.");
	}

	private static void TryMode(HidDevice device, byte mode, bool withStart, int holdSeconds)
	{
		var name = mode < DocumentedModes.Length
			? DocumentedModes[mode]
			: "(beyond the specification)";

		Console.WriteLine($"  mode 0x{mode:X2}  {name}");

		// Array size 4 with a 500 ms interval, so a cycling mode has something to cycle through.
		// Static ignores everything past element 0, per the spec.
		var config = new byte[] { LedConfiguration, mode, 0x04, 0x00, 0xF4, 0x01 };
		if (!Send(device, config, "Config")) return;
		Thread.Sleep(40);

		if (withStart && !Send(device, [LedStart], "Start")) return;

		Thread.Sleep(holdSeconds * 1000);
	}

	private static bool Send(HidDevice device, byte[] body, string label)
	{
		var report = new byte[VendorReportLength];
		report[0] = VendorReportId;
		Buffer.BlockCopy(body, 0, report, 1, body.Length);

		try
		{
			if (!device.TryOpen(out var stream))
			{
				Console.WriteLine("      could not open the interface");
				return false;
			}

			using (stream) stream.Write(report);
			Console.WriteLine($"      {label,-6} {BitConverter.ToString(report[..8])}");
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"      {label} failed: {ex.Message}");
			return false;
		}
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
