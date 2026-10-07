using HidSharp;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing;

// PG Cosmo — drive the headset's LEDs from the PC, over the dongle.
//
// WHY THIS IS NOT AN OPCODE GUESS. The whole sequence is read straight out of the dongle's own
// firmware source (`dongle701N`), so nothing here is swept or invented:
//
//   apps/common/device/cmedia/cmedia_common.c
//     __cmedia_hid_0xff_set_led_output_report() — the handler. buf[0] = report id, buf[1] = command:
//       0x01 START   — dumps the config and streams it to the headset
//       0x02 STOP
//       0x03 SET_CONFIGURATION — buf[2] = mode, buf[3..4] = colour count LE, buf[5..6] = interval LE
//       0x04 SET_COLOR_ARRAY   — buf[2..3] = offset LE, then FOUR RGB triples at buf[4], [7], [10], [13]
//     Any other command byte falls through the switch and is ignored, which is what makes this safe.
//
//   apps/dongle/adapter/vendor/G9.c
//     vendorOnLEDCtrlPayload() — G9 is this product. The payload is memcpy'd into a RAM buffer and
//     streamed to the headset over the LE Audio vendor channel (command 0x0b), chunked 7 bytes at a
//     time with an ack from the headset. No flash write is involved on either side.
//
// REPORT ID. The source builds this handler on report 0xFF, but the SHIPPED Cosmo dongle declares
// 0xBC and 0xFD (usage page 0xFF02, 32-byte in and out) — 0xFD is the CM002X1 licensing channel, so
// 0xBC is the LED report. That also explains the payload a GET_REPORT on 0xBC returns
// (01 57 01 03 B4 0E 01 06): it is the LED controller's state block, which is why two fields in it
// were mistaken for a battery and neither tracked a real discharge.
//
// SAFETY. Authorised explicitly by the user 2026-09-03. Writes only the four documented LED
// commands; sends no unknown opcode; keeps the colour count at 4 because the firmware ASSERTs on
// overflow (LED_COLOR_MAX_NUM) and an ASSERT on this SDK resets the device. Ends with STOP so the
// headset is never left mid-stream. Still gated behind an explicit flag.
internal static class CosmoLedProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;

	// The two vendor OUTPUT reports the shipped dongle declares (usage page 0xFF02, 32 bytes each).
	// 0xBC is the LED report on the reading above; 0xFD is documented as the CM002X1 licensing
	// channel, but Starlight's LED work targeted 0xFD, so both are worth trying on real hardware.
	private const byte LedReportIdBc = 0xBC;
	private const byte LedReportIdFd = 0xFD;
	private static byte LedReportId = LedReportIdBc;

	// LENGTH IS DECLARED PER REPORT, and getting it wrong is why the first attempt did nothing.
	// From the device's own descriptor:
	//   85 BC ... 09 10 95 0F 91 02   -> 0xBC OUTPUT = 15 data bytes, so 16 with the report id
	//   85 FD ... 09 10 95 1F 91 02   -> 0xFD OUTPUT = 31 data bytes, so 32
	// The colour-array command is cmd(1) + offset(2) + 4 x RGB(12) = 15 bytes, which fits 0xBC's
	// declared output EXACTLY — good evidence that 0xBC is the LED report and the first run simply
	// sent a 32-byte report the firmware would not parse.
	private static int ReportLength = 16;

	private const byte LedStart = 0x01;
	private const byte LedStop = 0x02;
	private const byte LedConfiguration = 0x03;
	private const byte LedColorArray = 0x04;
	private const byte ModeStatic = 0x00;

	// Four triples is what one SET_COLOR_ARRAY carries, and it stays well inside the firmware's cap.
	private const int ColourCount = 4;

	public static void Run(bool allowWrites, byte? reportId = null)
	{
		LedReportId = reportId ?? LedReportIdBc;
		ReportLength = LedReportId == LedReportIdFd ? 32 : 16;
		Console.WriteLine($"PG Cosmo — LED control over the dongle (CM002X1 sequence, report 0x{LedReportId:X2}, {ReportLength}-byte)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		if (!allowWrites)
		{
			Console.WriteLine("This arm WRITES to the device. Re-run with:  -- cosmo led --i-mean-it");
			return;
		}

		var device = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault();
		if (device is null)
		{
			Console.WriteLine("Not found. Is the Cosmo dongle plugged in and the headset switched on?");
			return;
		}

		Console.WriteLine($"Interface: {device.DevicePath}");
		Console.WriteLine();
		Console.WriteLine("Watch the headset. Each colour is held for 6 seconds.");
		Console.WriteLine();

		(string Name, byte R, byte G, byte B)[] colours =
		[
			("RED",   0xFF, 0x00, 0x00),
			("GREEN", 0x00, 0xFF, 0x00),
			("BLUE",  0x00, 0x00, 0xFF),
		];

		try
		{
			foreach (var (name, r, g, b) in colours)
			{
				Console.WriteLine($"--- {name} ---");
				if (!SendSequence(device, r, g, b)) return;
				Thread.Sleep(6000);
			}
		}
		finally
		{
			Console.WriteLine("--- STOP (leaving the headset in a clean state) ---");
			Send(device, Build([LedStop]), "Stop");
		}

		Console.WriteLine();
		Console.WriteLine("Done. If any colour appeared, LED control over the dongle works and the");
		Console.WriteLine("sequence above is the protocol to build on.");
	}

	// Stop -> Configuration -> Colour Array -> Start, exactly as the firmware handler expects.
	private static bool SendSequence(HidDevice device, byte r, byte g, byte b)
	{
		// Four identical triples at offset 0: buf[4], buf[7], buf[10], buf[13].
		var colour = new byte[] { LedColorArray, 0x00, 0x00, r, g, b, r, g, b, r, g, b, r, g, b };

		(string Label, byte[] Body)[] sequence =
		[
			("Stop",   [LedStop]),
			("Config", [LedConfiguration, ModeStatic, ColourCount, 0x00, 0x64, 0x00]),
			("Colour", colour),
			("Start",  [LedStart]),
		];

		foreach (var (label, body) in sequence)
		{
			if (!Send(device, Build(body), label)) return false;
			Thread.Sleep(40);
		}
		return true;
	}

	// [reportId][body...] padded to the declared output length.
	private static byte[] Build(byte[] body)
	{
		var report = new byte[ReportLength];
		report[0] = LedReportId;
		Array.Copy(body, 0, report, 1, Math.Min(body.Length, ReportLength - 1));
		return report;
	}

	private static bool Send(HidDevice device, byte[] report, string label)
	{
		Console.WriteLine($"  {label,-7} -> {string.Join(' ', report.Take(16).Select(x => x.ToString("X2")))} ...");

		// The interrupt endpoint first; a vendor report the driver will not route that way still
		// reaches the firmware through a SET_REPORT control transfer.
		try
		{
			if (device.TryOpen(out var stream))
			{
				using (stream) stream.Write(report);
				return true;
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"           interrupt write failed ({ex.GetType().Name}), trying control transfer");
		}

		using var handle = OpenDevice(device.DevicePath);
		if (handle is null || handle.IsInvalid)
		{
			Console.WriteLine("           could not open a raw handle");
			return false;
		}

		if (HidD_SetOutputReport(handle, report, report.Length))
			return true;

		Console.WriteLine($"           SET_REPORT failed (win32 {Marshal.GetLastWin32Error()})");
		return false;
	}

	private static SafeFileHandle? OpenDevice(string path)
	{
		var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
		return h.IsInvalid ? null : h;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
