using HidSharp;

namespace ProoveGaming.Device.Testing;

// ── TAIPAN (Telink) 2.4G dongle probe — DPI table + battery, over the wireless dongle ──────────
//
// Answers the open questions from the 2026-07-30 vendor Q&A
// (docs/mouse/taipan/taipan-vendor-qa-2026-07-30.md, items T2/T3/T6/T7/T8) that need real
// hardware, not just document analysis:
//
//   1. Does the dongle actually ACCEPT writes? TelinkMouseService's own comment says "the 2.4G
//      dongle relays the mouse's own pushes but not host->mouse frames, so reads return null and
//      writes no-op over the dongle" — but the vendor's T2 answer says the protocol is identical
//      to wired and 2.4G writes should work, and offered to debug if that's not what we see.
//      RunDpiWriteTest below is the direct test: change one DPI stage over the dongle, read it
//      back, then restore the original value.
//   2. What does the battery reply (0x08 -> 0x48) actually contain? Dumps every byte so the
//      vendor's percentage formula (T6: (100*(V-750))/(985-750)) can be matched against a real
//      reply — the vendor's own example values (750-985) are far too small to be the documented
//      mV field (e.g. 0x1068 = 4200 mV), so this probe reports every non-zero byte rather than
//      assuming which one the formula applies to.
//   3. Is this actually the 2.4G dongle, or Bluetooth? Both enumerate under the SAME PID
//      (0x8373) — classified here by device path (Bluetooth enumerates under a BTHENUM/BTHLE
//      bus, the dongle is a plain USB HID device) and cross-checked against the vendor-confirmed
//      per-transport report lengths (T8): the dongle reports Usage 0x0000 / UsagePage 0x0001 /
//      33-byte input+output, matching the frame length directly (the wired unit pads the same
//      frame into a 64-byte report instead).
//
// The DPI table read and the battery read are safe (read-only). The DPI write test mutates one
// stage on the device and always restores it afterwards, but still prompts for confirmation
// first, the same way the BLAZE reset spike (see git history) did for its write path.
internal static class TaipanDongleProbe
{
	private const int Vid = 0x248A;           // 9354
	private const int PidWired = 0xFB01;      // 64257 — cable, mouse itself enumerates
	private const int PidDongleOrBt = 0x8373; // 33651 — 2.4G dongle AND Bluetooth share this PID

	// ── Frame format — mirrors ProoveGaming.Infrastructure.Classes.TaipanFrame. Device.Testing
	// has no project reference to Infrastructure (see the other probes in this project), so the
	// framing is reproduced here rather than shared. ──────────────────────────────────────────
	private const int FrameLength = 33;
	private const byte ReportId = 0x05;
	private const byte DriverId = 0xFA;
	private const byte ChecksumXor = 0x66;
	private const int PayloadOffset = 8;
	private const int MaxPayload = FrameLength - PayloadOffset; // 25

	private const byte SeqFirst = 0xD1;    // erases the flash sector
	private const byte SeqContinue = 0xD2;
	private const byte SeqCommit = 0xD3;

	private const byte DpiWriteCmd = 0x02;
	private const byte DpiReadCmd = 0x42;
	private const byte ConfigWriteCmd = 0x01;
	private const byte ConfigReadCmd = 0x41;
	private const byte ButtonWriteCmd = 0x03;
	private const byte ButtonReadCmd = 0x43;
	private const byte BatteryCmd = 0x08;
	private const byte BatteryReplyCmd = 0x48;

	private const int DpiMaxStages = 8;
	private const int DpiBytesPerStage = 5;
	private const int DpiTableLength = 1 + DpiMaxStages * DpiBytesPerStage; // 41
	private const int ValidBankIdOffset = 0x007F;
	private const byte ValidBankId = 0x1A;
	private const int MsConfigLength = 0x13; // 19
	private const int DpiSwitchOffset = 0x11;
	private const int PollingRateOffset = 0x0B;

	// Button map table (0x03 write / 0x43 read) — mirrors ProoveGaming.Infrastructure.Classes.TaipanButtonMap.
	private const int ButtonHeaderLength = 2;
	private const int ButtonMaxButtons = 25;
	private const int ButtonEntryLength = 4;
	private const int ButtonTableLength = ButtonHeaderLength + ButtonMaxButtons * ButtonEntryLength; // 102
	private const int ButtonPollingRateCycleIndex = 7; // 1-based — factory default: 97 01 00 00
	private static int ButtonEntryOffset(int index) => ButtonHeaderLength + (index - 1) * ButtonEntryLength;

	private const int ReadTimeoutMs = 400;
	private const int ReadWindowMs = 1500;
	private const int PacketDelayMs = 12;

	// Distinguishing test for "does the dongle actually accept button-map writes, or does the frame
	// silently no-op?" — a plain factory-default rewrite is inconclusive on a mouse already at
	// defaults (buttons 1-6 all still worked after that write, but that's equally consistent with
	// "write landed" and "write did nothing"). This writes the SAME factory-default table but with
	// ONLY Button 3 (wheel/middle-click) remapped to a consumer Mute key (function 0x8E, usage
	// 0x00E2 per protocol.md's Button map / TaipanButtonMap.Consumer) — a single middle-click either
	// audibly/visibly mutes the system (write landed) or performs a normal middle-click (it didn't).
	// `revert: true` writes the pure factory-default table back (Button 3 = Middle again).
	public static void RunMuteDistinguishTest(bool revert)
	{
		Console.WriteLine(revert
			? "TAIPAN dongle probe — REVERT Button 3 to factory default (Middle-click)"
			: "TAIPAN dongle probe — Button 3 (wheel-click) -> Mute distinguishing test");
		Console.WriteLine("Preconditions: ProoveGamingDeviceHost STOPPED, TAIPAN connected via the 2.4G DONGLE.");
		Console.WriteLine();

		var target = DeviceList.Local.GetHidDevices(Vid, PidDongleOrBt)
			.FirstOrDefault(d => Len(d, output: true) == 33 && Len(d, output: false) == 33);
		if (target == null)
		{
			Console.WriteLine("No 33-byte dongle interface found. Is the TAIPAN connected via the 2.4G dongle?");
			return;
		}

		if (!target.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the interface. Is ProoveGamingDeviceHost running? Stop it and retry.");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = ReadTimeoutMs;
			stream.WriteTimeout = ReadTimeoutMs;

			var table = BuildDefaultButtonTable();
			if (!revert)
				SetButtonEntry(table, 3, 0x8E, 0x00, 0xE2, 0x00); // Consumer Mute (usage 0x00E2, LE)

			Console.WriteLine($"  writing table[{ButtonTableLength}] (Button 3 = {(revert ? "Middle-click (factory default)" : "Mute")}): {Hex(table)}");
			var wrote = WriteTable(stream, ButtonWriteCmd, table, ValidBankIdOffset);
			Console.WriteLine($"  write completed without exception: {wrote}");
			Console.WriteLine();
			Console.WriteLine(revert
				? "  Button 3 should now be a normal middle-click again."
				: "  Now click the wheel (Button 3 / middle-click) ONCE and check: does it mute/unmute system audio?");
		}
	}

	// Passive listener — mirrors what TaipanStateMonitor actually does in the App: no active query,
	// just hold the vendor collection open and wait for the mouse's own unsolicited 0x48 (battery) /
	// 0x49 (state) pushes, which only fire on a 2.4G link-establishment event (dongle plug-in, USB
	// unplug, physical mode-switch flip, wake from sleep) — NOT on-demand, and NOT just from opening
	// the handle. If nothing arrives within the window, move/click the mouse (wake it) or unplug and
	// replug the dongle to force a fresh link event.
	public static void RunBatteryPushListener(int windowSeconds)
	{
		Console.WriteLine("TAIPAN dongle probe — passive battery/state push listener (mirrors TaipanStateMonitor)");
		Console.WriteLine($"Listening for up to {windowSeconds}s. If nothing shows up, WAKE the mouse (click/move it)");
		Console.WriteLine("or unplug/replug the 2.4G dongle to force a fresh link-establishment push.");
		Console.WriteLine("Preconditions: ProoveGamingDeviceHost STOPPED, TAIPAN connected via the 2.4G DONGLE.");
		Console.WriteLine();

		// Outer loop tolerates the dongle actually disconnecting (unplug/replug is the most reliable
		// way to force a link-establishment push): a read failure closes the current handle and goes
		// back to re-enumerating until the device reappears, rather than giving up — mirrors
		// TaipanStateMonitor's own reader-per-path reconnect behaviour in the App.
		var deadline = DateTime.UtcNow.AddSeconds(windowSeconds);
		var buffer = new byte[FrameLength];
		var sawAny = false;

		while (DateTime.UtcNow < deadline)
		{
			var target = DeviceList.Local.GetHidDevices(Vid, PidDongleOrBt)
				.FirstOrDefault(d => Len(d, output: true) == 33 && Len(d, output: false) == 33);
			if (target == null)
			{
				Thread.Sleep(300);
				continue;
			}

			if (!target.TryOpen(out var stream))
			{
				Thread.Sleep(300);
				continue;
			}

			using (stream)
			{
				stream.ReadTimeout = 500;

				while (DateTime.UtcNow < deadline)
				{
					int read;
					try { read = stream.Read(buffer); }
					catch (TimeoutException) { continue; }
					catch (Exception)
					{
						Console.WriteLine("  (handle dropped — device likely unplugged; waiting for it to reappear...)");
						break; // back to the outer loop to re-enumerate and reopen
					}

					if (read <= 0) continue;
					sawAny = true;
					var span = buffer.AsSpan(0, read);
					Console.WriteLine($"  report: {Hex(span.ToArray())}");

					if (TaipanStateReportTryParseBattery(span, out var raw, out var usb))
					{
						int mv = raw * 4;
						int percent = (int)Math.Clamp(Math.Round((mv - 3300) * 100.0 / (4200 - 3300)), 0, 100);
						Console.WriteLine($"    >>> BATTERY PUSH: raw={raw} (~{mv} mV, quarter-mV interpretation) => {percent}% usbConnected={usb}");
					}
					else if (TaipanStateReportTryParseState(span, out var profile, out var stage, out var rate))
					{
						Console.WriteLine($"    >>> STATE PUSH: profile={profile} dpiStage={stage} reportRateRaw=0x{rate:X2}");
					}
				}
			}
		}

		if (!sawAny)
			Console.WriteLine("  No reports arrived in the window at all — dongle relayed nothing.");
	}

