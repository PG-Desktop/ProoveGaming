using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// PG TAIPAN — does the mouse need to be put into a CONFIGURATION MODE before it will accept a
// flash write over the 2.4G dongle?
//
// Origin: the vendor's own Bus Hound capture of 2026-09-02 (their driver, wired). Three things in
// it that our writer has never done, in the order they appear:
//
//   1. `0x46 00 00 00 00` sent FIVE times, ~170 ms apart, before anything else — and only the fifth
//      is answered. `0x46` is the read/ACK form of `0x06 CMD_SetMouseState`. Bracketed against
//      item 3, this reads as ENTER CONFIGURATION MODE (mouse reporting suppressed while the tables
//      are rewritten).
//   2. A commit that is ONE frame — ValidBankID at table offset 0x007F with `driverPara4 = 0xD3` —
//      answered with `driverPara4 = 0xEE`. We send two frames (0xD2 ValidBankID, then an empty
//      0xD3) and have never seen an 0xEE.
//   3. After every table, `0x40 F3 <bitmask>` with the mask running 04, 01, 02, 08, 10 — apparently
//      per-table (button, config, DPI, LED, macro). Also answered `0xEE`. We send none of these.
//   4. `0x46 01 00 00 00` at the very end — EXIT configuration mode.
//
// The hypothesis this probe tests: over the cable the mouse accepts a table write regardless, so
// the missing handshake never mattered; over the radio it does, and that is why our dongle writes
// are answered with total silence (docs/mouse/taipan/taipan-dongle-ack-evidence-2026-08-18.md).
//
// SAFETY
//
// `CMD_SetMouseState` can DISABLE the mouse — that is very likely what `Para1 = 0x00` does here,
// and it is why the vendor's driver ends with `Para1 = 0x01`. Every exit path in this probe,
// including exceptions and Ctrl+C, re-sends the enable frame; see EnsureEnabled and the finally
// block in Run. If a run is ever killed hard and the mouse stops responding, unplug and replug it
// (or power-cycle it) — the state is not persisted.
//
// Stages A–C touch no flash at all and run by default. Stage D performs the real table write and
// is gated behind an explicit `--write`, because the write's first packet (0xD1) ERASES the flash
// sector: an interrupted sequence can leave the button table blank, which on BLAZE meant unusable
// buttons. Stage D therefore always sends the COMPLETE table and never a partial one.
internal static class TaipanConfigModeProbe
{
	private const int Vid = 0x248A;
	private const int WiredPid = 0xFB01;
	private const int DonglePid = 0x8373;

	// Commands, named from docs/mouse/taipan/protocol.md.
	private const byte CmdSetMouseState = 0x06;
	private const byte CmdSetMouseStateAck = 0x46;   // read/ACK form, the one the vendor sends
	private const byte CmdApply = 0x40;              // read/ACK form of 0x00 CMD_Reset
	private const byte CmdBattery = 0x48;
	private const byte CmdButtonRead = 0x43;

	private const byte ApplyPara1 = 0xF3;
	private const byte ApplyMaskButton = 0x04;

	// The reply marker the vendor's capture shows for a committed table and for every apply frame.
	private const byte CommittedPara4 = 0xEE;

	private const int ReadWindowMs = 600;
	private const int PacketDelayMs = 12;
	private const int HandshakeGapMs = 170;          // the spacing in the vendor capture
	private const int HandshakeAttempts = 5;         // they needed five; so do we, at most

