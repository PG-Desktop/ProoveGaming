using HidSharp;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — what does the mouse REPLY to a configuration write? ────────────────────────────
//
// The vendor's answer (2026-08-18) was: "the data sent is exactly the same as with a wired
// connection. If there is no response, it means your proxy code is not connected correctly."
//
// That sentence assumes we looked at the response. We never have. TelinkMouseService.WriteFrameAsync
// is write-only — `stream.Write(frame)` then a 12 ms delay, no read — so every config write the app
// has ever issued, on EITHER transport, discarded whatever came back. "No response" was never
// measured; it was never asked for. That makes this probe the precondition for taking the vendor's
// claim to the next round: we cannot report what the mouse answers until we read it.
//
// WHAT IT DOES
//   Replays the button-map sequence EXACTLY as SetButtonBindingsAsync sends it — same table
//   builder, same command, same 0xD1 header / 0xD2 chunks / ValidBankID / 0xD3 commit order — but
//   drains and dumps the input pipe after every single frame.
//
// WHY IT RUNS ON BOTH TRANSPORTS
//   `dongle` alone cannot answer the question. If the dongle returns nothing we still would not
//   know whether that is the radio link failing or simply how this command behaves, because nobody
//   has ever recorded the wired replies either. The cable run is the REFERENCE: the vendor says the
//   two are identical, so a difference between them is the finding, and a match means "silent" is
//   just what this command does and the vendor's premise is wrong.
//
//   Run the cable first, then the dongle, and compare.
//
// WRITES. The documented button-map command over the documented collection — the same bytes the app
// already sends, and the same write `taipan config` has been performing. The 0xD1 header erases the
// button flash sector; the restore step at the end puts the default map back.
internal static class TaipanConfigResponseProbe
{
	private const int Vid = 0x248A;
	private const int DonglePid = 0x8373;   // 2.4G receiver
	private const int WiredPid = 0xFB01;    // cable

	// Mirrors TelinkMouseService's private constants. Duplicated rather than exposed because the
	// point is to send what the service sends; if these ever drift, this probe is measuring a
	// sequence the app does not use and the comparison is worthless.
	private const int EntryPacketBytes = 24;
	private const int PacketDelayMs = 12;

	// How long to keep reading after each frame before calling it silent. Far longer than the
	// service's 12 ms pacing — a reply we time out on is indistinguishable from no reply, and the
	// whole question here is which of those is happening. Slower pacing is safe (the sequencing
	// hazard on these devices is bursts that are too FAST), but it is a deviation worth naming.
	private const int ReadWindowMs = 150;

	// CMD_SetMouseState — a command that touches no flash, used by Sweep() to tell a collection that
	// answers reads from one that processes commands. See the note on Sweep() for why Para1 is not
	// varied.
	private const byte SetMouseStateCommand = 0x46;