	// Inlined copies of ProoveGaming.Infrastructure.Classes.TaipanStateReport's parse logic (this
	// probe project has no reference to Infrastructure — see the file-header note on the other
	// inlined helpers).
	private static bool TaipanStateReportTryParseBattery(ReadOnlySpan<byte> report, out int rawValue, out bool usbConnected)
	{
		rawValue = 0; usbConnected = false;
		if (report.Length < 8 || report[0] != ReportId || report[2] != DriverId || report[3] != BatteryReplyCmd) return false;
		if (report[1] != Checksum(report[3], report[4], report[5], report[6], report[7])) return false;
		rawValue = report[4] | (report[5] << 8);
		usbConnected = report[6] != 0;
		return rawValue > 0;
	}

	private static bool TaipanStateReportTryParseState(ReadOnlySpan<byte> report, out int profile, out int dpiStage, out byte reportRateRaw)
	{
		profile = 0; dpiStage = 0; reportRateRaw = 0;
		const byte stateReplyCmd = 0x49;
		if (report.Length < 8 || report[0] != ReportId || report[2] != DriverId || report[3] != stateReplyCmd) return false;
		if (report[1] != Checksum(report[3], report[4], report[5], report[6], report[7])) return false;
		profile = report[4];
		dpiStage = report[5];
		reportRateRaw = report[6];
		return true;
	}

	// ── Cross-collection read probe ───────────────────────────────────────────────────────────
	// Every read on the 33-byte driver collection (MI_00&COL04) comes back silent over the dongle,
	// while the mouse's own unsolicited 0x48/0x49 pushes DO arrive (RunBatteryPushListener). So
	// device->host relay works in general. This asks the obvious follow-up: when we send a QUERY on
	// COL04, is the reply relayed to a DIFFERENT collection instead of the one we asked on?
	//
	// Sends one query on COL04, listens on EVERY dongle collection with an input report at once,
	// and dumps whatever arrives anywhere. Non-destructive: a query carries no 0xD1, so no flash
	// sector is erased and nothing is written.
	public static void RunCrossCollectionReadProbe(int windowSeconds = 4)
	{
		Console.WriteLine("TAIPAN dongle — cross-collection read probe");
		Console.WriteLine("Sends a query on the 33-byte driver collection and listens on ALL dongle");
		Console.WriteLine("collections, in case the reply is relayed to a different one.");
		Console.WriteLine("Non-destructive: no 0xD1, nothing is written to flash.");
		Console.WriteLine();

		if (CableIsAlsoConnected())
		{
			Console.WriteLine("ABORTING — the USB cable is connected; the 2.4G link is idle. Unplug it.");
			return;
		}

		var dongle = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt && Len(d, output: false) > 0)
			.ToList();
		if (dongle.Count == 0)
		{
			Console.WriteLine("No dongle interfaces with an input report found.");
			return;
		}

		var writer = dongle.FirstOrDefault(d => Len(d, output: true) == FrameLength && Len(d, output: false) == FrameLength);
		if (writer == null)
		{
			Console.WriteLine("No 33/33 driver collection to send the query on.");
			return;
		}

		// One reader per collection, each on its own thread so a silent handle cannot starve the
		// others. Streams are disposed in the finally so the handles are released even on error.
		var streams = new List<(HidDevice Dev, HidStream Stream)>();
		var hits = new List<string>();
		var stop = false;
		var threads = new List<Thread>();

		try
		{
			foreach (var d in dongle)
			{
				if (!d.TryOpen(out var s))
				{
					Console.WriteLine($"  could not open {Short(d)} — skipping");
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
					var buf = new byte[Math.Max(FrameLength, Len(dev, output: false))];
					while (!Volatile.Read(ref stop))
					{
						int n;
						try { n = stream.Read(buf); }
						catch (TimeoutException) { continue; }
						catch { return; }
						if (n <= 0) continue;
						lock (hits) hits.Add($"  [{label}] {Hex(buf.AsSpan(0, n).ToArray())}");
					}
				})
				{ IsBackground = true };
				threads.Add(t);
				t.Start();
			}

			Console.WriteLine($"Listening on {streams.Count} collection(s) for {windowSeconds}s.");
			Console.WriteLine($"Sending queries on {Short(writer)}");
			Console.WriteLine();

			var writerStream = streams.FirstOrDefault(x => x.Dev.DevicePath == writer.DevicePath).Stream;
			if (writerStream == null)
			{
				Console.WriteLine("The driver collection could not be opened for writing.");
				return;
			}

			// One query per readable block, plus the battery command the vendor says the mouse
			// answers with a 0x48 push — that one is the control: if even IT stays silent here,
			// the link itself is asleep rather than the read path being unsupported.
			foreach (var (cmd, name, addr, len) in new[]
			{
				(DpiReadCmd,     "DPI table 0x42", 0x0000, DpiTableLength),
				(ConfigReadCmd,  "MsConfig 0x41",  0x0000, MsConfigLength),
				(ButtonReadCmd,  "Buttons 0x43",   0x0000, 16),
				(BatteryCmd,     "Battery 0x08",   0x0000, 0),
			})
			{
				Console.WriteLine($"  -> {name}");
				writerStream.Write(Build(cmd, (byte)(addr & 0xFF), (byte)(addr >> 8), (byte)len, SeqContinue, ReadOnlySpan<byte>.Empty));
				Thread.Sleep(600);
			}

