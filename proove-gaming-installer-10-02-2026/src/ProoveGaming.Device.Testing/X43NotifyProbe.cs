using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Uses the discovered notification channel (report ID 0x05 on the inLen=7
	// interface) to watch the firmware's own state while the user operates the
	// controls. Decisive test: if swiping the RGB switch with the effect-cycle
	// binding makes ledEffect step in the notifications while the light stays
	// dark, the LED logic is alive and the output driver is what's broken.
	public static class X43NotifyProbe
	{
		public static void Run()
		{
			var all = DeviceList.Local.GetHidDevices(0x04D9, 0xA245).ToList();
			var device = all.First(d => d.GetMaxFeatureReportLength() == 9 &&
										d.GetMaxOutputReportLength() == 65);
			var notifyDev = all.FirstOrDefault(d => d.GetMaxInputReportLength() == 7)
				?? throw new Exception("Notification interface (inLen=7) not found.");

			if (!device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");
			if (!notifyDev.TryOpen(out var notify))
				throw new Exception("Unable to open notification interface.");

			using (stream)
			using (notify)
			{
				notify.ReadTimeout = 400;
				var stop = new CancellationTokenSource();
				var listener = new Thread(() =>
				{
					while (!stop.IsCancellationRequested)
					{
						try
						{
							var buf = new byte[7];
							int n = notify.Read(buf, 0, 7);
							Console.WriteLine($"    NOTIFY: {BitConverter.ToString(buf, 0, n)}   " +
								$"(profile={buf[1]} rate={buf[2]} stage?={buf[3]} ledEffect?={buf[4]})");
						}
						catch (TimeoutException) { }
						catch { break; }
					}
				});
				listener.IsBackground = true;
				listener.Start();

				// Slot 7 = LED effect cycle (0A/03), everything else confirmed layout.
				WriteMatrix(stream, [0x03, 0x00, 0x00, 0x0A]);

				Console.WriteLine("STEP 1: swipe the RGB switch slowly 4 times (15s) — watch NOTIFY lines");
				Thread.Sleep(15000);

				Console.WriteLine("STEP 2: press the DPI button 3 times (10s) — stage byte should change");
				Thread.Sleep(10000);

				Console.WriteLine("STEP 3: writing param_0 effect=0x03 — watching for the write-ack notification");
				var p0 = new byte[64];
				p0[0] = 1; p0[2] = 0x03; p0[3] = 255; p0[4] = 3; p0[5] = 7;
				var cmd = new byte[9];
				cmd[1] = 0x02; cmd[7] = 1;
				stream.SetFeature(cmd);
				var rep = new byte[65];
				Buffer.BlockCopy(p0, 0, rep, 1, 64);
				stream.Write(rep);
				Thread.Sleep(3000);

				Console.WriteLine("STEP 4: swipe the switch 3 more times (12s) — does ledEffect step past 0x03?");
				Thread.Sleep(12000);

				stop.Cancel();
				Thread.Sleep(600);
				Console.WriteLine("Probe finished.");
			}
		}

		private static void WriteMatrix(HidStream stream, byte[] slot7)
		{
			var m = new byte[64];
			m[0]  = 0xF0; m[3]  = 0x02;
			m[4]  = 0xF1; m[7]  = 0x02;
			m[8]  = 0xF2; m[11] = 0x02;
			m[12] = 0xF4; m[15] = 0x02;
			m[16] = 0xF3; m[19] = 0x02;
			m[20] = 0x03; m[23] = 0x0B;
			m[24] = 0x03; m[26] = 0x02; m[27] = 0xF0;
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
