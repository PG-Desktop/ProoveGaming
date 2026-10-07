using HidSharp;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — does a battery REQUEST work over the 2.4G dongle? ──────────────────────────────
//
// The vendor answered three things (2026-08-07) that contradict what this repo currently believes:
//
//   1. 2.4G needs no separate fragmentation or format — send the FULL data set, identical to wired.
//   2. If a command gets no response, check the app is on the RIGHT HID interface.
//   3. Battery is NOT pushed automatically. Send a request; the device replies ONCE per request.
//      No new request, no new value.
//
// Point 3 is the actionable one, because we never implemented a request at all. TaipanStateReport
// only PARSES pushed 0x48 frames, and TaipanStateMonitor caches whatever the mouse volunteers on
// link establishment. Our note that "battery/echo queries go unanswered" was never about this
// command — nothing in the codebase has ever sent it.
//
// Point 2 matters for where the REPLY lands. The vendor's own interface-matching example looks for
// BOTH `MI_00&Col01` and `MI_00&Col04`, which hints the two are used together — so this listens on
// every readable collection at once rather than assuming the reply comes back on the one we wrote
// to. Measured layout over the dongle:
//
//   MI_00 Col01   in=7   out=0   feat=0
//   MI_00 Col02   in=3   out=0   feat=0
//   MI_00 Col03   in=2   out=0   feat=0
//   MI_00 Col04   in=33  out=33  feat=8    <- the vendor config collection, where we write
//
// WRITES, but only the documented battery opcode with zero parameters — the same protocol the app
// already speaks over the cable (docs/mouse/taipan/protocol.md). driverPara4 stays 0, so the
// flash-erasing sequence value (0xD1) is never sent.
internal static class TaipanBatteryProbe
{
	private const int Vid = 0x248A;

	// Every known PID, because this arm is now used to COMPARE transports: the reported battery
	// percentage differs between the cable and the dongle (2026-08-12), and the raw value is the
	// only way to tell a wrong formula from a genuine voltage difference.
	private static readonly Dictionary<int, string> KnownPids = new()
	{
		[0x8373] = "2.4G dongle",
		[0xFB01] = "wired",
		[0x8266] = "bluetooth",
	};

	// 0x48 is the battery opcode. The device uses it for the reply; the vendor says a request must
	// be sent to elicit one, and request/reply sharing an opcode is this protocol's usual shape.
	private const byte BatteryCommand = 0x48;

	// CMD_SetMouseState in its read/ACK form. The protocol pairs a write 0x0X with a read 0x4X
	// (0x01/0x41, 0x02/0x42, 0x03/0x43 ...), and the battery result fits that rule exactly: 0x08 is
	// the WRITE form and stays silent, while 0x48 — the READ form — answers every time. The battery
	// row in protocol.md is written as "0x08 -> reply 0x48" rather than as a write/read pair, which
	// is why the earlier work sent 0x08.
	//
	// 0x46 echoes back and touches NO flash, so it separates two things the button-map failure
	// cannot: whether a host->mouse command is received and processed at all, versus whether the
	// multi-frame flash sequence survives the radio link.
	//
	// Para1 = 0x01 (enabled) is the value TaipanDongleProbe already uses; the parameter is not
	// documented beyond "enable/disable", so it is reused rather than guessed at — sending an
	// unknown value to a command that can DISABLE the mouse is not worth the information.
	private const byte SetMouseStateCommand = 0x46;

	private const int Requests = 3;

	public static void Run()
	{
		Console.WriteLine("PG TAIPAN — battery request (any transport)");
		Console.WriteLine($"VID 0x{Vid:X4}");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("No TAIPAN present on any transport. Connect the cable or plug the dongle in.");
			return;
		}

		// One transport at a time, or the comparison is meaningless. With the cable plugged in the
		// mouse runs and charges over USB, so the 2.4G link has no active mouse behind it and the
		// dongle answers a battery request with a degenerate all-zero frame (raw=0, correctly
		// rejected by TryParseBattery) instead of a real reading. Taking the first interface's PID
		// as "the transport" silently mislabels that as a dongle result.
		var pids = interfaces.Select(d => d.ProductID).Distinct().OrderBy(x => x).ToList();
		if (pids.Count > 1)
		{
			Console.WriteLine("MORE THAN ONE TRANSPORT IS CONNECTED:");
			foreach (var candidate in pids)
				Console.WriteLine($"  PID 0x{candidate:X4} — {(KnownPids.TryGetValue(candidate, out var n) ? n : "unknown")}");

			Console.WriteLine();
			Console.WriteLine("Disconnect all but one and run again. Comparing battery across transports");
			Console.WriteLine("requires each reading to come from a mouse actually running on THAT link:");
			Console.WriteLine("  - dongle reading: cable UNPLUGGED");
			Console.WriteLine("  - wired reading:  dongle UNPLUGGED");
			return;
		}

