using HidSharp;

// T800 (Rongyuan RY5088) lighting probe.
//
// The transport is settled: mi_02's report descriptor is
//   06 FF FF  09 02  A1 01  09 02  25 7F ... 75 08  95 40  B1 02  C1 00
// i.e. usage page 0xFFFF / usage 0x02, a FEATURE report of 64 bytes with NO report ID — exactly
// the control transfer the vendor spec prints for every command (21 09 00 03 02 00 40 00). Writes
// to it are accepted by Windows without error. What is NOT settled is the payload: the CS byte is
// marked in every packet layout and defined by neither vendor document, so a rejected command is
// indistinguishable from an accepted one on the write side.
//
// This probe resolves that by READING BACK rather than by looking at the keyboard:
//
//   [1] Checksum      — mi_02 feature reports are bidirectional, so GET_LEDPARAM (0x87) can be
//                       sent under each candidate CS rule and the response inspected. A sane echo
//                       means the firmware parsed the command, which means that CS is the right
//                       one. Machine-checkable, no human judgement.
//   [2] Learn         — listens on mi_01 col05 (INPUT, report ID 0x05, the vendor-code channel).
//                       Pressing the keyboard's own lighting hotkeys makes the firmware announce
//                       its CURRENT effect / speed / brightness / option, which is ground truth
//                       for the encoding independent of any document.
//   [3] Write test    — only meaningful once [1] has settled the checksum.
//
// Run with ProoveGamingDeviceHost STOPPED and the T800 connected by USB.
public static class T800LedProbe
{
	private const int VID = 0x3151; // 12625
	private const int PID = 0x502D; // 20525

	// mi_02: 64 feature bytes + 1 report-ID byte. Windows always wants the report-ID byte at [0]
	// even for a descriptor that declares none, so protocol byte N lives at buffer index N+1.
	private const int ReportLen = 65;

	// mi_01 col05: 31 input bytes + report ID 0x05.
	private const int VendorCodeReportLen = 32;
	private const byte VendorCodeReportId = 0x05;

	private const byte CmdSetLedOnOff = 0x05;
	private const byte CmdSetLedParam = 0x07;
	private const byte CmdGetLedParam = 0x87;
	private const byte CmdGetInfo = 0x8F;

	private const byte EffectStatic = 0x01;
	private const byte ColourAp = 7;
	private const byte MaxBrightness = 4;

	private enum ChecksumKind
	{
		OnesComplement, // 0xFF - (sum & 0xFF) — the real one, per the ROYUAN notes
		Sum,            // (sum of protocol bytes) & 0xFF
		NegatedSum,     // two's complement, so the frame sums to zero
		Xor,            // XOR of the protocol bytes
		Zero,           // no checksum — proves whether the firmware validates it at all
	}

