using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — bounded sweep of LED command framings ──────────────────────────────────────
//
// Four framings have been tried individually and none drove the LED (see the plan's 0b results).
// This walks a structured MATRIX of the remaining plausible ones instead of one at a time.
//
// WHAT THIS IS NOT: an opcode sweep. Every frame here uses the documented CM002X1 commands
// (0x01 Start, 0x02 Stop, 0x03 Configuration, 0x04 Colour Array) in the documented order. What
// varies is only how the frame is DELIVERED and how its fields are packed:
//
//   report ID     0xFD (the one this device declares) vs 0xFF (the one the spec names)
//   length        32 (the declared output length) vs 16 (the spec's report size)
//   lead byte     none, or an inner report ID / length byte before the command
//   offset width  2 bytes (per spec) vs 1 byte — which shifts where the RGB triple lands
//
// Sending unknown OPCODES to a flash-backed vendor channel is what bricked BLAZE, and this does not
// do that. The risk here is bounded: known commands, known parameter ranges, one at a time.
//
// Each attempt is numbered and followed by a pause, so a change in the LED identifies WHICH framing
// worked — that is the whole point of running them in sequence rather than firing them all off.
//
// WRITES. Requires the same explicit opt-in as the single-shot arm.
internal static class StarlightLedSweep
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	private const byte VendorReportId = 0xFD;
	private const byte SpecLedReportId = 0xFF;

	private const byte LedStart = 0x01;
	private const byte LedStop = 0x02;
	private const byte LedConfiguration = 0x03;
	private const byte LedColorArray = 0x04;
	private const byte ModeStatic = 0x00;

	// One delivery shape. `Lead` is a byte inserted before the command, if any.
	private sealed record Framing(
		string Name,
		byte ReportId,
		int Length,
		byte? Lead,
		int OffsetWidth);

	// Ordered most-likely first, so a win comes early. The four already tried are included so the
	// run is self-contained and the numbering matches a single, complete matrix.
	private static readonly Framing[] Framings =
	[
		new("FD / 32 / no lead / 2-byte offset      (tried, direct)",   VendorReportId,  32, null, 2),
		new("FD / 32 / lead FF / 2-byte offset      (tried, envelope)", VendorReportId,  32, 0xFF, 2),
		new("FF / 16 / no lead / 2-byte offset      (tried, native)",   SpecLedReportId, 16, null, 2),
		new("FD / 16 / no lead / 2-byte offset      (tried, short)",    VendorReportId,  16, null, 2),

		new("FD / 32 / no lead / 1-byte offset",                        VendorReportId,  32, null, 1),
		new("FD / 16 / no lead / 1-byte offset",                        VendorReportId,  16, null, 1),
		new("FF / 32 / no lead / 2-byte offset",                        SpecLedReportId, 32, null, 2),
		new("FF / 16 / no lead / 1-byte offset",                        SpecLedReportId, 16, null, 1),
		new("FD / 16 / lead FF / 2-byte offset",                        VendorReportId,  16, 0xFF, 2),
		new("FD / 32 / lead 0F / 2-byte offset  (length byte)",         VendorReportId,  32, 0x0F, 2),
		new("FD / 32 / lead 00 / 2-byte offset",                        VendorReportId,  32, 0x00, 2),
		new("FD / 32 / lead 01 / 2-byte offset",                        VendorReportId,  32, 0x01, 2),
	];

	// Distinct target colours so a partial success is still informative: if the LED lands on a
	// colour we never sent, the bytes are being read from an offset we have not accounted for.
	private static readonly (string Name, byte R, byte G, byte B)[] Palette =
	[
		("RED",     0xFF, 0x00, 0x00),
		("GREEN",   0x00, 0xFF, 0x00),
		("BLUE",    0x00, 0x00, 0xFF),
		("YELLOW",  0xFF, 0xFF, 0x00),
		("MAGENTA", 0xFF, 0x00, 0xFF),
		("CYAN",    0x00, 0xFF, 0xFF),
		("WHITE",   0xFF, 0xFF, 0xFF),
		("ORANGE",  0xFF, 0x80, 0x00),
	];

	public static void Run(bool allowWrites, int holdSeconds = 5)
	{
		Console.WriteLine("PG Starlight — LED framing sweep");
		Console.WriteLine();

		if (!allowWrites)
		{
			Console.WriteLine("REFUSED: this arm WRITES to an undocumented vendor channel.");
			Console.WriteLine();
			Console.WriteLine($"  It sends {Framings.Length} framings of the DOCUMENTED command set — no unknown");
			Console.WriteLine("  opcodes, no random bytes. Each is one Stop/Configuration/Colour Array/Start");
			Console.WriteLine("  sequence, sent alone, with a pause so you can see which one works.");
			Console.WriteLine();
			Console.WriteLine("  Re-run with the explicit opt-in:");
			Console.WriteLine("      dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight sweep --i-mean-it");
			return;
		}

		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(() => d.GetMaxOutputReportLength()) == 32);

		if (device is null)
		{
			Console.WriteLine("Vendor interface not found. Is the headset connected and the DeviceHost stopped?");
			return;
		}

		Console.WriteLine($"{Framings.Length} framings, {holdSeconds}s each — about {Framings.Length * holdSeconds / 60 + 1} minutes.");
		Console.WriteLine();
		Console.WriteLine("WATCH THE HEADSET. Note the ATTEMPT NUMBER at which the LED changes, and what");
		Console.WriteLine("colour it becomes. Each attempt targets a different colour, so the colour itself");
		Console.WriteLine("tells us whether the bytes landed where we think they did.");
		Console.WriteLine();

		for (var i = 0; i < Framings.Length; i++)
		{
			var framing = Framings[i];
			var (colourName, r, g, b) = Palette[i % Palette.Length];

			Console.WriteLine(new string('=', 96));
			Console.WriteLine($"ATTEMPT {i + 1,2}/{Framings.Length}   target {colourName,-8}   {framing.Name}");

			SendSequence(device, framing, r, g, b);

			Console.WriteLine($"    ...holding {holdSeconds}s — look at the headset now.");
			Thread.Sleep(holdSeconds * 1000);
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("Sweep complete.");
		Console.WriteLine();
		Console.WriteLine("  LED CHANGED at attempt N  -> that framing is the one. Tell me N and the colour;");
		Console.WriteLine("                               the colour says whether the RGB bytes landed right.");
		Console.WriteLine("  NEVER CHANGED             -> the LED is not reachable through the documented");
		Console.WriteLine("                               command set on either report, in any packing we can");
		Console.WriteLine("                               construct. That is the vendor's answer to give, and");
		Console.WriteLine("                               the request is already drafted.");
		Console.WriteLine();
		Console.WriteLine("Unplug and replug afterwards — the device stores its LED table in flash, so a");
		Console.WriteLine("change that only appears after a replug is still a change.");
	}

	// One complete documented sequence in the given framing.
	private static void SendSequence(HidDevice device, Framing framing, byte r, byte g, byte b)
	{
		// Colour Array: offset (1 or 2 bytes, little-endian) then the RGB triple. Narrowing the
		// offset to one byte shifts the triple one position earlier, which is one of the two
		// explanations for the LED showing a colour we never asked for.
		var colourBody = framing.OffsetWidth == 2
			? new byte[] { LedColorArray, 0x00, 0x00, r, g, b }
			: [LedColorArray, 0x00, r, g, b];

		(string Label, byte[] Body)[] sequence =
		[
			("Stop", [LedStop]),
			("Config", [LedConfiguration, ModeStatic, 0x01, 0x00, 0x00, 0x00]),
			("Colour", colourBody),
			("Start", [LedStart]),
		];

		foreach (var (label, body) in sequence)
		{
			var report = Build(framing, body);
			if (!Send(device, framing, report, label)) return;
			Thread.Sleep(40);
		}
	}

	private static byte[] Build(Framing framing, byte[] body)
	{
		var report = new byte[framing.Length];
		report[0] = framing.ReportId;

		var offset = 1;
		if (framing.Lead is { } lead)
		{
			report[1] = lead;
			offset = 2;
		}

		Buffer.BlockCopy(body, 0, report, offset, Math.Min(body.Length, report.Length - offset));
		return report;
	}

	// A 32-byte report on the declared ID can go through HidSharp. Anything else — a different
	// length, or an ID the descriptor does not declare — has to use HidD_SetOutputReport, because
	// HidSharp always pads to the collection's declared output length.
	private static bool Send(HidDevice device, Framing framing, byte[] report, string label)
	{
		Console.WriteLine($"    {label,-7} {BitConverter.ToString(report[..Math.Min(12, report.Length)])}...");

		if (framing is { ReportId: VendorReportId, Length: 32 })
		{
			try
			{
				if (!device.TryOpen(out var stream)) return false;
				using (stream) stream.Write(report);
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"            write failed: {ex.Message}");
				return false;
			}
		}

		SafeFileHandle? handle = null;
		try
		{
			handle = CreateFileW(device.DevicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

			if (handle.IsInvalid)
			{
				Console.WriteLine($"            open failed (Win32 {Marshal.GetLastWin32Error()})");
				return false;
			}

			if (HidD_SetOutputReport(handle, report, report.Length))
				return true;

			Console.WriteLine($"            rejected by Windows (Win32 {Marshal.GetLastWin32Error()})");
			return false;
		}
		finally
		{
			handle?.Dispose();
		}
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}

	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareRead = 1;
	private const uint FileShareWrite = 2;
	private const uint OpenExisting = 3;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
