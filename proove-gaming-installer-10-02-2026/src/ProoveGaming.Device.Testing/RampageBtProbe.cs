using HidSharp;
using Microsoft.Win32.SafeHandles;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;
using System.Runtime.InteropServices;

namespace ProoveGaming.Device.Testing;

// ── Rampage over Bluetooth — vendor-channel probe ────────────────────────────────────────────────
//
// Answers the question docs/plans/add-rampage-bluetooth-rgb/ hangs on: while the Rampage keyboard
// is connected over Bluetooth LE, does Windows expose the SmartInfinite vendor collection
// (usage 0x20 / page 0xFF87) as an openable HID interface — and does the routine RGB write (0x15)
// work through it?
//
// Run with the keyboard connected over Bluetooth ONLY (no cable, ProoveGamingDeviceHost STOPPED):
//
//   stage 1 (always, READ-ONLY): enumerates EVERY HID device — no VID/PID filter, because a BLE
//     HID device takes its VID/PID from the DIS PnP-ID characteristic, which may differ from the
//     USB identity (0x28E9/0x3258) or be absent entirely. Reports each interface's path, VID/PID,
//     report lengths, and whether it carries the vendor usage; flags the Bluetooth-stack paths
//     (BTHENUM / BTHLEDEVICE).
//
//   stage 2 (always, READ-ONLY): for each Bluetooth vendor-usage candidate, reads the 64-byte
//     function/config block (0x14) — the block that holds the current RGB state. A correct reply
//     proves framing, checksum and transport end-to-end without writing anything.
//
//   stage 3 (`write` arg only, WRITES): drives Infrastructure's RampageKeyboardRgbService — the
//     SAME code path the DeviceHost uses for every colour change — against the Bluetooth
//     interface: static red, pause, static green, then a 0x14 read-back. Eyes on the LEDs decide;
//     write acks alone are not evidence (the key-matrix investigation proved acked-and-discarded
//     is this firmware's habit).
//
// Only documented, routinely-used commands are sent (0x14 read, and 0x15 via the production
// service). 0x20 (factory reset) and 0x31 round-trips are never sent — see
// docs/knowledge-base/rampage-keyboard-protocol/hardware-findings.md "Commands that must never be
// sent". Findings go into docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md.
internal static class RampageBtProbe
{
	private const int VendorUsagePage = 0xFF87;
	private const int VendorUsageId = 0x20;

