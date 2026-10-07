using System.Runtime.InteropServices;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight headset — Phase 0 probe ──────────────────────────────────────────────────────
//
// VID 0x0D8C (C-Media) / PID 0x036D, "Jieli Technology" / "PG Starlight".
// Plan and findings: docs/plans/add-starlight-headset/add-starlight-headset-plan.md
//
// The device has RGB (confirmed by the user), and we have a full command specification —
// "CM002X1 LED Control Specification" v0.3 (docs/headset/starlight/). But the spec puts LED control
// on report ID 0xFF, and this device does not declare 0xFF. Its only vendor report is 0xFD.
//
// A full device-tree survey shows no vendor/WinUSB interface either: MI_00 is the audio function and
// MI_03 is the only control surface. So if the LED is host-controllable at all, report 0xFD is the
// only route the commands could travel on. These arms are what settle that.
//
// ARMS AND WHAT THEY DO TO THE DEVICE:
//   (default)  READ-ONLY   descriptor survey — interfaces, report IDs, raw descriptors.
//   watch      READ-ONLY   guided passive listen on 0xFD; reports which byte offsets move when.
//   read       READ-ONLY   HidD_GetInputReport on 0xFD — a control-transfer READ, never a write.
//   led        WRITES      drives the documented LED sequence. Refuses to run without `--i-mean-it`.
//
// The `led` arm is the risky one: an unknown write to an unknown vendor channel is how the BLAZE
// flash corruption happened (docs/mouse/blaze), and this device persists LED state TO FLASH. It
// sends only the documented byte layout, one command at a time, and never sweeps opcodes.
internal static class StarlightProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	// The only vendor report this device declares: 32 bytes, 31 of them data, IN and OUT.
	private const byte VendorReportId = 0xFD;
	private const int VendorReportLength = 32;

	// From the LED spec: report ID 0xFF, 16 bytes total (report ID + command + 14 parameters).
	private const byte SpecLedReportId = 0xFF;
	private const int SpecLedReportLength = 16;

	// LED control commands (CM002X1 LED Control Specification v0.3).
	private const byte LedStart = 0x01;
	private const byte LedStop = 0x02;
	private const byte LedConfiguration = 0x03;
	private const byte LedColorArray = 0x04;

	// Operation modes for the Configuration command.
	private const byte ModeStatic = 0x00;

	// ── Descriptor survey (default arm, read-only) ────────────────────────────────────────────

	public static void Run()
	{
		Console.WriteLine($"PG Starlight — VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		var interfaces = FindInterfaces();
		if (interfaces.Count == 0) return;

		var ledCapable = false;

		foreach (var d in interfaces)
		{
			Console.WriteLine(new string('=', 96));
			Console.WriteLine(d.DevicePath);
			Console.WriteLine($"  in/out/feature max = {Len(() => d.GetMaxInputReportLength())}/" +
				$"{Len(() => d.GetMaxOutputReportLength())}/{Len(() => d.GetMaxFeatureReportLength())}");

			ReportDescriptor? rd = null;
			try { rd = d.GetReportDescriptor(); }
			catch (Exception ex) { Console.WriteLine($"  report descriptor unavailable: {ex.Message}"); }

			if (rd is not null)
			{
				foreach (var item in rd.DeviceItems)
				{
					Console.WriteLine("  collection usages: " +
						string.Join(", ", item.Usages.GetAllValues().Select(u => $"0x{u:X8}")));

					PrintReports("IN ", item.InputReports);
					PrintReports("OUT", item.OutputReports);
					PrintReports("FEA", item.FeatureReports);

					if (item.OutputReports.Any(r => r.ReportID == SpecLedReportId))
						ledCapable = true;
				}
			}

			try
			{
				var raw = d.GetRawReportDescriptor();
				Console.WriteLine($"  raw descriptor ({raw.Length} bytes): {BitConverter.ToString(raw)}");
			}
			catch (Exception ex) { Console.WriteLine($"  raw descriptor unavailable: {ex.Message}"); }

			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine(ledCapable
			? $"Output report 0x{SpecLedReportId:X2} IS declared — the LED spec applies directly."
			: $"No output report 0x{SpecLedReportId:X2} declared, so the spec's own channel does not\n" +
			  $"  exist here. Report 0x{VendorReportId:X2} is the only vendor channel — try `starlight led`.");
	}

	// ── Guided passive listen (read-only) ─────────────────────────────────────────────────────

	// Each phase is a physical action whose effect we expect to see on the wire. Same method that
	// decoded Cosmo's power state: correlate byte offsets against the phase they moved in.
	private static readonly (string Label, int Seconds, string Instruction)[] Phases =
	[
		("idle",       12, "Leave the headset alone."),
		("volume",     15, "Press volume + and volume - on the headset several times."),
		("mic-mute",   15, "Mute and unmute the microphone a few times."),
		("power-off",  18, "Switch the headset OFF. Leave it off until this phase ends."),
		("power-on",   18, "Switch the headset back ON."),
		("buttons",    15, "Press every other button on the headset."),
	];

	public static void RunWatch()
	{
		Console.WriteLine($"PG Starlight — guided listen on report 0x{VendorReportId:X2} (READ-ONLY)");
		Console.WriteLine();

		var device = FindVendorInterface();
		if (device is null) return;

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the vendor interface — is ProoveGamingDeviceHost stopped?");
			return;
		}

		// Offset -> the phases during which that offset was seen to change. This is what ties a
		// moving byte to the physical action that moved it.
		var changedInPhase = new Dictionary<int, SortedSet<string>>();
		var gate = new object();
		byte[]? previous = null;
		var frames = 0;
		var phase = "startup";

		using (stream)
		{
			stream.ReadTimeout = 500;
			using var cts = new CancellationTokenSource();

			var reader = Task.Run(() =>
			{
				var buf = new byte[Math.Max(VendorReportLength, Len(() => device.GetMaxInputReportLength(), 64))];
				while (!cts.IsCancellationRequested)
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { break; }
					if (n <= 0) continue;

					var frame = buf[..n];
					lock (gate)
					{
						frames++;
						if (previous is not null)
						{
							for (var i = 0; i < Math.Min(previous.Length, frame.Length); i++)
							{
								if (previous[i] == frame[i]) continue;
								if (!changedInPhase.TryGetValue(i, out var set))
									changedInPhase[i] = set = [];
								set.Add(phase);
							}
						}
						previous = frame;
					}
				}
			}, cts.Token);

			foreach (var (label, seconds, instruction) in Phases)
			{
				lock (gate) phase = label;
				Console.WriteLine(new string('-', 96));
				Console.WriteLine($"PHASE: {label}   ({seconds}s)");
				Console.WriteLine($"  {instruction}");
				Thread.Sleep(seconds * 1000);
			}

			cts.Cancel();
			reader.Wait(TimeSpan.FromSeconds(2));
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"Frames received: {frames}");

		if (frames == 0)
		{
			Console.WriteLine("The device pushed NOTHING on this report. That matches Cosmo, where the");
			Console.WriteLine("dongle was silent but still answered a control-transfer read — try `starlight read`.");
			return;
		}

		if (changedInPhase.Count == 0)
		{
			Console.WriteLine("Frames arrived but no byte ever changed — the payload is constant.");
			return;
		}

		Console.WriteLine("Offsets that moved, and the phase(s) they moved in:");
		foreach (var (offset, phases) in changedInPhase.OrderBy(kv => kv.Key))
			Console.WriteLine($"  byte {offset,2}: {string.Join(", ", phases)}");
	}

	// ── Control-transfer read (read-only) ─────────────────────────────────────────────────────

	public static void RunRead(int samples = 10)
	{
		Console.WriteLine($"PG Starlight — HidD_GetInputReport on 0x{VendorReportId:X2} (READ-ONLY)");
		Console.WriteLine("A control-transfer READ. No host->device write is involved.");
		Console.WriteLine();

		var device = FindVendorInterface();
		if (device is null) return;

		byte[]? first = null;

		for (var i = 0; i < samples; i++)
		{
			var frame = TryGetInputReport(device.DevicePath);
			if (frame is null)
			{
				Console.WriteLine($"  [{i + 1,2}] read FAILED");
			}
			else
			{
				Console.WriteLine($"  [{i + 1,2}] {BitConverter.ToString(frame)}");
				first ??= frame;
			}

			if (i < samples - 1) Thread.Sleep(1000);
		}

		Console.WriteLine();
		if (first is null)
		{
			Console.WriteLine($"0x{VendorReportId:X2} does not answer a control read. Unlike Cosmo's 0xBC, this");
			Console.WriteLine("channel is not readable — so there is no passive route to battery or state.");
			return;
		}

		Console.WriteLine("If every line above is identical, the payload is static and carries no live state.");
		Console.WriteLine("Re-run across a power cycle and a battery drain before concluding anything.");
	}

	// ── LED drive (WRITES — opt-in required) ──────────────────────────────────────────────────

	public static void RunLed(bool allowWrites, string variant = "direct",
		byte r = 0xFF, byte g = 0x00, byte b = 0x00, bool step = false)
	{
		Console.WriteLine($"PG Starlight — LED drive test ({variant.ToUpperInvariant()} variant)");
		Console.WriteLine();

		if (!allowWrites)
		{
			Console.WriteLine("REFUSED: this arm WRITES to an undocumented vendor channel.");
			Console.WriteLine();
			Console.WriteLine("  The device persists its LED table TO FLASH, and an unknown write to an unknown");
			Console.WriteLine("  channel is how the BLAZE flash corruption happened (docs/mouse/blaze).");
			Console.WriteLine();
			Console.WriteLine("  It sends only the documented CM002X1 layout — Stop, Configuration, Colour Array,");
			Console.WriteLine("  Start — one command at a time, and never sweeps opcodes.");
			Console.WriteLine();
			Console.WriteLine("  Re-run with the explicit opt-in to proceed:");
			Console.WriteLine("      dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight led --i-mean-it");
			return;
		}

		var device = FindVendorInterface();
		if (device is null) return;

		Console.WriteLine("Sending the documented sequence: Stop -> Configuration -> Colour Array -> Start.");
		Console.WriteLine($"Colour Array payload: {r:X2} {g:X2} {b:X2} at bytes 4-6 (the spec's RGB order).");

		switch (variant)
		{
			case "envelope":
				// 0xFD carries 31 data bytes for a 15-byte LED payload, so a tunnel is plausible:
				// the outer report wrapping the spec's frame with the inner report ID leading it.
				Console.WriteLine($"Wrapping each frame: byte 1 = 0x{SpecLedReportId:X2} (the spec's own");
				Console.WriteLine("report ID), then the documented command and parameters. 32 bytes on 0xFD.");
				break;

			case "native":
				// THE SPEC EXACTLY AS WRITTEN: report ID 0xFF, 16 bytes. Skipped in the first two
				// rounds on the assumption that an undeclared report ID cannot be written — that was
				// an inference, never a tested fact. HidD_SetOutputReport is used directly because
				// HidSharp pads every write to the collection's declared 32-byte output length,
				// which would corrupt a report the spec defines as exactly 16.
				Console.WriteLine($"Report ID 0x{SpecLedReportId:X2}, 16 bytes — the specification");
				Console.WriteLine("verbatim, sent with HidD_SetOutputReport so the length is not padded.");
				break;

			case "short":
				// The documented 16-byte length, but on the report ID this device actually declares.
				// Firmware that dispatches on report length would reject the 32-byte version.
				Console.WriteLine($"Report ID 0x{VendorReportId:X2} at the specification's 16-byte length,");
				Console.WriteLine("rather than the 32 bytes the descriptor declares.");
				break;

			default:
				Console.WriteLine($"Report ID 0x{VendorReportId:X2}, 32 bytes, spec frame at byte 1.");
				break;
		}

		Console.WriteLine();

		// The four documented commands, in the documented order. Stop first: the spec requires the
		// sequence to begin from a stopped state, and Stop is what erases the stored table so we are
		// not layering onto an unknown one. Static mode fixes array size at 1 and interval at 0.
		// All multi-byte fields little-endian (spec v0.2).
		(string Label, byte[] Body)[] sequence =
		[
			("Stop", [LedStop]),
			("Configuration (Static, 1 colour, 0 ms)", [LedConfiguration, ModeStatic, 0x01, 0x00, 0x00, 0x00]),
			($"Colour Array (offset 0, bytes 4-6 = {r:X2} {g:X2} {b:X2})",
				[LedColorArray, 0x00, 0x00, r, g, b]),
			("Start", [LedStart]),
		];

		if (step)
		{
			Console.WriteLine("STEPWISE: each command is sent on its own, then it waits. Look at the LED");
			Console.WriteLine("after every step and note WHICH command changed it. That is the whole point —");
			Console.WriteLine("a colour that never changes tells us nothing about the colour bytes, but it");
			Console.WriteLine("tells us a great deal about which commands the firmware actually parses.");
			Console.WriteLine();
		}

		// The raw variants deliberately do NOT open a HidSharp stream. They talk to the device
		// through CreateFileW + HidD_SetOutputReport, which is the only way to send a report ID the
		// descriptor does not declare, or a length other than the declared one. Requiring a HidSharp
		// open first would let a failed TryOpen block the spec-verbatim attempt — the variant most
		// likely to work — for a handle it never uses.
		if (variant is "native" or "short")
		{
			var reportId = variant == "native" ? SpecLedReportId : VendorReportId;
			foreach (var (label, body) in sequence)
			{
				if (!SendRaw(device.DevicePath, reportId, body, label)) return;
				Pause(step, label);
			}

			return;
		}

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the vendor interface — is ProoveGamingDeviceHost stopped?");
			return;
		}

		using (stream)
		{
			foreach (var (label, body) in sequence)
			{
				if (!Send(stream, variant == "envelope", label, body)) return;
				Pause(step, label);
			}
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("Sequence sent.");
		Console.WriteLine();
		Console.WriteLine("The colour has not followed the payload across two different values, so the");
		Console.WriteLine("colour bytes are not reaching the LED. What matters now is WHICH command lands.");
		Console.WriteLine();
		Console.WriteLine("  Static green appeared after STOP        -> only Stop is understood. Green is the");
		Console.WriteLine("     and nothing after it changed it         device's default state, per the spec:");
		Console.WriteLine("                                             \"drive the LED to default status\".");
		Console.WriteLine("  Static green appeared after CONFIG      -> Configuration lands; the Colour Array");
		Console.WriteLine("                                             command is the one being rejected.");
		Console.WriteLine("  Nothing changed until START            -> the stored table is being displayed,");
		Console.WriteLine("                                             so our Colour Array never replaced it.");
		Console.WriteLine();
		Console.WriteLine("Re-run with --step to find out which one it is.");
		Console.WriteLine();
		Console.WriteLine("Unplug and replug to check whether anything persisted to flash.");
	}

	// Writes one LED command as a 0xFD output report.
	//
	// A write completing tells us almost NOTHING. The spec promises the device stalls a malformed
	// set-output-report, but that describes report 0xFF on a CM002X1. Here 0xFD is a *declared*
	// 32-byte output report, so the HID stack accepts any 32 bytes and passes them to firmware
	// whether or not it understands them. Only the LED itself is evidence.
	private static bool Send(HidStream stream, bool envelope, string label, byte[] body)
	{
		var report = new byte[VendorReportLength];
		report[0] = VendorReportId;

		// Envelope variant: the spec's own report ID leads the payload, then its frame.
		var offset = 1;
		if (envelope)
		{
			report[1] = SpecLedReportId;
			offset = 2;
		}

		Buffer.BlockCopy(body, 0, report, offset, body.Length);

		Console.WriteLine($"  OUT  {BitConverter.ToString(report[..16])}   {label}");

		try
		{
			stream.Write(report);
			Thread.Sleep(50);
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine();
			Console.WriteLine($"  STALLED: {ex.Message}");
			Console.WriteLine("  The device rejected this frame, which the spec says is how it reports a bad");
			Console.WriteLine("  format. The payload layout is wrong for this firmware — do NOT sweep opcodes.");
			return false;
		}
	}

	// Between commands in stepwise mode: long enough to look at the headset and decide whether that
	// one command changed anything.
	private static void Pause(bool step, string label)
	{
		if (!step)
		{
			Thread.Sleep(50);
			return;
		}

		Console.WriteLine($"       ^ sent \"{label}\". Look at the LED now. Continuing in 6s...");
		Console.WriteLine();
		Thread.Sleep(6000);
	}

	// Writes one command at the specification's exact 16-byte length via HidD_SetOutputReport.
	//
	// HidSharp always pads a write to the collection's declared output length (32 here), so it
	// cannot express a 16-byte report at all. This path can, and it can also attempt a report ID the
	// descriptor does not declare — which is the whole point: whether Windows permits that is a
	// fact to measure, not to assume.
	private static bool SendRaw(string devicePath, byte reportId, byte[] body, string label)
	{
		var report = new byte[SpecLedReportLength];
		report[0] = reportId;
		Buffer.BlockCopy(body, 0, report, 1, body.Length);

		Console.WriteLine($"  OUT  {BitConverter.ToString(report)}   {label}");

		SafeFileHandle? handle = null;
		try
		{
			handle = CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

			if (handle.IsInvalid)
			{
				Console.WriteLine($"       open failed (Win32 {Marshal.GetLastWin32Error()})");
				return false;
			}

			if (HidD_SetOutputReport(handle, report, report.Length))
				return true;

			// ERROR_INVALID_PARAMETER (87) here means Windows rejected the report — almost certainly
			// because the ID or the length does not match the descriptor. That is a real answer.
			Console.WriteLine($"       REJECTED by Windows (Win32 {Marshal.GetLastWin32Error()})");
			return false;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"       failed: {ex.Message}");
			return false;
		}
		finally
		{
			handle?.Dispose();
		}
	}

	// ── Helpers ───────────────────────────────────────────────────────────────────────────────

	private static List<HidDevice> FindInterfaces()
	{
		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
			Console.WriteLine("Not found. Is the headset plugged in, and is ProoveGamingDeviceHost stopped?");
		return interfaces;
	}

	// The 32-byte in/out collection — the only one carrying the vendor report.
	private static HidDevice? FindVendorInterface()
	{
		var device = FindInterfaces().FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength(), 0) == VendorReportLength);

		if (device is null)
			Console.WriteLine($"No interface with a {VendorReportLength}-byte output report was found.");

		return device;
	}

	private static void PrintReports(string label, IEnumerable<Report> reports)
	{
		foreach (var r in reports)
			Console.WriteLine($"    {label} report 0x{r.ReportID:X2}  length={r.Length}");
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

	// HidD_GetInputReport — a USB control-transfer READ of the input report. Mirrors
	// CosmoStateReader's shape; kept local because Starlight has no confirmed report format yet and
	// an unproven device does not belong in Infrastructure.
	private static byte[]? TryGetInputReport(string devicePath)
	{
		if (string.IsNullOrEmpty(devicePath) || !OperatingSystem.IsWindows())
			return null;

		SafeFileHandle? handle = null;
		try
		{
			// Shared read/write: Windows holds this interface open too, so an exclusive open would
			// fail. We only read.
			handle = CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

			if (handle.IsInvalid) return null;

			var buffer = new byte[VendorReportLength];
			buffer[0] = VendorReportId;

			return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			handle?.Dispose();
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