	// Drives the SHIPPING TelinkMouseService over the dongle instead of hand-built frames. The probe
	// proving a framing works has never been the same claim as the app working: this closes that gap
	// by exercising the exact code path the DeviceHost uses.
	public static async Task RunServiceAsync(bool restore)
	{
		Console.WriteLine("PG TAIPAN — button remap over the 2.4G dongle, through TelinkMouseService");
		Console.WriteLine("This is the SHIPPING code path, not hand-built frames.");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, DonglePid).FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null)
		{
			Console.WriteLine("No vendor collection on the dongle. Is it plugged in and the mouse on?");
			return;
		}

		Console.WriteLine($"Vendor collection: {device.DevicePath}");
		Console.WriteLine();

		// Wake the mouse: it sleeps quickly, and a sleepy unit answers nothing for a second or two.
		Console.WriteLine("Waking the mouse (battery query x3)...");
		if (device.TryOpen(out var wake))
		{
			using (wake)
			{
				wake.ReadTimeout = 200;
				for (var i = 0; i < 3; i++)
				{
					wake.Write(TaipanFrame.Build(CmdBattery, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
					var got = Drain(wake, 600);
					Console.WriteLine($"  attempt {i + 1}: {got.Count} frame(s)");
					if (got.Count > 0) break;
				}
			}
		}
		Console.WriteLine();

		var service = new TelinkMouseService(device, null, deviceKey: string.Empty);

		Console.WriteLine("Writing: right click -> My PC ...");
		var ok = await service.SetButtonBindingsAsync(
			[new ButtonBinding(MouseButton.Right, MouseButtonAction.MyComputer, null)]);
		Console.WriteLine($"  SetButtonBindingsAsync returned {ok}");
		Console.WriteLine();
		Console.WriteLine("  Press the RIGHT mouse button — File Explorer should open if it took.");
		Console.WriteLine("  Waiting 15 s...");
		await Task.Delay(15000);
		Console.WriteLine();

		if (restore)
		{
			Console.WriteLine("Restoring the factory button map...");
			var back = await service.SetButtonBindingsAsync(
				[new ButtonBinding(MouseButton.Right, MouseButtonAction.RightClick, null)]);
			Console.WriteLine($"  restore returned {back}");
		}
		else
		{
			Console.WriteLine("--keep given: the remap is left in place.");
		}
	}

	// Counts how many times the mouse is dropped into configuration mode by ONE apply that touches
	// two tables (DPI stage table + MsConfig active stage). Each entry is a visible ~1 s freeze, so
	// "two freezes with a pause between" is literally two sessions. Expect exactly one.
	public static async Task RunApplyFreezeCountAsync()
	{
		Console.WriteLine("PG TAIPAN — how many configuration-mode sessions does one apply use?");
		Console.WriteLine("Writing the DPI stage table AND the active stage in a single apply.");
		Console.WriteLine("Each session = one visible freeze. Expect ONE.");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, DonglePid).FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null)
		{
			Console.WriteLine("No vendor collection on the dongle.");
			return;
		}

		if (device.TryOpen(out var wake))
		{
			using (wake)
			{
				wake.ReadTimeout = 200;
				for (var i = 0; i < 3 && Drain(wake, 600).Count == 0; i++)
					wake.Write(TaipanFrame.Build(CmdBattery, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
			}
		}

		// The logger is how the sessions are counted: EnterConfigModeAsync logs on failure only, so
		// instead we count the enter/exit FRAMES by watching the service's own debug output is not
		// available here — count sessions by timing the freeze instead, and report the elapsed time.
		var service = new TelinkMouseService(device, null, deviceKey: string.Empty);

		var stages = new[]
		{
			new DpiStageParams(800, 0, 0, 0), new DpiStageParams(1600, 0, 0, 0),
			new DpiStageParams(3200, 0, 0, 0), new DpiStageParams(6400, 0, 0, 0),
		};

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var ok = await service.ApplyConfigurationAsync(new DeviceConfiguration
		{
			DpiStages = stages,
			ActiveStage = 1,
		});
		sw.Stop();

		Console.WriteLine($"  ApplyConfigurationAsync -> {ok} in {sw.ElapsedMilliseconds} ms");
		Console.WriteLine();
		Console.WriteLine("  Watch the mouse: it should have frozen ONCE, not twice with a gap.");
	}

	// Battery over the dongle, parsed the way the SERVICE parses it. The raw frame arriving is not
	// the same claim as the App showing a level: an 0x48 reply whose app flag is set carries the
	// checksum of the same frame with Para4 = 0, and the parser used to reject exactly those.
	public static void RunBatteryParseAsync()
	{
		Console.WriteLine("PG TAIPAN — battery over the dongle, through the real parser");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, DonglePid).FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null || !device.TryOpen(out var stream))
		{
			Console.WriteLine("No dongle vendor collection, or it is held by another process.");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = 200;
			for (var attempt = 1; attempt <= 4; attempt++)
			{
				stream.Write(TaipanFrame.Build(CmdBattery, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
				foreach (var f in Drain(stream, 700))
				{
					var appFlag = f.Length > 7 ? f[7] : (byte)0;
					var parsed = TaipanStateReport.TryParseBattery(f, out var raw, out var usb);
					var absent = TaipanStateReport.IsMouseAbsentReply(f);
					Console.WriteLine($"  {Hex(f)[..23]}...  appFlag=0x{appFlag:X2}");
					Console.WriteLine(parsed
						? $"    PARSED raw={raw} -> {TaipanStateReport.PercentFromRaw(raw)}%  usb={usb}"
						: absent ? "    parsed as MOUSE ABSENT (zero reading)"
								 : "    >>> REJECTED by the parser <<<");
				}
				Thread.Sleep(200);
			}
		}
	}

	public static void Run(string transport, bool write, bool restore)
	{
		bool wired = transport.Equals("wired", StringComparison.OrdinalIgnoreCase);
		int pid = wired ? WiredPid : DonglePid;

		Console.WriteLine("PG TAIPAN — configuration-mode handshake probe");
		Console.WriteLine($"Transport: {(wired ? "WIRED (cable)" : "2.4G DONGLE")}   VID 0x{Vid:X4} PID 0x{pid:X4}");
		Console.WriteLine($"Stage D (real table write): {(write ? "ENABLED (--write)" : "skipped — pass --write to enable")}");
		Console.WriteLine();
		Console.WriteLine(wired
			? "Cable run. This is the CONTROL: it shows what the handshake does on a transport where\n" +
			  "configuration already works, so the dongle run has a reference."
			: "UNPLUG THE USB CABLE FIRST. While cabled the mouse runs in wired mode and the radio\n" +
			  "link is idle, so a dongle result would prove nothing.");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, pid).FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null)
		{
			Console.WriteLine($"No {TaipanFrame.Length}/{TaipanFrame.Length} vendor collection on PID 0x{pid:X4}.");
			Console.WriteLine(wired ? "Is the cable plugged in?" : "Is the dongle plugged in and the mouse switched on?");
			return;
		}

		Console.WriteLine($"Vendor collection: {device.DevicePath}");
		Console.WriteLine();

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("TryOpen failed — another process (the DeviceHost?) may hold the interface.");
			Console.WriteLine("Stop the ProoveGamingDeviceHost service and re-run.");
			return;
		}

		bool entered = false;

		using (stream)
		{
			stream.ReadTimeout = 200;

			// Ctrl+C must not leave the mouse disabled.
			ConsoleCancelEventHandler onCancel = (_, e) =>
			{
				e.Cancel = true;
				Console.WriteLine("\n(interrupted — re-enabling the mouse before exit)");
				EnsureEnabled(stream);
				Environment.Exit(1);
			};
			Console.CancelKeyPress += onCancel;

			try
			{
				if (!PreFlight(stream)) return;

				entered = StageA_EnterConfigMode(stream);
				StageB_Apply(stream);
				StageC_ReadBack(stream);

				if (write)
				{
					if (!entered && !wired)
					{
						Console.WriteLine(new string('=', 96));
						Console.WriteLine("STAGE D — SKIPPED");
						Console.WriteLine("  The mouse never acknowledged the config-mode handshake, so a write now would");
						Console.WriteLine("  only reproduce the silence we already have. Nothing was written.");
						Console.WriteLine();
					}
					else
					{
						StageD_Write(stream, restore);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"!! {ex.GetType().Name}: {ex.Message}");
			}
			finally
			{
				Console.CancelKeyPress -= onCancel;
				// Unconditional: the mouse must never be left in configuration mode.
				EnsureEnabled(stream);
			}
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("SUMMARY");
		Console.WriteLine($"  Config-mode handshake acknowledged: {(entered ? "YES" : "no")}");
		Console.WriteLine("  Paste this transcript into docs/mouse/taipan/ and into the vendor thread.");
	}

	// The battery request is the one command PROVEN to round-trip on the dongle, so it establishes
	// that the link is live before anything is concluded from silence further down.
	private static bool PreFlight(HidStream stream)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("PRE-FLIGHT — battery request (0x48), the known-good round-trip");
		var replies = Send(stream, "0x48 battery", TaipanFrame.Build(CmdBattery, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));

		if (replies.Count == 0)
		{
			Console.WriteLine("  NO REPLY to the command we know replies. The link is down or the interface is");
			Console.WriteLine("  held elsewhere — every result below would be meaningless. Stopping.");
			return false;
		}
		return true;
	}

	// STAGE A — the handshake. Writes no flash.
	private static bool StageA_EnterConfigMode(HidStream stream)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("STAGE A — enter configuration mode: 0x46 Para1=0x00, up to 5 attempts");
		Console.WriteLine("  The vendor's driver sends this five times ~170 ms apart and only the fifth is");
		Console.WriteLine("  answered. Touches no flash.");
		Console.WriteLine();

		for (int attempt = 1; attempt <= HandshakeAttempts; attempt++)
		{
			var replies = Send(stream, $"attempt {attempt}/{HandshakeAttempts}",
				TaipanFrame.Build(CmdSetMouseStateAck, 0x00, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty));

			if (replies.Count > 0)
			{
				Console.WriteLine($"  >>> ACKNOWLEDGED on attempt {attempt}. The mouse is in configuration mode.");
				Console.WriteLine();
				return true;
			}
			Thread.Sleep(HandshakeGapMs);
		}

		Console.WriteLine("  >>> No acknowledgement after 5 attempts.");
		Console.WriteLine();
		return false;
	}

	// STAGE B — the apply command. Writes no table; asks the firmware to (re)load one.
	private static void StageB_Apply(HidStream stream)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("STAGE B — apply command: 0x40 Para1=0xF3 Para2=0x04 (button table)");
		Console.WriteLine("  Sent by the vendor after every table and never by us. Expected reply p4=0xEE.");
		Console.WriteLine();

		var replies = Send(stream, "0x40 F3 04", TaipanFrame.Build(CmdApply, ApplyPara1, ApplyMaskButton, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
		ReportCommitted(replies);
	}

	// STAGE C — read-back. Has never answered on any transport; retried now that the mouse may be
	// in configuration mode.
	private static void StageC_ReadBack(HidStream stream)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("STAGE C — read the button map back: 0x43");
		Console.WriteLine("  Has never answered before. If configuration mode unlocks it, we finally get a");
		Console.WriteLine("  way to VERIFY a write instead of pressing the button and guessing.");
		Console.WriteLine();

		Send(stream, "0x43 read button map", TaipanFrame.Build(CmdButtonRead, 0x00, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
	}

	// STAGE D — the real write, in the vendor's exact framing. Gated behind --write.
	private static void StageD_Write(HidStream stream, bool restore)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("STAGE D — button table write, vendor framing (right click -> My PC)");
		Console.WriteLine("  The first packet (0xD1) ERASES the flash sector, so the COMPLETE table is always");
		Console.WriteLine("  sent. Differences from our shipping writer: the commit is ONE frame (ValidBankID");
		Console.WriteLine("  with p4=0xD3, not 0xD2 + an empty 0xD3), and it is followed by the 0x40 apply.");
		Console.WriteLine();

		var mypc = TaipanButtonMap.BuildTable(
			[new ButtonBinding(MouseButton.Right, MouseButtonAction.MyComputer, null)]);
		WriteTable(stream, mypc, "REMAP");

		Console.WriteLine("  Press the right mouse button now — File Explorer should open if the write took.");
		Console.WriteLine("  Waiting 15 s...");
		Thread.Sleep(15000);
		Console.WriteLine();

		if (restore)
		{
			Console.WriteLine("  Restoring the factory button map.");
			WriteTable(stream, TaipanButtonMap.CreateDefault(), "RESTORE");
		}
		else
		{
			Console.WriteLine("  --keep given: the remap is left in place.");
		}
	}

	// One complete table write in the vendor's framing.
	private static void WriteTable(HidStream stream, byte[] table, string label)
	{
		const byte cmd = TaipanButtonMap.ButtonCommand;
		const int chunk = 24;

		Console.WriteLine($"  [{label}] header (0xD1, erases the sector)");
		Send(stream, "header", TaipanFrame.Build(cmd, 0x00, 0x00, TaipanButtonMap.HeaderLength, TaipanFrame.SeqFirst,
			table.AsSpan(0, TaipanButtonMap.HeaderLength)));

		int entries = table.Length - TaipanButtonMap.HeaderLength;
		for (int written = 0; written < entries; written += chunk)
		{
			int len = Math.Min(chunk, entries - written);
			int offset = TaipanButtonMap.HeaderLength + written;
			Send(stream, $"chunk @0x{offset:X4} ({len} bytes)", TaipanFrame.Build(
				cmd, (byte)(offset & 0xFF), (byte)(offset >> 8), (byte)len, TaipanFrame.SeqContinue,
				table.AsSpan(offset, len)));
		}

		// THE CHANGE: one combined commit frame, as the vendor sends, instead of our two.
		Console.WriteLine($"  [{label}] combined commit — ValidBankID with p4=0xD3 (vendor form)");
		var commit = Send(stream, "commit", TaipanFrame.Build(
			cmd, TaipanButtonMap.ValidBankIdOffset & 0xFF, TaipanButtonMap.ValidBankIdOffset >> 8, 0x01,
			TaipanFrame.SeqCommit, [TaipanButtonMap.ValidBankId]));
		ReportCommitted(commit);

		Console.WriteLine($"  [{label}] apply — 0x40 F3 04");
		var applied = Send(stream, "apply", TaipanFrame.Build(CmdApply, ApplyPara1, ApplyMaskButton, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
		ReportCommitted(applied);
	}

	// Re-enables mouse reporting. Safe to call more than once, and called on every exit path —
	// leaving the device in configuration mode is the one outcome this probe must never produce.
	private static void EnsureEnabled(HidStream stream)
	{
		try
		{
			stream.Write(TaipanFrame.Build(CmdSetMouseStateAck, 0x01, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
			Thread.Sleep(PacketDelayMs);
			// Belt and braces: the write form too, with the Para1 = 0x01 our shipping code already
			// uses and which is the only value on this command we have ever confirmed safe.
			stream.Write(TaipanFrame.Build(CmdSetMouseState, 0x01, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
			Thread.Sleep(PacketDelayMs);
		}
		catch
		{
			// The stream may already be gone; nothing further can be done here, and the state is
			// not persisted — a replug clears it.
		}
	}

	private static void ReportCommitted(List<byte[]> replies)
	{
		var committed = replies.Any(f => f.Length > 7 && f[7] == CommittedPara4);
		Console.WriteLine(committed
			? "    >>> p4=0xEE seen — the firmware reports this as COMMITTED."
			: "    (no 0xEE marker in the reply)");
		Console.WriteLine();
	}

	private static List<byte[]> Send(HidStream stream, string label, byte[] frame)
	{
		Console.WriteLine($"  {label}");
		Console.WriteLine($"    -> {Hex(frame)}");

		try
		{
			stream.Write(frame);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    !! WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
			return [];
		}

		var replies = Drain(stream, ReadWindowMs);
		if (replies.Count == 0)
		{
			Console.WriteLine("    <- (silence)");
		}
		else
		{
			foreach (var f in replies)
			{
				Console.WriteLine($"    <- {Hex(f)}");
				if (f.Length > 7)
					Console.WriteLine($"       cmd=0x{f[3]:X2} p1=0x{f[4]:X2} p2=0x{f[5]:X2} p3=0x{f[6]:X2} p4=0x{f[7]:X2}");
			}
		}

		Thread.Sleep(PacketDelayMs);
		return replies;
	}

	private static List<byte[]> Drain(HidStream stream, int windowMs)
	{
		var frames = new List<byte[]>();
		var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);

		while (DateTime.UtcNow < deadline)
		{
			var buf = new byte[TaipanFrame.Length];
			int n;
			try { n = stream.Read(buf); }
			catch (TimeoutException) { continue; }
			catch { break; }

			if (n > 0) frames.Add(buf.AsSpan(0, n).ToArray());
		}

		return frames;
	}

	private static string Hex(byte[] data) => string.Join(' ', data.Select(b => b.ToString("X2")));

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return -1; }
	}
}
