using HidSharp;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── PG Cosmo — request/response probe on the vendor channel ───────────────────────────────────
//
// The insight this probe is built on comes from the C-Media CM002X1 guide (docs/headset/cosmo),
// section 3.4, describing the READ flow for the sibling report 0xFD:
//
//   "USB device needs to execute I2C read ... and GETS DATA READY IN DEVICE BUFFER (reserve 16
//    bytes). UNTIL host sending 'get input report' to device which means device has to return data
//    to host with HID 0x13"
//
// So this device family answers in TWO steps: send an OUT report (the request), then GET_REPORT to
// collect the reply from a device-side buffer.
//
// Every earlier probe only ever did step 2 on 0xBC — it asked for a reply without asking a
// question. That is the most likely reason its payload is frozen at
//     01 57 01 03 B4 0E 01 06
// across ~58 samples: it is a STALE RESPONSE BUFFER, not live state. (Byte 19 moves anyway because
// the dongle maintains link state independently of that buffer.)
//
// Stage 1 validates the two-step pattern using 0xFD, which is FULLY DOCUMENTED, with its
// non-destructive I2C READ command. Reading a crypto chip's register is the chip's intended use.
// Only once that round-trips do we point the same pattern at the undocumented 0xBC.
//
// Stage 2 writes to 0xBC. Authorised by the user. Guarded: zero/minimal payloads, a denylist over
// the opcode ranges reset and firmware-update commands habitually occupy, a device-health check
// after every single write, and an immediate abort if the device stops enumerating.
internal static class CosmoBridgeProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;
	private const byte VendorReportId = 0xBC;
	private const byte XearReportId = 0xFD;
	private const byte Cm002X1SlaveAddress = 0x7A;
	private const int BufferLength = 32;

	public static void Run(bool probeVendorChannel)
	{
		Console.WriteLine("PG Cosmo — request/response probe on the vendor channel");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault();
		if (device is null)
		{
			Console.WriteLine("Not found. Is the Cosmo dongle plugged in?");
			return;
		}

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the interface for writing.");
			return;
		}

		using (stream)
		using (var handle = OpenDevice(device.DevicePath))
		{
			if (handle is null || handle.IsInvalid)
			{
				Console.WriteLine("Could not open a raw handle for GET_REPORT.");
				return;
			}

			stream.ReadTimeout = 300;

			StageOne_ValidateWithDocumentedXearRead(stream, handle);

			if (!probeVendorChannel)
			{
				Console.WriteLine();
				Console.WriteLine("Stage 2 (0xBC requests) NOT run. Re-run with:  -- bridge bc");
				return;
			}

			StageTwo_VendorRequests(stream, handle);
		}
	}

	// ── Stage 1: the documented 0xFD I2C read ────────────────────────────────
	//
	// OUT:  FD 12 7A <map_addr> <len> <don't care...>
	// GET:  FD 13 7A <map_addr> <len> <data...>
	//
	// A 0x13 in byte 1 of the reply proves the two-step pattern works and that our OUT frames are
	// being decoded — which is exactly what Stage 2 needs to know before it starts guessing.
	private static void StageOne_ValidateWithDocumentedXearRead(HidStream stream, SafeFileHandle handle)
	{
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("STAGE 1 — validate write-then-GET_REPORT using the DOCUMENTED 0xFD I2C read");
		Console.WriteLine("          (non-destructive: reading a register of the Xear crypto chip)");
		Console.WriteLine(new string('=', 78));

		var before = GetReport(handle, XearReportId);
		Console.WriteLine($"  before : {Hex(before)}");

		var request = new byte[BufferLength];
		request[0] = XearReportId;
		request[1] = 0x12;                    // documented I2C READ command
		request[2] = Cm002X1SlaveAddress;     // 0x7A, fixed per the guide
		request[3] = 0x00;                    // map_addr: first register
		request[4] = 0x08;                    // len: 8 bytes (guide allows <= 16)

		if (!TryWrite(stream, request, "0xFD I2C read"))
			return;

		Thread.Sleep(200);

		var after = GetReport(handle, XearReportId);
		Console.WriteLine($"  after  : {Hex(after)}");

		if (after is null)
		{
			Console.WriteLine("  => GET_REPORT on 0xFD failed. The pattern cannot be validated this way.");
			return;
		}

		if (after[1] == 0x13)
			Console.WriteLine("  => CONFIRMED: byte 1 = 0x13, the documented reply. Two-step pattern WORKS.");
		else if (before is not null && !after.AsSpan().SequenceEqual(before))
			Console.WriteLine("  => Buffer CHANGED after the request — the device is answering, though not with 0x13.");
		else
			Console.WriteLine("  => No change. Either the firmware ignores 0xFD requests, or it needs a valid\n" +
							  "     authentication sequence first. Stage 2 is still worth running.");
	}

	// ── Stage 2: the same pattern against the undocumented 0xBC ──────────────
	private static void StageTwo_VendorRequests(HidStream stream, SafeFileHandle handle)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("STAGE 2 — request/response on 0xBC   ** THIS WRITES TO THE DEVICE **");
		Console.WriteLine(new string('=', 78));

		var baseline = GetReport(handle, VendorReportId);
		Console.WriteLine($"  baseline response buffer: {Hex(baseline)}");
		Console.WriteLine();

		// The documented sibling puts its command at offset 1, so that is where these go. Low
		// opcodes first: status/query commands cluster there, and the denylist keeps us out of the
		// ranges where reset and firmware-update commands live.
		for (int op = 0x01; op <= 0x30; op++)
		{
			var opcode = (byte)op;
			if (IsDenied(opcode))
			{
				Console.WriteLine($"  0x{opcode:X2}  SKIPPED (denylisted: reset / firmware-update range)");
				continue;
			}

			var request = new byte[BufferLength];
			request[0] = VendorReportId;
			request[1] = opcode;

			if (!TryWrite(stream, request, $"0xBC opcode 0x{opcode:X2}"))
				continue;

			Thread.Sleep(180);

			var reply = GetReport(handle, VendorReportId);
			if (reply is null)
			{
				Console.WriteLine($"  0x{opcode:X2}  GET_REPORT failed");
			}
			else if (baseline is not null && !reply.AsSpan().SequenceEqual(baseline))
			{
				Console.WriteLine($"  0x{opcode:X2}  *** RESPONSE CHANGED ***");
				Console.WriteLine($"        was: {Hex(baseline)}");
				Console.WriteLine($"        now: {Hex(reply)}");
				Console.WriteLine($"        diff@ {string.Join(',', Diff(baseline, reply))}");
				// Track the new value so the next opcode is compared against current reality.
				baseline = reply;
			}
			else
			{
				Console.WriteLine($"  0x{opcode:X2}  (buffer unchanged)");
			}

			if (!DeviceList.Local.GetHidDevices(Vid, Pid).Any())
			{
				Console.WriteLine();
				Console.WriteLine($"  !! DEVICE DISAPPEARED after opcode 0x{opcode:X2} — ABORTING.");
				Console.WriteLine("  !! Unplug/replug the dongle and power-cycle the headset.");
				Console.WriteLine($"  !! Record this: opcode 0x{opcode:X2} must never be sent again.");
				return;
			}
		}

		Console.WriteLine();
		Console.WriteLine("Stage 2 finished, device still enumerating.");
		Console.WriteLine("Any '*** RESPONSE CHANGED ***' line is a live command worth pursuing.");
	}

	// Opcodes conventionally used for destructive operations. Skipping a handful of candidates costs
	// nothing next to the cost of being wrong about one.
	private static bool IsDenied(byte opcode) => opcode switch
	{
		>= 0xF0 => true,
		0xA5 or 0x5A or 0xAA or 0x55 => true,
		0x20 => true,   // Smart Infinite factory reset in this repo; different family, still skipped
		_ => false,
	};

	private static bool TryWrite(HidStream stream, byte[] frame, string what)
	{
		try
		{
			stream.Write(frame);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  write failed ({what}): {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	private static byte[]? GetReport(SafeFileHandle handle, byte reportId)
	{
		var buffer = new byte[BufferLength];
		buffer[0] = reportId;
		return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
	}

	private static IEnumerable<int> Diff(byte[] a, byte[] b)
	{
		for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
			if (a[i] != b[i]) yield return i;
	}

	private static SafeFileHandle? OpenDevice(string? path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		return CreateFileW(path, 0x80000000 | 0x40000000, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

	private static string Hex(byte[]? data)
	{
		if (data is null) return "<no reply>";
		var sb = new StringBuilder(data.Length * 3);
		foreach (var b in data) sb.Append(b.ToString("X2")).Append(' ');
		return sb.ToString().TrimEnd();
	}
}
