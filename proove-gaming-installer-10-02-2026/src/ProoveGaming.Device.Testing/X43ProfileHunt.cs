using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Tests whether the LED-disable state is per-profile. All corrupting writes went
	// to profile 0 only; earlier profile cycling only SWITCHED (profiles 1-5 may
	// ship empty). Here we write a COMPLETE valid config (param_0 + all 3 param_1
	// blocks) INTO each profile 1-5 and then activate it — profile-scoped flash for
	// those profiles was never touched by the corruption, so if the LED latch is
	// per-profile, one of these should light up.
	public static class X43ProfileHunt
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
				for (byte profile = 1; profile <= 5; profile++)
				{
					Console.WriteLine($"--- Writing full config into profile {profile} and activating (watch LED, 5s) ---");

					// param_1: all 3 blocks, complete content, targeted at this profile.
					SendCommand(stream, 0x03, profile, 0x00, 0, 0, 0x07, 3);
					var p1 = BuildFullParam1();
					for (int i = 0; i < 3; i++)
					{
						var rep = new byte[65];
						Buffer.BlockCopy(p1, i * 64, rep, 1, 64);
						stream.Write(rep);
						Thread.Sleep(20);
					}

					// param_0: rainbow breathing, max brightness, targeted at this profile.
					SendCommand(stream, 0x02, profile, 0, 0, 0, 0, 1);
					var p0 = new byte[64];
					p0[0] = 1;    // 1 ms report rate
					p0[2] = 0x02; // effect
					p0[3] = 255;  // brightness
					p0[4] = 3;    // speed
					p0[5] = 7;    // colour loop
					var rep0 = new byte[65];
					Buffer.BlockCopy(p0, 0, rep0, 1, 64);
					stream.Write(rep0);
					Thread.Sleep(50);

					// Activate this profile.
					SendCommand(stream, 0x01, profile);
					Thread.Sleep(5000);
				}

				Console.WriteLine("--- Back to profile 0 ---");
				SendCommand(stream, 0x01, 0x00);
				Console.WriteLine("If the LED lit during profile N, say which N — we can simply stay on it.");
			}
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

		// Complete 192-byte param_1: DPI table, colour palettes, key matrix.
		private static byte[] BuildFullParam1()
		{
			var p1 = new byte[192];

			// Block 0 — DPI table + sane hardware settings.
			p1[0] = 8;   // btn_debounce ms
			p1[1] = 8;   // wheel_debounce ms
			p1[3] = 2;   // sensor_lift mm
			p1[4] = 8;   // dpiStage_Max
			p1[5] = 100; // scale_x = 1.0
			p1[7] = 100; // scale_y = 1.0
			ushort[] dpi = [800, 1200, 1600, 2000, 2400, 3200, 4800, 6400];
			for (int i = 0; i < 8; i++)
			{
				p1[9  + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[10 + i * 2] = (byte)(dpi[i] >> 8);
				p1[25 + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[26 + i * 2] = (byte)(dpi[i] >> 8);
			}

			// Block 1 — DPI indicator colours (64-87) + LED colours (88-111).
			byte[][] palette =
			[
				[0xFF, 0x00, 0x00], [0x00, 0xFF, 0x00], [0x00, 0x00, 0xFF], [0xFF, 0xFF, 0x00],
				[0x00, 0xFF, 0xFF], [0xFF, 0x00, 0xFF], [0xFF, 0xFF, 0xFF], [0xFF, 0x80, 0x00],
			];
			for (int s = 0; s < 8; s++)
			{
				p1[64 + s * 3] = palette[s][0]; p1[65 + s * 3] = palette[s][1]; p1[66 + s * 3] = palette[s][2];
				p1[88 + s * 3] = palette[s][0]; p1[89 + s * 3] = palette[s][1]; p1[90 + s * 3] = palette[s][2];
			}

			// Block 2 — key matrix (offset 128): confirmed working layout.
			p1[128] = 0xF0; p1[131] = 0x02; // Left
			p1[132] = 0xF1; p1[135] = 0x02; // Right
			p1[136] = 0xF2; p1[139] = 0x02; // wheel -> Middle
			p1[140] = 0xF4; p1[143] = 0x02; // top side -> Forward
			p1[144] = 0xF3; p1[147] = 0x02; // bottom side -> Back
			p1[148] = 0x03; p1[151] = 0x0B; // DPI button -> DPI loop
			p1[152] = 0x03; p1[154] = 0x02; p1[155] = 0xF0; // polling -> rate loop
			p1[156] = 0x03; p1[159] = 0x0A; // RGB switch -> LED effect cycle

			return p1;
		}
	}
}