	// Non-interactive sweep, for running without a human watching the keyboard.
	//
	// The read-back idea in [1] turns out not to work on this firmware: GetFeature returns the
	// driver's cached copy of the last SetFeature, so a GET_LEDPARAM "reply" is just our own
	// request echoed and proves nothing. What DOES work is the notification channel — the firmware
	// announces state changes on col05 (`05 03 xx 04` for main-light on/off, `05 04 xx` for
	// effect), so a command it actually parsed is observable without looking at the board.
	//
	// Sends only SET_LEDONOFF (0x05) and SET_LEDPARAM (0x07). Deliberately never touches reset
	// (0x01), flash, or the bootloader sequence.
	public static void RunAuto()
	{
		var all = DeviceList.Local.GetHidDevices(VID, PID).ToList();
		var control = all.FirstOrDefault(d => Safe(d, 'f') == ReportLen
			&& d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true);
		var notify = all.FirstOrDefault(d => Safe(d, 'i') == VendorCodeReportLen
			&& d.DevicePath?.Contains("col05", StringComparison.OrdinalIgnoreCase) == true);

		Console.WriteLine($"control={control?.DevicePath ?? "MISSING"}");
		Console.WriteLine($"notify ={notify?.DevicePath ?? "MISSING"}");
		if (control == null) return;

		var seen = new List<string>();
		using var stop = new CancellationTokenSource();
		var listener = notify == null ? null : Task.Run(() =>
		{
			if (!notify.TryOpen(out var s)) { Console.WriteLine("!! could not open notify channel"); return; }
			using (s)
			{
				s.ReadTimeout = 300;
				var buf = new byte[VendorCodeReportLen];
				while (!stop.IsCancellationRequested)
				{
					int n;
					try { n = s.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { break; }
					if (n < 4 || buf[0] != VendorCodeReportId) continue;
					lock (seen) seen.Add($"{string.Join(' ', buf.Take(5).Select(x => x.ToString("X2")))}  {DescribeVendorCode(buf)}");
				}
			}
		});

		Thread.Sleep(500);
		Drain(seen, "baseline (before any command)");

		// The decisive test. The firmware echoes a reply with the opcode in byte 0 and the current
		// LED state behind it — but only for a command it accepted, so a GET_LEDPARAM that comes
		// back with a populated body identifies the correct checksum on its own. (GetFeature
		// otherwise returns the driver's cached copy of our own request, which is why an all-zero
		// body proves rejection rather than "the light is set to zero".)
		Console.WriteLine("\n### GET_LEDPARAM read-back per checksum candidate");
		foreach (ChecksumKind kind in Enum.GetValues<ChecksumKind>())
		{
			var request = new byte[ReportLen];
			request[1] = CmdGetLedParam;
			request[8] = Checksum(request, 1, 7, kind);

			var reply = Exchange(control, request);
			var body = reply?.Skip(1).Take(9).ToArray();
			bool populated = body != null && body.Skip(1).Any(x => x != 0x00);

			Console.WriteLine($"  {kind,-15} sent {string.Join(' ', request.Skip(1).Take(9).Select(x => x.ToString("X2")))}"
				+ $"  ->  {(body == null ? "no reply" : string.Join(' ', body.Select(x => x.ToString("X2"))))}"
				+ (populated ? "   <== ACCEPTED" : ""));

			if (populated && body != null)
				Console.WriteLine($"        effect=0x{body[1]:X2} speed={body[2]} bri={body[3]} option=0x{body[4]:X2} (colour slot {body[4] & 0x0F}, variant {body[4] >> 4}) rgb={body[5]:X2}{body[6]:X2}{body[7]:X2}");
		}

		foreach (ChecksumKind kind in Enum.GetValues<ChecksumKind>())
		{
			Console.WriteLine($"\n=== checksum = {kind} ===");

			Send(control, BuildOnOff(mainOff: true, kind), "LEDONOFF off");
			Thread.Sleep(800);
			Drain(seen, "  after off");

			Send(control, BuildOnOff(mainOff: false, kind), "LEDONOFF on");
			Thread.Sleep(800);
			Drain(seen, "  after on");

			Send(control, BuildLedParam(EffectStatic, 0, MaxBrightness, ColourAp, 0xFF, 0x00, 0x00, kind), "LEDPARAM static red");
			Thread.Sleep(800);
			Drain(seen, "  after param");

			var verify = new byte[ReportLen];
			verify[1] = CmdGetLedParam;
			verify[8] = Checksum(verify, 1, 7, kind);
			var back = Exchange(control, verify);
			if (back != null)
				Console.WriteLine($"  read-back: {string.Join(' ', back.Skip(1).Take(9).Select(x => x.ToString("X2")))}");
		}

		// Leave the board lit whatever happened, so a sweep never ends with a dark keyboard.
		foreach (ChecksumKind kind in Enum.GetValues<ChecksumKind>())
			Send(control, BuildOnOff(mainOff: false, kind), $"restore on ({kind})");

		stop.Cancel();
		listener?.Wait(2000);
		Console.WriteLine("\nSweep complete.");
	}

	// Sends a hand-supplied frame (protocol bytes, no report ID, no CS — the correct checksum is
	// appended) and reads the state back. Used to confirm that the bytes T800KeyboardRgbService
	// builds are the bytes the firmware retains, without this project having to depend on it.
	public static void RunSend(string[] hex)
	{
		var control = DeviceList.Local.GetHidDevices(VID, PID)
			.FirstOrDefault(d => Safe(d, 'f') == ReportLen
				&& d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true);
		if (control == null) { Console.WriteLine("mi_02 not found"); return; }

		var packet = new byte[ReportLen];
		for (int i = 0; i < hex.Length; i++)
			packet[i + 1] = Convert.ToByte(hex[i], 16);

		// CS position follows the parameter count: byte 8 for the 7-parameter LEDPARAM/SLEDPARAM,
		// byte 7 for everything else.
		bool bit8 = packet[1] is CmdSetLedParam or 0x08;
		packet[bit8 ? 9 : 8] = Checksum(packet, 1, bit8 ? 8 : 7, ChecksumKind.OnesComplement);

		Console.WriteLine($"send:      {string.Join(' ', packet.Skip(1).Take(10).Select(x => x.ToString("X2")))}");
		Send(control, packet, "frame");
		Thread.Sleep(150);

		var request = new byte[ReportLen];
		request[1] = CmdGetLedParam;
		request[8] = Checksum(request, 1, 7, ChecksumKind.OnesComplement);
		var reply = Exchange(control, request);
		if (reply == null) { Console.WriteLine("no reply"); return; }

		var b = reply.Skip(1).Take(9).ToArray();
		Console.WriteLine($"read-back: {string.Join(' ', b.Select(x => x.ToString("X2")))}");
		Console.WriteLine($"  effect=0x{b[1]:X2} speed={b[2]} bri={b[3]} option=0x{b[4]:X2} (colour slot {b[4] & 0x0F}, variant {b[4] >> 4}) rgb={b[5]:X2}{b[6]:X2}{b[7]:X2}");
	}

	// Speed ladder. The firmware stores any speed byte we send without clamping — 0, 6 and 7 all
	// read back unchanged — so read-back cannot say which values are meaningful, nor which end of
	// the range is fast. Only watching the board can.
	//
	// It matters because the two sources disagree: the YC3123 LED Param Table calls the field 0–4,
	// while the ROYUAN notes say the wire carries 5..1 (inverted). If the truth is 0..5 then `0` is
	// a faster step than anything the vendor app emits, and the app's 100% should map to it.
	//
	// Runs Wave in solid red so the direction of travel is obvious, holding each step long enough
	// to judge.
	public static void RunSpeedLadder()
	{
		var control = DeviceList.Local.GetHidDevices(VID, PID)
			.FirstOrDefault(d => Safe(d, 'f') == ReportLen
				&& d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true);
		if (control == null) { Console.WriteLine("mi_02 not found"); return; }

		// Breathing, not Wave. A pulse rate is unmistakable even when the difference is small,
		// whereas a wave travelling across a compact board is genuinely hard to time by eye — if
		// the first ladder looked flat, that may be why rather than the speed byte being inert.
		Console.WriteLine("Breathing, solid red. Each step holds 8s and is announced before it starts.");
		Console.WriteLine("Count the pulses in each step. Watch for: does the PULSE RATE change at all?\n");

		for (byte wire = 0; wire <= 5; wire++)
		{
			// Re-latch first. Some firmware in this family only re-reads the animation parameters
			// when the effect itself changes, so writing a new speed under an unchanged effect can
			// be stored (read-back confirms it) and yet never applied. Dropping to Static for a
			// moment forces the next write to be a genuine effect change.
			var relatch = new byte[ReportLen];
			relatch[1] = CmdSetLedParam;
			relatch[2] = EffectStatic;
			relatch[3] = 3;
			relatch[4] = MaxBrightness;
			relatch[5] = ColourAp;
			relatch[6] = 0xFF;
			relatch[9] = Checksum(relatch, 1, 8, ChecksumKind.OnesComplement);
			Send(control, relatch, "  (re-latch: static)");
			Thread.Sleep(400);

			var p = new byte[ReportLen];
			p[1] = CmdSetLedParam;
			p[2] = 0x02;        // Breathe
			p[3] = wire;        // the value under test
			p[4] = MaxBrightness;
			p[5] = ColourAp;
			p[6] = 0xFF;
			p[9] = Checksum(p, 1, 8, ChecksumKind.OnesComplement);

			Console.WriteLine($"\n>>> WIRE SPEED = {wire}  ({string.Join(' ', p.Skip(1).Take(9).Select(x => x.ToString("X2")))})  - watch for 8s");
			Send(control, p, $"breathe speed {wire}");
			Thread.Sleep(8000);
		}

		Console.WriteLine("\nIf the pulse rate differed: which value was fastest?");
		Console.WriteLine("If every step pulsed identically: the speed byte is not driving this firmware,");
		Console.WriteLine("and the app's speed slider cannot control the T800 through SET_LEDPARAM.");
	}

	// Listens to the vendor-code channel only. No writes at all.
	//
	// This is the one measurement that settles the speed question outright: when the board's OWN
	// lighting shortcuts change the speed, the firmware announces the new value as `05 05 <speed>`.
	// That gives the real range and the real direction straight from the device — no vendor doc, no
	// inference, and no dependence on our command path being right.
	public static void RunLearn()
	{
		var notify = DeviceList.Local.GetHidDevices(VID, PID)
			.FirstOrDefault(d => Safe(d, 'i') == VendorCodeReportLen
				&& d.DevicePath?.Contains("col05", StringComparison.OrdinalIgnoreCase) == true);

		Learn(notify);
	}

	private static void Drain(List<string> seen, string label)
	{
		lock (seen)
		{
			if (seen.Count == 0) { Console.WriteLine($"{label}: (silence)"); return; }
			Console.WriteLine($"{label}: {seen.Count} notification(s)");
			foreach (var s in seen) Console.WriteLine($"      {s}");
			seen.Clear();
		}
	}

	public static void Run()
	{
		Console.WriteLine("T800 LED probe (VID 0x3151 / PID 0x502D)");
		Console.WriteLine("========================================");

		var all = DeviceList.Local.GetHidDevices(VID, PID).ToList();
		if (all.Count == 0)
		{
			Console.WriteLine("No T800 found. Connect it over USB and stop the ProoveGamingDeviceHost service.");
			return;
		}

		Console.WriteLine($"{all.Count} interface(s):");
		foreach (var d in all)
			Console.WriteLine($"  feat={Safe(d, 'f'),3}  out={Safe(d, 'o'),3}  in={Safe(d, 'i'),3}  {d.DevicePath}");

		var control = all.FirstOrDefault(d => Safe(d, 'f') == ReportLen
			&& d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true)
			?? all.FirstOrDefault(d => Safe(d, 'f') == ReportLen);

		if (control == null)
		{
			Console.WriteLine($"\nNo interface exposes a {ReportLen}-byte feature report — the vendor channel moved.");
			return;
		}

		Console.WriteLine($"\ncontrol (mi_02, feature): {control.DevicePath}");

		var notify = all.FirstOrDefault(d => Safe(d, 'i') == VendorCodeReportLen
			&& d.DevicePath?.Contains("col05", StringComparison.OrdinalIgnoreCase) == true);
		Console.WriteLine($"notify  (col05, input)  : {notify?.DevicePath ?? "not found"}");

		while (true)
		{
			Console.WriteLine("\n  [1] Resolve the checksum (read-back, no need to watch the keyboard)");
			Console.WriteLine("  [2] Learn the encoding — listen while you press the board's lighting hotkeys");
			Console.WriteLine("  [3] Write test — solid red under each checksum candidate");
			Console.WriteLine("  [Q] Quit");
			Console.Write("Choice: ");

			var choice = Console.ReadLine()?.Trim().ToUpperInvariant();
			switch (choice)
			{
				case "1": ResolveChecksum(control); break;
				case "2": Learn(notify); break;
				case "3": WriteTest(control); break;
				case "Q": case null: return;
			}
		}
	}

	// ── [1] Checksum ─────────────────────────────────────────────────────────────
	// Sends GET_LEDPARAM under each candidate and inspects the reply. The firmware only answers a
	// command it accepted, so a reply that is neither all-zero nor all-0xFF identifies the rule.
	private static void ResolveChecksum(HidDevice control)
	{
		Console.WriteLine("\nResolving checksum via GET_LEDPARAM (0x87) read-back...\n");

		foreach (ChecksumKind kind in Enum.GetValues<ChecksumKind>())
		{
			foreach (var (cmd, label) in new[] { (CmdGetLedParam, "GET_LEDPARAM"), (CmdGetInfo, "GET_INFOR") })
			{
				var request = new byte[ReportLen];
				request[1] = cmd;
				request[8] = Checksum(request, 1, 7, kind);

				var reply = Exchange(control, request);
				if (reply == null)
				{
					Console.WriteLine($"  {kind,-11} {label,-13} -> no reply (exchange failed)");
					continue;
				}

				var body = reply.Skip(1).Take(12).ToArray();
				bool interesting = body.Any(x => x != 0x00) && body.Any(x => x != 0xFF);
				Console.WriteLine($"  {kind,-11} {label,-13} -> {string.Join(' ', body.Select(x => x.ToString("X2")))}  {(interesting ? "<== PARSED" : "")}");
			}
		}

		Console.WriteLine("\nThe candidate with a non-trivial reply is the checksum. Put it in");
		Console.WriteLine("T800KeyboardRgbService.Checksum. If none replies, the firmware may not answer reads at");
		Console.WriteLine("all — use [2] to learn the encoding from the device's own notifications instead.");
	}

	// ── [2] Learn ────────────────────────────────────────────────────────────────
	// The firmware announces its own state on the vendor-code channel whenever lighting changes
	// on-device. That is ground truth for the effect / option encoding, and it needs no checksum,
	// no writes and no working command path — it is pure observation.
	private static void Learn(HidDevice? notify)
	{
		if (notify == null)
		{
			Console.WriteLine("\ncol05 notification interface not found — cannot listen.");
			return;
		}

		Console.WriteLine("\nListening on the vendor-code channel for 60s.");
		Console.WriteLine("Use the KEYBOARD's own lighting shortcuts now (Fn + the effect / brightness / speed keys).");
		Console.WriteLine("Each change should print the value the firmware actually uses.\n");

		if (!notify.TryOpen(out var stream))
		{
			Console.WriteLine("TryOpen failed on the notification interface.");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = 1000;
			var deadline = DateTime.UtcNow.AddSeconds(60);
			var buf = new byte[VendorCodeReportLen];

			while (DateTime.UtcNow < deadline)
			{
				int read;
				try { read = stream.Read(buf); }
				catch (TimeoutException) { continue; }
				catch (Exception ex) { Console.WriteLine($"read failed: {ex.Message}"); break; }

				if (read < 4 || buf[0] != VendorCodeReportId) continue;

				// Report ID 0x05 IS the spec's "05H" first byte, so the documented
				// Byte1/Byte2/Byte3 land at buf[1..3].
				Console.WriteLine($"  {string.Join(' ', buf.Take(6).Select(x => x.ToString("X2")))}   {DescribeVendorCode(buf)}");
			}
		}

		Console.WriteLine("\nDone listening.");
	}

	private static string DescribeVendorCode(byte[] b) => b[1] switch
	{
		0x01 => $"profile = {b[2]}",
		0x02 => $"report rate = {b[2]}",
		0x03 => b[3] switch
		{
			0x04 => $"MAIN LIGHT on/off = {b[2]}  (0 = on, 1 = off)",
			0x05 => $"side light on/off = {b[2]}",
			_ => $"keyboard option {b[3]:X2} = {b[2]}",
		},
		0x04 => $"LED EFFECT = 0x{b[2]:X2}",
		0x05 => $"LED SPEED = {b[2]}",
		0x06 => $"LED BRIGHTNESS = {b[2]}",
		0x07 => $"LED OPTION = 0x{b[2]:X2}  (colour slot {b[2] & 0x0F}, type {b[2] >> 4})",
		0x08 => $"SLED effect = 0x{b[2]:X2}",
		0x0E => $"battery = {b[2]}",
		0xFF => $"vendor key = 0x{b[2]:X2} {(b[3] == 1 ? "down" : "up")}",
		_ => $"category 0x{b[1]:X2} value {b[2]}",
	};

	// ── [3] Write test ───────────────────────────────────────────────────────────
	private static void WriteTest(HidDevice control)
	{
		Console.WriteLine("\nEach step sends SOLID RED at full brightness under a different checksum rule.");
		Console.WriteLine("Watch the keyboard; note which step lights it.\n");

		foreach (ChecksumKind kind in Enum.GetValues<ChecksumKind>())
		{
			Send(control, BuildOnOff(mainOff: true, kind), $"{kind}: off");
			Thread.Sleep(600);
			Send(control, BuildOnOff(mainOff: false, kind), $"{kind}: on");
			Send(control, BuildLedParam(EffectStatic, 0, MaxBrightness, ColourAp, 0xFF, 0x00, 0x00, kind), $"{kind}: red");

			// Read the state back so the log says what the firmware RETAINED, not just what we
			// asked for — the whole point of the mi_02 channel being bidirectional.
			var verify = new byte[ReportLen];
			verify[1] = CmdGetLedParam;
			verify[8] = Checksum(verify, 1, 7, kind);
			var reply = Exchange(control, verify);
			if (reply != null)
				Console.WriteLine($"      read-back: {string.Join(' ', reply.Skip(1).Take(10).Select(x => x.ToString("X2")))}");

			Console.WriteLine("      is the board solid RED? (Enter for next candidate)");
			Console.ReadLine();
		}
	}

	// ── Plumbing ─────────────────────────────────────────────────────────────────

	// One open/SetFeature/GetFeature cycle. Kept on a single stream so the firmware sees the read
	// as belonging to the command that preceded it.
	private static byte[]? Exchange(HidDevice device, byte[] request)
	{
		try
		{
			if (!device.TryOpen(out var stream)) return null;
			using (stream)
			{
				stream.SetFeature(request);
				Thread.Sleep(20);
				var reply = new byte[ReportLen];
				stream.GetFeature(reply);
				return reply;
			}
		}
		catch
		{
			return null;
		}
	}

	private static void Send(HidDevice device, byte[] packet, string label)
	{
		Console.WriteLine($"   {label,-20} {string.Join(' ', packet.Skip(1).Take(10).Select(x => x.ToString("X2")))}");
		try
		{
			if (!device.TryOpen(out var stream))
			{
				Console.WriteLine("      TryOpen failed");
				return;
			}

			using (stream)
				stream.SetFeature(packet);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"      SetFeature failed: {ex.Message}");
		}
	}

