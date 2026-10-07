using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── PG Cosmo headset — Phase 0 probe ──────────────────────────────────────────────────────────
//
// Gate for docs/plans/add-cosmo-headset. It answers ONE question before any protocol code is
// written: does this headset expose a vendor-defined channel we could drive at all?
//
// What enumeration already told us:
//   VID 0x0D8C (C-Media) / PID 0x0334, "THI Headphone" / "PG Cosmo", USB composite.
//   MI_00 = USB Audio Class  -> the Windows "Headphones (PG Cosmo)" / "Microphone (PG Cosmo)" endpoints.
//   MI_03 = one HID interface, in=32 / out=32 / feature=0, classed "HID-compliant consumer control".
//   Serial number 00006F64096B22E7 — a REAL iSerialNumber, unlike BLAZE and TAIPAN.
//
// The 32-byte OUT report is the only hint a vendor channel might exist: a plain consumer-control
// collection has no need for one. If the descriptor shows only usage page 0x0C (Consumer), there is
// nothing to drive and RGB / device-side volume are impossible on this hardware.
//
// STRICTLY READ-ONLY. No writes of any kind — an unknown write to an unknown channel is exactly how
// the BLAZE flash corruption happened (see docs/mouse/blaze). If this probe finds a vendor
// collection, the write experiments belong in a follow-up plan with the vendor's protocol in hand.
internal static class CosmoProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;

	public static void Run(int listenSeconds = 20)
	{
		Console.WriteLine("PG Cosmo — Phase 0 probe (read-only)");
		Console.WriteLine($"Looking for VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();

		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("Not found. Is the headset (or its dongle) plugged in?");
			return;
		}

		Console.WriteLine($"{interfaces.Count} HID interface(s):");
		Console.WriteLine();

		var vendorCollections = 0;
		foreach (var d in interfaces)
		{
			Console.WriteLine(new string('-', 78));
			Console.WriteLine($"  path      : {d.DevicePath}");
			Console.WriteLine($"  serial    : {Try(d.GetSerialNumber)}");
			Console.WriteLine($"  manufact. : {Try(d.GetManufacturer)}   product: {Try(d.GetProductName)}");
			Console.WriteLine($"  reports   : in={Len(d, false)} out={Len(d, true)} feature={FeatureLen(d)}");

			try
			{
				var rd = d.GetReportDescriptor();

				foreach (var item in rd.DeviceItems)
				{
					foreach (var usage in item.Usages.GetAllValues())
					{
						var page = (usage >> 16) & 0xFFFF;
						var id = usage & 0xFFFF;
						// Usage pages 0xFF00-0xFFFF are vendor-defined: that is the channel we are
						// looking for. 0x0C is Consumer (the volume keys Windows already handles).
						var vendor = page >= 0xFF00;
						if (vendor) vendorCollections++;
						Console.WriteLine($"    collection usagePage=0x{page:X4} usage=0x{id:X4}{(vendor ? "   <-- VENDOR-DEFINED" : "")}");
					}
				}

				foreach (var r in rd.Reports)
					Console.WriteLine($"    report type={r.ReportType,-7} id=0x{r.ReportID:X2} length={r.Length}");

				// The top-level collection usage is not the whole story: a vendor channel is often
				// declared as a vendor-defined USAGE PAGE item deeper inside an otherwise standard
				// collection. Scan the raw descriptor for "Usage Page (0xFFxx)" — encoded as
				// 0x06 lo hi with hi >= 0xFF for the 16-bit form, or 0x05 xx for the 8-bit form.
				var raw = d.GetRawReportDescriptor();
				Console.WriteLine($"    raw descriptor ({raw.Length} bytes): {Hex(raw)}");
				for (var i = 0; i + 2 < raw.Length; i++)
				{
					if (raw[i] == 0x06 && raw[i + 2] >= 0xFF)
					{
						var page = raw[i + 1] | (raw[i + 2] << 8);
						Console.WriteLine($"    RAW vendor usage page 0x{page:X4} at byte {i}   <-- VENDOR-DEFINED");
						vendorCollections++;
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"    could not parse descriptor: {ex.GetType().Name}: {ex.Message}");
			}
		}

		Console.WriteLine(new string('=', 78));
		Console.WriteLine(vendorCollections > 0
			? $"VERDICT: {vendorCollections} vendor-defined collection(s) found — a control channel MAY exist.\n" +
			  "         RGB / device-side volume become worth pursuing, with the vendor's protocol."
			: "VERDICT: NO vendor-defined collection — only standard pages.\n" +
			  "         Nothing here can be driven: RGB and device-side volume are not possible on this\n" +
			  "         hardware, and the Audio section must use the OS endpoint volume (as planned).");
		Console.WriteLine(new string('=', 78));

		ListenForInputReports(interfaces, listenSeconds);
	}

	// Watches every readable interface while the user presses the headset's controls. Shows what the
	// device volunteers — if the only traffic is consumer-control keycodes, that confirms the
	// descriptor's story from the other direction.
	private static void ListenForInputReports(List<HidDevice> interfaces, int seconds)
	{
		Console.WriteLine();
		Console.WriteLine($"Listening for {seconds}s — press the headset's volume +/-, mute, and any other control.");
		Console.WriteLine();

		var streams = new List<(HidDevice Dev, HidStream Stream)>();
		var stop = false;
		var threads = new List<Thread>();

		try
		{
			foreach (var d in interfaces.Where(x => Len(x, output: false) > 0))
			{
				if (!d.TryOpen(out var s))
				{
					// Windows opens consumer-control collections exclusively; that is expected and is
					// itself informative (it means Windows, not us, owns those keys).
					Console.WriteLine($"  (could not open {Short(d)} — Windows holds it exclusively)");
					continue;
				}
				s.ReadTimeout = 200;
				streams.Add((d, s));
			}

			foreach (var (dev, stream) in streams)
			{
				var label = Short(dev);
				var t = new Thread(() =>
				{
					var buf = new byte[Math.Max(32, Len(dev, output: false))];
					while (!Volatile.Read(ref stop))
					{
						int n;
						try { n = stream.Read(buf); }
						catch (TimeoutException) { continue; }
						catch { return; }
						if (n > 0)
							Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {label}: {Hex(buf.AsSpan(0, n).ToArray())}");
					}
				})
				{ IsBackground = true };
				threads.Add(t);
				t.Start();
			}

			if (streams.Count == 0)
				Console.WriteLine("  No interface could be opened for reading.");

			Thread.Sleep(seconds * 1000);
		}
		finally
		{
			Volatile.Write(ref stop, true);
			foreach (var t in threads) t.Join(500);
			foreach (var (_, s) in streams) s.Dispose();
		}

		Console.WriteLine();
		Console.WriteLine("Done.");
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return 0; }
	}

	private static int FeatureLen(HidDevice d)
	{
		try { return d.GetMaxFeatureReportLength(); }
		catch { return 0; }
	}

	private static string Short(HidDevice d)
	{
		var p = d.DevicePath ?? string.Empty;
		var i = p.IndexOf('#');
		var j = i >= 0 ? p.IndexOf('#', i + 1) : -1;
		return j > i ? p[(i + 1)..j] : p;
	}

	private static string Try(Func<string?> f)
	{
		try { return f() ?? "<empty>"; }
		catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
	}

	private static string Hex(byte[] data) => string.Join(' ', data.Select(b => b.ToString("X2")));
}
