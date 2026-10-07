using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Sweeps the timing dimension: varying delays between the param_1 write and the
	// param_0 write (both orders), between the 9-byte command header and its data,
	// and between the three param_1 data blocks. If the firmware drops the effect
	// when a write lands during a flash commit, one of these gaps should light it.
	public static class X43DelayHunt
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

				// ── Phase 1: param_1 -> gap -> param_0 ─────────────────────────
				int[] gaps = [0, 20, 50, 100, 200, 500, 1000, 2000];
				foreach (var gap in gaps)
				{
					WriteParam1(stream, blockGap: 20, headerGap: 0);
					Thread.Sleep(gap);
					WriteParam0(stream, headerGap: 0);
					Console.WriteLine($"STEP {step++}: param_1 -> {gap}ms -> param_0 (watch 3s)");
					Thread.Sleep(3000);
				}

				// ── Phase 2: reversed order, param_0 -> gap -> param_1 ─────────
				foreach (var gap in new[] { 50, 500, 2000 })
				{
					WriteParam0(stream, headerGap: 0);
					Thread.Sleep(gap);
					WriteParam1(stream, blockGap: 20, headerGap: 0);
					Console.WriteLine($"STEP {step++}: param_0 -> {gap}ms -> param_1 (watch 3s)");
					Thread.Sleep(3000);
				}

				// ── Phase 3: header -> data gaps ───────────────────────────────
				foreach (var hg in new[] { 50, 200, 500 })
				{
					WriteParam1(stream, blockGap: 20, headerGap: hg);
					Thread.Sleep(100);
					WriteParam0(stream, headerGap: hg);
					Console.WriteLine($"STEP {step++}: header->data gap {hg}ms (watch 3s)");
					Thread.Sleep(3000);
				}

				// ── Phase 4: block -> block gaps within param_1 ────────────────
				foreach (var bg in new[] { 0, 100, 500 })
				{
					WriteParam1(stream, blockGap: bg, headerGap: 0);
					Thread.Sleep(100);
					WriteParam0(stream, headerGap: 0);
					Console.WriteLine($"STEP {step++}: block gap {bg}ms (watch 3s)");
					Thread.Sleep(3000);
				}

				// ── Phase 5: repetition — param_0 written twice, then thrice ───
				WriteParam0(stream, headerGap: 0);
				Thread.Sleep(100);
				WriteParam0(stream, headerGap: 0);
				Console.WriteLine($"STEP {step++}: param_0 twice, 100ms apart (watch 3s)");
				Thread.Sleep(3000);

				for (int i = 0; i < 3; i++) { WriteParam0(stream, headerGap: 0); Thread.Sleep(400); }
				Console.WriteLine($"STEP {step++}: param_0 three times, 400ms apart (watch 3s)");
				Thread.Sleep(3000);

				Console.WriteLine("Delay sweep finished (device left on factory config + effect 0x02).");
			}
		}

		private static void WriteParam0(HidStream stream, int headerGap)
		{
			SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
			if (headerGap > 0) Thread.Sleep(headerGap);
			var p0 = new byte[64];
			p0[0] = 1;    // 1 ms report rate
			p0[1] = 2;    // gear 3
			p0[2] = 0x02; // effect
			p0[3] = 255;
			p0[4] = 3;
			p0[5] = 7;    // colour loop
			var rep = new byte[65];
			Buffer.BlockCopy(p0, 0, rep, 1, 64);
			stream.Write(rep);
		}

		private static void WriteParam1(HidStream stream, int blockGap, int headerGap)
		{
			var p1 = BuildFactoryParam1();
			SendCommand(stream, 0x03, 0x00, 0x00, 0, 0, 0x07, 3);
			if (headerGap > 0) Thread.Sleep(headerGap);
			for (int i = 0; i < 3; i++)
			{
				var rep = new byte[65];
				Buffer.BlockCopy(p1, i * 64, rep, 1, 64);
				stream.Write(rep);
				if (blockGap > 0) Thread.Sleep(blockGap);
			}
		}

		private static byte[] BuildFactoryParam1()
		{
			var p1 = new byte[192];
			p1[0] = 8; p1[1] = 8; p1[3] = 2; p1[4] = 6; p1[5] = 100; p1[7] = 100;
			ushort[] dpi = [800, 1200, 2400, 3600, 6000, 12000, 0, 0];
			for (int i = 0; i < 8; i++)
			{
				p1[9  + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[10 + i * 2] = (byte)(dpi[i] >> 8);
				p1[25 + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[26 + i * 2] = (byte)(dpi[i] >> 8);
			}
			byte[] gearColors =
			[
				255, 0, 0,   0, 255, 0,   0, 0, 255,   255, 255, 0,
				0, 255, 255, 128, 0, 128, 255, 255, 255, 0, 0, 0,
			];
			Buffer.BlockCopy(gearColors, 0, p1, 64, 24);
			for (int s = 0; s < 8; s++) p1[88 + s * 3] = 255; // LED slots: red

			p1[128] = 0xF0; p1[131] = 0x02;
			p1[132] = 0xF1; p1[135] = 0x02;
			p1[136] = 0xF2; p1[139] = 0x02;
			p1[140] = 0xF4; p1[143] = 0x02;
			p1[144] = 0xF3; p1[147] = 0x02;
			p1[148] = 0x03; p1[151] = 0x0A; // lighting effect cycle
			p1[152] = 0x03; p1[154] = 0x02; p1[155] = 0xF0; // rate loop
			return p1;
		}

		private static void SendCommand(HidStream stream, byte command,
			byte p1 = 0, byte p2 = 0, byte p3 = 0, byte p4 = 0, byte p5 = 0, byte p6 = 0)
		{
			var report = new byte[9];
			report[1] = command;
			report[2] = p1; report[3] = p2; report[4] = p3;
			report[5] = p4; report[6] = p5; report[7] = p6;
			stream.SetFeature(report);
		}
	}
}
