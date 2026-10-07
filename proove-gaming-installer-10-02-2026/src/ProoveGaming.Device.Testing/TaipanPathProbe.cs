using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — what does HidSharp actually offer us to write to? ──────────────────────────────
//
// Every write this repo has ever made to TAIPAN over the dongle went to the FIRST interface whose
// input and output report lengths are both 33, which in practice has always been MI_00&Col04. That
// selection was never audited against the full enumeration, so this arm prints the whole list with
// no filtering: which interfaces exist, which are writable, and which one the existing probes pick.
//
// The vendor's advice was "if a command gets no response, check the app is on the right HID
// interface". Their own matching example names MI_00&Col01 AND MI_00&Col04, but Windows also
// exposes MI_01 as a keyboard interface — a second USB interface we have never enumerated, let
// alone written to.
internal static class TaipanPathProbe
{
	private const int Vid = 0x248A;

	// Labels only. The scan itself enumerates EVERY PID on the vendor ID rather than these three,
	// because a hardcoded PID list turns "the device enumerates differently than we expect" into
	// "not present" — the one failure mode that would quietly hide the answer we are looking for.
	private static readonly Dictionary<int, string> KnownPids = new()
	{
		[0x8373] = "2.4G dongle",
		[0xFB01] = "wired",
		[0x8266] = "bluetooth",
	};

	public static void Run()
	{
		Console.WriteLine("PG TAIPAN — full HID enumeration (no filtering)");
		Console.WriteLine();

		var byPid = DeviceList.Local.GetHidDevices(Vid)
			.GroupBy(d => d.ProductID)
			.OrderBy(g => g.Key)
			.ToList();

		if (byPid.Count == 0)
		{
			Console.WriteLine($"No HID device on VID 0x{Vid:X4} at all — neither dongle nor cable is connected.");
			return;
		}

		foreach (var group in byPid)
		{
			var pid = group.Key;
			var devices = group.ToList();
			var label = KnownPids.TryGetValue(pid, out var known) ? known : "UNKNOWN PID — not in our catalog";

			Console.WriteLine(new string('=', 100));
			Console.WriteLine($"PID 0x{pid:X4} — {label}: {devices.Count} interface(s)");
			Console.WriteLine();

			// The selection every existing probe makes, so it can be compared against the list.
			var chosen = devices.FirstOrDefault(d =>
				Len(() => d.GetMaxOutputReportLength()) == 33 &&
				Len(() => d.GetMaxInputReportLength()) == 33);

			foreach (var d in devices)
			{
				var inLen = Len(() => d.GetMaxInputReportLength());
				var outLen = Len(() => d.GetMaxOutputReportLength());
				var featLen = Len(() => d.GetMaxFeatureReportLength());

				// Whether we can open it at all is as important as its report lengths — Windows
				// claims some collections exclusively, and an interface we cannot open is not a
				// candidate no matter how promising its descriptor looks.
				var openable = d.TryOpen(out var probe);
				if (openable) probe.Dispose();

				Console.WriteLine($"  {(ReferenceEquals(d, chosen) ? "->" : "  ")} in={inLen,-3} out={outLen,-3} feat={featLen,-3} " +
					$"{(openable ? "openable" : "LOCKED  ")}  usage={Usage(d)}");
				Console.WriteLine($"       {d.DevicePath}");
			}

			Console.WriteLine();
			Console.WriteLine(chosen is null
				? "  No 33/33 interface — the existing probes would find nothing here."
				: $"  '->' marks the interface every existing probe writes to.");
			Console.WriteLine();
		}

		var missing = KnownPids.Where(kv => byPid.All(g => g.Key != kv.Key)).ToList();
		if (missing.Count > 0)
		{
			Console.WriteLine(new string('=', 100));
			foreach (var (pid, label) in missing)
				Console.WriteLine($"  not present: 0x{pid:X4} ({label})");
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 100));
		Console.WriteLine("Interfaces marked LOCKED cannot be opened for writing by us at all.");
		Console.WriteLine("An interface with out=0 cannot receive a config write regardless of anything else.");
	}

	// Top-level usage page/usage, which is what distinguishes a mouse collection from a keyboard
	// or a vendor-defined one. Vendor collections live at page 0xFF00 and above.
	private static string Usage(HidDevice d)
	{
		try
		{
			var report = d.GetReportDescriptor();
			var items = report.DeviceItems;
			if (items.Count == 0) return "none";

			var usages = items[0].Usages.GetAllValues().Take(1).ToList();
			if (usages.Count == 0) return "none";

			var u = usages[0];
			var page = (u >> 16) & 0xFFFF;
			return $"0x{page:X4}:0x{u & 0xFFFF:X4}{(page >= 0xFF00 ? " (vendor)" : "")}";
		}
		catch
		{
			return "unreadable";
		}
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
