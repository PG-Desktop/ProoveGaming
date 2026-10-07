using HidSharp;
using System.Text;

namespace ProoveGaming.Device.Testing;

// ── PG Cosmo — status-channel probe (STRICTLY READ-ONLY) ──────────────────────────────────────
//
// Goal: find power on/off, battery level and mic-mute state on the undocumented vendor report.
//
// What we already know (docs/plans/add-cosmo-headset):
//   MI_03 carries usage page 0xFF02 with two bidirectional vendor reports, 0xBC and 0xFD.
//   0xFD is IDENTIFIED and closed: it is C-Media's Xear DRM bridge to a CM002X1 crypto chip
//   (see the vendor PDF in docs/headset/cosmo). It carries no device state.
//   => 0xBC (14 B in / 15 B out) is the only remaining candidate, and is what this probe watches.
//
// Method: the TAIPAN playbook. That dongle relays device->host pushes but ignores host->device
// writes, and its battery was solved by listening rather than querying (see TaipanStateMonitor).
// A 2.4G headset dongle almost certainly behaves the same way, so this probe LISTENS while the
// user operates the headset, and correlates frames with known physical actions.
//
// NO WRITES. Not one. An unknown write to an unknown channel is exactly how the BLAZE flash
// corruption happened (docs/mouse/blaze). If passive listening turns out to be insufficient —
// i.e. the dongle only answers when polled — that is a finding to report, and the decision to
// try writes belongs to the user, not to this probe.
//
// Output is a per-report-ID summary of WHICH BYTE OFFSETS EVER CHANGED and the values each took.
// A byte that only moves when you mute the mic is the mic-mute byte; a byte that counts slowly
// downward is the battery. That summary is what turns raw frames into a protocol.
internal static class CosmoStatusProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;

	private sealed class ReportStats
	{
		public byte[] First = [];
		public byte[] Last = [];
		public int Count;
		// Distinct values seen per byte offset. Small sets are the interesting ones: a byte that
		// only ever holds 0x00/0x01 is a flag, a byte with many values is a level or a counter.
		public readonly List<SortedSet<byte>> ValuesByOffset = [];
		// The phase label(s) during which this offset was seen to change — this is what ties a
		// byte to a physical action instead of leaving a wall of hex to eyeball.
		public readonly Dictionary<int, SortedSet<string>> ChangedInPhase = [];
	}

	// Guided phases. Each is a physical action whose effect we expect to see on the wire.
	private static readonly (string Label, int Seconds, string Instruction)[] Phases =
	[
		("idle",        12, "Leave the headset ON and untouched. (baseline — anything moving here is a heartbeat)"),
		("mic-mute",    18, "Mute and UNMUTE the microphone a few times (use the physical mic switch/button)."),
		("power-off",   18, "Switch the headset OFF. Leave it off until this phase ends."),
		("power-on",    18, "Switch the headset back ON."),
		("volume",      15, "Press volume + and - on the headset a few times."),
		("final-idle",  10, "Leave it alone again."),
	];

	public static void Run()
	{
		Console.WriteLine("PG Cosmo — status-channel probe (READ-ONLY, no writes)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid)
			.Where(d => Len(d, output: false) > 0)
			.ToList();

		if (interfaces.Count == 0)
		{
			Console.WriteLine("Not found. Is the Cosmo dongle plugged in?");
			return;
		}

		Console.WriteLine($"{interfaces.Count} readable HID interface(s). Opening...");

		var streams = new List<(HidDevice Dev, HidStream Stream, string Label)>();
		foreach (var d in interfaces)
		{
			if (!d.TryOpen(out var s))
			{
				// Windows opens consumer-control collections exclusively. That is expected for the
				// media-key collection and is itself informative, but if EVERY interface is locked
				// we cannot listen at all — which is a finding, not a crash.
				Console.WriteLine($"  LOCKED  {Short(d)}  (Windows holds it exclusively)");
				continue;
			}
			s.ReadTimeout = 250;
			streams.Add((d, s, Short(d)));
			Console.WriteLine($"  open    {Short(d)}  in={Len(d, false)} out={Len(d, true)}");
		}

		if (streams.Count == 0)
		{
			Console.WriteLine();
			Console.WriteLine("Could not open ANY interface for reading — every one is held exclusively.");
			Console.WriteLine("Stop the ProoveGamingDeviceHost service and try again; if it still fails,");
			Console.WriteLine("the status channel is not reachable by passive listening on this OS.");
			return;
		}

		// reportId -> stats. Frames are grouped by their leading report-ID byte.
		var stats = new Dictionary<byte, ReportStats>();
		var currentPhase = "startup";
		var stop = false;
		var gate = new Lock();

		var threads = new List<Thread>();
		foreach (var (dev, stream, label) in streams)
		{
			var t = new Thread(() =>
			{
				var buf = new byte[Math.Max(64, Len(dev, output: false))];
				while (!Volatile.Read(ref stop))
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { return; }
					if (n <= 0) continue;

					var frame = buf.AsSpan(0, n).ToArray();
					lock (gate) Record(stats, frame, label, currentPhase);
				}
			})
			{ IsBackground = true };
			threads.Add(t);
			t.Start();
		}

		try
		{
			var presence = new List<(string Phase, int Interfaces)>();

			foreach (var (label, seconds, instruction) in Phases)
			{
				lock (gate) currentPhase = label;

				Console.WriteLine();
				Console.WriteLine(new string('=', 78));
				Console.WriteLine($"PHASE: {label}   ({seconds}s)");
				Console.WriteLine($"  >>> {instruction}");
				Console.WriteLine(new string('=', 78));

				for (var remaining = seconds; remaining > 0; remaining--)
				{
					Console.Write($"\r  {remaining,3}s remaining   ");
					Thread.Sleep(1000);
				}
				Console.WriteLine("\r                          ");

				// Second, independent answer to "is the headset ON?": some 2.4G dongles drop or
				// re-add an interface when the headset powers down, which would let the App detect
				// power state with no vendor protocol at all. Counted at each phase boundary so a
				// change lines up with the power-off / power-on phases.
				var count = DeviceList.Local.GetHidDevices(Vid, Pid).Count();
				presence.Add((label, count));
				Console.WriteLine($"  (HID interfaces present after '{label}': {count})");
			}

			PrintPresence(presence);
		}
		finally
		{
			Volatile.Write(ref stop, true);
			foreach (var t in threads) t.Join(600);
			foreach (var (_, s, _) in streams) s.Dispose();
		}

		PrintSummary(stats);
	}

	private static void PrintPresence(List<(string Phase, int Interfaces)> presence)
	{
		Console.WriteLine();
		Console.WriteLine("USB presence per phase (a change across power-off/power-on would mean the");
		Console.WriteLine("App can detect ON/OFF from enumeration alone, with no vendor protocol):");
		foreach (var (phase, count) in presence)
			Console.WriteLine($"    {phase,-12} {count} interface(s)");

		Console.WriteLine(presence.Select(p => p.Interfaces).Distinct().Count() > 1
			? "  => COUNT CHANGED: enumeration tracks the headset's power state. Usable today."
			: "  => count never changed: the dongle stays enumerated with the headset off, so power\n"
			  + "     state must come from the 0xBC frames above (same as TAIPAN's dongle).");
	}

	private static void Record(Dictionary<byte, ReportStats> stats, byte[] frame, string label, string phase)
	{
		var reportId = frame[0];
		if (!stats.TryGetValue(reportId, out var st))
		{
			st = new ReportStats { First = frame, Last = frame };
			for (var i = 0; i < frame.Length; i++)
				st.ValuesByOffset.Add([]);
			stats[reportId] = st;

			Console.WriteLine();
			Console.WriteLine($"  [{phase}] NEW report 0x{reportId:X2} on {label}: {Hex(frame)}");
		}

		st.Count++;

		// Grow if a later frame is longer than the first (shouldn't happen, but never index past).
		while (st.ValuesByOffset.Count < frame.Length)
			st.ValuesByOffset.Add([]);

		for (var i = 0; i < frame.Length; i++)
			st.ValuesByOffset[i].Add(frame[i]);

		// Only print frames that actually differ from the previous one for this report — a
		// heartbeat repeating the same 14 bytes would otherwise bury the interesting transitions.
		if (!frame.AsSpan().SequenceEqual(st.Last))
		{
			var changed = new List<int>();
			for (var i = 0; i < Math.Min(frame.Length, st.Last.Length); i++)
			{
				if (frame[i] == st.Last[i]) continue;
				changed.Add(i);
				if (!st.ChangedInPhase.TryGetValue(i, out var phases))
					st.ChangedInPhase[i] = phases = [];
				phases.Add(phase);
			}

			Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] [{phase,-10}] 0x{reportId:X2} {Hex(frame)}"
				+ $"   changed@ {string.Join(',', changed)}");
			st.Last = frame;
		}
	}

	private static void PrintSummary(Dictionary<byte, ReportStats> stats)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 78));
		Console.WriteLine("SUMMARY");
		Console.WriteLine(new string('=', 78));

		if (stats.Count == 0)
		{
			Console.WriteLine("No input reports at all. The dongle pushes nothing unsolicited, so the");
			Console.WriteLine("status channel (if any) must be POLLED — which needs a host->device write");
			Console.WriteLine("on report 0xBC, and that is a decision for a human, not this probe.");
			return;
		}

		foreach (var (reportId, st) in stats.OrderBy(kv => kv.Key))
		{
			Console.WriteLine();
			Console.WriteLine($"Report 0x{reportId:X2} — {st.Count} frames, {st.First.Length} bytes");
			Console.WriteLine($"  first: {Hex(st.First)}");
			Console.WriteLine($"  last:  {Hex(st.Last)}");
			Console.WriteLine("  per-byte behaviour (offsets that never changed are omitted):");

			var anyVariable = false;
			for (var i = 0; i < st.ValuesByOffset.Count; i++)
			{
				var values = st.ValuesByOffset[i];
				if (values.Count <= 1) continue;
				anyVariable = true;

				var phases = st.ChangedInPhase.TryGetValue(i, out var p) ? string.Join('/', p) : "-";
				var shown = values.Count <= 12
					? string.Join(' ', values.Select(v => v.ToString("X2")))
					: $"{values.Count} distinct, {values.Min():X2}..{values.Max():X2}";

				Console.WriteLine($"    byte {i,2}: {shown,-40} changed during: {phases}");
			}

			if (!anyVariable)
				Console.WriteLine("    (every byte constant — this report carries no state)");
		}

		Console.WriteLine();
		Console.WriteLine("How to read this:");
		Console.WriteLine("  * a byte that changed ONLY during 'mic-mute'  -> mic mute flag");
		Console.WriteLine("  * a byte that changed ONLY during power-off/on -> link/power state");
		Console.WriteLine("  * a byte with many values in a smooth range   -> battery level (raw)");
		Console.WriteLine("  * a byte changing during 'idle' too           -> counter / heartbeat, ignore");
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return 0; }
	}

	private static string Short(HidDevice d)
	{
		var p = d.DevicePath ?? string.Empty;
		var i = p.IndexOf('#');
		var j = i >= 0 ? p.IndexOf('#', i + 1) : -1;
		return j > i ? p[(i + 1)..j] : p;
	}

	private static string Hex(byte[] data)
	{
		var sb = new StringBuilder(data.Length * 3);
		foreach (var b in data)
			sb.Append(b.ToString("X2")).Append(' ');
		return sb.ToString().TrimEnd();
	}
}