	public static async Task RunAsync(bool allowWrites)
	{
		Console.WriteLine("Rampage Bluetooth vendor-channel probe");
		Console.WriteLine("Expected state: keyboard connected over Bluetooth ONLY, cable unplugged, DeviceHost stopped.");
		Console.WriteLine();

		var candidates = DumpAllInterfaces();

		if (candidates.Count == 0)
		{
			Console.WriteLine();
			Console.WriteLine(">>> RESULT: no Bluetooth-stack interface carries the vendor usage 0x20/page 0xFF87.");
			Console.WriteLine(">>> The SmartInfinite vendor channel is NOT exposed over BLE on this unit — record this");
			Console.WriteLine(">>> in bluetooth-findings.md; the plan's fallback (read-only RGB on BT + vendor request) applies.");
			return;
		}

		foreach (var device in candidates)
		{
			Console.WriteLine();
			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"Candidate: VID 0x{device.VendorID:X4} PID 0x{device.ProductID:X4}");
			Console.WriteLine($"  {device.DevicePath}");
			Console.WriteLine(new string('=', 78));

			DumpDescriptor(device);

			var funcBlock = ReadFuncBlock(device);
			if (funcBlock == null)
				continue;

			if (allowWrites)
				await WriteTestAsync(device);
			else
				Console.WriteLine("\n  Stage 3 skipped — re-run with `rampage-bt write` to attempt the 0x15 RGB write.");
		}
	}

	// ── stage 1: enumeration ─────────────────────────────────────────────────

	private static List<HidDevice> DumpAllInterfaces()
	{
		var all = DeviceList.Local.GetHidDevices()
			.OrderBy(d => d.VendorID).ThenBy(d => d.ProductID)
			.ThenBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		Console.WriteLine($"HID interfaces visible to the OS: {all.Count}");
		Console.WriteLine();

		var bluetoothCandidates = new List<HidDevice>();
		foreach (var d in all)
		{
			bool vendorUsage = HasVendorUsage(d, out var usageError);
			bool bluetooth = IsBluetoothPath(d.DevicePath);
			int outLen = Len(d, output: true);
			int inLen = Len(d, output: false);

			// Every interface is listed — the point of the dump is to see what the BLE identity
			// actually is, so nothing is filtered; markers make the interesting rows findable.
			Console.WriteLine(
				$"  {(bluetooth ? "BT " : "   ")}{(vendorUsage ? "VENDOR " : "       ")}" +
				$"VID 0x{d.VendorID:X4} PID 0x{d.ProductID:X4} out={outLen,3} in={inLen,3}  {d.DevicePath}");
			if (bluetooth && usageError != null)
				Console.WriteLine($"      (vendor usage unconfirmed: {usageError})");

			// The usage match is the vendor's own rule, but a BLE collection whose descriptor
			// cannot be parsed still qualifies by the 64/64 shape — the same fallback the
			// production SmartInfiniteInterfaceSelector keeps for exactly this case.
			if (bluetooth && (vendorUsage || (outLen == 64 && inLen == 64)))
				bluetoothCandidates.Add(d);
		}

		Console.WriteLine();
		Console.WriteLine($"Bluetooth-stack vendor-channel candidates (usage match or 64/64 shape): {bluetoothCandidates.Count}");
		return bluetoothCandidates;
	}

	// What the collection actually declares: its usages (the BLE build may use a different
	// vendor page than USB's FF87:20) and every report's type/id/length — the write must be
	// framed with the DECLARED output report id, or the HID stack rejects it before the radio.
	private static void DumpDescriptor(HidDevice device)
	{
		Console.WriteLine();
		Console.WriteLine("  Report descriptor of this collection");
		try
		{
			var desc = device.GetReportDescriptor();
			foreach (var item in desc.DeviceItems)
			{
				var usages = item.Usages.GetAllValues()
					.Select(u => $"{(u >> 16):X4}:{(u & 0xFFFF):X2}");
				Console.WriteLine($"    usages: {string.Join(", ", usages)}");
				foreach (var report in item.Reports)
					Console.WriteLine($"    report {report.ReportType,-7} id=0x{report.ReportID:X2} len={report.Length}");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    dump failed: {ex.Message}");
		}
	}

	// ── stage 2: 0x14 read ───────────────────────────────────────────────────

	// The 64-byte function/config block is read as two packets (offset 0 x 56, offset 56 x 8), no
	// session bracket — per the factory clarifications of 2026-08. Replies can run one command
	// behind the request (see hardware-findings.md), so they are matched by echoed command+address.
	private static byte[]? ReadFuncBlock(HidDevice device)
	{
		Console.WriteLine();
		Console.WriteLine("  Function/config block (0x14) — current RGB state lives here");

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("  Could not open the interface. Is ProoveGamingDeviceHost stopped?");
			return null;
		}

		using (stream)
		{
			// BLE needs slacker timeouts than the cable: a write rides the GATT connection
			// interval, and the first one also has to wake the link from its idle interval.
			stream.ReadTimeout = 3000;
			stream.WriteTimeout = 3000;

			var block = new byte[64];
			foreach (var (addr, len) in new[] { ((ushort)0, 56), ((ushort)56, 8) })
			{
				if (!WriteWithRetry(stream, device.DevicePath,
					RampageProtocol.BuildReadCommand(RampageProtocol.CmdReadFuncInfo, addr, (byte)len), addr))
					return null;

				var reply = ReplyFor(stream, RampageProtocol.CmdReadFuncInfo, addr);
				if (reply == null)
				{
					Console.WriteLine($"    no valid reply for addr {addr} — transport does not answer 0x14 here.");
					return null;
				}
				Buffer.BlockCopy(reply, RampageProtocol.ResponsePayloadOffset, block, addr, len);
				Thread.Sleep(4);
			}

			Console.WriteLine("    raw: " + string.Join("-", block.Take(24).Select(x => x.ToString("X2"))));
			Console.WriteLine($"    decoded: mode={block[1]} level={block[2]} speed={block[3]} RGB={block[5]} " +
				$"colour=#{block[6]:X2}{block[7]:X2}{block[8]:X2} colourIndex={block[9]}");
			Console.WriteLine("    >>> 0x14 READ WORKS over this interface — transport, framing and checksum confirmed.");
			return block;
		}
	}

	// ── stage 3: 0x15 via the production service ─────────────────────────────

	private static async Task WriteTestAsync(HidDevice device)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  WRITE TEST — RampageKeyboardRgbService (0x15), the DeviceHost's own RGB path");
		Console.WriteLine(new string('-', 78));

		var service = new RampageKeyboardRgbService(device);

		// App effect byte 0x04 = Static. Two clearly different colours so the change is
		// unmistakable on the board; whatever lighting the user had is restored later by the App's
		// reapply-on-connect, not by this probe.
		Console.WriteLine("    sending: Static RED, full brightness ...");
		var redOk = await service.SetLedColorAsync(0xFF, 0x00, 0x00, effect: 0x04, brightness: 0xFF, speed: 3);
		Console.WriteLine($"    write sequence reported: {(redOk ? "OK" : "FAILED")}   -> LOOK AT THE KEYBOARD");
		Thread.Sleep(3000);

		Console.WriteLine("    sending: Static GREEN, full brightness ...");
		var greenOk = await service.SetLedColorAsync(0x00, 0xFF, 0x00, effect: 0x04, brightness: 0xFF, speed: 3);
		Console.WriteLine($"    write sequence reported: {(greenOk ? "OK" : "FAILED")}   -> DID IT CHANGE TO GREEN?");
		Thread.Sleep(1000);

		// Read-back closes the acked-and-discarded loophole: 0x14 must now show mode=10 (SteadyOn)
		// and the green colour.
		var after = ReadFuncBlock(device);
		if (after != null)
			Console.WriteLine(after[1] == 10 && after[6] == 0x00 && after[7] == 0xFF
				? "    >>> READ-BACK MATCHES the green write — 0x15 PERSISTS over Bluetooth."
				: "    >>> read-back does NOT match the write — acked-and-discarded, record as not working.");
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	// Windows can fail the first write on a freshly opened HID handle (ERROR_INVALID_FUNCTION
	// while the previous handle is still closing — see hardware-findings.md), and a BLE link in a
	// slow connection interval can time the first write out. After the WriteFile attempts, fall
	// back to HidD_SetOutputReport: on HID-over-GATT some stacks accept output reports only
	// through the control path, and the observed candidate declares Output id=0x06 — exactly the
	// SetOutputReport shape.
	private static bool WriteWithRetry(HidStream stream, string devicePath, byte[] report, ushort addr)
	{
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			try
			{
				stream.Write(report);
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"    WriteFile attempt {attempt}/2 at addr {addr} failed: {ex.Message}");
				Thread.Sleep(200);
			}
		}

		if (SetOutputReport(devicePath, report))
		{
			Console.WriteLine($"    HidD_SetOutputReport at addr {addr}: OK (control-path write works where WriteFile does not)");
			return true;
		}

		if (Win32WriteFile(devicePath, report))
		{
			Console.WriteLine($"    Win32 WriteFile at addr {addr}: OK (raw handle write works where HidSharp does not)");
			return true;
		}
		return false;
	}

	// Third framing of the same write, on a raw synchronous handle — separates a HidSharp
	// overlapped-IO artifact from a genuine stack refusal. Bounded by a task wait so a write the
	// stack never completes cannot hang the probe.
	private static bool Win32WriteFile(string devicePath, byte[] report)
	{
		using var handle = NativeMethods.CreateFile(
			devicePath,
			NativeMethods.GenericRead | NativeMethods.GenericWrite,
			NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
			IntPtr.Zero,
			NativeMethods.OpenExisting,
			0,
			IntPtr.Zero);

		if (handle.IsInvalid)
		{
			Console.WriteLine($"    Win32 WriteFile open failed, err={Marshal.GetLastWin32Error()}");
			return false;
		}

		bool ok = false;
		int error = 0;
		var writeTask = Task.Run(() =>
		{
			ok = NativeMethods.WriteFile(handle, report, (uint)report.Length, out _, IntPtr.Zero);
			if (!ok) error = Marshal.GetLastWin32Error();
		});

		if (!writeTask.Wait(3000))
		{
			Console.WriteLine("    Win32 WriteFile did not complete within 3 s");
			return false;
		}
		if (!ok)
			Console.WriteLine($"    Win32 WriteFile failed, err={error}");
		return ok;
	}

	private static bool SetOutputReport(string devicePath, byte[] report)
	{
		using var handle = NativeMethods.CreateFile(
			devicePath,
			NativeMethods.GenericRead | NativeMethods.GenericWrite,
			NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
			IntPtr.Zero,
			NativeMethods.OpenExisting,
			0,
			IntPtr.Zero);

		if (handle.IsInvalid)
		{
			Console.WriteLine($"    HidD_SetOutputReport open failed, err={Marshal.GetLastWin32Error()}");
			return false;
		}

		if (!NativeMethods.HidD_SetOutputReport(handle, report, report.Length))
		{
			Console.WriteLine($"    HidD_SetOutputReport failed, err={Marshal.GetLastWin32Error()}");
			return false;
		}
		return true;
	}

	private static class NativeMethods
	{
		public const uint GenericRead = 0x80000000;
		public const uint GenericWrite = 0x40000000;
		public const uint FileShareRead = 0x00000001;
		public const uint FileShareWrite = 0x00000002;
		public const uint OpenExisting = 3;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern SafeFileHandle CreateFile(
			string lpFileName, uint dwDesiredAccess, uint dwShareMode,
			IntPtr lpSecurityAttributes, uint dwCreationDisposition,
			uint dwFlagsAndAttributes, IntPtr hTemplateFile);

		[DllImport("hid.dll", SetLastError = true)]
		public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] report, int reportLength);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer,
			uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
	}

	// Replies run one command behind the request on this firmware; match on echoed cmd + addr.
	private static byte[]? ReplyFor(HidStream stream, byte cmd, ushort addr)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			var reply = new byte[RampageProtocol.ReportLength];
			try
			{
				if (stream.Read(reply) < RampageProtocol.HeaderLength) continue;
			}
			catch (TimeoutException) { return null; }
			catch (Exception ex)
			{
				Console.WriteLine($"    read FAILED: {ex.Message}");
				return null;
			}

			int replyAddr = reply[2] | (reply[3] << 8);
			if (reply[1] == cmd && replyAddr == addr &&
				reply[RampageProtocol.ResponseStatusOffset] == RampageProtocol.ResponseSuccess)
				return reply;
		}
		return null;
	}

	// Same rule as SmartInfiniteInterfaceSelector.IsBluetoothDevicePath (internal to
	// Infrastructure): BTHENUM = classic Bluetooth HID, BTHLEDEVICE = HID over GATT, and current
	// Windows 11 builds the HOGP path from the HID service UUID (0x1812) instead.
	private static bool IsBluetoothPath(string? path) =>
		path != null &&
		(path.Contains("bthenum", StringComparison.OrdinalIgnoreCase) ||
		 path.Contains("bthle", StringComparison.OrdinalIgnoreCase) ||
		 path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase));

	private static bool HasVendorUsage(HidDevice d, out string? error)
	{
		error = null;
		try
		{
			return d.GetReportDescriptor().DeviceItems
				.SelectMany(i => i.Usages.GetAllValues())
				.Any(u => (u >> 16) == VendorUsagePage && (u & 0xFFFF) == VendorUsageId);
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return -1; }
	}
}