	public static void Run(string transport = "dongle", string buttonName = "right",
		int holdSeconds = 20, bool restore = true)
	{
		bool wired = transport.Equals("wired", StringComparison.OrdinalIgnoreCase);
		int pid = wired ? WiredPid : DonglePid;

		Console.WriteLine("PG TAIPAN — response capture for a configuration write");
		Console.WriteLine($"Transport: {(wired ? "WIRED (cable)" : "2.4G DONGLE")}   VID 0x{Vid:X4} PID 0x{pid:X4}");
		Console.WriteLine();
		Console.WriteLine(wired
			? "Plug the USB cable in. This run is the REFERENCE — it records what the mouse replies\n" +
			  "when configuration is known to work, so the dongle run has something to be compared to."
			: "UNPLUG THE USB CABLE FIRST. While cabled the mouse runs in wired mode and the radio\n" +
			  "link is idle, so a dongle test proves nothing.");
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

		using (stream)
		{
			stream.ReadTimeout = 200;

			// PRE-FLIGHT. The battery request is the one command already PROVEN to round-trip on the
			// dongle (2026-08-07), so it establishes two things before any flash is touched: the link
			// is live, and this collection does deliver replies at all. Without it, a silent config
			// write could just be a dead link.
			Console.WriteLine(new string('=', 96));
			Console.WriteLine("PRE-FLIGHT — battery request (0x48), the known-good round-trip");
			stream.Write(TaipanFrame.Build(0x48, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
			var preflight = Drain(stream, 600);
			Dump(preflight, "    ");

			if (preflight.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine("  NO REPLY to the command we know replies. The link is down or the interface is");
				Console.WriteLine("  held elsewhere — a config-write result from this run would be meaningless.");
				return;
			}

			foreach (var f in preflight)
			{
				if (TaipanStateReport.TryParseBattery(f, out var raw, out var usb))
				{
					Console.WriteLine($"    decoded: battery raw={raw} ({TaipanStateReport.PercentFromRaw(raw)}%), usb={usb}");
					if (!wired && usb)
					{
						Console.WriteLine();
						Console.WriteLine("  ABORTING: the mouse reports USB connected, so it is in wired mode and this");
						Console.WriteLine("  would test the cable, not the dongle. Unplug the cable and re-run.");
						return;
					}
				}
			}

			Console.WriteLine();

			var (button, target) = Resolve(buttonName.ToLowerInvariant());

			Console.WriteLine(new string('=', 96));
			Console.WriteLine($"WRITE — remapping {button.ToString().ToUpperInvariant()} -> {target}, one frame at a time");
			Console.WriteLine();

			int replies = WriteSequence(stream, Bindings(button, target));

			Console.WriteLine();
			Console.WriteLine(new string('=', 96));
			Console.WriteLine($"RESULT — {replies} reply frame(s) across the whole write sequence");
			Console.WriteLine();

			if (replies == 0)
			{
				Console.WriteLine("  The mouse answered the battery request but said NOTHING to any frame of the");
				Console.WriteLine("  config write. Since the read path demonstrably works on this collection, this");
				Console.WriteLine("  is a real silence, not a missed read — which is exactly the point to put back");
				Console.WriteLine("  to the vendor, quoting the frames above.");
			}
			else
			{
				Console.WriteLine("  The write IS answered. Compare these frames against the other transport's run:");
				Console.WriteLine("  same replies = the dongle behaves as the cable and the write is being accepted;");
				Console.WriteLine("  different    = the divergence is the finding, at the frame where it starts.");
			}

			Console.WriteLine();
			Console.WriteLine($"  Now press the {button.ToString().ToUpperInvariant()} button. {holdSeconds}s.");
			Console.WriteLine("    FILE EXPLORER / THIS PC OPENS  -> the write TOOK EFFECT.");
			Console.WriteLine($"    still behaves as {Normal(button),-14} -> the write did not take.");
			Console.WriteLine();
			Console.WriteLine("  (What the button actually does is the ground truth here — replies only tell us");
			Console.WriteLine("   what the firmware SAYS, and the two can disagree.)");

			Thread.Sleep(holdSeconds * 1000);

			if (!restore)
			{
				Console.WriteLine();
				Console.WriteLine("  --keep given: leaving the remap in place. Re-run without it, or apply any");
				Console.WriteLine("  preset from the app over the cable, to restore the default map.");
				return;
			}

			Console.WriteLine();
			Console.WriteLine(new string('=', 96));
			Console.WriteLine($"RESTORE — {button.ToString().ToUpperInvariant()} -> {Normal(button)}");
			Console.WriteLine();
			WriteSequence(stream, Bindings(button, Normal(button)));
			Console.WriteLine();
			Console.WriteLine($"  Press {button.ToString().ToUpperInvariant()} again — it should behave normally now.");
		}
	}

	// ── Endpoint map ───────────────────────────────────────────────────────────────────────────
	//
	// Closes the one hypothesis the wired/dongle comparison cannot rule out on its own. The vendor
	// said "your proxy code is not connected correctly", and the most plausible reading of that is
	// "you are talking to the wrong endpoint" — the dongle exposes several HID collections and we
	// picked ours by a 33/33 in/out length filter.
	//
	// So: send both a documented QUERY and a documented COMMAND to every collection on the dongle
	// and record which ones answer.
	//
	//   0x48  battery request  — a pure read, proven to round-trip since 2026-08-07.
	//   0x46  CMD_SetMouseState — a COMMAND that touches no flash, so it separates "this collection
	//         answers reads" from "this collection processes commands". Para1 = 0x01 (enabled) is
	//         reused from TaipanDongleProbe, never guessed: the parameter is undocumented beyond
	//         enable/disable and a wrong value on this command can DISABLE the mouse.
	//
	// READ-ONLY with respect to flash — neither command writes a table, so this is safe to run
	// across every interface. If the collection we have been using is the only one that answers,
	// the wrong-endpoint explanation is dead and the silence on 0x03 is the finding.
	public static void Sweep()
	{
		Console.WriteLine("PG TAIPAN — dongle endpoint map (no flash written)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{DonglePid:X4}");
		Console.WriteLine();
		Console.WriteLine("UNPLUG THE USB CABLE FIRST — otherwise the mouse is in wired mode.");
		Console.WriteLine();

		var all = DeviceList.Local.GetHidDevices(Vid, DonglePid).ToList();
		if (all.Count == 0)
		{
			Console.WriteLine("No dongle interfaces found. Is the receiver plugged in?");
			return;
		}

		Console.WriteLine($"{all.Count} interface(s) on the dongle PID:");
		Console.WriteLine();

		foreach (var d in all)
		{
			int inLen = Len(() => d.GetMaxInputReportLength());
			int outLen = Len(() => d.GetMaxOutputReportLength());

			Console.WriteLine($"  {d.DevicePath}");
			Console.WriteLine($"    in={inLen} out={outLen}");

			if (outLen != TaipanFrame.Length || inLen != TaipanFrame.Length)
			{
				Console.WriteLine("    skipped — not a 33/33 vendor collection, cannot carry the frame format.");
				Console.WriteLine();
				continue;
			}

			if (!d.TryOpen(out var stream))
			{
				Console.WriteLine("    TryOpen FAILED — held by another process (the DeviceHost?).");
				Console.WriteLine();
				continue;
			}

			using (stream)
			{
				stream.ReadTimeout = 200;

				stream.Write(TaipanFrame.Build(0x48, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
				var battery = Drain(stream, 600);
				Console.WriteLine($"    0x48 battery : {(battery.Count > 0 ? "REPLY" : "silence")}");
				Dump(battery, "      ");

				stream.Write(TaipanFrame.Build(SetMouseStateCommand, 0x01, 0, 0, 0, ReadOnlySpan<byte>.Empty));
				var echo = Drain(stream, 600);
				Console.WriteLine($"    0x46 command : {(echo.Count > 0 ? "REPLY" : "silence")}");
				Dump(echo, "      ");
			}

			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("READING THE RESULT");
		Console.WriteLine();
		Console.WriteLine("  Only the collection we already use answers  -> there is no other endpoint to try;");
		Console.WriteLine("     the silence on the 0x03 button-map write is a property of that command over the");
		Console.WriteLine("     radio link, not of our endpoint choice.");
		Console.WriteLine();
		Console.WriteLine("  Some OTHER collection answers 0x46          -> that is the candidate config channel;");
		Console.WriteLine("     re-run `taipan response dongle` against it before reporting anything to the vendor.");
	}

	// The button-map write, frame for frame as SetButtonBindingsAsync issues it, with the input pipe
	// drained after each one. Returns the total number of reply frames seen.
	//
	// Macro upload is deliberately omitted: none of these bindings is macro-bound, so the service
	// would skip it too (UploadMacrosAsync is a no-op when nothing is macro-bound).
	private static int WriteSequence(HidStream stream, List<ButtonBinding> bindings)
	{
		var table = TaipanButtonMap.BuildTable(bindings);
		byte cmd = TaipanButtonMap.ButtonCommand;
		int replies = 0;

		// 1) Header (max-event count) — first packet, ERASES the flash sector.
		replies += Frame(stream, "HEADER   (0xD1, erases sector)", TaipanFrame.Build(
			cmd, 0x00, 0x00, TaipanButtonMap.HeaderLength, TaipanFrame.SeqFirst,
			table.AsSpan(0, TaipanButtonMap.HeaderLength)));

		// 2) Entry region (table offset 0x0002 onward) in entry-aligned chunks.
		int entriesLength = table.Length - TaipanButtonMap.HeaderLength;
		int chunk = 0;
		for (int written = 0; written < entriesLength; written += EntryPacketBytes)
		{
			int len = Math.Min(EntryPacketBytes, entriesLength - written);
			int tableOffset = TaipanButtonMap.HeaderLength + written;
			replies += Frame(stream, $"CHUNK {++chunk,-2} (0xD2, offset 0x{tableOffset:X4}, {len} B)", TaipanFrame.Build(
				cmd, (byte)(tableOffset & 0xFF), (byte)(tableOffset >> 8), (byte)len, TaipanFrame.SeqContinue,
				table.AsSpan(tableOffset, len)));
		}

		// 3) ValidBankID -> commit.
		replies += Frame(stream, "VALIDBANK(0xD2, bank id 0x1A)", TaipanFrame.Build(
			cmd, TaipanButtonMap.ValidBankIdOffset & 0xFF, TaipanButtonMap.ValidBankIdOffset >> 8, 0x01,
			TaipanFrame.SeqContinue, [TaipanButtonMap.ValidBankId]));
		replies += Frame(stream, "COMMIT   (0xD3)", TaipanFrame.Build(
			cmd, 0x00, 0x00, 0x00, TaipanFrame.SeqCommit, ReadOnlySpan<byte>.Empty));

		return replies;
	}

	// Writes one frame, then reads until the pipe goes quiet. Prints what went out and what came
	// back, so the console transcript is directly quotable to the vendor.
	private static int Frame(HidStream stream, string label, byte[] frame)
	{
		Console.WriteLine($"  {label}");
		Console.WriteLine($"    -> {Hex(frame, frame.Length)}");

		try
		{
			stream.Write(frame);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    !! WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
			return 0;
		}

		var replies = Drain(stream, ReadWindowMs);
		if (replies.Count == 0)
			Console.WriteLine("    <- (silence)");
		else
			Dump(replies, "    ");

		Thread.Sleep(PacketDelayMs);
		Console.WriteLine();
		return replies.Count;
	}

	// Collects every input report that arrives within the window. Returns them in order.
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

	private static void Dump(List<byte[]> frames, string indent)
	{
		foreach (var f in frames)
		{
			Console.WriteLine($"{indent}<- {Hex(f, f.Length)}");

			// Byte3 is driverCMD — the command the device is answering about. Byte7 is driverPara4,
			// which carries the sequencing value on the way out and may echo status on the way back.
			if (f.Length > 7)
				Console.WriteLine($"{indent}   cmd=0x{f[3]:X2} p1=0x{f[4]:X2} p2=0x{f[5]:X2} p3=0x{f[6]:X2} p4=0x{f[7]:X2}");
		}
	}

	private static string Hex(byte[] data, int len) =>
		string.Join(' ', data.Take(len).Select(b => b.ToString("X2")));

	// Which button to remap, and to what. MyComputer is unmistakable on the desk — File Explorer
	// opens — which matters because the reply frames alone cannot tell us the write took effect.
	private static (MouseButton Button, MouseButtonAction Target) Resolve(string name) => name switch
	{
		"wheel" => (MouseButton.Wheel, MouseButtonAction.MyComputer),
		"forward" => (MouseButton.Forward, MouseButtonAction.MyComputer),
		"back" => (MouseButton.Back, MouseButtonAction.MyComputer),
		_ => (MouseButton.Right, MouseButtonAction.MyComputer),
	};

	private static MouseButtonAction Normal(MouseButton button) => button switch
	{
		MouseButton.Left => MouseButtonAction.LeftClick,
		MouseButton.Right => MouseButtonAction.RightClick,
		MouseButton.Wheel => MouseButtonAction.WheelClick,
		MouseButton.Forward => MouseButtonAction.Forward,
		_ => MouseButtonAction.Back,
	};

	// Every remappable button named explicitly — the vendor's "full data set", identical to what the
	// wired path sends. Anything omitted would be written as its default rather than left alone.
	private static List<ButtonBinding> Bindings(MouseButton changed, MouseButtonAction action) =>
	[
		new(MouseButton.Left, changed == MouseButton.Left ? action : MouseButtonAction.LeftClick),
		new(MouseButton.Right, changed == MouseButton.Right ? action : MouseButtonAction.RightClick),
		new(MouseButton.Wheel, changed == MouseButton.Wheel ? action : MouseButtonAction.WheelClick),
		new(MouseButton.Forward, changed == MouseButton.Forward ? action : MouseButtonAction.Forward),
		new(MouseButton.Back, changed == MouseButton.Back ? action : MouseButtonAction.Back),
	];

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
