using HidSharp;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — can the dongle tell us the mouse is switched OFF? ──────────────────────────────
//
// The dongle is a USB device in its own right: it stays enumerated whether or not a mouse is
// talking through it. So "dongle present" is NOT "mouse present", and today the App cannot tell the
// difference — it keeps showing the last cached battery level after the mouse is switched off.
// Cosmo solves the same problem with a status report whose byte 19 says whether the headset is on.
// This arm looks for TAIPAN's equivalent.
//
// One data point already exists (2026-08-14): with the mouse running on its CABLE — so nothing on
// the radio — a battery request over the dongle came back as an all-zero frame,
// `05-24-FA-48-00-00-...`, i.e. a reply with raw = 0. TaipanStateReport.TryParseBattery already
// rejects that (`rawValue > 0`). If switching the mouse OFF produces the same thing, we have a
// presence signal for free and no new opcode is needed.
//
// WRITES only the documented battery request (cmd 0x48, all parameters zero) — the same frame
// TaipanStateMonitor already sends on every refresh pass. driverPara4 stays 0, so the flash-erasing
// sequence value (0xD1) is never sent.
internal static class TaipanPresenceProbe
{
	private const int Vid = 0x248A;
	private const int DonglePid = 0x8373;

	private const int PollIntervalMs = 2000;
	private const int ReplyWindowMs = 1200;

	public static async Task RunAsync(int phaseSeconds)
	{
		Console.WriteLine("PG TAIPAN — does the dongle report the mouse being switched OFF?");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid).ToList();
		var pids = interfaces.Select(d => d.ProductID).Distinct().ToList();

		if (!pids.Contains(DonglePid))
		{
			Console.WriteLine($"The 2.4G dongle (PID 0x{DonglePid:X4}) is not present.");
			Console.WriteLine(pids.Count == 0
				? "Nothing on this vendor ID at all — plug the dongle in."
				: $"Found instead: {string.Join(", ", pids.Select(p => $"0x{p:X4}"))}");
			return;
		}

		if (pids.Count > 1)
		{
			Console.WriteLine("MORE THAN ONE TRANSPORT IS CONNECTED: " +
				string.Join(", ", pids.Select(p => $"0x{p:X4}")));
			Console.WriteLine("Unplug the cable. With the mouse running on USB it is not on the radio,");
			Console.WriteLine("which is a DIFFERENT state from the one being measured here.");
			return;
		}

		var target = interfaces.FirstOrDefault(d =>
			d.ProductID == DonglePid &&
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (target is null)
		{
			Console.WriteLine($"No {TaipanFrame.Length}/{TaipanFrame.Length} vendor collection on the dongle.");
			return;
		}

		Console.WriteLine($"writing to {Short(target)}");
		Console.WriteLine();

		var on = await PhaseAsync(target, "MOUSE ON", phaseSeconds,
			"Leave the mouse switched ON and move it a little.");

		Console.WriteLine();
		Console.WriteLine(new string('=', 92));
		Console.WriteLine(">>> NOW SWITCH THE MOUSE OFF (the slider underneath), then press Enter. <<<");
		Console.ReadLine();

		var off = await PhaseAsync(target, "MOUSE OFF", phaseSeconds,
			"Leave it switched off.");

		Report(on, off);
	}

	private sealed record Phase(string Name, int Requests, List<byte[]> Frames, List<(int Raw, bool Usb)> Parsed);

	private static async Task<Phase> PhaseAsync(HidDevice device, string name, int seconds, string instruction)
	{
		Console.WriteLine(new string('-', 92));
		Console.WriteLine($"PHASE: {name} — {seconds}s. {instruction}");

		var frames = new List<byte[]>();
		var parsed = new List<(int, bool)>();
		var request = TaipanFrame.Build(TaipanStateReport.BatteryRequest, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty);

		var deadline = DateTime.UtcNow.AddSeconds(seconds);
		var requests = 0;

		while (DateTime.UtcNow < deadline)
		{
			requests++;

			try
			{
				if (!device.TryOpen(out var stream))
				{
					Console.WriteLine("  could not open the collection");
					break;
				}

				using (stream)
				{
					stream.ReadTimeout = ReplyWindowMs;
					stream.Write(request);

					var buf = new byte[TaipanFrame.Length];
					var replyBy = DateTime.UtcNow.AddMilliseconds(ReplyWindowMs);
					var got = false;

					while (DateTime.UtcNow < replyBy)
					{
						int n;
						try { n = stream.Read(buf); }
						catch (TimeoutException) { break; }
						catch { break; }

						if (n <= 3 || buf[3] != TaipanStateReport.BatteryReply) continue;

						got = true;
						var frame = buf[..n];
						frames.Add(frame);

						if (TaipanStateReport.TryParseBattery(frame, out var raw, out var usb))
						{
							parsed.Add((raw, usb));
							Console.WriteLine($"  reply: raw={raw} usb={usb}  {BitConverter.ToString(frame[..8])}...");
						}
						else
						{
							// The interesting case: a reply that exists but carries nothing usable.
							Console.WriteLine($"  reply: UNPARSEABLE (raw=0?)  {BitConverter.ToString(frame[..8])}...");
						}
						break;
					}

					if (!got) Console.WriteLine("  no reply");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  request failed: {ex.Message}");
			}

			await Task.Delay(PollIntervalMs);
		}

		return new Phase(name, requests, frames, parsed);
	}

	private static void Report(Phase on, Phase off)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 92));
		Console.WriteLine("RESULT");
		Console.WriteLine();
		Console.WriteLine($"  {on.Name,-10} {on.Requests} request(s) -> {on.Frames.Count} reply(s), {on.Parsed.Count} with a usable level");
		Console.WriteLine($"  {off.Name,-10} {off.Requests} request(s) -> {off.Frames.Count} reply(s), {off.Parsed.Count} with a usable level");
		Console.WriteLine();

		if (on.Parsed.Count == 0)
		{
			Console.WriteLine("  The MOUSE ON phase produced no usable level either, so this run cannot");
			Console.WriteLine("  distinguish anything. Check the mouse is awake and paired to THIS dongle.");
			return;
		}

		if (off.Parsed.Count == 0 && off.Frames.Count > 0)
		{
			Console.WriteLine("  SIGNAL FOUND — the dongle still replies with the mouse off, but the reply");
			Console.WriteLine("  carries no usable level (raw=0). TryParseBattery already rejects it, so the");
			Console.WriteLine("  App only needs to treat 'no fresh reading' as 'mouse not present' instead of");
			Console.WriteLine("  serving the last cached percentage.");
		}
		else if (off.Frames.Count == 0)
		{
			Console.WriteLine("  SIGNAL FOUND — the dongle goes SILENT with the mouse off, while it answered");
			Console.WriteLine("  every request with it on. Absence of a reply is the presence signal; the App");
			Console.WriteLine("  needs a staleness rule rather than a new opcode.");
		}
		else
		{
			Console.WriteLine("  NO CLEAN SIGNAL — the dongle answered with a usable level even with the mouse");
			Console.WriteLine("  switched off, which means it is serving a cached value of its own. Compare the");
			Console.WriteLine("  raw numbers above: if they are frozen while ON-phase values drift, staleness is");
			Console.WriteLine("  still usable, but only as a heuristic.");
			Console.WriteLine();
			Console.WriteLine($"    ON  raws: {string.Join(", ", on.Parsed.Select(p => p.Raw))}");
			Console.WriteLine($"    OFF raws: {string.Join(", ", off.Parsed.Select(p => p.Raw))}");
		}
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
