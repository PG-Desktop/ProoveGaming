using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Writes the TRUE factory configuration from "X43 Standard Mouse Function
	// Specifications 3311.xlsx": 6 DPI gears (800/1200/2400*/3600/6000/12000) with
	// the factory gear colours, key 6 = lighting-effect switch (0A/03), key 7 =
	// report-rate loop, slots 7-15 unbound (the RGB slide switch is a hardware
	// gate, not a matrix key, at the factory).
	public static class X43FactoryConfig
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
				var p1 = new byte[192];

				// Block 0: factory DPI table.
				p1[0] = 8;   // btn_debounce
				p1[1] = 8;   // wheel_debounce
				p1[3] = 2;   // sensor_lift
				p1[4] = 6;   // 6 gears
				p1[5] = 100; // scale_x
				p1[7] = 100; // scale_y
				ushort[] dpi = [800, 1200, 2400, 3600, 6000, 12000, 0, 0];
				for (int i = 0; i < 8; i++)
				{
					p1[9  + i * 2] = (byte)(dpi[i] & 0xFF);
					p1[10 + i * 2] = (byte)(dpi[i] >> 8);
					p1[25 + i * 2] = (byte)(dpi[i] & 0xFF);
					p1[26 + i * 2] = (byte)(dpi[i] >> 8);
				}

				// Block 1: factory gear colours + LED colour table (red fill).
				byte[] gearColors =
				[
					255, 0, 0,   0, 255, 0,   0, 0, 255,   255, 255, 0,
					0, 255, 255, 128, 0, 128, 255, 255, 255, 0, 0, 0,
				];
				Buffer.BlockCopy(gearColors, 0, p1, 64, 24);
				for (int s = 0; s < 8; s++) p1[88 + s * 3] = 255; // LED slots: red

				// Block 2: factory key table.
				p1[128] = 0xF0; p1[131] = 0x02; // Left
				p1[132] = 0xF1; p1[135] = 0x02; // Right
				p1[136] = 0xF2; p1[139] = 0x02; // Middle (wheel)
				p1[140] = 0xF4; p1[143] = 0x02; // top side -> Forward
				p1[144] = 0xF3; p1[147] = 0x02; // bottom side -> Back
				p1[148] = 0x03; p1[151] = 0x0A; // key 6 -> lighting effect cycle
				p1[152] = 0x03; p1[154] = 0x02; p1[155] = 0xF0; // key 7 -> rate loop
				// slots 7-15: unbound

				SendCommand(stream, 0x03, 0x00, 0x00, 0, 0, 0x07, 3);
				for (int i = 0; i < 3; i++)
				{
					var rep = new byte[65];
					Buffer.BlockCopy(p1, i * 64, rep, 1, 64);
					stream.Write(rep);
					Thread.Sleep(20);
				}
				Console.WriteLine("Factory param_1 written (6 gears, factory colours, factory key table).");

				// param_0: gear 3 (2400), effect 0x02, max brightness.
				var p0 = new byte[64];
				p0[0] = 1;    // 1 ms
				p0[1] = 2;    // gear 3
				p0[2] = 0x02;
				p0[3] = 255;
				p0[4] = 3;
				p0[5] = 7;
				SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				var rep0 = new byte[65];
				Buffer.BlockCopy(p0, 0, rep0, 1, 64);
				stream.Write(rep0);
				Console.WriteLine("Factory param_0 written (gear 3 = 2400 DPI, effect 0x02, bri 255).");
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
	}
}
