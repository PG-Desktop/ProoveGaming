using HidSharp;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── PG Cosmo — active interrogation of vendor report 0xBC ─────────────────────────────────────
//
// Context: the passive probe (CosmoStatusProbe) proved the dongle pushes NOTHING on 0xBC — zero
// frames across mic mute, power off/on and volume. So battery / power / mic state, if reachable at
// all, must be POLLED. This probe does the polling.
//
// It runs in two stages, safest first:
//
//   STAGE A — GET_REPORT (read-only, zero risk).
//     HidD_GetInputReport issues a USB control-transfer READ of an input report. No command byte is
//     guessed and nothing is written to the device. If the firmware keeps a current-state input
//     report, this returns it outright. This had not been tried before and costs nothing.
//
//   STAGE B — opcode sweep (WRITES; opt-in, guarded).
//     Only runs with the explicit "write" argument. Sends [0xBC][opcode][00...] and listens for a
//     reply. Guarded as described below.
//
// Why a sweep is dangerous, and what limits the danger here:
//   The documented sibling report 0xFD puts its COMMAND BYTE at offset 1 (0x11 = I2C write,
//   0x12 = I2C read, 0x13 = reply), so 0xBC very likely follows the same shape. The risk is landing
//   on a command that means "factory reset" or "enter firmware update" instead of "report status".
//   Mitigations:
//     * Payload is all zeroes. A destructive command usually needs a magic argument; an all-zero
//       payload is the least likely to satisfy one.
//     * A denylist skips the opcode ranges those commands habitually live in (see SkipOpcode).
//     * The sweep starts at 0x01 and stays low — status/query opcodes cluster there.
//     * After every single write the device is re-enumerated; the sweep ABORTS the moment the
//       device disappears, so a bad opcode stops the run rather than being followed by 200 more.
//
// This is the probe the BLAZE incident argues against running casually. It exists because passive
// listening is exhausted and the user asked for it explicitly.
internal static class CosmoPokeProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;
	private const byte VendorReportId = 0xBC;
	private const byte ConsumerReportId = 0x03;

	public static void Run(bool allowWrites, byte firstOpcode = 0x01, byte lastOpcode = 0x1F)
	{
		Console.WriteLine("PG Cosmo — active 0xBC interrogation");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault();
		if (device is null)
		{
			Console.WriteLine("Not found. Is the Cosmo dongle plugged in?");
			return;
		}

		Console.WriteLine($"Interface: {device.DevicePath}");
		Console.WriteLine();

		StageA_GetInputReport(device);

		if (!allowWrites)
		{
			Console.WriteLine();
			Console.WriteLine("Stage B (opcode sweep) NOT run — it writes to the device.");
			Console.WriteLine("Re-run with:  -- poke write     to enable it.");
			return;
		}

		StageB_OpcodeSweep(device, firstOpcode, lastOpcode);
	}

	// ── Stage A: control-transfer read, no writes ─────────────────────────────
	private static void StageA_GetInputReport(HidDevice device)
	{
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("STAGE A — HidD_GetInputReport (read-only control transfer, no writes)");
		Console.WriteLine(new string('=', 78));

		using var handle = OpenDevice(device.DevicePath);
		if (handle is null || handle.IsInvalid)
		{
			Console.WriteLine("  Could not open a raw handle to the interface.");
			return;
		}

		foreach (var reportId in new[] { VendorReportId, ConsumerReportId })
		{
			// The buffer must be the interface's max INPUT report length, with byte 0 = report ID.
			var length = Math.Max(32, SafeLen(device));
			var buffer = new byte[length];
			buffer[0] = reportId;

			if (HidD_GetInputReport(handle, buffer, buffer.Length))
			{
				Console.WriteLine($"  0x{reportId:X2}  OK   {Hex(buffer)}");
				if (reportId == VendorReportId && buffer.Skip(1).Any(b => b != 0))
					Console.WriteLine("       ^^ NON-ZERO vendor payload — this may already be the state we want.");
			}
			else
			{
				var err = Marshal.GetLastWin32Error();
				Console.WriteLine($"  0x{reportId:X2}  FAIL (win32 {err}) — firmware does not answer GET_REPORT for this id");
			}
		}
	}

	// ── Stage A2: guided GET_REPORT watch (still read-only) ───────────────────
	//
	// Stage A proved the firmware answers GET_REPORT on 0xBC with a live payload. Polling that same
	// control-transfer read while the user changes physical state tells us which byte means what —
	// the correlation the passive listener could never get, because the dongle pushes nothing.
	//
	// Still zero writes: GET_REPORT is a read. The opcode sweep is not needed if this succeeds.
	public static void RunWatch()
	{
		Console.WriteLine("PG Cosmo — 0xBC state watch (READ-ONLY: GET_REPORT polling, no writes)");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault();
		if (device is null)
		{
			Console.WriteLine("Not found. Is the Cosmo dongle plugged in?");
			return;
		}

		using var handle = OpenDevice(device.DevicePath);
		if (handle is null || handle.IsInvalid)
		{
			Console.WriteLine("Could not open a raw handle to the interface.");
			return;
		}

		var length = Math.Max(32, SafeLen(device));
		byte[]? previous = null;
		var changedOffsets = new Dictionary<int, SortedSet<string>>();
		var valuesByOffset = new Dictionary<int, SortedSet<byte>>();

		(string Label, int Seconds, string Instruction)[] phases =
		[
			("idle",       10, "Leave the headset ON and untouched (baseline)."),
			("mic-mute",   18, "Mute and UNMUTE the microphone a few times."),
			("power-off",  20, "Switch the headset OFF. Leave it off for the whole phase."),
			("power-on",   20, "Switch the headset back ON."),
			("final-idle",  8, "Leave it alone again."),
		];

		foreach (var (label, seconds, instruction) in phases)
		{
			Console.WriteLine();
			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"PHASE: {label}   ({seconds}s)");
			Console.WriteLine($"  >>> {instruction}");
			Console.WriteLine(new string('=', 78));

			var deadline = DateTime.UtcNow.AddSeconds(seconds);
			while (DateTime.UtcNow < deadline)
			{
				var buffer = new byte[length];
				buffer[0] = VendorReportId;

				if (!HidD_GetInputReport(handle, buffer, buffer.Length))
				{
					// A failing read is itself a signal: the dongle may stop answering when the
					// headset is off, which would make power state detectable on its own.
					Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{label,-10}] GET_REPORT FAILED (win32 {Marshal.GetLastWin32Error()})");
					Thread.Sleep(700);
					continue;
				}

				for (var i = 0; i < buffer.Length; i++)
				{
					if (!valuesByOffset.TryGetValue(i, out var set))
						valuesByOffset[i] = set = [];
					set.Add(buffer[i]);
				}

				if (previous is null || !buffer.AsSpan().SequenceEqual(previous))
				{
					var changed = new List<int>();
					if (previous is not null)
					{
						for (var i = 0; i < buffer.Length; i++)
						{
							if (buffer[i] == previous[i]) continue;
							changed.Add(i);
							if (!changedOffsets.TryGetValue(i, out var p))
								changedOffsets[i] = p = [];
							p.Add(label);
						}
					}

					Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{label,-10}] {Hex(buffer)}"
						+ (changed.Count > 0 ? $"   changed@ {string.Join(',', changed)}" : "   (first)"));
					previous = buffer;
				}

				Thread.Sleep(700);
			}
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("SUMMARY — offsets that ever changed");
		Console.WriteLine(new string('=', 78));

		if (changedOffsets.Count == 0)
		{
			Console.WriteLine("  Nothing changed. The payload is static, so it is not live state.");
			return;
		}

		foreach (var (offset, phasesSeen) in changedOffsets.OrderBy(k => k.Key))
		{
			var values = valuesByOffset[offset];
			var shown = values.Count <= 12
				? string.Join(' ', values.Select(v => v.ToString("X2")))
				: $"{values.Count} distinct, {values.Min():X2}..{values.Max():X2}";
			Console.WriteLine($"  byte {offset,2}: {shown,-40} changed during: {string.Join('/', phasesSeen)}");
		}
	}

	// ── Stage B: guarded opcode sweep (writes) ────────────────────────────────
	private static void StageB_OpcodeSweep(HidDevice device, byte first, byte last)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 78));
		Console.WriteLine($"STAGE B — opcode sweep 0x{first:X2}..0x{last:X2}  ** THIS WRITES TO THE DEVICE **");
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("  Payload is all zeroes. Dangerous opcode ranges are skipped.");
		Console.WriteLine("  The sweep aborts immediately if the device stops enumerating.");
		Console.WriteLine();

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("  Could not open the interface for writing.");
			return;
		}

		var outLength = Math.Max(32, SafeOutLen(device));
		var inLength = Math.Max(32, SafeLen(device));

		using (stream)
		{
			stream.ReadTimeout = 400;

			for (int op = first; op <= last; op++)
			{
				var opcode = (byte)op;
				if (SkipOpcode(opcode))
				{
					Console.WriteLine($"  0x{opcode:X2}  SKIPPED (denylisted — reset / firmware-update range)");
					continue;
				}

				var frame = new byte[outLength];
				frame[0] = VendorReportId;
				frame[1] = opcode;

				try
				{
					stream.Write(frame);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  0x{opcode:X2}  write failed: {ex.GetType().Name}: {ex.Message}");
					continue;
				}

				// Drain whatever the device says back within the read timeout.
				var replies = new List<byte[]>();
				var deadline = DateTime.UtcNow.AddMilliseconds(500);
				while (DateTime.UtcNow < deadline)
				{
					var buf = new byte[inLength];
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { break; }
					catch { break; }
					if (n > 0) replies.Add(buf.AsSpan(0, n).ToArray());
				}

				if (replies.Count == 0)
				{
					Console.WriteLine($"  0x{opcode:X2}  (no reply)");
				}
				else
				{
					foreach (var r in replies)
					{
						var marker = r[0] == VendorReportId ? "  <<< VENDOR REPLY" : "";
						Console.WriteLine($"  0x{opcode:X2}  -> {Hex(r)}{marker}");
					}
				}

				// Health check after EVERY write: a device that vanished means the last opcode did
				// something we did not intend, and continuing would compound it.
				Thread.Sleep(150);
				if (!DeviceList.Local.GetHidDevices(Vid, Pid).Any())
				{
					Console.WriteLine();
					Console.WriteLine($"  !! DEVICE DISAPPEARED after opcode 0x{opcode:X2} — ABORTING THE SWEEP.");
					Console.WriteLine("  !! Unplug and replug the dongle, then power-cycle the headset.");
					Console.WriteLine($"  !! Record this: opcode 0x{opcode:X2} must never be sent again.");
					return;
				}
			}
		}

		Console.WriteLine();
		Console.WriteLine("Sweep finished, device still enumerating.");
		Console.WriteLine("Any line marked '<<< VENDOR REPLY' is a live opcode worth pursuing.");
	}

	// Opcodes whose values are conventionally used for destructive operations across the HID
	// peripheral firmwares in this repo and in the wider vendor ecosystem. Skipping them costs a
	// few candidate values and removes the outcomes we cannot undo.
	private static bool SkipOpcode(byte opcode) => opcode switch
	{
		// Bootloader / DFU entry and factory reset live at the top of the space almost universally.
		>= 0xF0 => true,
		// Classic bootloader magics.
		0xA5 or 0x5A or 0xAA or 0x55 => true,
		// 0x20 is the Smart Infinite factory reset in this repo (different family, but the cost of
		// skipping one opcode is nothing next to the cost of being wrong).
		0x20 => true,
		// The Xear I2C bridge commands, in case 0xBC shares 0xFD's decoder.
		0x11 or 0x12 or 0x13 => false,
		_ => false,
	};

	// ── Win32 ─────────────────────────────────────────────────────────────────
	private static SafeFileHandle? OpenDevice(string? path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		const uint GenericRead = 0x80000000;
		const uint GenericWrite = 0x40000000;
		const uint FileShareRead = 1, FileShareWrite = 2;
		const uint OpenExisting = 3;

		var h = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
			IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
		return h;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);

	private static int SafeLen(HidDevice d)
	{
		try { return d.GetMaxInputReportLength(); } catch { return 32; }
	}

	private static int SafeOutLen(HidDevice d)
	{
		try { return d.GetMaxOutputReportLength(); } catch { return 32; }
	}

	private static string Hex(byte[] data)
	{
		var sb = new StringBuilder(data.Length * 3);
		foreach (var b in data) sb.Append(b.ToString("X2")).Append(' ');
		return sb.ToString().TrimEnd();
	}
}