			Thread.Sleep(windowSeconds * 1000);
		}
		finally
		{
			Volatile.Write(ref stop, true);
			foreach (var t in threads) t.Join(500);
			foreach (var (_, s) in streams) s.Dispose();
		}

		Console.WriteLine();
		lock (hits)
		{
			if (hits.Count == 0)
			{
				Console.WriteLine("NOTHING arrived on ANY dongle collection.");
				Console.WriteLine("Move/click the mouse during the window to confirm the radio link is up —");
				Console.WriteLine("the mouse collection (usage 0x0002) should then report motion.");
			}
			else
			{
				Console.WriteLine($"{hits.Count} report(s) arrived:");
				foreach (var h in hits.Take(60)) Console.WriteLine(h);
			}
		}
	}

	// ── Vendor-exact DPI table write ──────────────────────────────────────────────────────────
	// Reproduces the vendor's own USB capture (docs/mouse/taipan/fabric-info, image_574065711_1)
	// frame for frame, because our WriteTable differs from it in two ways that USB tolerates and
	// the 2.4G link may not:
	//
	//   1. CHUNKING. We pack the 41-byte table into 25-byte blobs (two frames). The vendor sends
	//      one field per frame: a 1-byte stage count at 0x0000, then each 5-byte stage at its own
	//      address (0x0001, 0x0006, 0x000B, …). This is what their T8 answer means by
	//      "我们这边的是分开下的" — "ours is sent split up".
	//   2. COMMIT. We write ValidBankId with 0xD2 and then send a separate empty 0xD3 at address 0.
	//      In the capture the ValidBankId frame at 0x007F IS the 0xD3 — there is no extra frame.
	//
	// Verified against the capture byte for byte, including the checksum:
	//   05 a8 fa 02 00 00 01 d1 | 06   -> 0xFA+02+00+00+01+D1 = 0x1CE -> 0xCE ^ 0x66 = 0xA8 ✓
	//   05 b2 fa 02 01 00 05 d2 | 01 14 00 14 00
	//
	// Stage encoding, from the vendor's own worked values: [enable, CPI_lo, CPI_hi, CPI_lo, CPI_hi]
	// with the register = CPI/50 (their 0x14 = 20 = 1000 CPI), X mirrored to Y. Kept below 12750 CPI
	// so the high byte stays 0 and we avoid the disputed "doubling" flag in T7.
	//
	// DESTRUCTIVE: the opening 0xD1 erases the DPI flash sector.
	public static void RunVendorExactDpiWrite(int cpi, int stageCount = 7)
	{
		Console.WriteLine("TAIPAN dongle — VENDOR-EXACT DPI table write");
		Console.WriteLine($"Writing {stageCount} stage(s), all at {cpi} CPI, using the vendor's frame sequence.");
		Console.WriteLine();

		if (CableIsAlsoConnected())
		{
			Console.WriteLine("ABORTING — the USB cable is connected; the 2.4G link is idle. Unplug it.");
			return;
		}
		if (cpi < 50 || cpi > 12750 || cpi % 50 != 0)
		{
			Console.WriteLine($"CPI must be a multiple of 50 between 50 and 12750 (got {cpi}).");
			return;
		}

		var writer = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt)
			.FirstOrDefault(d => Len(d, output: true) == FrameLength && Len(d, output: false) == FrameLength);
		if (writer == null)
		{
			Console.WriteLine("No 33/33 driver collection found on the dongle.");
			return;
		}
		if (!writer.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the driver collection. Is the DeviceHost running?");
			return;
		}

		using (stream)
		{
			stream.WriteTimeout = ReadTimeoutMs;
			var reg = (byte)(cpi / 50);

			void Send(string what, byte[] frame)
			{
				Console.WriteLine($"  {what,-28} {Hex(frame.AsSpan(0, 16).ToArray())}");
				stream.Write(frame);
				Thread.Sleep(PacketDelayMs);
			}

			try
			{
				// 1. Stage count, 0xD1 — erases the sector.
				Send("count @0x0000 (D1)", Build(DpiWriteCmd, 0x00, 0x00, 0x01, SeqFirst, [(byte)stageCount]));

				// 2. One frame per 5-byte stage, at its own address.
				for (var i = 0; i < DpiMaxStages; i++)
				{
					var addr = 1 + i * DpiBytesPerStage;
					var enabled = i < stageCount;
					byte[] payload = enabled
						? [0x01, reg, 0x00, reg, 0x00]
						: [0x00, 0x00, 0x00, 0x00, 0x00];
					Send($"stage {i} @0x{addr:X4} (D2)",
						Build(DpiWriteCmd, (byte)(addr & 0xFF), (byte)(addr >> 8), DpiBytesPerStage, SeqContinue, payload));
				}

				// 3. ValidBankId at 0x007F carrying the commit — in the capture this frame IS the D3.
				Send("ValidBankId @0x007F (D3)",
					Build(DpiWriteCmd, ValidBankIdOffset & 0xFF, ValidBankIdOffset >> 8, 0x01, SeqCommit, [ValidBankId]));

				Console.WriteLine();
				Console.WriteLine("All frames sent without a USB error (that only means the DONGLE accepted");
				Console.WriteLine("them — it says nothing about whether the MOUSE applied them).");
				Console.WriteLine();
				Console.WriteLine($"CHECK BY HAND NOW: move the mouse. At {cpi} CPI on every stage the cursor");
				Console.WriteLine("should be dramatically slower, and pressing the DPI button should no longer");
				Console.WriteLine("change the speed. If nothing changed, the dongle did not apply the write.");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	// ── Vendor-exact BUTTON MAP write ─────────────────────────────────────────────────────────
	// Same frame shape as RunVendorExactDpiWrite (per-field frames, ValidBankId carrying the 0xD3)
	// but for the button table, command 0x03. The earlier button test (2026-07-31, wheel->Mute) used
	// our 24-byte chunking, so this is a genuinely new attempt: it is the only remaining difference
	// between what we send and what the vendor's tool sends.
	//
	// Expectation is low — the DPI table already failed over the dongle in exactly this frame shape,
	// and buttons ride the same transport — but it is cheap and it tests the binding path directly.
	//
	// STANDARD TEST BINDING: Button 2 (RIGHT CLICK) -> My Computer (0x8F 08 08 00 = Win+E). Chosen
	// because the result is unmistakable and needs no interpretation — right-click either opens File
	// Explorer (the write landed) or shows the usual context menu (it did not). Left click is never
	// touched, so a half-landed write cannot cost the ability to click.
	//
	// interFrameDelayMs exists because the dongle is documented in TelinkMouseService to "relay each
	// report over the air and silently drop back-to-back packets". The default 12 ms was tuned for
	// USB; if the radio needs longer, a fast burst would be dropped and look exactly like a refused
	// write. Worth trying at 60-100 ms before concluding anything.
	//
	// DESTRUCTIVE: the opening 0xD1 erases the button-map flash sector. Recovery if it half-lands is
	// the cable (wired writes work), or `revert: true` here.
	// Set_MouseState, from the vendor's own command table (fabric-info image_574065711_2):
	//   Byte3 = 0x46, Byte4 = 1 为可用 / 0 为禁用 ("1 = enabled, 0 = disabled"),
	//   Byte6 = 0 为所有灯正常 / 1 为关掉所有灯 ("0 = lights normal, 1 = all lights off").
	// Sent as a prelude on the theory that the driver channel has to be enabled before the receiver
	// will relay config frames over the air — the one documented command that looks like a
	// mode/enable switch. Only ever sent with 1 (enabled); 0 would disable the mouse.
	private const byte SetMouseStateCmd = 0x46;

	public static void RunVendorExactButtonWrite(
		bool revert = false, int interFrameDelayMs = PacketDelayMs, bool enableMouseStateFirst = false)
	{
		Console.WriteLine(revert
			? "TAIPAN dongle — VENDOR-EXACT button map write (REVERT to factory default)"
			: "TAIPAN dongle — VENDOR-EXACT button map write (RIGHT CLICK -> My PC)");
		Console.WriteLine($"Inter-frame delay: {interFrameDelayMs} ms");
		Console.WriteLine();

		if (CableIsAlsoConnected())
		{
			Console.WriteLine("ABORTING — the USB cable is connected; the 2.4G link is idle. Unplug it.");
			return;
		}

		// Identify every receiver present, so a result can be attributed to a specific unit. Two
		// receivers plugged in at once would make "the dongle" ambiguous, and the firmware revision
		// is the variable under investigation (ours 0x0215 vs the vendor's 0x6539), so both are
		// printed rather than assumed.
		var candidates = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt
				&& Len(d, output: true) == FrameLength && Len(d, output: false) == FrameLength)
			.ToList();

		if (candidates.Count == 0)
		{
			Console.WriteLine("No 33/33 driver collection found on the dongle.");
			return;
		}

		Console.WriteLine($"Receiver(s) with a driver collection: {candidates.Count}");
		foreach (var c in candidates)
		{
			Console.WriteLine($"  {Short(c)}");
			Console.WriteLine($"    firmware (bcdDevice) : 0x{ReleaseBcd(c):X4}   (vendor's working unit: 0x6539)");
			Console.WriteLine($"    productName          : {c.GetProductName()}");
		}
		if (candidates.Count > 1)
		{
			Console.WriteLine();
			Console.WriteLine("More than one receiver is connected — unplug all but the one under test,");
			Console.WriteLine("otherwise the result cannot be attributed to a specific unit.");
			return;
		}

		var writer = candidates[0];
		Console.WriteLine();
		if (!writer.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the driver collection. Is the DeviceHost running?");
			return;
		}

		using (stream)
		{
			stream.WriteTimeout = ReadTimeoutMs;

			var table = BuildDefaultButtonTable();
			// Slot 7 is the polling-rate cycle in the vendor's worked example, but THIS SKU has no
			// such physical button — the slot is valid in the table and wired to nothing. Writing a
			// control for hardware that does not exist is noise, so it is cleared here.
			SetButtonEntry(table, ButtonPollingRateCycleIndex, 0x00, 0x00, 0x00, 0x00);

			const int remapped = 2; // Button 2 = right click
			if (!revert)
				SetButtonEntry(table, remapped, 0x8F, 0x08, 0x08, 0x00); // Win+E = My Computer

			void Send(string what, byte[] frame)
			{
				Console.WriteLine($"  {what,-30} {Hex(frame.AsSpan(0, 14).ToArray())}");
				stream.Write(frame);
				Thread.Sleep(interFrameDelayMs);
			}

			try
			{
				// 0. Optional channel-open attempt: Set_MouseState = enabled.
				if (enableMouseStateFirst)
				{
					Send("Set_MouseState=1 (0x46)",
						Build(SetMouseStateCmd, 0x01, 0x00, 0x00, 0x00, ReadOnlySpan<byte>.Empty));
					Thread.Sleep(200);
				}

				// 1. Two-byte header at 0x0000 with 0xD1 — erases the sector.
				Send("header @0x0000 (D1)",
					Build(ButtonWriteCmd, 0x00, 0x00, ButtonHeaderLength, SeqFirst,
						table.AsSpan(0, ButtonHeaderLength)));

				// 2. One frame per 4-byte entry, each at its own address — the "sent split up" shape.
				for (var i = 1; i <= ButtonMaxButtons; i++)
				{
					var addr = ButtonEntryOffset(i);
					Send($"button {i,2} @0x{addr:X4} (D2)",
						Build(ButtonWriteCmd, (byte)(addr & 0xFF), (byte)(addr >> 8), ButtonEntryLength,
							SeqContinue, table.AsSpan(addr, ButtonEntryLength)));
				}

				// 3. ValidBankId carrying the commit.
				Send("ValidBankId @0x007F (D3)",
					Build(ButtonWriteCmd, ValidBankIdOffset & 0xFF, ValidBankIdOffset >> 8, 0x01,
						SeqCommit, [ValidBankId]));

				Console.WriteLine();
				Console.WriteLine("All frames sent without a USB error (that only means the DONGLE accepted");
				Console.WriteLine("them — it says nothing about whether the MOUSE applied them).");
				Console.WriteLine();
				Console.WriteLine(revert
					? "CHECK BY HAND: right-click should show the normal context menu again."
					: "CHECK BY HAND NOW: right-click anywhere.\n" +
					  "  - File Explorer / My PC opens  -> the write LANDED over the dongle.\n" +
					  "  - normal context menu          -> it did not.\n" +
					  "\n" +
					  "THEN, before concluding: switch the mouse OFF and ON again and right-click once\n" +
					  "more. A write can be staged and only applied when the radio link is next\n" +
					  "established, which would look identical to a refused write until you re-link.");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	// ── Feature-report handshake probe ────────────────────────────────────────────────────────
	// The dongle's driver collection carries an 8-byte FEATURE report (id 0x05) that the wired
	// driver collection does not have. It is the last structural difference between the transport
	// that works and the one that does not, and the only remaining candidate for a "channel open"
	// the receiver might require before relaying config frames.
	//
	// Its semantics are undocumented, so this is deliberately staged to keep the blind part small:
	//
	//   1. read the current value (zero risk),
	//   2. write back the SAME bytes — a no-op by construction, which tests only whether SetFeature
	//      is accepted at all, without changing any state,
	//   3. read again and confirm nothing moved,
	//   4. immediately attempt the button write, in case step 2 opened a session.
	//
    // No invented values are sent. If SetFeature is rejected outright, that answers the question
	// with no risk taken at all.
	public static void RunFeatureHandshakeProbe(int interFrameDelayMs = 80)
	{
		Console.WriteLine("TAIPAN dongle — FEATURE-report handshake probe");
		Console.WriteLine("Writes back only the bytes already present; no invented values.");
		Console.WriteLine();

		if (CableIsAlsoConnected())
		{
			Console.WriteLine("ABORTING — the USB cable is connected; the 2.4G link is idle. Unplug it.");
			return;
		}

		var dev = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt)
			.FirstOrDefault(d => Len(d, output: true) == FrameLength && FeatureLen(d) > 0);
		if (dev == null)
		{
			Console.WriteLine("No dongle driver collection with a feature report found.");
			return;
		}
		if (!dev.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the driver collection.");
			return;
		}

		using (stream)
		{
			var len = FeatureLen(dev);

			byte[] Read()
			{
				var b = new byte[len];
				b[0] = ReportId;
				stream.GetFeature(b);
				return b;
			}

			byte[] baseline;
			try
			{
				baseline = Read();
				Console.WriteLine($"  1. baseline feature       : {Hex(baseline)}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  1. GetFeature FAILED: {ex.GetType().Name}: {ex.Message}");
				return;
			}

			try
			{
				stream.SetFeature((byte[])baseline.Clone());
				Console.WriteLine("  2. SetFeature (same bytes): ACCEPTED — feature writes are supported");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  2. SetFeature REJECTED    : {ex.GetType().Name}: {ex.Message}");
				Console.WriteLine("     -> the receiver does not take feature writes; this lever is closed.");
				return;
			}

			Thread.Sleep(200);
			try
			{
				var after = Read();
				Console.WriteLine($"  3. feature after write    : {Hex(after)}"
					+ (after.SequenceEqual(baseline) ? "  (unchanged, as intended)" : "  ** CHANGED **"));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  3. re-read FAILED: {ex.GetType().Name}: {ex.Message}");
			}

			// 4. With the feature exchange done on this same open handle, retry the config write.
			Console.WriteLine();
			Console.WriteLine("  4. retrying the button write on this same handle...");
			Console.WriteLine();

			var table = BuildDefaultButtonTable();
			SetButtonEntry(table, ButtonPollingRateCycleIndex, 0x00, 0x00, 0x00, 0x00);
			SetButtonEntry(table, 2, 0x8F, 0x08, 0x08, 0x00); // right click -> My PC

			void Send(string what, byte[] frame)
			{
				Console.WriteLine($"     {what,-28} {Hex(frame.AsSpan(0, 12).ToArray())}");
				stream.Write(frame);
				Thread.Sleep(interFrameDelayMs);
			}

			try
			{
				Send("header @0x0000 (D1)",
					Build(ButtonWriteCmd, 0x00, 0x00, ButtonHeaderLength, SeqFirst, table.AsSpan(0, ButtonHeaderLength)));
				for (var i = 1; i <= ButtonMaxButtons; i++)
				{
					var addr = ButtonEntryOffset(i);
					Send($"button {i,2} @0x{addr:X4} (D2)",
						Build(ButtonWriteCmd, (byte)(addr & 0xFF), (byte)(addr >> 8), ButtonEntryLength,
							SeqContinue, table.AsSpan(addr, ButtonEntryLength)));
				}
				Send("ValidBankId @0x007F (D3)",
					Build(ButtonWriteCmd, ValidBankIdOffset & 0xFF, ValidBankIdOffset >> 8, 0x01, SeqCommit, [ValidBankId]));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"     write failed: {ex.GetType().Name}: {ex.Message}");
				return;
			}

			Console.WriteLine();
			Console.WriteLine("CHECK BY HAND: right-click. My PC opens -> the feature exchange unlocked it.");
			Console.WriteLine("Normal context menu -> it did not, and this lever is exhausted too.");
		}
	}

	// ── Link-state probe ──────────────────────────────────────────────────────────────────────
	// Two reported problems, same root cause: over the dongle the host has no liveness signal from
	// the MOUSE, only the receiver's USB presence.
	//
	//   1. Battery appears once and never again. It arrives only in the mouse's unsolicited 0x48
	//      push, which fires on link establishment — so if nothing re-links while the app is
	//      running, there is nothing to show.
	//   2. The device reads as "connected" with the mouse switched fully OFF, because the receiver
	//      stays enumerated on USB regardless of whether any mouse is talking to it.
	//
	// This watches BOTH channels at once while the user toggles the mouse's power switch:
	//   - the HID device list (does anything appear/disappear when the mouse goes off?)
	//   - the driver collection (does a 0x48/0x49 push arrive when it comes back on?)
	//
	// Read-only.
	public static void RunLinkStateProbe(int seconds = 45)
	{
		Console.WriteLine("TAIPAN — link-state probe (read-only)");
		Console.WriteLine();
		Console.WriteLine("  While this runs, please:");
		Console.WriteLine("    1. switch the mouse OFF   (wait ~5 s)");
		Console.WriteLine("    2. switch it back ON      (wait ~5 s)");
		Console.WriteLine("    3. move it a little");
		Console.WriteLine();

		var driver = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt)
			.FirstOrDefault(d => Len(d, output: true) == FrameLength && Len(d, output: false) == FrameLength);

		HidStream? pushStream = null;
		if (driver != null && driver.TryOpen(out var s))
		{
			s.ReadTimeout = 200;
			pushStream = s;
		}
		else
		{
			Console.WriteLine("  (could not open the driver collection — device-list changes only)");
		}

		var stop = false;
		Thread? reader = null;
		if (pushStream != null)
		{
			reader = new Thread(() =>
			{
				var buf = new byte[FrameLength];
				while (!Volatile.Read(ref stop))
				{
					int n;
					try { n = pushStream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { return; }
					if (n <= 0) continue;

					var span = buf.AsSpan(0, n);
					var kind = n > 3 && span[3] == BatteryReplyCmd ? "0x48 BATTERY"
						: n > 3 && span[3] == 0x49 ? "0x49 STATE"
						: "push";
					Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] {kind}: {Hex(span.ToArray())}");
				}
			})
			{ IsBackground = true };
			reader.Start();
		}

		string Snapshot() => string.Join("\n",
			DeviceList.Local.GetHidDevices(Vid)
				.Select(d => $"{d.ProductID:X4} {Short(d)} in={Len(d, false)} out={Len(d, true)}")
				.OrderBy(x => x));

		var previous = Snapshot();
		Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] baseline: {previous.Split('\n').Length} interface(s)");

		var deadline = DateTime.UtcNow.AddSeconds(seconds);
		while (DateTime.UtcNow < deadline)
		{
			Thread.Sleep(700);
			var now = Snapshot();
			if (now == previous) continue;

			Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] DEVICE LIST CHANGED:");
			foreach (var gone in previous.Split('\n').Except(now.Split('\n')))
				Console.WriteLine($"      - removed: {gone}");
			foreach (var added in now.Split('\n').Except(previous.Split('\n')))
				Console.WriteLine($"      + added  : {added}");
			previous = now;
		}

		Volatile.Write(ref stop, true);
		reader?.Join(500);
		pushStream?.Dispose();

		Console.WriteLine();
		Console.WriteLine("  Done. What this tells us:");
		Console.WriteLine("   - device list changed on OFF  -> we have a real liveness signal to use.");
		Console.WriteLine("   - device list did NOT change  -> the receiver hides the mouse's state, and");
		Console.WriteLine("     'connected' cannot be derived from USB presence alone.");
		Console.WriteLine("   - a 0x48 arrived on switch-ON -> battery can be refreshed by re-linking.");
	}

	// ── Report-descriptor comparison ──────────────────────────────────────────────────────────
	// The vendor-exact write produced no effect over the dongle, so before escalating we check the
	// one thing that would make every frame a silent no-op regardless of content: whether the
	// dongle's driver collection actually declares OUTPUT report id 0x05 at 32 payload bytes. If it
	// declares a different id (or no output report at all), HidSharp will happily hand our buffer
	// to the driver and Windows will drop it, which looks exactly like "the mouse ignored it".
	//
	// Prints the wired collection alongside it when the cable is present, so the two can be diffed.
	// Read-only.
	public static void RunReportDescriptorDump()
	{
		Console.WriteLine("TAIPAN — HID report descriptor comparison (read-only)");
		Console.WriteLine();

		foreach (var d in DeviceList.Local.GetHidDevices(Vid).OrderBy(d => d.ProductID).ThenBy(d => d.DevicePath))
		{
			if (Len(d, output: true) == 0 && Len(d, output: false) < FrameLength)
				continue; // skip the tiny consumer/mouse collections — they cannot carry a 33-byte frame

			var kind = d.ProductID == PidWired ? "WIRED" : "DONGLE";
			var (page, usage) = FirstUsage(d);
			Console.WriteLine(new string('-', 78));
			Console.WriteLine($"[{kind}] {Short(d)}");
			// Printed in the vendor's own field order so their fabric-info line can be diffed
			// against ours directly, VersionNumber included.
			Console.WriteLine($"  VendorID:0x{d.VendorID:X4} ProductID:0x{d.ProductID:X4} " +
				$"VersionNumber:0x{ReleaseBcd(d):X4} Usage:0x{usage:X4} , UsagePage:0x{page:X4} " +
				$",FeatureReportByteLength:0x{FeatureLen(d):X4} ,InputReportByteLength:0x{Len(d, false):X2} " +
				$",OutputReportByteLength:0x{Len(d, true):X2}");

			try
			{
				var rd = d.GetReportDescriptor();
				foreach (var r in rd.Reports)
				{
					Console.WriteLine($"    report type={r.ReportType,-7} id=0x{r.ReportID:X2} length={r.Length}");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"    could not parse descriptor: {ex.GetType().Name}: {ex.Message}");
			}
		}

		Console.WriteLine(new string('-', 78));
		Console.WriteLine("Looking for: an Output report with id 0x05 on the DONGLE collection. If the only");
		Console.WriteLine("output report there has a different id, our 0x05 frames can never reach the mouse.");

		// The dongle's driver collection carries an 8-byte FEATURE report that the wired driver
		// collection does not. Reading it is harmless and may show dongle-side state (link status,
		// paired channel, firmware id). Writing it is NOT attempted — its semantics are undocumented
		// and a wrong value there could plausibly unpair the receiver.
		var dongleDriver = DeviceList.Local.GetHidDevices(Vid)
			.Where(d => d.ProductID == PidDongleOrBt)
			.FirstOrDefault(d => Len(d, output: true) == FrameLength && FeatureLen(d) > 0);
		if (dongleDriver == null) return;

		Console.WriteLine();
		Console.WriteLine($"Reading the {FeatureLen(dongleDriver)}-byte FEATURE report (id 0x{ReportId:X2}) on the dongle collection:");
		if (!dongleDriver.TryOpen(out var fs))
		{
			Console.WriteLine("  could not open the collection.");
			return;
		}
		using (fs)
		{
			try
			{
				var buf = new byte[FeatureLen(dongleDriver)];
				buf[0] = ReportId;
				fs.GetFeature(buf);
				Console.WriteLine($"  feature = {Hex(buf)}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  GetFeature failed: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	// HidSharp exposes bcdDevice as a Version with DECIMAL Major/Minor (it has already decoded the
	// BCD). The vendor quotes the raw word, so re-encode each decimal digit into its own nibble.
	//
	// Getting this wrong is not cosmetic: (Major << 8) | Minor renders the wired unit's real 0x1007
	// as "0x0A07" — because Major 10 becomes 0x0A instead of 0x10 — which reads as a firmware
	// mismatch against the vendor's dump when there is none.
	private static int ReleaseBcd(HidDevice d)
	{
		try
		{
			var v = d.ReleaseNumber;
			return ((v.Major / 10) << 12) | ((v.Major % 10) << 8) | ((v.Minor / 10) << 4) | (v.Minor % 10);
		}
		catch { return 0; }
	}

	private static string Short(HidDevice d)
	{
		var p = d.DevicePath ?? string.Empty;
		var i = p.IndexOf('#');
		var j = i >= 0 ? p.IndexOf('#', i + 1) : -1;
		return j > i ? p[(i + 1)..j] : p;
	}

	public static void Run()
	{
		Console.WriteLine("TAIPAN dongle probe — DPI table + battery over the 2.4G dongle");
		Console.WriteLine("Preconditions: ProoveGamingDeviceHost STOPPED (it holds the HID handle open),");
		Console.WriteLine("TAIPAN connected via the 2.4G DONGLE (not the cable).");
		Console.WriteLine();

		var all = DeviceList.Local.GetHidDevices(Vid).ToList();
		if (all.Count == 0)
		{
			Console.WriteLine($"No devices found on VID 0x{Vid:X4}. Is the TAIPAN dongle plugged in?");
			return;
		}

		var wired = all.Where(d => d.ProductID == PidWired).ToList();
		var dongleOrBt = all.Where(d => d.ProductID == PidDongleOrBt).ToList();

		Console.WriteLine($"Found {wired.Count} interface(s) on PID 0x{PidWired:X4} (wired) and " +
			$"{dongleOrBt.Count} interface(s) on PID 0x{PidDongleOrBt:X4} (2.4G dongle OR Bluetooth — same PID).");
		Console.WriteLine();

		foreach (var d in wired) ReportInterface(d, "WIRED");
		foreach (var d in dongleOrBt) ReportInterface(d, "DONGLE/BT (PID shared)");

		if (dongleOrBt.Count == 0)
		{
			Console.WriteLine();
			Console.WriteLine("No PID 0x8373 interface found — plug in the 2.4G dongle (not the cable) and retry.");
			return;
		}

		// Refuse to produce a result that cannot mean anything. Running this with the cable attached
		// is how "the dongle rejects everything" gets concluded from what is really just an idle
		// radio link with no mouse on the far end.
		if (CableIsAlsoConnected())
		{
			Console.WriteLine();
			Console.WriteLine(new string('!', 78));
			Console.WriteLine("ABORTING — the USB CABLE is connected as well (PID 0x{0:X4} is enumerated).", PidWired);
			Console.WriteLine();
			Console.WriteLine("While cabled, the mouse runs in WIRED mode: the 2.4G link is idle and nothing");
			Console.WriteLine("is listening on the far end of the dongle, so every read here would time out");
			Console.WriteLine("and every write would go nowhere — which is indistinguishable from the dongle");
			Console.WriteLine("genuinely refusing host->device frames, and is almost certainly how that");
			Console.WriteLine("conclusion was reached before.");
			Console.WriteLine();
			Console.WriteLine("UNPLUG THE CABLE, leave only the 2.4G dongle, confirm the mouse still moves");
			Console.WriteLine("the cursor, then run this again.");
			Console.WriteLine(new string('!', 78));
			return;
		}

		// Pick the vendor-confirmed driver-channel interface: 33-byte in/out (Q&A T8). Falls back
		// to the first candidate if none match exactly, so the probe still runs and reports what
		// it actually found instead of silently giving up.
		var target = dongleOrBt.FirstOrDefault(d => Len(d, output: true) == 33 && Len(d, output: false) == 33)
			?? dongleOrBt[0];

		Console.WriteLine();
		Console.WriteLine(new string('=', 78));
		Console.WriteLine($"Using interface : {target.DevicePath}");
		Console.WriteLine($"Connection type : {ClassifyTransport(target.DevicePath)}");
		Console.WriteLine(new string('=', 78));

		if (!target.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the interface. Is ProoveGamingDeviceHost running? Stop it and retry.");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = ReadTimeoutMs;
			stream.WriteTimeout = ReadTimeoutMs;

			var dpiTable = RunDpiRead(stream);
			RunMsConfigRead(stream);
			RunBatteryRead(stream);

			if (dpiTable != null)
			{
				Console.WriteLine();
				Console.Write("Run the DPI WRITE round-trip test over the dongle now? Changes stage 0 " +
					"temporarily and restores it afterwards. [y/N]: ");
				var answer = Console.ReadLine();
				if (string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
					RunDpiWriteTest(stream, dpiTable);
				else
					Console.WriteLine("Skipped write test.");
			}
			else
			{
				RunBlindDpiWriteTest(stream);
			}

			// Highest index among the stages this session actually wrote (see DefaultDpiStagesCpi /
			// BuildDefaultTable) — index 6 = 12000 DPI, the max. If the earlier table write never ran
			// or failed, this still targets stage 6, matching what the physical DPI button already
			// cycles through per the user's own report.
			RunSetActiveDpiStageTest(stream, maxStageIndex: DefaultDpiStagesCpi.Length - 1);

			RunButtonMapTest(stream);
		}
	}

	// ── Button map (0x03 write / 0x43 read) — for the T2 "can we set buttons over the dongle?" check ──

	/// <summary>
	/// Reads the button map (0x43), then — if the read succeeds — flips ONE benign byte (Button 7's
	/// BtModify, the polling-rate-cycle entry's parameter byte) and writes the WHOLE 102-byte table
	/// back (the 0xD1 first packet erases the sector, same as DPI/MsConfig), reads it back to confirm,
	/// then restores the original. Button 7 is chosen because it is not a click button — worst case
	/// during the brief write/verify window is the polling-rate-cycle button behaving oddly, not a
	/// dropped click. If the read fails, falls back to a blind write of the factory default map
	/// (same shape as RunBlindDpiWriteTest) since there is no real baseline to patch/restore.
	/// </summary>
	private static void RunButtonMapTest(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("BUTTON MAP READ (0x43) — for the T2 'can we set buttons over the dongle?' check");
		Console.WriteLine(new string('-', 78));

		var table = ReadTable(stream, ButtonReadCmd, 0, ButtonTableLength);
		if (table == null)
		{
			Console.WriteLine("  READ FAILED — no reply came back.");
			RunBlindButtonMapWriteTest(stream);
			return;
		}

		Console.WriteLine("  READ OK.");
		Console.WriteLine($"  raw[{ButtonTableLength}]: {Hex(table)}");
		Console.WriteLine($"  max button-event count: {(table[0] | (table[1] << 8))}");
		for (var i = 1; i <= ButtonMaxButtons; i++)
		{
			var o = ButtonEntryOffset(i);
			if (table[o] == 0x00) continue; // empty slot
			Console.WriteLine($"    button {i,2}: {table[o]:X2} {table[o + 1]:X2} {table[o + 2]:X2} {table[o + 3]:X2}");
		}

		Console.WriteLine();
		Console.Write("  Run the BUTTON MAP WRITE round-trip test over the dongle now? Temporarily " +
			"changes Button 7 (polling-rate cycle)'s parameter byte and restores it afterwards. [y/N]: ");
		var answer = Console.ReadLine();
		if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("  Skipped write test.");
			return;
		}

		var modified = (byte[])table.Clone();
		var o7 = ButtonEntryOffset(ButtonPollingRateCycleIndex);
		var before = modified[o7 + 1];
		modified[o7 + 1] = (byte)(before == 0x00 ? 0x01 : 0x00); // BtModify: 0x01 = cycle 1/2/4/8, 0x00 = fixed

		Console.WriteLine($"  button 7 BtModify before: 0x{before:X2}");
		Console.WriteLine($"  writing                 : 0x{modified[o7 + 1]:X2} (whole {ButtonTableLength}-byte table re-sent)");

		var wrote = WriteTable(stream, ButtonWriteCmd, modified, ValidBankIdOffset);
		Console.WriteLine($"  write completed without exception: {wrote}");

		Thread.Sleep(150);
		var after = ReadTable(stream, ButtonReadCmd, 0, ButtonTableLength);
		if (after == null)
		{
			Console.WriteLine("  READ-BACK FAILED after the write — could not confirm whether the write landed.");
		}
		else
		{
			bool stuck = after[o7 + 1] == modified[o7 + 1];
			Console.WriteLine($"  read back      : button 7 BtModify = 0x{after[o7 + 1]:X2}");
			Console.WriteLine(stuck
				? "  >>> WRITE STUCK — the dongle DOES accept button-map writes."
				: "  >>> did NOT stick — write was accepted on the wire (no exception) but had no effect.");
		}

		Console.WriteLine();
		Console.WriteLine("  restoring the original table ...");
		var restoredOk = WriteTable(stream, ButtonWriteCmd, table, ValidBankIdOffset);
		Thread.Sleep(150);
		var verify = ReadTable(stream, ButtonReadCmd, 0, ButtonTableLength);
		bool restored = verify != null && verify[o7 + 1] == table[o7 + 1];
		Console.WriteLine(restored
			? "  restore confirmed OK."
			: "  RESTORE DID NOT CONFIRM — re-run this probe's read step before trusting the button map.");
	}

	// Mirrors TaipanButtonMap.CreateDefault() exactly, for the case where the read path is dead and
	// there is no real baseline to patch/restore.
	private static byte[] BuildDefaultButtonTable()
	{
		var t = new byte[ButtonTableLength];
		t[0] = 0x0A; // max button-event count, low byte (vendor worked example)
		t[1] = 0x00;
		SetButtonEntry(t, 1, 0x81, 0x00, 0x00, 0x00);  // Left
		SetButtonEntry(t, 2, 0x82, 0x00, 0x00, 0x00);  // Right
		SetButtonEntry(t, 3, 0x83, 0x00, 0x00, 0x00);  // Middle
		SetButtonEntry(t, 4, 0x85, 0x00, 0x00, 0x00);  // B5 / Forward
		SetButtonEntry(t, 5, 0x84, 0x00, 0x00, 0x00);  // B4 / Back
		SetButtonEntry(t, 6, 0x88, 0x00, 0x00, 0x00);  // DPI cycle
		SetButtonEntry(t, 7, 0x97, 0x01, 0x00, 0x00);  // Polling-rate cycle
		SetButtonEntry(t, 23, 0x8B, 0x00, 0x00, 0x00); // Wheel up
		SetButtonEntry(t, 24, 0x8C, 0x00, 0x00, 0x00); // Wheel down
		return t;
	}

	private static void SetButtonEntry(byte[] table, int index, byte fn, byte modify, byte a1, byte a2)
	{
		var o = ButtonEntryOffset(index);
		table[o] = fn;
		table[o + 1] = modify;
		table[o + 2] = a1;
		table[o + 3] = a2;
	}

	private static void RunBlindButtonMapWriteTest(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("BLIND BUTTON MAP WRITE — no prior read, cannot verify or restore automatically");
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  The button map read failed above. This writes the FACTORY DEFAULT map instead —");
		Console.WriteLine("  if you had custom button bindings configured, THEY WILL BE REPLACED and there is");
		Console.WriteLine("  no way for this tool to read them back or undo it. Verify afterwards by hand:");
		Console.WriteLine("  press each button and confirm it does what the factory default table says.");
		Console.WriteLine();
		Console.Write("  Type 'yes' to proceed, anything else to skip: ");
		var answer = Console.ReadLine();
		if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("  Skipped.");
			return;
		}

		var table = BuildDefaultButtonTable();
		Console.WriteLine($"  writing default table[{ButtonTableLength}]: {Hex(table)}");
		var wrote = WriteTable(stream, ButtonWriteCmd, table, ValidBankIdOffset);
		Console.WriteLine($"  write completed without exception: {wrote}");
		Console.WriteLine();
		Console.WriteLine("  Cannot confirm via read-back. Please check the mouse by hand now.");
	}

	// ── connection-type classification ──────────────────────────────────────────────────────

	// Windows HID device paths never literally contain the word "usb" — a physical USB device
	// (which is exactly what the 2.4G dongle is, from Windows' point of view: a USB receiver) shows
	// up as \\?\hid#vid_xxxx&pid_xxxx&mi_xx[&col0x]#...#{GUID}. A mouse paired DIRECTLY over
	// Bluetooth (no physical dongle) instead enumerates under a Bluetooth bus, and that DOES show up
	// in the path (bthenum# / a bthle{...} GUID segment). So: match the Bluetooth markers first: if
	// none hit and the path has the vid_/pid_ HID pattern, it's a physical USB device — for PID
	// 0x8373 specifically, that means the 2.4G dongle (Bluetooth PID 0x8373 without a dongle plugged
	// in would show a bthenum path instead, not this one).
	// The dongle and Bluetooth share PID 0x8373 and can only be told apart by the bus in the device
	// path; the cable is a different PID entirely. Classify by PID FIRST — the old version tested
	// only "does the path contain vid_/pid_", which is true of every USB HID device, so it labelled
	// the CABLE's own interfaces "2.4G DONGLE". That is actively misleading in this investigation,
	// where the whole question is which transport answered.
	private static string ClassifyTransport(string? path)
	{
		if (string.IsNullOrEmpty(path)) return "unknown (no device path)";
		var p = path.ToLowerInvariant();

		if (p.Contains("bthenum") || p.Contains("bthle") || p.Contains("bluetoothle"))
			return "BLUETOOTH (path enumerates under a Bluetooth bus) — vendor T2 says BT is NOT supported for config";
		if (p.Contains($"pid_{PidWired:x4}"))
			return "WIRED (cable; the mouse itself enumerates under this PID)";
		if (p.Contains($"pid_{PidDongleOrBt:x4}"))
			return "2.4G DONGLE (USB receiver; shares its PID with Bluetooth, distinguished by bus above)";
		return "UNKNOWN bus (path matched no known pattern — report this path)";
	}

	// The mouse only enumerates under the wired PID while the cable is actually plugged in, and in
	// that state it runs in wired mode — the 2.4G radio link is idle and nothing is listening on the
	// other end of the dongle. Every read then times out with "no reply", which looks exactly like
	// "the dongle rejects host->device frames" but proves nothing. Detect and say so up front.
	private static bool CableIsAlsoConnected() =>
		DeviceList.Local.GetHidDevices(Vid).Any(d => d.ProductID == PidWired);

	private static void ReportInterface(HidDevice d, string label)
	{
		var (page, id) = FirstUsage(d);
		Console.WriteLine($"  [{label}] path={d.DevicePath}");
		Console.WriteLine($"      usage=0x{id:X4} usagePage=0x{page:X4} in={Len(d, false)} out={Len(d, true)} feature={FeatureLen(d)}");
		Console.WriteLine($"      transport: {ClassifyTransport(d.DevicePath)}");
	}

	private static (int page, int id) FirstUsage(HidDevice d)
	{
		try
		{
			var usages = d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).ToList();
			if (usages.Count == 0) return (0, 0);
			var u = usages[0];
			return ((int)(u >> 16), (int)(u & 0xFFFF));
		}
		catch { return (0, 0); }
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return -1; }
	}

	private static int FeatureLen(HidDevice d)
	{
		try { return d.GetMaxFeatureReportLength(); }
		catch { return -1; }
	}

	// ── DPI table (0x02 write / 0x42 read) ────────────────────────────────────────────────────

	private static byte[]? RunDpiRead(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("DPI TABLE READ (0x42)");
		Console.WriteLine(new string('-', 78));

		var table = ReadTable(stream, DpiReadCmd, 0, DpiTableLength);
		if (table == null)
		{
			Console.WriteLine("  READ FAILED — no reply came back. This is exactly what " +
				"TelinkMouseService's own comment predicts for the dongle ('reads return null'); " +
				"if this keeps failing, that comment is confirmed and T2 needs a direct follow-up " +
				"with the vendor.");
			return null;
		}

		Console.WriteLine("  READ OK.");
		Console.WriteLine($"  raw[{DpiTableLength}]: {Hex(table)}");
		Console.WriteLine($"  stage count byte (offset 0): {table[0]}");
		Console.WriteLine();
		Console.WriteLine("  Decoded BOTH candidate T7 layouts side by side — compare the DPI values");
		Console.WriteLine("  against what the mouse is actually set to right now on its physical stages:");
		Console.WriteLine();
		Console.WriteLine("  stage | enable | App's current assumption (16-bit reg, X=Y) | Vendor T7 (8-bit value + doubled flag)");

		for (var i = 0; i < DpiMaxStages; i++)
		{
			var o = 1 + i * DpiBytesPerStage;
			byte enable = table[o], b1 = table[o + 1], b2 = table[o + 2], b3 = table[o + 3], b4 = table[o + 4];

			// Current App interpretation: [enable, X_lo, X_hi, Y_lo, Y_hi], one 16-bit register (x50 CPI units).
			int register = b1 | (b2 << 8);
			int cpiMirrored = register * 50;

			// Vendor T7 interpretation: [enable, X_value, X_doubled, Y_value, Y_doubled], independent axes.
			int xCpi = b1 * (b2 != 0 ? 2 : 1);
			int yCpi = b3 * (b4 != 0 ? 2 : 1);

			Console.WriteLine($"    {i}   |  0x{enable:X2}  | reg=0x{register:X4} -> {cpiMirrored,6} DPI (mirrored X=Y)" +
				$"           | X: {b1,3}x{(b2 != 0 ? 2 : 1)}={xCpi,4}  Y: {b3,3}x{(b4 != 0 ? 2 : 1)}={yCpi,4}  (raw {b1:X2} {b2:X2} {b3:X2} {b4:X2})");
		}

		Console.WriteLine();
		Console.WriteLine("  Whichever column's numbers actually match the mouse's real DPI settings");
		Console.WriteLine("  (check via the physical DPI indicator LED / vendor software) is the correct");
		Console.WriteLine("  layout — update TaipanDpiTable.cs and docs/mouse/taipan/protocol.md accordingly.");

		return table;
	}

	/// <summary>
	/// Changes stage 0's low DPI byte by one register step, writes the WHOLE table back (the
	/// 0xD1 first packet erases the sector, so a partial write would zero every other stage),
	/// reads it back to confirm the write actually landed, then restores the original table.
	/// Mirrors SmartInfiniteProbe.WriteTest's always-restore pattern.
	/// </summary>
	private static void RunDpiWriteTest(HidStream stream, byte[] originalTable)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("DPI TABLE WRITE ROUND-TRIP TEST (0x02) — over the dongle");
		Console.WriteLine(new string('-', 78));

		var table = (byte[])originalTable.Clone();
		var before = (table[1], table[2]);
		// Bump the low byte of stage 0's first value field by 1 (wrapping), leaving every other
		// byte — including the stage-count header and every other stage — untouched.
		table[1] = (byte)(table[1] + 1);

		Console.WriteLine($"  stage 0 before: byte1=0x{before.Item1:X2} byte2=0x{before.Item2:X2}");
		Console.WriteLine($"  writing        : byte1=0x{table[1]:X2} byte2=0x{table[2]:X2} (whole {DpiTableLength}-byte table re-sent)");

		var wrote = WriteTable(stream, DpiWriteCmd, table, ValidBankIdOffset);
		Console.WriteLine($"  write completed without exception: {wrote}");

		Thread.Sleep(150);
		var after = ReadTable(stream, DpiReadCmd, 0, DpiTableLength);
		if (after == null)
		{
			Console.WriteLine("  READ-BACK FAILED after the write — could not confirm whether the write landed.");
		}
		else
		{
			bool stuck = after[1] == table[1] && after[2] == table[2];
			Console.WriteLine($"  read back      : byte1=0x{after[1]:X2} byte2=0x{after[2]:X2}");
			Console.WriteLine(stuck
				? "  >>> WRITE STUCK — the dongle DOES accept writes. TelinkMouseService's " +
				  "'writes no-op over the dongle' comment is OUTDATED and should be corrected."
				: "  >>> did NOT stick — write was accepted on the wire (no exception) but had no " +
				  "effect on the device. Matches TelinkMouseService's existing assumption.");
		}

		Console.WriteLine();
		Console.WriteLine("  restoring the original table ...");
		var restoredOk = WriteTable(stream, DpiWriteCmd, originalTable, ValidBankIdOffset);
		Thread.Sleep(150);
		var verify = ReadTable(stream, DpiReadCmd, 0, DpiTableLength);
		bool restored = verify != null && verify[1] == originalTable[1] && verify[2] == originalTable[2];
		Console.WriteLine(restored
			? "  restore confirmed OK."
			: "  RESTORE DID NOT CONFIRM — re-run this probe's read step, or power-cycle the mouse, " +
			  "and check stage 0's DPI by hand before trusting it.");
	}

	// App's own catalog default DPI stages for TAIPAN (appsettings.json DevicesMaps → PG TAIPAN →
	// DpiStages). Used as the blind-write payload below because it's a known-good, coherent set —
	// not a guess patched onto unknown existing data — so if the write does nothing, the mouse is
	// left exactly as it was, and if it works, the mouse ends up in a normal, usable state that the
	// App's own "reset to a preset" flow can build on afterwards.
	private static readonly int[] DefaultDpiStagesCpi = [800, 1200, 2400, 3600, 4800, 6000, 12000];

	/// <summary>
	/// Blind DPI-table write with NO prior read: used only when the read path is already confirmed
	/// dead (RunDpiRead returned null), so there is no real baseline to patch-and-restore. Writes
	/// the App's own default 7-stage table (mirrors TaipanDpiTable.BuildTable's current 16-bit
	/// register / mirrored-X=Y encoding) and — because reads don't work here — asks the user to
	/// verify by hand (DPI button / cursor feel) instead of reading the device back. Requires typing
	/// the full word "yes", not just "y", given there is no automated undo if this goes wrong.
	/// </summary>
	private static void RunBlindDpiWriteTest(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("BLIND DPI TABLE WRITE — no prior read, cannot verify or restore automatically");
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  The DPI table read failed above, so there is no real baseline to patch and");
		Console.WriteLine("  restore. This writes a KNOWN-GOOD table instead — the App's own default 7");
		Console.WriteLine("  stages (800/1200/2400/3600/4800/6000/12000 DPI, 8th stage disabled) — using");
		Console.WriteLine("  the encoding TaipanDpiTable.cs currently uses (16-bit register, X mirrors Y).");
		Console.WriteLine();
		Console.WriteLine("  If you had custom DPI values configured, THEY WILL BE REPLACED by these");
		Console.WriteLine("  defaults and there is no way for this tool to read them back or undo it —");
		Console.WriteLine("  because the read path over this dongle is what we're testing and it failed.");
		Console.WriteLine("  Verify afterwards by hand: cycle the physical DPI button and check the");
		Console.WriteLine("  indicator LED / cursor speed across all 7 stages.");
		Console.WriteLine();
		Console.Write("  Type 'yes' to proceed, anything else to skip: ");
		var answer = Console.ReadLine();
		if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("  Skipped.");
			return;
		}

		var table = BuildDefaultDpiTable();
		Console.WriteLine($"  writing default table[{DpiTableLength}]: {Hex(table)}");
		var wrote = WriteTable(stream, DpiWriteCmd, table, ValidBankIdOffset);
		Console.WriteLine($"  write completed without exception: {wrote}");
		Console.WriteLine();
		Console.WriteLine("  Cannot confirm via read-back (that path is what failed above). Please check");
		Console.WriteLine("  the mouse by hand now: cycle the DPI button through all 7 stages and see");
		Console.WriteLine("  whether the sensitivity/LED steps changed from before this write.");
	}

	// Mirrors ProoveGaming.Infrastructure.Classes.TaipanDpiTable.BuildTable exactly (same [enable,
	// X_lo, X_hi, Y_lo, Y_hi] layout, CPI/50 register, X mirrored to Y) for DefaultDpiStagesCpi.
	private static byte[] BuildDefaultDpiTable()
	{
		var table = new byte[DpiTableLength];
		table[0] = (byte)DefaultDpiStagesCpi.Length;

		for (var i = 0; i < DpiMaxStages; i++)
		{
			var offset = 1 + i * DpiBytesPerStage;
			if (i >= DefaultDpiStagesCpi.Length)
			{
				table[offset] = 0x00; // disabled
				continue;
			}

			var register = (ushort)Math.Round(DefaultDpiStagesCpi[i] / 50.0, MidpointRounding.AwayFromZero);
			var lo = (byte)(register & 0xFF);
			var hi = (byte)(register >> 8);

			table[offset] = 0x01;     // enabled
			table[offset + 1] = lo;   // X
			table[offset + 2] = hi;
			table[offset + 3] = lo;   // Y mirrors X
			table[offset + 4] = hi;
		}

		return table;
	}

	// ── MsConfig (0x01 write / 0x41 read) — bonus context for T3 (DpiSwitch lag) ──────────────

	private static void RunMsConfigRead(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("MsConfig READ (0x41) — for DpiSwitch / polling-rate context (Q&A T3)");
		Console.WriteLine(new string('-', 78));

		var block = ReadTable(stream, ConfigReadCmd, 0, MsConfigLength);
		if (block == null)
		{
			Console.WriteLine("  READ FAILED — no reply.");
			return;
		}

		Console.WriteLine("  READ OK.");
		Console.WriteLine($"  raw[{MsConfigLength}]: {Hex(block)}");
		Console.WriteLine($"  DpiSwitch (0x{DpiSwitchOffset:X2})       : {block[DpiSwitchOffset]}  " +
			"(stored stage — per T3 this can lag the physical button by one press; cross-check against " +
			"the DPI indicator LED, not just this byte)");
		Console.WriteLine($"  PollingRate profile-1 (0x{PollingRateOffset:X2}): 0x{block[PollingRateOffset]:X2}");
	}

	// The vendor's own worked full MsConfig write example, from TaipanMsConfig.cs's header comment:
	// "01 01 01 01 00 00 00 01 03 01 FF 01 02 04 08 01 00 00 00". Used ONLY as the base for a blind
	// write below (no real read available) — it's a vendor-confirmed-valid block, not a guess, so
	// patching just DpiSwitch (offset 0x11) onto it is far safer than inventing values byte-by-byte.
	private static readonly byte[] VendorExampleMsConfig =
		[0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x03, 0x01, 0xFF, 0x01, 0x02, 0x04, 0x08, 0x01, 0x00, 0x00, 0x00];

	/// <summary>
	/// Sets the active DPI stage (MsConfig.DpiSwitch, offset 0x11) to <paramref name="maxStageIndex"/>
	/// over the dongle. Retries the MsConfig read a few times first — now that the DPI table write is
	/// confirmed to land (per the user's own physical-button check), a fresh read might succeed too,
	/// in which case this does a REAL read-modify-write (safe: everything else in the block is left
	/// exactly as read). Only falls back to a blind write, patched onto the vendor's own worked
	/// example block, if every read attempt still comes back empty.
	/// </summary>
	private static void RunSetActiveDpiStageTest(HidStream stream, int maxStageIndex)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine($"SET ACTIVE DPI STAGE = {maxStageIndex} (max) over the dongle (Q&A T2/T3)");
		Console.WriteLine(new string('-', 78));

		byte[]? block = null;
		const int retries = 5;
		for (var attempt = 0; attempt < retries && block == null; attempt++)
		{
			if (attempt > 0) Thread.Sleep(200);
			var read = ReadTable(stream, ConfigReadCmd, 0, MsConfigLength);
			if (read != null && IsPlausibleMsConfig(read))
				block = read;
			else
				Console.WriteLine($"  MsConfig read attempt {attempt + 1}/{retries}: " +
					(read == null ? "no reply" : "looked like an erased/implausible sector"));
		}

		if (block != null)
		{
			Console.WriteLine("  Got a real MsConfig read — doing a safe read-modify-write (only DpiSwitch changes).");
			Console.WriteLine($"  before: raw[{MsConfigLength}]={Hex(block)}  DpiSwitch=0x{block[DpiSwitchOffset]:X2}");
			block[DpiSwitchOffset] = (byte)maxStageIndex;
			var wrote = WriteTable(stream, ConfigWriteCmd, block, bankIdOffset: null);
			Console.WriteLine($"  write completed without exception: {wrote}");
			Console.WriteLine("  Check the mouse now: DPI indicator LED / cursor speed should show the max (12000 DPI) stage.");
			return;
		}

		Console.WriteLine();
		Console.WriteLine("  Every MsConfig read attempt failed — same as before. No real baseline to patch.");
		Console.WriteLine("  BLIND MsConfig write available, using the VENDOR'S OWN worked example block");
		Console.WriteLine("  (from TaipanMsConfig.cs) with only DpiSwitch changed to the max stage.");
		Console.WriteLine();
		Console.WriteLine("  This is HIGHER RISK than the DPI-table blind write: MsConfig also holds wheel");
		Console.WriteLine("  speed, lift-off distance, profile index, DPI-LED behaviour, polling rate (all 5");
		Console.WriteLine("  profiles) and the profile-switch bitmask — ALL of those will be overwritten with");
		Console.WriteLine("  the vendor's example values, not your current settings, because we cannot read");
		Console.WriteLine("  your real config over this dongle to preserve it.");
		Console.WriteLine();
		Console.Write("  Type 'yes' to proceed, anything else to skip: ");
		var answer = Console.ReadLine();
		if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine("  Skipped.");
			return;
		}

		var blind = (byte[])VendorExampleMsConfig.Clone();
		blind[DpiSwitchOffset] = (byte)maxStageIndex;
		Console.WriteLine($"  writing blind block[{MsConfigLength}]: {Hex(blind)}");
		var blindWrote = WriteTable(stream, ConfigWriteCmd, blind, bankIdOffset: null);
		Console.WriteLine($"  write completed without exception: {blindWrote}");
		Console.WriteLine();
		Console.WriteLine("  Cannot confirm via read-back. Check the mouse by hand: DPI indicator / cursor");
		Console.WriteLine("  speed should now show the max stage. Polling rate and lift-off may also have");
		Console.WriteLine("  changed to the vendor example's values — re-apply your preferences from the App");
		Console.WriteLine("  afterward regardless of what this test shows.");
	}

	// Mirrors TaipanMsConfig.IsPlausible: an erased/half-committed sector reads back as all-0x00 or
	// all-0xFF, and treating that as real config would blind-patch a value into garbage.
	private static bool IsPlausibleMsConfig(byte[] block)
	{
		if (block.Length < MsConfigLength) return false;
		var allZero = true;
		var allOnes = true;
		for (var i = 0; i < MsConfigLength; i++)
		{
			if (block[i] != 0x00) allZero = false;
			if (block[i] != 0xFF) allOnes = false;
			if (!allZero && !allOnes) return true;
		}
		return false;
	}

	// ── Battery (0x08 -> reply 0x48) ────────────────────────────────────────────────────────

	private static void RunBatteryRead(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("BATTERY READ (0x08 -> 0x48) — for the T6 percentage-formula check");
		Console.WriteLine(new string('-', 78));

		WriteFrame(stream, Build(BatteryCmd, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));
		var reply = ReadReply(stream, BatteryReplyCmd);
		if (reply == null)
		{
			Console.WriteLine("  READ FAILED — no 0x48 reply came back.");
			return;
		}

		Console.WriteLine("  READ OK.");
		Console.WriteLine($"  raw[{FrameLength}]: {Hex(reply)}");
		Console.WriteLine("  non-zero bytes: " + string.Join(", ", Enumerable.Range(0, reply.Length)
			.Where(i => reply[i] != 0)
			.Select(i => $"[{i:D2}]=0x{reply[i]:X2}={reply[i]}")));

		Console.WriteLine();
		int para1 = reply[4], para2 = reply[5], para3 = reply[6], para4 = reply[7];
		int mv = para1 | (para2 << 8);
		Console.WriteLine($"  documented fields — Para1/Para2 as mV (protocol.md): {mv} mV  " +
			$"(Para1={para1:X2} Para2={para2:X2})");
		Console.WriteLine($"  Para3 (USB-connected flag) : 0x{para3:X2}");
		Console.WriteLine($"  Para4 (app-connected flag) : 0x{para4:X2}");
		if (reply.Length > PayloadOffset + 2)
			Console.WriteLine($"  FIFO[0..1] (product ID)    : 0x{reply[PayloadOffset]:X2}{reply[PayloadOffset + 1]:X2}");

		Console.WriteLine();
		Console.WriteLine("  Vendor T6 formula uses example values in the 750-985 range — NOT this mV field.");
		Console.WriteLine("  Bytes above whose VALUE falls in roughly that range (candidates for the field the");
		Console.WriteLine("  formula actually applies to):");
		var candidates = Enumerable.Range(0, reply.Length)
			.Where(i => reply[i] >= 100) // byte can't reach 750 alone; check 16-bit LE pairs too
			.ToList();
		for (var i = 0; i < reply.Length - 1; i++)
		{
			int pair = reply[i] | (reply[i + 1] << 8);
			if (pair is >= 700 and <= 1050)
				Console.WriteLine($"    bytes [{i:D2}..{i + 1:D2}] as 16-bit LE = {pair}  (0x{reply[i]:X2} 0x{reply[i + 1]:X2})");
		}
		Console.WriteLine("  If nothing printed above, none of this reply's byte pairs land near the vendor's");
		Console.WriteLine("  750-985 example range — the formula likely applies to a field this command doesn't");
		Console.WriteLine("  return at all, which is itself worth reporting back per the doc's open follow-up #2.");
	}

	// ── wire helpers ─────────────────────────────────────────────────────────────────────────

	private static byte[]? ReadTable(HidStream stream, byte readCmd, int address, int length)
	{
		var result = new byte[length];
		var offset = 0;

		while (offset < length)
		{
			var chunk = Math.Min(MaxPayload, length - offset);
			var addr = address + offset;
			WriteFrame(stream, Build(readCmd, (byte)(addr & 0xFF), (byte)(addr >> 8), (byte)chunk, 0x00, ReadOnlySpan<byte>.Empty));

			var reply = ReadReply(stream, readCmd);
			if (reply == null) return null;

			reply.AsSpan(PayloadOffset, chunk).CopyTo(result.AsSpan(offset, chunk));
			offset += chunk;
		}

		return result;
	}

	// `bankIdOffset` is null for tables that take NO ValidBankID packet — MsConfig is the one such
	// table (see TelinkMouseService.WriteTableAsync's comment: sending a BankID packet for MsConfig
	// writes 0x1A far outside its 19-byte block and the firmware rejects the whole transaction).
	private static bool WriteTable(HidStream stream, byte cmd, byte[] table, int? bankIdOffset)
	{
		try
		{
			for (var offset = 0; offset < table.Length; offset += MaxPayload)
			{
				var len = Math.Min(MaxPayload, table.Length - offset);
				var seq = offset == 0 ? SeqFirst : SeqContinue;
				WriteFrame(stream, Build(cmd, (byte)(offset & 0xFF), (byte)(offset >> 8), (byte)len, seq,
					table.AsSpan(offset, len)));
			}

			if (bankIdOffset is { } bank)
			{
				WriteFrame(stream, Build(cmd, (byte)(bank & 0xFF), (byte)(bank >> 8), 0x01,
					SeqContinue, [ValidBankId]));
			}
			WriteFrame(stream, Build(cmd, 0x00, 0x00, 0x00, SeqCommit, ReadOnlySpan<byte>.Empty));
			return true;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    write failed: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	private static void WriteFrame(HidStream stream, byte[] frame)
	{
		stream.Write(frame);
		Thread.Sleep(PacketDelayMs);
	}

	// Drains input reports until the reply echoing `expectedCmd` arrives, or the window elapses.
	// Reports whatever it saw even on failure — distinguishes "the dongle is completely silent"
	// from "the dongle replies, but not with the exact cmd/driverID echo we expected", which would
	// point at a different problem (wrong report matching) rather than "reads don't work at all".
	private static byte[]? ReadReply(HidStream stream, byte expectedCmd)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(ReadWindowMs);
		var buffer = new byte[FrameLength];
		var unmatched = new List<byte[]>();

		while (DateTime.UtcNow < deadline)
		{
			int read;
			try { read = stream.Read(buffer); }
			catch (TimeoutException) { continue; }

			if (read >= PayloadOffset && buffer[0] == ReportId && buffer[2] == DriverId && buffer[3] == expectedCmd)
				return (byte[])buffer.Clone();

			if (read > 0 && unmatched.Count < 5)
				unmatched.Add(buffer.AsSpan(0, read).ToArray());
		}

		if (unmatched.Count > 0)
		{
			Console.WriteLine($"    (no matching 0x{expectedCmd:X2} reply, but {unmatched.Count} other report(s) arrived on this handle during the window:)");
			foreach (var u in unmatched)
				Console.WriteLine($"      {Hex(u)}");
		}

		return null;
	}

	private static byte Checksum(byte cmd, byte p1, byte p2, byte p3, byte p4) =>
		(byte)((byte)(DriverId + cmd + p1 + p2 + p3 + p4) ^ ChecksumXor);

	private static byte[] Build(byte cmd, byte p1, byte p2, byte p3, byte p4, ReadOnlySpan<byte> payload)
	{
		if (payload.Length > MaxPayload)
			throw new ArgumentException($"Payload of {payload.Length} bytes exceeds the {MaxPayload}-byte FIFO", nameof(payload));

		var frame = new byte[FrameLength];
		frame[0] = ReportId;
		frame[1] = Checksum(cmd, p1, p2, p3, p4);
		frame[2] = DriverId;
		frame[3] = cmd;
		frame[4] = p1;
		frame[5] = p2;
		frame[6] = p3;
		frame[7] = p4;
		payload.CopyTo(frame.AsSpan(PayloadOffset));
		return frame;
	}

	private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));
}
