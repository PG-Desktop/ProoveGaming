using System.Runtime.InteropServices;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — interactive capability console ──────────────────────────────────────────────
//
// One place to see what this device will and will not let us change, and to actually change the
// parts that work. Built for the question "what can the App offer for Starlight?", so every section
// says plainly whether it is a shipping capability, a diagnostic, or a dead end.
//
//   dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight console
//
// SECTIONS
//   1  CM002X1 coprocessor probe   READ-ONLY. The documented I2C-over-HID handshake. NEW — see below.
//   2  Audio, live                 The license-free setters that land: mute, volume, default role,
//                                  channel test. Each restores what it changed.
//   3  RGB verdict                 Re-reads the descriptor and reports why lighting is unreachable.
//   4  Xear property matrix        Delegates to `starlight xear`.
//
// WHY SECTION 1 IS NEW, AND WHY IT MATTERS FOR RGB
//
// The 2026-08-07 conclusion "report 0xFD returns 32 zero bytes in every state" was reached by
// passive listening and by calling HidD_GetInputReport on 0xFD directly. But per C-Media's
// "CM002X1 Xear Audio Technology Authentication User Guide" (docs/headset/cosmo/), that report is a
// REQUEST/RESPONSE bridge, not a pollable status register:
//
//   Set Output Report   FD 12 7A <map_addr> <len> ...   host asks the device to I2C-read the chip
//   (device performs the I2C transaction into a 16-byte buffer)
//   Get Input Report    FD 13 7A <map_addr> <len> <data...>   host collects the result
//
// Reading without first sending 0x12 returns an unpopulated buffer — which is exactly the all-zeros
// we recorded. So that measurement never actually asked the coprocessor anything, and "no CM002X1"
// was never established. This section performs the handshake properly.
//
// It matters for lighting because the CM002X1 is the very part the "CM002X1 LED Control
// Specification" is written for. If a coprocessor answers here, this is a real CM002X1 design and
// the LED spec becomes relevant again on a channel we have not tried. If nothing answers after a
// correct handshake, the chip is genuinely absent and both Xear licensing and that LED spec are
// closed for this product — which is worth knowing with certainty either way.
//
// SAFETY. Section 1 issues I2C *reads* only (command 0x12). It never sends 0x11 (I2C write), never
// touches flash, and never sweeps unknown opcodes — the two commands used here are the two the
// vendor documents for exactly this purpose. Section 2 restores every value it sets.
internal static class StarlightConsole
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	private const byte VendorReportId = 0xFD;
	private const int VendorReportLength = 32;

	// From the authentication guide, section 3.2-3.4.
	private const byte CmdI2cRead = 0x12;
	private const byte CmdI2cReturn = 0x13;
	private const byte CoprocessorSlaveAddress = 0x7A;

	public static void Run()
	{
		Banner();

		while (true)
		{
			Console.WriteLine();
			Console.WriteLine("  1  CM002X1 coprocessor probe   (read-only I2C handshake)");
			Console.WriteLine("  2  Audio — change things live  (restores every value)");
			Console.WriteLine("  3  RGB — verdict and evidence");
			Console.WriteLine("  4  Xear property matrix        (how to run it)");
			Console.WriteLine("  0  Quit");
			Console.Write("> ");

			var choice = Console.ReadLine();
			if (choice is null || choice.Trim() is "0" or "q" or "quit")
			{
				return;
			}

			switch (choice.Trim())
			{
				case "1": CoprocessorProbe(); break;
				case "2": AudioSection(); break;
				case "3": RgbVerdict(); break;
				case "4": XearHint(); break;
				default: Console.WriteLine("  ?"); break;
			}
		}
	}

	private static void Banner()
	{
		Console.WriteLine("PG Starlight — capability console");
		Console.WriteLine(new string('=', 78));

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("HID: not found — plug the dongle in, and stop ProoveGamingDeviceHost if it is running.");
			return;
		}

		Console.WriteLine($"HID: {interfaces.Count} interface(s) on VID 0x{Vid:X4} / PID 0x{Pid:X4}");
		foreach (var d in interfaces)
		{
			Console.WriteLine($"     out={Len(() => d.GetMaxOutputReportLength())}B  " +
				$"in={Len(() => d.GetMaxInputReportLength())}B  " +
				$"feature={Len(() => d.GetMaxFeatureReportLength())}B  {d.DevicePath}");
		}
	}

	// ── 1. CM002X1 coprocessor ────────────────────────────────────────────────────────────────

	private static void CoprocessorProbe()
	{
		Console.WriteLine();
		Console.WriteLine("CM002X1 coprocessor probe — documented I2C-over-HID read handshake");
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  Set Output  FD 12 7A <map> <len>      ask the device to I2C-read the chip");
		Console.WriteLine("  Get Input   FD 13 7A <map> <len> ...  collect the result");
		Console.WriteLine("  READ-ONLY: command 0x11 (I2C write) is never sent.");
		Console.WriteLine();

		var device = FindVendorInterface();
		if (device is null)
		{
			return;
		}

		// Baseline: what a bare read returns with no request sent. This is what the 2026-08-07
		// measurement did, and reproducing it here makes the comparison explicit rather than
		// remembered.
		var bare = TryGetInputReport(device.DevicePath);
		Console.WriteLine($"  baseline (no 0x12 sent)   {Describe(bare)}");
		Console.WriteLine();

		// Sweep the whole sub-address space. I2C READS only, so this cannot change chip state.
		// Two different positives are possible and they are NOT equally strong:
		//   echoed header + zero data  -> the FIRMWARE implements the bridge; the chip may or may
		//                                 not be answering, or that register really is zero.
		//   echoed header + real data  -> something behind the bridge returned content. This is the
		//                                 evidence that a chip is actually there.
		var echoed = 0;
		var withData = new List<(byte Map, byte[] Frame)>();

		Console.WriteLine("  sweeping sub-addresses 0x00-0xFF (I2C reads only)…");
		for (var map = 0; map <= 0xFF; map++)
		{
			const byte len = 16;
			var request = new byte[VendorReportLength];
			request[0] = VendorReportId;
			request[1] = CmdI2cRead;
			request[2] = CoprocessorSlaveAddress;
			request[3] = (byte)map;
			request[4] = len;

			if (!TrySetOutputReport(device.DevicePath, request))
			{
				continue;
			}

			// The device has to run a real I2C transaction before the buffer is valid.
			Thread.Sleep(25);

			var reply = TryGetInputReport(device.DevicePath);
			if (reply is null || reply.Length < 5)
			{
				continue;
			}

			var headerOk = reply[1] == CmdI2cReturn
				&& reply[2] == CoprocessorSlaveAddress
				&& reply[3] == (byte)map
				&& reply[4] == len;
			if (!headerOk)
			{
				continue;
			}

			echoed++;
			if (reply.Skip(5).Take(len).Any(b => b != 0))
			{
				withData.Add(((byte)map, reply));
			}
		}

		Console.WriteLine();
		Console.WriteLine($"  header echoed correctly : {echoed} / 256 sub-addresses");
		Console.WriteLine($"  returned non-zero data  : {withData.Count}");
		Console.WriteLine();

		foreach (var (map, frame) in withData)
		{
			Console.WriteLine($"  map 0x{map:X2}  {BitConverter.ToString(frame, 5, 16)}");
		}

		// Repeatability is what separates a real read from an artefact. An unacknowledged I2C bus
		// floats high and reads back as noise or drifting values; genuine register content is
		// byte-identical every time. Without this check, "162 addresses returned data" could just
		// be a bus nobody is driving.
		if (withData.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("  stability check — same address read 5x, must be byte-identical:");
			foreach (var (map, first) in withData.Take(4).Concat(withData.TakeLast(1)).DistinctBy(x => x.Map))
			{
				var stable = true;
				for (var i = 0; i < 4; i++)
				{
					Thread.Sleep(25);
					var again = ReadOnce(device.DevicePath, map);
					if (again is null || !again.Skip(5).Take(16).SequenceEqual(first.Skip(5).Take(16)))
					{
						stable = false;
						break;
					}
				}

				Console.WriteLine($"    map 0x{map:X2}  {(stable ? "STABLE — identical across 5 reads" : "UNSTABLE — value drifts, likely a floating bus")}");
			}
		}

		// CONTROL. Everything above is worthless without this. If a request aimed at a slave
		// address where nothing can exist behaves the same as one aimed at 0x7A, then the reply is
		// the firmware echoing our own header back and the "data" is an undriven bus — not a
		// coprocessor. Only a difference between 0x7A and the controls proves a chip is there.
		Console.WriteLine();
		Console.WriteLine("  CONTROL — identical request sent to slave addresses that should hold nothing:");
		var payloads = new Dictionary<byte, string>();
		foreach (var slave in new byte[] { CoprocessorSlaveAddress, 0x10, 0x50, 0x00 })
		{
			var request = new byte[VendorReportLength];
			request[0] = VendorReportId;
			request[1] = CmdI2cRead;
			request[2] = slave;
			request[3] = 0x81;
			request[4] = 16;

			TrySetOutputReport(device.DevicePath, request);
			Thread.Sleep(25);
			var reply = TryGetInputReport(device.DevicePath);

			var echo = reply is { Length: >= 5 } && reply[1] == CmdI2cReturn && reply[2] == slave;
			var payload = reply is null ? "read FAILED" : BitConverter.ToString(reply, 5, 16);
			payloads[slave] = payload;
			Console.WriteLine($"    slave 0x{slave:X2}{(slave == CoprocessorSlaveAddress ? " (real)" : "       ")}  echo={(echo ? "yes" : "no ")}  {payload}");
		}

		// An I2C master reading a slave that never ACKs sees the line pulled high — all 0xFF. So
		// "controls are all-FF while 0x7A is not" is exactly the signature of a device that IS
		// acknowledging at 0x7A, and it is the only part of this probe that proves a chip exists.
		var controlsAllHigh = new byte[] { 0x10, 0x50, 0x00 }
			.All(s => payloads.TryGetValue(s, out var p) && p.Replace("-", "").All(c => c == 'F'));
		var realIsDifferent = payloads.TryGetValue(CoprocessorSlaveAddress, out var real)
			&& real != payloads[0x10];

		Console.WriteLine();
		if (controlsAllHigh && realIsDifferent)
		{
			Console.WriteLine("  A REAL I2C DEVICE ACKNOWLEDGES AT 0x7A.");
			Console.WriteLine("  The controls read back all-0xFF — an undriven bus. 0x7A does not. Something");
			Console.WriteLine("  is driving that bus, and 0x7A is the address C-Media documents for the");
			Console.WriteLine("  CM002X1 Xear authentication coprocessor.");
			Console.WriteLine();
			Console.WriteLine("  This overturns TWO recorded conclusions:");
			Console.WriteLine("    - 2026-08-07 'report 0xFD returns nothing in every state'. That test read");
			Console.WriteLine("      the buffer without sending the 0x12 request first, so it was reading an");
			Console.WriteLine("      unpopulated buffer. The channel is alive and PARSES commands.");
			Console.WriteLine("    - 'Starlight has no CM002X1, so Xear can never be licensed'. It has one.");
			Console.WriteLine();
			Console.WriteLine("  Note the payload at 0x7A is NOT stable across reads. For a challenge/response");
			Console.WriteLine("  auth chip that is expected — the real flow writes a cipher with 0x11 first,");
			Console.WriteLine("  and we deliberately never send 0x11. Do not read meaning into these bytes.");
			Console.WriteLine();
			Console.WriteLine("  What this unlocks — a RESPONSE ORACLE we did not have before. Because 0x12");
			Console.WriteLine("  visibly changes the input buffer, we can finally tell 'command understood'");
			Console.WriteLine("  from 'command ignored' on 0xFD. The 2026-08-07 LED attempts could not, which");
			Console.WriteLine("  is why 'none changed the LED' was never conclusive.");
		}
		else if (echoed > 0)
		{
			Console.WriteLine("  The firmware implements the bridge (headers echo) but the controls look the");
			Console.WriteLine("  same as 0x7A — so this is the firmware echoing our own header back, not a");
			Console.WriteLine("  chip answering. No CM002X1 is demonstrated.");
		}
		else
		{
			Console.WriteLine("  Nothing answered a correctly-framed request. Unlike the 2026-08-07 result,");
			Console.WriteLine("  this IS a real negative — the request was sent as the vendor documents it.");
		}
	}

	// One full request/response cycle at a single sub-address.
	private static byte[]? ReadOnce(string devicePath, byte map, byte len = 16)
	{
		var request = new byte[VendorReportLength];
		request[0] = VendorReportId;
		request[1] = CmdI2cRead;
		request[2] = CoprocessorSlaveAddress;
		request[3] = map;
		request[4] = len;

		if (!TrySetOutputReport(devicePath, request))
		{
			return null;
		}

		Thread.Sleep(25);
		return TryGetInputReport(devicePath);
	}

	private static string Describe(byte[]? frame) =>
		frame is null ? "read FAILED"
		: frame.All(b => b == 0) ? "all zeros"
		: BitConverter.ToString(frame, 0, Math.Min(frame.Length, 16)) + "…";

	// ── 2. Audio ──────────────────────────────────────────────────────────────────────────────

	private static void AudioSection()
	{
		Console.WriteLine();
		Console.WriteLine("Audio — the license-free capabilities that actually land on this device");
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  These are Core Audio underneath. The App can do all of them WITHOUT the Xear");
		Console.WriteLine("  SDK, via AudioEndpointService — this section only proves they work here.");
		Console.WriteLine();
		Console.WriteLine("  Run the live exercise (mute, volume, default role, channel test)? [y/N]");
		Console.Write("  > ");

		if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
		{
			return;
		}

		StarlightXearWriteProbe.Run(allowWrites: true, sdkDir: null);
	}

	// ── 3. RGB ────────────────────────────────────────────────────────────────────────────────

	private static void RgbVerdict()
	{
		Console.WriteLine();
		Console.WriteLine("RGB — why lighting is not reachable, re-checked against the live descriptor");
		Console.WriteLine(new string('-', 78));

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("  Device not present.");
			return;
		}

		var reportIds = new SortedSet<byte>();
		var usagePages = new SortedSet<uint>();
		var hasFeature = false;

		foreach (var d in interfaces)
		{
			ReportDescriptor descriptor;
			try
			{
				descriptor = d.GetReportDescriptor();
			}
			catch
			{
				continue;
			}

			foreach (var r in descriptor.Reports)
			{
				reportIds.Add((byte)r.ReportID);
				if (r.ReportType == ReportType.Feature)
				{
					hasFeature = true;
				}
			}

			foreach (var c in descriptor.DeviceItems)
			{
				foreach (var u in c.Usages.GetAllValues())
				{
					usagePages.Add(u >> 16);
				}
			}
		}

		Console.WriteLine($"  report IDs declared : {string.Join(", ", reportIds.Select(r => $"0x{r:X2}"))}");
		Console.WriteLine($"  usage pages         : {string.Join(", ", usagePages.Select(p => $"0x{p:X2}"))}");
		Console.WriteLine($"  feature reports     : {(hasFeature ? "yes" : "none")}");
		Console.WriteLine();

		// The three routes a Windows host has to an RGB device, each checked against the above.
		Console.WriteLine($"  [{(reportIds.Contains(0xFF) ? "?" : "X")}] C-Media LED spec        needs output report 0xFF — "
			+ (reportIds.Contains(0xFF) ? "DECLARED, retest the spec!" : "not declared"));
		Console.WriteLine($"  [{(usagePages.Contains(0x59) ? "?" : "X")}] Windows Dynamic Lighting needs HID LampArray usage page 0x59 — "
			+ (usagePages.Contains(0x59) ? "PRESENT!" : "absent"));
		Console.WriteLine($"  [{(reportIds.Contains(0xFD) ? "~" : "X")}] Vendor channel 0xFD      declared, but 12 command framings x 16 "
			+ "operation modes were tried on 2026-08-07 and none moved the LED");
		Console.WriteLine();
		Console.WriteLine("  The Xear SDK adds NOTHING here: it has no LED API of any kind. Its only");
		Console.WriteLine("  lighting property, SystemDynamicLighting, is a READ of the Windows setting.");
		Console.WriteLine();
		Console.WriteLine("  What would actually unblock this, in order of likelihood:");
		Console.WriteLine("    1. Section 1 finding a live CM002X1 — the LED spec targets that exact part.");
		Console.WriteLine("    2. A USB capture of vendor software driving the LED. Decodes it in one session.");
		Console.WriteLine("    3. The vendor stating the 0xFD framing for PID_036D.");
		Console.WriteLine();
		Console.WriteLine("  Existing write arms, if you want to re-drive the documented sequence:");
		Console.WriteLine("    starlight led --i-mean-it      starlight effects --i-mean-it      starlight sweep --i-mean-it");
	}

	private static void XearHint()
	{
		Console.WriteLine();
		Console.WriteLine("  Full Xear SDK property matrix (68 OK / 156 license-gated / 12 unsupported):");
		Console.WriteLine("    dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight xear --activate");
		Console.WriteLine("  Findings: docs/headset/starlight/starlight-research-2026-09.md");
	}

	// ── HID plumbing ──────────────────────────────────────────────────────────────────────────

	private static HidDevice? FindVendorInterface()
	{
		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(() => d.GetMaxOutputReportLength(), 0) == VendorReportLength);

		if (device is null)
		{
			Console.WriteLine($"  No interface with a {VendorReportLength}-byte output report found.");
		}

		return device;
	}

	private static string Len(Func<int> get)
	{
		try { return get().ToString(); }
		catch { return "?"; }
	}

	private static int Len(Func<int> get, int fallback)
	{
		try { return get(); }
		catch { return fallback; }
	}

	private static byte[]? TryGetInputReport(string devicePath)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		using var handle = Open(devicePath);
		if (handle is null || handle.IsInvalid)
		{
			return null;
		}

		var buffer = new byte[VendorReportLength];
		buffer[0] = VendorReportId;
		return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
	}

	private static bool TrySetOutputReport(string devicePath, byte[] report)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		using var handle = Open(devicePath);
		return handle is not null && !handle.IsInvalid
			&& HidD_SetOutputReport(handle, report, report.Length);
	}

	// Shared read/write: Windows keeps this interface open, so an exclusive open would fail.
	private static SafeFileHandle? Open(string devicePath)
	{
		if (string.IsNullOrEmpty(devicePath))
		{
			return null;
		}

		try
		{
			return CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
		}
		catch
		{
			return null;
		}
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
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