		var pid = pids[0];
		var transport = KnownPids.TryGetValue(pid, out var known) ? known : $"UNKNOWN PID 0x{pid:X4}";
		Console.WriteLine($"transport: {transport} (PID 0x{pid:X4})");
		Console.WriteLine();

		var writeTarget = interfaces.FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (writeTarget is null)
		{
			Console.WriteLine($"No {TaipanFrame.Length}/{TaipanFrame.Length} vendor collection found.");
			return;
		}

		Console.WriteLine($"{interfaces.Count} interface(s); writing to {Short(writeTarget)}");
		Console.WriteLine();

		// Listen on EVERY readable collection. The whole point of the vendor's note about interfaces
		// is that the reply may not come back where we wrote.
		var frames = new List<(DateTime At, string Source, byte[] Payload)>();
		var gate = new object();
		var streams = new List<HidStream>();
		using var cts = new CancellationTokenSource();
		var readers = new List<Task>();

		foreach (var d in interfaces)
		{
			if (Len(() => d.GetMaxInputReportLength()) <= 0) continue;
			if (!d.TryOpen(out var stream))
			{
				Console.WriteLine($"  LOCKED  {Short(d)}");
				continue;
			}

			Console.WriteLine($"  listening on {Short(d)}");
			streams.Add(stream);
			stream.ReadTimeout = 300;

			var label = Short(d);
			readers.Add(Task.Run(() =>
			{
				var buf = new byte[Math.Max(64, Len(() => d.GetMaxInputReportLength()))];
				while (!cts.IsCancellationRequested)
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { break; }
					if (n > 0) lock (gate) frames.Add((DateTime.UtcNow, label, buf[..n]));
				}
			}, cts.Token));
		}

		Console.WriteLine();

		// ── Echo test first: does ANY host->mouse command get processed? ──────────────────────
		//
		// Runs before the battery loop because it answers the more fundamental question. If this
		// echoes, the dongle carries commands and the button-map failure is specific to the flash
		// write. If it does not echo while battery does, the mouse answers reads but ignores
		// non-flash writes, which is a different and much stranger shape.
		Console.WriteLine(new string('-', 96));
		Console.WriteLine($"ECHO TEST — CMD_SetMouseState (0x{SetMouseStateCommand:X2}), Para1=0x01, no flash involved");

		int echoBefore;
		lock (gate) echoBefore = frames.Count;

		var echo = TaipanFrame.Build(SetMouseStateCommand, 0x01, 0, 0, 0, ReadOnlySpan<byte>.Empty);
		Console.WriteLine($"  sending {BitConverter.ToString(echo[..8])}...");

		try
		{
			if (writeTarget.TryOpen(out var echoStream))
				using (echoStream) echoStream.Write(echo);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  write failed: {ex.Message}");
		}

		Thread.Sleep(2000);

		lock (gate)
		{
			var arrived = frames.Skip(echoBefore).ToList();
			var echoed = arrived.Any(f => f.Payload.Length > 3 && f.Payload[3] == SetMouseStateCommand);

			Console.WriteLine($"  -> {arrived.Count} frame(s); echo of 0x{SetMouseStateCommand:X2}: {(echoed ? "YES" : "no")}");
			foreach (var (_, source, payload) in arrived)
				Console.WriteLine($"     [{source}] {BitConverter.ToString(payload[..Math.Min(12, payload.Length)])}...");
		}

		Console.WriteLine();

		// The request itself: documented opcode, all parameters zero, no payload.
		var request = TaipanFrame.Build(BatteryCommand, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty);

		for (var i = 1; i <= Requests; i++)
		{
			int before;
			lock (gate) before = frames.Count;

			Console.WriteLine(new string('-', 96));
			Console.WriteLine($"REQUEST {i}/{Requests}: {BitConverter.ToString(request[..8])}...");

			try
			{
				if (!writeTarget.TryOpen(out var w))
				{
					Console.WriteLine("  could not open the write interface");
					break;
				}
				using (w) w.Write(request);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  write failed: {ex.Message}");
				break;
			}

			// The vendor says exactly one reply per request, so a short wait is enough; a longer one
			// would only blur which request a frame answered.
			Thread.Sleep(2500);

			lock (gate)
			{
				var arrived = frames.Skip(before).ToList();
				Console.WriteLine($"  -> {arrived.Count} frame(s) after this request");
				foreach (var (_, source, payload) in arrived)
					Console.WriteLine($"     [{source}] {BitConverter.ToString(payload)}");
			}

			Console.WriteLine();
		}

		cts.Cancel();
		Task.WaitAll([.. readers], TimeSpan.FromSeconds(2));
		foreach (var s in streams) s.Dispose();

		Report(frames);
	}

	private static void Report(List<(DateTime At, string Source, byte[] Payload)> frames)
	{
		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"RESULT — {frames.Count} frame(s) total across {Requests} request(s)");
		Console.WriteLine();

		if (frames.Count == 0)
		{
			Console.WriteLine("Nothing came back at all.");
			Console.WriteLine();
			Console.WriteLine("  Either the battery request is not opcode 0x48, or it needs parameters we did not");
			Console.WriteLine("  send, or the dongle genuinely does not relay host->mouse commands. Ask the vendor");
			Console.WriteLine("  for the exact request frame — we now have a specific question: 'what are the");
			Console.WriteLine("  cmd and Para1-4 bytes of the battery request?'");
			return;
		}

		var batteries = frames
			.Where(f => TaipanStateReport.TryParseBattery(f.Payload, out _, out _))
			.ToList();

		foreach (var (source, count) in frames.GroupBy(f => f.Source).Select(g => (g.Key, g.Count())))
			Console.WriteLine($"  {count,3} frame(s) on {source}");

		Console.WriteLine();

		if (batteries.Count == 0)
		{
			Console.WriteLine("Frames arrived, but none parsed as a 0x48 battery reply. The raw payloads above");
			Console.WriteLine("are the evidence — compare byte 3 against the opcode table in");
			Console.WriteLine("docs/mouse/taipan/protocol.md to see what the device actually answered with.");
			return;
		}

		Console.WriteLine($"  {batteries.Count} BATTERY REPLY(S) PARSED:");
		foreach (var (_, source, payload) in batteries)
		{
			TaipanStateReport.TryParseBattery(payload, out var raw, out var usb);
			var percent = TaipanStateReport.PercentFromRaw(raw);
			Console.WriteLine($"     [{source}] raw={raw}  ->  {percent}%   usb={usb}   {BitConverter.ToString(payload[..8])}...");
		}

		Console.WriteLine();
		Console.WriteLine("  RAW is the number to compare across transports. The shipped curve maps");
		Console.WriteLine("  raw 750 -> 0% and raw 985 -> 100%, clamped, so a raw difference of ~24 is");
		Console.WriteLine("  worth ~10 percentage points.");
		Console.WriteLine();
		Console.WriteLine("  usb=True means the mouse is being CHARGED. A lithium cell reads higher under");
		Console.WriteLine("  charge than at rest, so a higher percentage on the cable is expected physics,");
		Console.WriteLine("  not necessarily a wrong formula. Run this on BOTH transports within a couple of");
		Console.WriteLine("  minutes — the real state of charge cannot move much in that time, so any raw");
		Console.WriteLine("  difference is the charging offset.");

		Console.WriteLine();
		var echoed = frames.Any(f => f.Payload.Length > 3 && f.Payload[3] == SetMouseStateCommand);
		Console.WriteLine(echoed
			? "  ECHO: CMD_SetMouseState came back. Host->mouse commands ARE received and processed,\n" +
			  "        so the button-map failure is specific to the multi-frame FLASH sequence\n" +
			  "        (header 0xD1 erase -> 0xD2 chunks -> ValidBankID -> 0xD3 commit), not to the\n" +
			  "        transport. That is the question to put to the vendor."
			: "  ECHO: CMD_SetMouseState did NOT come back, even though battery does. The mouse answers\n" +
			  "        read-form opcodes (0x4X) but ignores this one — so 'writes reach the device' is\n" +
			  "        still unproven, and the battery result may be a read path only.");
		Console.WriteLine();

		Console.WriteLine("  => The request/response battery mechanism WORKS over the dongle. This replaces the");
		Console.WriteLine("     passive listener: the app can ask for battery on demand instead of waiting for");
		Console.WriteLine("     a link-establishment push, which is what TaipanStateMonitor does today.");
		Console.WriteLine();
		Console.WriteLine("     Note WHICH collection the reply arrived on — if it is not the one we wrote to,");
		Console.WriteLine("     that is the interface point the vendor was making.");
	}

	private static string Short(HidDevice d)
	{
		var path = d.DevicePath;
		var i = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
		var tag = i >= 0 ? path[i..Math.Min(i + 14, path.Length)] : "?";
		return $"{tag} in={Len(() => d.GetMaxInputReportLength())} out={Len(() => d.GetMaxOutputReportLength())}";
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
