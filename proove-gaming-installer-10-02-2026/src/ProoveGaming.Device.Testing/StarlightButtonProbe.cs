using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — what can the host see when a button is pressed? ─────────────────────────────
//
// The headset has two buttons and a mechanical volume wheel:
//
//   Button 1   short = play/pause          long = power off
//   Button 2   short = next RGB effect     long = microphone mute / unmute
//   Wheel      output level (mechanical)
//
// If any of those reach the host, the app can react to them — show the mute state, follow the RGB
// effect, and so on. This probe finds out which ones do.
//
// WHY THIS EXISTS SEPARATELY FROM `starlight watch`
//
// That arm listened on COL02 (the 32-byte vendor collection) alone and reported "no frames". That
// was true, and it was the wrong place to look: media and function buttons are consumer-control
// keys, and on this device those live on COL01, report 0x01. Nothing was ever listening there.
//
// So this probe listens EVERYWHERE, two ways at once, because they fail differently:
//
//   HidSharp  reads collections we can open. Windows claims consumer-control devices, so an open
//             may be refused outright — which is itself a result worth printing.
//   RawInput  receives input from collections Windows claims for itself, which is precisely the
//             case ReadFile is denied on. This is how the BLAZE DPI button was read.
//
// Every frame is stamped with the phase it arrived in, so a keypress is tied to the button that
// produced it rather than guessed at.
//
// READ-ONLY. Enumerates, opens for reading, and registers for input. Never writes to the device.
internal static class StarlightButtonProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	// The collections this device declares (both consumer page 0x0C, usage 0x01). Registered
	// explicitly so a gap in Raw Input's enumeration cannot silently lose the buttons.
	private static readonly (ushort Page, ushort Usage)[] DeclaredCollections =
	[
		(0x000C, 0x0001),
	];

	// Power-off is deliberately absent: it disconnects the device and would end the session. It is
	// also the one action whose effect we can already observe, as a disappearing device.
	private static readonly (string Label, int Seconds, string Instruction)[] Phases =
	[
		("idle", 8,
			"Do NOT touch the headset. This is the baseline — anything seen here is background noise."),
		("button1-short", 15,
			"Press BUTTON 1 briefly (play/pause). Do it 3 times, pausing between presses."),
		("button2-short", 15,
			"Press BUTTON 2 briefly (next RGB effect). Do it 3 times, pausing between presses."),
		("button2-long", 20,
			"HOLD BUTTON 2 to mute the microphone, wait, then HOLD it again to unmute. Twice."),
		("wheel", 12,
			"Turn the volume wheel up, then down, a few times."),
	];

	private sealed record Frame(string Phase, string Source, byte[] Payload);

	public static void Run()
	{
		Console.WriteLine("PG Starlight — button and wheel capture (READ-ONLY)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("Not found. Is the headset connected, and is ProoveGamingDeviceHost stopped?");
			return;
		}

		var frames = new List<Frame>();
		var gate = new object();
		var phase = "startup";

		void Record(string source, byte[] payload)
		{
			lock (gate) frames.Add(new Frame(phase, source, payload));
		}

		// ── Open every interface we are allowed to ────────────────────────────────────────────
		Console.WriteLine($"{interfaces.Count} HID interface(s):");
		var streams = new List<HidStream>();
		using var cts = new CancellationTokenSource();
		var readers = new List<Task>();

		foreach (var d in interfaces)
		{
			var label = Describe(d);

			if (!d.TryOpen(out var stream))
			{
				// Not a failure of the probe — Windows holding a consumer-control collection
				// exclusively is exactly why the Raw Input path below exists.
				Console.WriteLine($"  LOCKED  {label}  (Windows holds it; Raw Input will cover it)");
				continue;
			}

			Console.WriteLine($"  open    {label}");
			streams.Add(stream);
			stream.ReadTimeout = 400;

			readers.Add(Task.Run(() =>
			{
				var buf = new byte[Math.Max(64, Len(() => d.GetMaxInputReportLength(), 64))];
				while (!cts.IsCancellationRequested)
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { break; }
					if (n > 0) Record($"hid {label}", buf[..n]);
				}
			}, cts.Token));
		}

		// ── Raw Input, on its own thread with its own message pump ────────────────────────────
		var totalSeconds = Phases.Sum(p => p.Seconds) + 4;
		var rawInput = Task.Run(() =>
			RawInputDpi.Listen(Vid, Pid, totalSeconds, "  (Raw Input registered)",
				report => Record("rawinput", report), DeclaredCollections));

		// Let registration settle before the baseline starts.
		Thread.Sleep(1500);

		// ── Guided phases ─────────────────────────────────────────────────────────────────────
		foreach (var (label, seconds, instruction) in Phases)
		{
			lock (gate) phase = label;

			Console.WriteLine();
			Console.WriteLine(new string('-', 96));
			Console.WriteLine($"PHASE: {label}   ({seconds}s)");
			Console.WriteLine($"  {instruction}");

			var before = FrameCount(gate, frames);
			Thread.Sleep(seconds * 1000);
			var after = FrameCount(gate, frames);

			Console.WriteLine($"  -> {after - before} frame(s) captured in this phase.");
		}

		cts.Cancel();
		Task.WaitAll([.. readers], TimeSpan.FromSeconds(2));
		foreach (var s in streams) s.Dispose();
		rawInput.Wait(TimeSpan.FromSeconds(5));

		Report(frames);
	}

	private static int FrameCount(object gate, List<Frame> frames)
	{
		lock (gate) return frames.Count;
	}

	private static void Report(List<Frame> frames)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"RESULT — {frames.Count} frame(s) total");
		Console.WriteLine();

		if (frames.Count == 0)
		{
			Console.WriteLine("Nothing arrived on any interface, by either path.");
			Console.WriteLine();
			Console.WriteLine("That would mean the buttons are handled entirely inside the headset and the");
			Console.WriteLine("dongle never reports them — the same answer Cosmo gave. The app could then only");
			Console.WriteLine("react to their SIDE EFFECTS: the OS volume level, and the microphone's mute");
			Console.WriteLine("state as Windows sees it. Both are already readable through Core Audio.");
			return;
		}

		// Baseline first: anything that also occurs while the headset is untouched is noise, not a
		// button, and saying so up front stops a periodic report being mistaken for a keypress.
		var idle = frames.Where(f => f.Phase == "idle")
			.Select(f => Hex(f.Payload)).ToHashSet();

		if (idle.Count > 0)
		{
			Console.WriteLine($"Baseline noise ({idle.Count} distinct frame(s) seen while idle):");
			foreach (var h in idle.Take(5)) Console.WriteLine($"  {h}");
			Console.WriteLine();
		}

		foreach (var phase in Phases.Select(p => p.Label))
		{
			var inPhase = frames.Where(f => f.Phase == phase).ToList();
			Console.WriteLine(new string('-', 96));
			Console.WriteLine($"PHASE {phase}: {inPhase.Count} frame(s)");

			if (inPhase.Count == 0)
			{
				Console.WriteLine("  nothing — this action is not visible to the host");
				continue;
			}

			var distinct = inPhase
				.GroupBy(f => (f.Source, Hex(f.Payload)))
				.OrderByDescending(g => g.Count());

			foreach (var g in distinct)
			{
				var novel = idle.Contains(g.Key.Item2) ? "  (also seen idle — noise)" : string.Empty;
				Console.WriteLine($"  x{g.Count(),-3} [{g.Key.Source}] {g.Key.Item2}{novel}");
			}
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("Reading this:");
		Console.WriteLine("  A frame that appears ONLY in one phase identifies that button. Its differing");
		Console.WriteLine("  byte is the button code, and the app can bind to it.");
		Console.WriteLine("  Frames marked '(also seen idle)' are periodic traffic, not keypresses.");
		Console.WriteLine("  A phase with nothing means the headset handles that action internally.");
	}

	private static string Hex(byte[] payload) => BitConverter.ToString(payload);

	private static string Describe(HidDevice d)
	{
		var path = d.DevicePath;
		var col = path.Contains("col01", StringComparison.OrdinalIgnoreCase) ? "COL01"
			: path.Contains("col02", StringComparison.OrdinalIgnoreCase) ? "COL02"
			: "?";
		return $"{col} in={Len(() => d.GetMaxInputReportLength(), 0)} out={Len(() => d.GetMaxOutputReportLength(), 0)}";
	}

	private static int Len(Func<int> get, int fallback)
	{
		try { return get(); }
		catch { return fallback; }
	}
}
