using HidSharp;

namespace ProoveGaming.Device.Testing
{
	// Explores the BLAZE physical RGB slide switch (key-matrix slot 7).
	//
	// Slot 7 has two modes (docs/mouse/blaze/key-matrix.md):
	//   • factory  [03 00 00 0A] — mode 0x0A: each swipe cycles the LED effect in FIRMWARE.
	//   • vendorkey[01 00 00 F1] — each swipe instead emits a 0x05 report on the vendor
	//                              notification collection, so SOFTWARE can decide the RGB.
	//
	// Goal: (1) OBSERVE what the switch emits today (read-only), (2) optionally REBIND it to
	// vendor-key mode so we can drive RGB from the toggle, (3) RESTORE factory. The matrix
	// write reuses the capture-confirmed factory baseline + one slot override (block 2 only),
	// the same safe path as the macro spike — so a bad experiment can always be reverted.
	public static class RgbSwitchProbe
	{
		private const int VID = 0x04D9;
		private const int PID = 0xA245;

		// Slot 7 = the RGB slide switch. 4-byte key-matrix entries.
		private static readonly byte[] Slot7Factory   = [0x03, 0x00, 0x00, 0x0A]; // LED-effect cycle (firmware)
		private static readonly byte[] Slot7VendorKey = [0x01, 0x00, 0x00, 0xF1]; // emit 0x05 to software

		public static void Run()
		{
			Console.WriteLine("BLAZE RGB slide switch probe");
			Console.WriteLine("============================");
			Console.WriteLine("  [L] Listen only  — toggle the switch and see what it emits (read-only, safe)");
			Console.WriteLine("  [V] Vendor-key   — rebind slot 7 so each swipe sends a 0x05 report, then listen");
			Console.WriteLine("  [R] Restore      — put slot 7 back to factory (switch cycles LED effect again)");
			Console.WriteLine("  [Q] Quit");
			Console.Write("Choice: ");
			var key = Console.ReadKey().KeyChar;
			Console.WriteLine();

			switch (char.ToUpperInvariant(key))
			{
				case 'L':
					Listen();
					break;
				case 'V':
					WriteSlot7(Slot7VendorKey, "vendor-key [01 00 00 F1]");
					Listen();
					break;
				case 'R':
					WriteSlot7(Slot7Factory, "factory [03 00 00 0A]");
					break;
				default:
					Console.WriteLine("Nothing changed.");
					break;
			}
		}

		private static void Listen()
		{
			// Track the notification bytes so we can flag which one moves when the switch is
			// toggled. 0x05 report layout: [0]=0x05 [1]=profile [2]=reportRate [3]=dpi_stage
			// [4]=ledEffect [5]=0 [6]=0  (docs/mouse/blaze/protocol.md).
			byte lastProfile = 0xFF, lastRate = 0xFF, lastStage = 0xFF, lastEffect = 0xFF;
			var first = true;

			RawInputDpi.Listen(45,
				"STEP 1 (control): press the physical DPI BUTTON 3-4 times — lines SHOULD appear.\n" +
				"STEP 2: then TOGGLE the RGB slide switch slowly a few times.\n" +
				"If STEP 1 shows nothing, the listener isn't receiving (stop DeviceHost + App and retry).",
				report =>
				{
					if (report.Length < 5 || report[0] != 0x05)
					{
						Console.WriteLine($"  other report: {BitConverter.ToString(report)}");
						return;
					}

					byte profile = report[1], rate = report[2], stage = report[3], effect = report[4];
					Console.WriteLine($"  0x05  profile={profile} rate={rate} dpi_stage={stage} ledEffect={effect}");

					if (!first)
					{
						if (effect != lastEffect)  Console.WriteLine($"    >>> ledEffect changed {lastEffect} -> {effect}  (RGB switch toggle?)");
						if (profile != lastProfile) Console.WriteLine($"    >>> profile changed {lastProfile} -> {profile}");
					}
					first = false;
					lastProfile = profile; lastRate = rate; lastStage = stage; lastEffect = effect;
				});

			Console.WriteLine("Listening done.");
		}

		private static void WriteSlot7(byte[] entry4, string label)
		{
			Console.WriteLine($"About to rebind slot 7 (RGB switch) to {label}.");
			Console.WriteLine("This writes block 2 only (factory baseline + slot 7). Press ENTER to apply, Ctrl+C to abort.");
			Console.ReadLine();

			var device = DeviceList.Local
				.GetHidDevices(VID, PID)
				.First(d => d.GetMaxOutputReportLength() == 65 && d.GetMaxFeatureReportLength() == 9);

			if (!device.TryOpen(out var stream))
				throw new Exception("cannot open vendor interface");

			using (stream)
			{
				WriteKeyMatrix(stream, 7, entry4);
				Console.WriteLine($"Slot 7 written = {BitConverter.ToString(entry4)}.");
			}
		}

		// Overlays one 4-byte entry onto the capture-confirmed factory key matrix and writes
		// block 2 only (status 0x04, times 1). Identical baseline to the macro spike's
		// WriteKeyMatrix — see docs/mouse/blaze/key-matrix.md.
		private static void WriteKeyMatrix(HidStream stream, int slot, byte[] entry4)
		{
			var m = new byte[64];
			m[0] = 0xF0; m[3] = 0x02;   // KEY0 Left
			m[4] = 0xF1; m[7] = 0x02;   // KEY1 Right
			m[8] = 0xF2; m[11] = 0x02;  // KEY2 Wheel
			m[12] = 0xF4; m[15] = 0x02; // KEY3 Forward (B5)
			m[16] = 0xF3; m[19] = 0x02; // KEY4 Back (B4)
			m[20] = 0x03; m[23] = 0x0B; // KEY5 DPI+ loop
			m[24] = 0x03; m[27] = 0x06; // KEY6 report-rate loop
			m[28] = 0x03; m[31] = 0x0A; // KEY7 LED cycle (overwritten below)
			m[35] = 0x02; m[39] = 0x02; m[43] = 0x02; m[47] = 0x02; m[51] = 0x02; m[55] = 0x02; // KEY8-13
			m[57] = 0x01; m[59] = 0x04; m[61] = 0x02; m[63] = 0x04; // KEY14/15 ACPI

			Buffer.BlockCopy(entry4, 0, m, slot * 4, 4);

			// SetFeature [0, 0x03, profile=0, index=block2, 0, 0, status=0x04, times=1, cs=0]
			var feature = new byte[9];
			feature[1] = 0x03; feature[2] = 0x00; feature[3] = 0x02; feature[6] = 0x04; feature[7] = 0x01;
			stream.SetFeature(feature);

			var report = new byte[65];
			Buffer.BlockCopy(m, 0, report, 1, 64);
			stream.Write(report);
		}
	}
}
