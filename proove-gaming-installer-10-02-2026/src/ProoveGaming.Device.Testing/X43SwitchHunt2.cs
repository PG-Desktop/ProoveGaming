using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Round 2 of RGB-switch (slot 7) binding hunt, covering the key modes never
	// tried: Fn (F0/00), Vendor key (F1), reserved modes 06-09, and DPI codes.
	// Starts with a control (keyboard 'b') to confirm the slot still registers,
	// and ends by binding a vendor key and LISTENING on every openable interface
	// for the 11-byte vendor report while the user swipes — if the report arrives,
	// the OEM design handles the LED toggle in software and our DeviceHost can
	// replicate it.
	public static class X43SwitchHunt2
	{
		public static void Run()
		{
			var all = DeviceList.Local.GetHidDevices(0x04D9, 0xA245).ToList();
			var device = all.First(d => d.GetMaxFeatureReportLength() == 9 &&
										d.GetMaxOutputReportLength() == 65);

			if (!device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// ── Control: slot 7 = keyboard 'b' ──────────────────────────────
				Console.WriteLine("CONTROL: slot 7 = letter 'b'. Open Notepad and swipe the switch ON.");
				Console.WriteLine("         It should type b repeatedly. (10s)");
				WriteMatrix(stream, [0x05, 0x00, 0x00, 0x01]);
				Thread.Sleep(10000);

				// ── Candidate bindings ──────────────────────────────────────────
				(byte[] entry, string name)[] candidates =
				[
					([0x00, 0x00, 0x00, 0xF0], "Fn key (F0 type 00)"),
					([0x01, 0x00, 0x00, 0xF1], "Vendor key code 01"),
					([0x00, 0x00, 0x00, 0xF1], "Vendor key code 00"),
					([0x01, 0x00, 0x00, 0x06], "reserved mode 06 code 01"),
					([0x01, 0x00, 0x00, 0x07], "reserved mode 07 code 01"),
					([0x01, 0x00, 0x00, 0x08], "reserved mode 08 code 01"),
					([0x01, 0x00, 0x00, 0x09], "reserved mode 09 code 01"),
					([0x03, 0x00, 0x00, 0x0B], "DPI loop (control #2 — should change DPI on swipe)"),
				];

				foreach (var (entry, name) in candidates)
				{
					WriteMatrix(stream, entry);
					Console.WriteLine($"CANDIDATE: {name} — swipe OFF->ON->OFF now (8s)");
					Thread.Sleep(8000);
				}

				// ── Vendor-key listen ───────────────────────────────────────────
				Console.WriteLine();
				Console.WriteLine("LISTEN TEST: slot 7 = vendor key. Listening on all openable interfaces.");
				Console.WriteLine("             Swipe the switch several times during the next 12 seconds!");
				WriteMatrix(stream, [0x01, 0x00, 0x00, 0xF1]);

				var readers = new List<Thread>();
				var stop = new CancellationTokenSource();
				foreach (var dev in all)
				{
					int inLen = dev.GetMaxInputReportLength();
					if (inLen <= 0) continue;
					if (!dev.TryOpen(out var rs))
					{
						Console.WriteLine($"  (interface inLen={inLen} not openable — skipped)");
						continue;
					}
					var t = new Thread(() =>
					{
						using (rs)
						{
							rs.ReadTimeout = 500;
							while (!stop.IsCancellationRequested)
							{
								try
								{
									var buf = new byte[inLen];
									int n = rs.Read(buf, 0, inLen);
									// Ignore pure mouse-movement noise: report all packets on
									// small interfaces, and non-zero packets elsewhere.
									Console.WriteLine($"  REPORT iface(inLen={inLen}): {BitConverter.ToString(buf, 0, n)}");
								}
								catch (TimeoutException) { }
								catch { break; }
							}
						}
					});
					t.IsBackground = true;
					t.Start();
					readers.Add(t);
				}

				Thread.Sleep(12000);
				stop.Cancel();
				Thread.Sleep(600);

				// ── Restore known-good binding ──────────────────────────────────
				WriteMatrix(stream, [0x03, 0x00, 0x00, 0x0A]);
				Console.WriteLine("Slot 7 left at LED-control effect-cycle (0A/03). Done.");
			}
		}

		// Full confirmed key matrix with the supplied slot-7 entry.
		private static void WriteMatrix(HidStream stream, byte[] slot7)
		{
			var m = new byte[64];
			m[0]  = 0xF0; m[3]  = 0x02; // Left
			m[4]  = 0xF1; m[7]  = 0x02; // Right
			m[8]  = 0xF2; m[11] = 0x02; // wheel -> Middle
			m[12] = 0xF4; m[15] = 0x02; // top side -> Forward
			m[16] = 0xF3; m[19] = 0x02; // bottom side -> Back
			m[20] = 0x03; m[23] = 0x0B; // DPI button -> DPI loop
			m[24] = 0x03; m[26] = 0x02; m[27] = 0xF0; // polling -> rate loop
			Buffer.BlockCopy(slot7, 0, m, 28, 4);

			var cmd = new byte[9];
			cmd[1] = 0x03; cmd[3] = 2; cmd[6] = 0x04; cmd[7] = 1;
			stream.SetFeature(cmd);
			var rep = new byte[65];
			Buffer.BlockCopy(m, 0, rep, 1, 64);
			stream.Write(rep);
			Thread.Sleep(80);
		}
	}
}
