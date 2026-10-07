using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Tries the LED command family recovered from the OEM VS750 executable's symbol
	// table — commands we never used before (we only used 0x02/0x03 param writes and
	// the old-doc 0x0A):
	//   0x0D SetRGBLedEffect(effect, option1, option2)  — runtime effect
	//   0x0C SetFixLEDColor(uchar*)                      — fixed colour (data block)
	//   0x10 SetLED8StageColor(stage, uchar*)            — per-stage colour (data block)
	//   0x80 SyncData()                                  — apply/commit
	// Hypothesis: param_0/param_1 only STORE settings; 0x0D is the command that
	// actually drives the LED, and we never issued it.
	public static class X43OemLedCmds
	{
		public static void Run()
		{
			var device = DeviceList.Local
				.GetHidDevices(0x04D9, 0xA245)
				.First(d => d.GetMaxFeatureReportLength() == 9 &&
							d.GetMaxOutputReportLength() == 65);

			if (!device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				int step = 1;

				// ── 0x0D SetRGBLedEffect(effect, option1, option2) ─────────────
				// Sweep effect codes 0x01-0x09 (per PDF LED effect table). option1
				// often = brightness/speed, option2 = colour slot. Try a couple of
				// option combos per effect.
				Console.WriteLine("=== cmd 0x0D SetRGBLedEffect(effect, opt1, opt2) ===");
				for (byte eff = 0x01; eff <= 0x09; eff++)
				{
					SendCmd(stream, 0x0D, eff, 0xFF, 0x07); // full brightness, colour loop
					Console.WriteLine($"STEP {step++}: 0x0D effect=0x{eff:X2} opt1=0xFF opt2=0x07 (watch 2.5s)");
					Thread.Sleep(2500);
				}

				// Repeat 0x0D with an explicit SyncData after each.
				Console.WriteLine("=== 0x0D + 0x80 SyncData ===");
				for (byte eff = 0x01; eff <= 0x04; eff++)
				{
					SendCmd(stream, 0x0D, eff, 0x80, 0x00);
					SendCmd(stream, 0x80); // SyncData
					Console.WriteLine($"STEP {step++}: 0x0D effect=0x{eff:X2} + SyncData (watch 2.5s)");
					Thread.Sleep(2500);
				}

				// ── 0x0C SetFixLEDColor(uchar*) — header + colour data block ────
				Console.WriteLine("=== cmd 0x0C SetFixLEDColor (red) ===");
				foreach (var (times, label) in new[] { ((byte)1, "1 block"), ((byte)3, "3 blocks") })
				{
					SendCmd(stream, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, times);
					var data = new byte[64];
					for (int s = 0; s < 8; s++) { data[s * 3] = 0xFF; } // red per slot
					var rep = new byte[65];
					Buffer.BlockCopy(data, 0, rep, 1, 64);
					stream.Write(rep);
					SendCmd(stream, 0x80);
					Console.WriteLine($"STEP {step++}: 0x0C fixed red ({label}) + Sync (watch 2.5s)");
					Thread.Sleep(2500);
				}

				// ── 0x10 SetLED8StageColor(stage, uchar*) ──────────────────────
				Console.WriteLine("=== cmd 0x10 SetLED8StageColor ===");
				SendCmd(stream, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 1);
				var stageData = new byte[64];
				byte[] pal = [255,0,0, 0,255,0, 0,0,255, 255,255,0, 0,255,255, 128,0,128, 255,255,255, 255,128,0];
				Buffer.BlockCopy(pal, 0, stageData, 0, 24);
				var srep = new byte[65];
				Buffer.BlockCopy(stageData, 0, srep, 1, 64);
				stream.Write(srep);
				SendCmd(stream, 0x80);
				Console.WriteLine($"STEP {step++}: 0x10 8-stage palette + Sync (watch 2.5s)");
				Thread.Sleep(2500);

				// ── param_0 store THEN 0x0D apply (the missing combo?) ─────────
				Console.WriteLine("=== param_0 store (0x02) THEN 0x0D apply ===");
				SendCmd(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				var p0 = new byte[64];
				p0[0] = 1; p0[2] = 0x02; p0[3] = 255; p0[4] = 3; p0[5] = 7;
				var prep = new byte[65];
				Buffer.BlockCopy(p0, 0, prep, 1, 64);
				stream.Write(prep);
				Thread.Sleep(50);
				SendCmd(stream, 0x0D, 0x02, 0xFF, 0x07);
				SendCmd(stream, 0x80);
				Console.WriteLine($"STEP {step++}: param_0 + 0x0D + Sync (watch 3s)");
				Thread.Sleep(3000);

				Console.WriteLine("Finished.");
			}
		}

		private static void SendCmd(HidStream stream, byte command,
			byte p1 = 0, byte p2 = 0, byte p3 = 0, byte p4 = 0, byte p5 = 0, byte p6 = 0)
		{
			var report = new byte[9];
			report[1] = command;
			report[2] = p1; report[3] = p2; report[4] = p3;
			report[5] = p4; report[6] = p5; report[7] = p6;
			// Checksum: (sum bytes1..7) & 0xFF XOR 0xAA, per PDF (firmware ignores it,
			// but the OEM computes it, so include it for fidelity).
			int sum = 0;
			for (int i = 1; i <= 7; i++) sum += report[i];
			report[8] = (byte)((sum & 0xFF) ^ 0xAA);
			stream.SetFeature(report);
		}
	}
}