	// SET_LEDPARAM — seven parameters, CS at protocol byte 8 (buffer index 9).
	private static byte[] BuildLedParam(byte effect, byte speed, byte brightness, byte option, byte r, byte g, byte b, ChecksumKind kind)
	{
		var p = new byte[ReportLen];
		p[1] = CmdSetLedParam;
		p[2] = effect;
		p[3] = speed;
		p[4] = brightness;
		p[5] = option;
		p[6] = r;
		p[7] = g;
		p[8] = b;
		p[9] = Checksum(p, 1, 8, kind);
		return p;
	}

	// SET_LEDONOFF — six parameters, CS at protocol byte 7 (buffer index 8).
	private static byte[] BuildOnOff(bool mainOff, ChecksumKind kind)
	{
		var p = new byte[ReportLen];
		p[1] = CmdSetLedOnOff;
		p[2] = mainOff ? (byte)1 : (byte)0;
		p[3] = mainOff ? (byte)1 : (byte)0;
		p[8] = Checksum(p, 1, 7, kind);
		return p;
	}

	private static byte Checksum(byte[] packet, int from, int to, ChecksumKind kind)
	{
		int sum = 0;
		byte xor = 0;
		for (int i = from; i <= to; i++)
		{
			sum += packet[i];
			xor ^= packet[i];
		}

		return kind switch
		{
			ChecksumKind.OnesComplement => (byte)(0xFF - (sum & 0xFF)),
			ChecksumKind.Sum => (byte)(sum & 0xFF),
			ChecksumKind.NegatedSum => (byte)(-sum & 0xFF),
			ChecksumKind.Xor => xor,
			_ => 0,
		};
	}

	private static int Safe(HidDevice d, char which)
	{
		try
		{
			return which switch
			{
				'f' => d.GetMaxFeatureReportLength(),
				'o' => d.GetMaxOutputReportLength(),
				_ => d.GetMaxInputReportLength(),
			};
		}
		catch { return -1; }
	}
}
