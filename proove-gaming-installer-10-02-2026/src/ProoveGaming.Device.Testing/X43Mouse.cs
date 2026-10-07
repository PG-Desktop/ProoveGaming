using HidSharp;
using System.Drawing;

namespace ProoveGaming.Device.Testing
{
	public sealed class X43Mouse : IDisposable
	{
		private const int VID = 0x04D9;
		private const int PID = 0xA245;

		private readonly HidDevice _device;

		public X43Mouse()
		{
			_device = DeviceList.Local
				.GetHidDevices(VID, PID)
				.First(d => d.GetMaxFeatureReportLength() == 9 &&
							d.GetMaxOutputReportLength() == 65);
		}

		public void Dispose()
		{
		}

		private void SendCommand(
	HidStream stream,
	byte command,
	byte p1 = 0,
	byte p2 = 0,
	byte p3 = 0,
	byte p4 = 0,
	byte p5 = 0,
	byte p6 = 0)
		{
			var report = new byte[9];

			report[0] = 0;          // Report ID
			report[1] = command;
			report[2] = p1;
			report[3] = p2;
			report[4] = p3;
			report[5] = p4;
			report[6] = p5;
			report[7] = p6;
			report[8] = 0;          // checksum ignored

			stream.SetFeature(report);
		}

		private void WriteParameter0(
	HidStream stream,
	byte[] parameter0)
		{
			if (parameter0.Length != 64)
				throw new ArgumentException();

			SendCommand(
				stream,
				0x02,
				0x00,   // profile
				0,
				0,
				0,
				0,
				1);

			var report = new byte[65];

			report[0] = 0;

			Buffer.BlockCopy(
				parameter0,
				0,
				report,
				1,
				64);

			stream.Write(report);
		}

		private void WriteParameter1(
	HidStream stream,
	byte[] parameter1)
		{
			SendCommand(
				stream,
				0x03,
				0x00,
				0x00,
				0,
				0,
				0x07,
				3);

			for (int i = 0; i < 3; i++)
			{
				var report = new byte[65];

				report[0] = 0;

				Buffer.BlockCopy(
					parameter1,
					i * 64,
					report,
					1,
					64);

				stream.Write(report);
			}
		}

		private void SendFeatureCommand(
			byte command,
			byte b1 = 0,
			byte b2 = 0,
			byte b3 = 0,
			byte b4 = 0,
			byte b5 = 0,
			byte b6 = 0)
		{
			byte[] report = new byte[65];

			report[1] = command;
			report[2] = b1;
			report[3] = b2;
			report[4] = b3;
			report[5] = b4;
			report[6] = b5;
			report[7] = b6;

			//_stream.SetFeature(report);
		}

		private void WriteReport(byte[] payload)
		{
			byte[] report = new byte[65];

			Buffer.BlockCopy(payload, 0, report, 1, payload.Length);

			//_stream.Write(report);
		}

		//private void UploadParameter0(byte[] parameter0)
		//{
		//	SendFeatureCommand(
		//		command: 0x02,
		//		b1: 0,     // profile
		//		b6: 1);    // one 64-byte packet

		//	WriteReport(parameter0);
		//}

		//private void UploadParameter1(byte[] parameter1)
		//{
		//	SendFeatureCommand(
		//		command: 0x03,
		//		b1: 0,
		//		b2: 0,
		//		b3: 0,
		//		b4: 0,
		//		b5: 0x07,      // all three blocks changed
		//		b6: 3);

		//	for (int i = 0; i < 3; i++)
		//	{
		//		byte[] packet = new byte[64];

		//		Buffer.BlockCopy(
		//			parameter1,
		//			i * 64,
		//			packet,
		//			0,
		//			64);

		//		WriteReport(packet);
		//	}
		//}

		// Writes the correct 64-byte key matrix to param_1 block 2 and restores
		// a working LED effect in param_0. Call this when buttons are dead because
		// a previous WriteParameter1 with status=0x07 zeroed all three blocks at once.
		//
		// Key entry in memory = [Code0, Code1, Code2, Mode] (4 bytes, lowest address first).
		// Mode 0x02 = Mouse Button; codes F0=Left, F1=Right, F2=Middle, F3=B4, F4=B5.
		// Mode 0x0B = DPI function; code 0x03 = DPI cycle loop.
		// KEY2=B4 and KEY3=B5 confirmed from docs/x43_protocol.txt table at block-2 description.
		public void RestoreDefaults()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// Build the 64-byte default key matrix. Physical slot order confirmed by
				// hardware testing 2026-07-02: 0=Left 1=Right 2=WHEEL CLICK 3=top side
				// 4=bottom side 5=DPI button, then (deduced) 6=polling 7=RGB.
				var keyMatrix = new byte[64];
				// KEY0: Left click
				keyMatrix[0] = 0xF0; keyMatrix[1] = 0x00; keyMatrix[2] = 0x00; keyMatrix[3] = 0x02;
				// KEY1: Right click
				keyMatrix[4] = 0xF1; keyMatrix[5] = 0x00; keyMatrix[6] = 0x00; keyMatrix[7] = 0x02;
				// KEY2: wheel click -> Middle button
				keyMatrix[8]  = 0xF2; keyMatrix[9]  = 0x00; keyMatrix[10] = 0x00; keyMatrix[11] = 0x02;
				// KEY3: top side button -> Forward (B5)
				keyMatrix[12] = 0xF4; keyMatrix[13] = 0x00; keyMatrix[14] = 0x00; keyMatrix[15] = 0x02;
				// KEY4: bottom side button -> Back (B4)
				keyMatrix[16] = 0xF3; keyMatrix[17] = 0x00; keyMatrix[18] = 0x00; keyMatrix[19] = 0x02;
				// KEY5: DPI button -> DPI+ loop (cycle through stages)
				keyMatrix[20] = 0x03; keyMatrix[21] = 0x00; keyMatrix[22] = 0x00; keyMatrix[23] = 0x0B;
				// KEY6: polling button -> Report rate loop (Special key F0, type 01, code 03)
				keyMatrix[24] = 0x03; keyMatrix[25] = 0x00; keyMatrix[26] = 0x01; keyMatrix[27] = 0xF0;
				// KEY7: RGB button -> LED control (mode 0A, code 01 — code 03 did nothing)
				keyMatrix[28] = 0x01; keyMatrix[29] = 0x00; keyMatrix[30] = 0x00; keyMatrix[31] = 0x0A;
				// KEY8–KEY15: null — already zero from new byte[64]

				// Write only block 2. StatusBitmask=0x04=bit2=block2 changed, StartBlockIndex=2, Times=1.
				SendCommand(stream, 0x03, 0x00, 2, 0, 0, 0x04, 1);
				var keyReport = new byte[65];
				Buffer.BlockCopy(keyMatrix, 0, keyReport, 1, 64);
				stream.Write(keyReport);

				Console.WriteLine("Key matrix (block 2) restored.");
				Thread.Sleep(100);

				// Restore block 1 (DPI indicator colors + LED effect colors) — it was also
				// zeroed by the allBlocks wipe. Fill the 8 DPI indicator slots and the 8 LED
				// color slots with distinct sane colors so slot-0 effects have a color to show.
				var block1 = new byte[64];
				byte[][] palette =
				[
					[0xFF, 0x00, 0x00], [0x00, 0xFF, 0x00], [0x00, 0x00, 0xFF], [0xFF, 0xFF, 0x00],
					[0x00, 0xFF, 0xFF], [0xFF, 0x00, 0xFF], [0xFF, 0xFF, 0xFF], [0xFF, 0x80, 0x00],
				];
				for (int s = 0; s < 8; s++)
				{
					// DPI indicator colors: block-1 bytes 0–23
					block1[s * 3]     = palette[s][0];
					block1[s * 3 + 1] = palette[s][1];
					block1[s * 3 + 2] = palette[s][2];
					// LED effect colors: block-1 bytes 24–47
					block1[24 + s * 3]     = palette[s][0];
					block1[24 + s * 3 + 1] = palette[s][1];
					block1[24 + s * 3 + 2] = palette[s][2];
				}
				SendCommand(stream, 0x03, 0x00, 1, 0, 0, 0x02, 1);
				var colorReport = new byte[65];
				Buffer.BlockCopy(block1, 0, colorReport, 1, 64);
				stream.Write(colorReport);

				Console.WriteLine("Color table (block 1) restored.");
				Thread.Sleep(100);

				// Restore LED. Rainbow Breathing (effect=0x02, slot=7) works without needing
				// a specific colour in the colour table, so this is robust regardless of what
				// is currently stored in block 1.
				var p0 = new byte[64];
				p0[2] = 0x02; // Breathing
				p0[3] = 150;  // ~60% brightness
				p0[4] = 3;    // medium speed
				p0[5] = 7;    // slot 7 = rainbow (cycles all colours automatically)
				SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				var p0Report = new byte[65];
				Buffer.BlockCopy(p0, 0, p0Report, 1, 64);
				stream.Write(p0Report);

				Console.WriteLine("LED effect restored (Rainbow Breathing).");
			}
		}

		// Sweeps LED effect types 0x01–0x09 with option byte 0 and 1, plus block-1
		// reserve-byte experiments, ~4s per step. The user watches the mouse and
		// reports which step number lights the LED. Used to hunt for the missing
		// LED-enable parameter after the allBlocks wipe.
		public void LedHunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// Make sure profile 0 is active (command 01H).
				SendCommand(stream, 0x01, 0x00);
				Thread.Sleep(100);
				Console.WriteLine("Profile 0 selected.");

				WriteColorBlock(stream, reserveFill: 0x00);
				Thread.Sleep(100);

				int step = 1;
				foreach (byte option in new byte[] { 0, 1 })
				{
					for (byte effect = 0x01; effect <= 0x09; effect++)
					{
						// Slot 0 = custom colour (block-1 slot 0 = red); brightness max, medium speed.
						WriteEffect(stream, effect, brightness: 255, speed: 3, colorSlot: 0, option: option);
						Console.WriteLine($"STEP {step}: effect=0x{effect:X2} option={option} slot=0 bri=255");
						Thread.Sleep(4000);
						step++;
					}
				}

				// Reserve-byte experiments: maybe the firmware stores an LED-enable flag
				// in block-1 bytes 48–63, which the allBlocks wipe zeroed.
				foreach (byte fill in new byte[] { 0x01, 0xFF })
				{
					WriteColorBlock(stream, reserveFill: fill);
					Thread.Sleep(100);
					WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
					Console.WriteLine($"STEP {step}: block1 reserve=0x{fill:X2} + rainbow breathing");
					Thread.Sleep(4000);
					step++;
				}

				// Leave the device on the known-good June-30 config.
				WriteColorBlock(stream, reserveFill: 0x00);
				Thread.Sleep(100);
				WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
				Console.WriteLine($"STEP {step}: final — rainbow breathing (June-30 known-good config)");
			}
		}

		// Restores every block-0 field to sane values — including scale_x/scale_y
		// (bytes 5–8, = 100 for 1.0 scaling) which no code path ever restored after
		// the allBlocks wipe — then tests block-0 reserve fills and a valid report
		// rate in param_0, ~4s per step.
		public void Block0Hunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				WriteColorBlock(stream, reserveFill: 0x00);
				Thread.Sleep(100);

				int step = 1;
				foreach (byte reserveFill in new byte[] { 0x00, 0x01, 0xFF })
				{
					WriteBlock0(stream, reserveFill);
					Thread.Sleep(100);
					WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
					Console.WriteLine($"STEP {step}: full block0 (scale=100) reserve=0x{reserveFill:X2} + rainbow breathing");
					Thread.Sleep(4000);
					step++;
				}

				// Leave block 0 with zero reserve, then try param_0 with a VALID report
				// rate (byte0=1 → 1ms/1000Hz). Every write so far used reportrate=0,
				// which is not among the documented values (1/2/4/8 ms).
				WriteBlock0(stream, 0x00);
				Thread.Sleep(100);

				var p0 = new byte[64];
				p0[0] = 1;   // report rate: 1 ms
				p0[2] = 0x02;
				p0[3] = 255;
				p0[4] = 3;
				p0[5] = 7;
				SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				var report = new byte[65];
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Console.WriteLine($"STEP {step}: param_0 with reportrate=1ms + rainbow breathing");
				Thread.Sleep(4000);
				step++;

				// param_0 bytes 7–63 filled with 0x01 — in case per-effect config lives there.
				for (int i = 7; i < 64; i++) p0[i] = 0x01;
				SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Console.WriteLine($"STEP {step}: param_0 bytes7-63=0x01 + rainbow breathing");
				Thread.Sleep(4000);
				step++;

				// Final: clean param_0, valid report rate, rainbow.
				Array.Clear(p0, 7, 57);
				SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Console.WriteLine($"STEP {step}: final — clean param_0, reportrate=1ms, rainbow breathing");
			}
		}

		// Tests candidate key-matrix bindings for the RGB button (physical slot 7).
		// Theory: the firmware latches an LED on/off state that only the RGB button
		// can toggle; the allBlocks wipe erased the button's binding, so the latch is
		// stuck at "off" and no param_0 effect write can light the LED. Each candidate
		// is written to slot 7, then the user has 10 seconds to press the RGB button
		// several times. If the LED lights, that candidate is the correct binding.
		public void RgbButtonHunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// Valid effect in param_0 so the LED has something to show once enabled.
				WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
				Thread.Sleep(100);

				// [Code, Byte1, Byte2, Mode]; 0A = LED Control, F0 = Special key.
				(byte[] entry, string name)[] candidates =
				[
					([0x02, 0x00, 0x00, 0x0A], "LED ctrl code 02"),
					([0x04, 0x00, 0x00, 0x0A], "LED ctrl code 04"),
					([0x05, 0x00, 0x00, 0x0A], "LED ctrl code 05"),
					([0x00, 0x01, 0x02, 0xF0], "Special F0 type02 [00,01]"),
					([0x00, 0x02, 0x02, 0xF0], "Special F0 type02 [00,02]"),
					([0x00, 0x03, 0x02, 0xF0], "Special F0 type02 [00,03]"),
					([0x01, 0x00, 0x02, 0xF0], "Special F0 type02 code01"),
					([0x02, 0x00, 0x02, 0xF0], "Special F0 type02 code02"),
				];

				for (int i = 0; i < candidates.Length; i++)
				{
					WriteKeyMatrix(stream, candidates[i].entry);
					Console.WriteLine($"CANDIDATE {i + 1}/{candidates.Length}: {candidates[i].name} — PRESS THE RGB BUTTON A FEW TIMES NOW (10s)");
					Thread.Sleep(10000);
				}

				Console.WriteLine("Sweep finished. Slot 7 left at the last candidate.");
			}
		}

		// Replays the exact sequence from commit 79855b8 ("mouse has led on finally!",
		// 2026-06-30) which our current code diverged from in four ways: it computed a
		// checksum in byte 8, sent cmd 0x0A runtime LED-control commands, sent cmd 0x01
		// ACTIVATE *after* param writes, and used brightness 1–9 with different effect
		// codes (0x01=rainbow, 0x02=static, 0x04=breathing). Block 2 is never touched.
		public void BreakthroughReplay()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				Console.WriteLine("STEP 1: cmd 0x0A runtime — effect=rainbow, bri=9, spd=3 (watch 5s)");
				SendCommandCS(stream, 0x0A, 0x00, 0x01, 0x04, 0, 0, 0); // effect = rainbow flowing
				Thread.Sleep(5);
				SendCommandCS(stream, 0x0A, 0x01, 9, 0x04, 0, 0, 0);    // brightness (OEM range 1-9)
				Thread.Sleep(5);
				SendCommandCS(stream, 0x0A, 0x02, 3, 0x04, 0, 0, 0);    // speed
				Thread.Sleep(5000);

				Console.WriteLine("STEP 2: cmd 0x0A runtime — effect=static(0x02), slot 0 (watch 5s)");
				SendCommandCS(stream, 0x0A, 0x00, 0x02, 0x04, 0, 0, 0);
				Thread.Sleep(5);
				SendCommandCS(stream, 0x0A, 0x03, 0x00, 0x04, 0, 0, 0); // color slot 0
				Thread.Sleep(5000);

				Console.WriteLine("STEP 3: param_0 old codes (effect=0x01 rainbow, bri=9) + block1 red + ACTIVATE (watch 5s)");
				var p0 = new byte[64];
				p0[0] = 1;    // report rate 1 ms
				p0[2] = 0x01; // old code: rainbow flowing
				p0[3] = 9;    // brightness in OEM 1-9 range
				p0[4] = 3;
				p0[5] = 7;    // all colors
				SendCommandCS(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				var report = new byte[65];
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Thread.Sleep(50);
				WriteColorBlockCS(stream);
				Thread.Sleep(50);
				SendCommandCS(stream, 0x01, 0x00, 0, 0, 0, 0, 0); // ACTIVATE after params
				Thread.Sleep(5000);

				Console.WriteLine("STEP 4: param_0 old codes (effect=0x04 breathing, slot 0, bri=9) + ACTIVATE (watch 5s)");
				p0[2] = 0x04; // old code: breathing
				p0[5] = 0;    // slot 0 (red)
				SendCommandCS(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Thread.Sleep(50);
				SendCommandCS(stream, 0x01, 0x00, 0, 0, 0, 0, 0);
				Thread.Sleep(5000);

				Console.WriteLine("STEP 5: modern codes (effect=0x02, slot 7, bri=255) with checksum + ACTIVATE (watch 5s)");
				p0[2] = 0x02;
				p0[3] = 255;
				p0[5] = 7;
				SendCommandCS(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
				Buffer.BlockCopy(p0, 0, report, 1, 64);
				stream.Write(report);
				Thread.Sleep(50);
				SendCommandCS(stream, 0x01, 0x00, 0, 0, 0, 0, 0);
				Thread.Sleep(5000);

				Console.WriteLine("Replay finished.");
			}
		}

		// Same 9-byte feature command as SendCommand but with the checksum from the
		// breakthrough commit: byte8 = ((sum of bytes 1..7) & 0xFF) ^ 0xAA.
		private static void SendCommandCS(HidStream stream, byte command,
			byte p1 = 0, byte p2 = 0, byte p3 = 0, byte p4 = 0, byte p5 = 0, byte p6 = 0)
		{
			var report = new byte[9];
			report[0] = 0;
			report[1] = command;
			report[2] = p1; report[3] = p2; report[4] = p3;
			report[5] = p4; report[6] = p5; report[7] = p6;
			int sum = 0;
			for (int i = 1; i <= 7; i++) sum += report[i];
			report[8] = (byte)((sum & 0xFF) ^ 0xAA);
			stream.SetFeature(report);
		}

		// Block-1 colour write (all slots red) with checksummed command header.
		private void WriteColorBlockCS(HidStream stream)
		{
			var block1 = new byte[64];
			for (int s = 0; s < 8; s++)
			{
				block1[s * 3] = 0xFF;      // DPI indicator colors: red
				block1[24 + s * 3] = 0xFF; // LED colors: red
			}
			SendCommandCS(stream, 0x03, 0x00, 1, 0, 0, 0x02, 1);
			var report = new byte[65];
			Buffer.BlockCopy(block1, 0, report, 1, 64);
			stream.Write(report);
		}

		// Single-colour breathing at max brightness: colour table (slot 0 = red)
		// written before param_0, the order confirmed working on 2026-06-30.
		public void SetBreathingRedMax()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				WriteColorBlock(stream, reserveFill: 0x00);
				Thread.Sleep(100);
				WriteEffect(stream, 0x02, brightness: 255, speed: 3, colorSlot: 0, option: 0);
				Console.WriteLine("Breathing / slot 0 (red) / brightness 255 / speed 3 written.");
			}
		}

		// Sweeps LED-control binding candidates on slot 7 — now KNOWN to be the RGB
		// slide switch (it typed 'b' continuously while ON in the MapUnknownSlots
		// diagnostic). The switch acts as a held key while ON, so the firmware
		// executes the bound function while it is on. The user swipes the switch
		// OFF->ON->OFF during each window and watches for the LED.
		public void RgbSwitchHunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// Valid effect in param_0 so the LED has something to show once enabled.
				WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
				Thread.Sleep(100);

				(byte[] entry, string name)[] candidates =
				[
					([0x00, 0x00, 0x00, 0x0A], "LED ctrl code 00"),
					([0x01, 0x00, 0x00, 0x0A], "LED ctrl code 01"),
					([0x02, 0x00, 0x00, 0x0A], "LED ctrl code 02"),
					([0x03, 0x00, 0x00, 0x0A], "LED ctrl code 03"),
					([0x04, 0x00, 0x00, 0x0A], "LED ctrl code 04"),
					([0x05, 0x00, 0x00, 0x0A], "LED ctrl code 05"),
					([0x06, 0x00, 0x00, 0x0A], "LED ctrl code 06"),
					([0x07, 0x00, 0x00, 0x0A], "LED ctrl code 07"),
					([0x08, 0x00, 0x00, 0x0A], "LED ctrl code 08"),
					([0x03, 0x01, 0x00, 0x0A], "LED ctrl code 03 aux 01"),
					([0x01, 0x01, 0x00, 0x0A], "LED ctrl code 01 aux 01"),
				];

				for (int i = 0; i < candidates.Length; i++)
				{
					WriteKeyMatrix(stream, candidates[i].entry);
					Console.WriteLine($"CANDIDATE {i + 1}/{candidates.Length}: {candidates[i].name} — SWIPE THE RGB SWITCH OFF->ON->OFF NOW (10s)");
					Thread.Sleep(10000);
				}

				Console.WriteLine("Sweep finished. Slot 7 left at the last candidate.");
			}
		}

		// Diagnostic: binds slots 6–15 to keyboard letters a–j (mode 01H, HID usage
		// codes 0x04–0x0D). The user opens a text editor, presses the polling button
		// and swipes the RGB slide switch; whichever letters get typed reveal which
		// physical control sits in which matrix slot. Slots 0–5 keep the confirmed
		// bindings so the mouse stays usable.
		public void MapUnknownSlots()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				var keyMatrix = new byte[64];
				keyMatrix[0]  = 0xF0; keyMatrix[3]  = 0x02; // Left
				keyMatrix[4]  = 0xF1; keyMatrix[7]  = 0x02; // Right
				keyMatrix[8]  = 0xF2; keyMatrix[11] = 0x02; // wheel -> Middle
				keyMatrix[12] = 0xF4; keyMatrix[15] = 0x02; // top side -> Forward
				keyMatrix[16] = 0xF3; keyMatrix[19] = 0x02; // bottom side -> Back
				keyMatrix[20] = 0x03; keyMatrix[23] = 0x0B; // DPI button -> DPI loop

				// Slots 6..15 -> 'a'..'j'
				for (int slot = 6; slot <= 15; slot++)
				{
					keyMatrix[slot * 4]     = (byte)(0x04 + (slot - 6)); // HID usage: a=0x04 … j=0x0D
					keyMatrix[slot * 4 + 3] = 0x01;                      // mode 01H = keyboard key
				}

				SendCommand(stream, 0x03, 0x00, 2, 0, 0, 0x04, 1);
				var report = new byte[65];
				Buffer.BlockCopy(keyMatrix, 0, report, 1, 64);
				stream.Write(report);

				Console.WriteLine("Slots 6-15 bound to letters a-j:");
				Console.WriteLine("  slot 6=a  slot 7=b  slot 8=c  slot 9=d  slot 10=e");
				Console.WriteLine("  slot 11=f slot 12=g slot 13=h slot 14=i slot 15=j");
				Console.WriteLine();
				Console.WriteLine("Open Notepad, then press the polling button and swipe the RGB switch");
				Console.WriteLine("back and forth. Report which letters get typed.");
			}
		}

		// Exhaustive hunt across every undocumented/reserved range the protocol has,
		// ordered safest-first. Phase A maps the firmware's real command table by
		// probing unknown command bytes and reading the GetFeature echo. Phases B-D
		// sweep reserved param_0 values (effects 0x0A+, colour slots 8+, speeds,
		// options). Phase E sweeps sensor_config (block 0 byte 2, marked "not in
		// use"). Phase F tries magic profile values hunting the factory-reset
		// trigger. Never writes the read-only 4th param block (sensor calibration
		// risk). Ends with a full clean restore.
		public void MegaHunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// ── Phase A: command-space probe ────────────────────────────────
				Console.WriteLine("=== PHASE A: probing command bytes 0x06-0x7F, 0x84-0xFF (watch for LED/disconnect) ===");
				var echoes = new List<string>();
				for (int c = 0x06; c <= 0xFF; c++)
				{
					if (c == 0x04 || c == 0x05) continue;           // macro/string writes stall for data
					if (c >= 0x80 && c <= 0x83) continue;           // documented reads
					if (c == 0x01 || c == 0x02 || c == 0x03) continue;
					try
					{
						SendCommand(stream, (byte)c);
						Thread.Sleep(30);
						var buf = new byte[9];
						stream.GetFeature(buf);
						bool any = false;
						for (int i = 1; i < 9; i++) if (buf[i] != 0) { any = true; break; }
						if (any)
						{
							var line = $"cmd 0x{c:X2} -> {BitConverter.ToString(buf)}";
							echoes.Add(line);
							Console.WriteLine($"  ECHO: {line}");
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  !!! cmd 0x{c:X2} caused {ex.GetType().Name}: {ex.Message} — NOTE THIS COMMAND");
						Thread.Sleep(2000);
						if (!_device.TryOpen(out var reopened)) { Console.WriteLine("  Device gone — stopping Phase A."); break; }
						reopened.Dispose();
					}
				}
				Console.WriteLine($"  Phase A done: {echoes.Count} commands echoed.");

				// ── Phase B: reserved effect codes ──────────────────────────────
				Console.WriteLine("=== PHASE B: effect codes 0x0A-0xFF, bri=255 (watch LED; ~4 min) ===");
				for (int eff = 0x0A; eff <= 0xFF; eff++)
				{
					WriteEffect(stream, (byte)eff, 255, 3, colorSlot: 7, option: 0);
					if (eff % 16 == 0) Console.WriteLine($"  ... now at effect 0x{eff:X2}");
					Thread.Sleep(800);
				}

				// ── Phase C: colour slots 8+ ────────────────────────────────────
				Console.WriteLine("=== PHASE C: colour slots beyond 7 (watch LED) ===");
				byte[] slots = [8, 9, 0x0A, 0x0F, 0x10, 0x20, 0x40, 0x7F, 0x80, 0xAA, 0xFF];
				foreach (var slot in slots)
				{
					WriteEffect(stream, 0x02, 255, 3, colorSlot: slot, option: 0);
					Console.WriteLine($"  slot 0x{slot:X2}");
					Thread.Sleep(1200);
				}

				// ── Phase D: out-of-range speed and option ──────────────────────
				Console.WriteLine("=== PHASE D: speed 0/8+/0xFF and option 2+ (watch LED) ===");
				byte[] speeds = [0, 8, 0x10, 0x80, 0xFF];
				foreach (var spd in speeds)
				{
					WriteEffect(stream, 0x02, 255, spd, colorSlot: 7, option: 0);
					Console.WriteLine($"  speed 0x{spd:X2}");
					Thread.Sleep(1200);
				}
				byte[] options = [2, 3, 4, 0x10, 0x80, 0xFF];
				foreach (var opt in options)
				{
					WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: opt);
					Console.WriteLine($"  option 0x{opt:X2}");
					Thread.Sleep(1200);
				}

				// ── Phase E: sensor_config sweep ────────────────────────────────
				Console.WriteLine("=== PHASE E: sensor_config bits (block0 byte2, 'not in use') — cursor may act odd ===");
				byte[] cfgs = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0xFF];
				foreach (var cfg in cfgs)
				{
					var b0 = BuildFullParam1(); // block 0 is first 64 bytes
					b0[2] = cfg;
					SendCommand(stream, 0x03, 0x00, 0, 0, 0, 0x01, 1);
					var rep = new byte[65];
					Buffer.BlockCopy(b0, 0, rep, 1, 64);
					stream.Write(rep);
					Thread.Sleep(100);
					WriteEffect(stream, 0x02, 255, 3, colorSlot: 7, option: 0);
					Console.WriteLine($"  sensor_config 0x{cfg:X2}");
					Thread.Sleep(1500);
				}

				// ── Phase F: magic profile values ───────────────────────────────
				Console.WriteLine("=== PHASE F: magic profile values on cmd 0x01 (watch for reset/LED/disconnect) ===");
				byte[] magics = [0x07, 0x08, 0x10, 0x55, 0x5A, 0xA5, 0xAA, 0xF0, 0xFE];
				foreach (var m in magics)
				{
					try
					{
						SendCommand(stream, 0x01, m);
						Console.WriteLine($"  profile 0x{m:X2}");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  !!! profile 0x{m:X2} caused {ex.GetType().Name} — NOTE THIS VALUE");
					}
					Thread.Sleep(2000);
				}
				SendCommand(stream, 0x01, 0x00);

				// ── Final: full clean restore ───────────────────────────────────
				Console.WriteLine("=== RESTORE: full 3-block write + rainbow param_0 ===");
				var full = BuildFullParam1();
				WriteParameter1(stream, full);
				Thread.Sleep(50);
				var p0 = new byte[64];
				p0[0] = 1; p0[2] = 0x02; p0[3] = 255; p0[4] = 3; p0[5] = 7;
				WriteParameter0(stream, p0);
				Console.WriteLine("Restore complete. Buttons/DPI/colors correct.");

				if (echoes.Count > 0)
				{
					Console.WriteLine();
					Console.WriteLine("UNDOCUMENTED COMMANDS THAT ECHOED (report these):");
					foreach (var e in echoes) Console.WriteLine("  " + e);
				}
			}
		}

		// Deep-probes undocumented command 0x8F (discovered by MegaHunt Phase A — it
		// echoes like the documented 0x8x reads but with an empty payload). Varies
		// each parameter byte and read delay, logging every non-empty response.
		// Also probes its presumed write-pair 0x0F with the same variations.
		public void Probe8F()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				foreach (byte cmd in new byte[] { 0x8F, 0x0F })
				{
					Console.WriteLine($"--- cmd 0x{cmd:X2}: varying p1 ---");
					for (byte p1 = 0; p1 <= 8; p1++)
					{
						SendCommand(stream, cmd, p1);
						Thread.Sleep(150);
						var buf = new byte[9];
						stream.GetFeature(buf);
						Console.WriteLine($"  p1={p1}: {BitConverter.ToString(buf)}");
						Thread.Sleep(100);
					}

					Console.WriteLine($"--- cmd 0x{cmd:X2}: varying p5/p6 (status/times style) ---");
					(byte p5, byte p6)[] combos = [(0x01, 1), (0x07, 3), (0x08, 1), (0xFF, 1)];
					foreach (var (p5, p6) in combos)
					{
						SendCommand(stream, cmd, 0, 0, 0, 0, p5, p6);
						Thread.Sleep(150);
						var buf = new byte[9];
						stream.GetFeature(buf);
						Console.WriteLine($"  p5=0x{p5:X2} p6={p6}: {BitConverter.ToString(buf)}");
						Thread.Sleep(100);
					}
				}

				// Longer settle then re-read, in case the response fills asynchronously.
				SendCommand(stream, 0x8F);
				Thread.Sleep(1000);
				var late = new byte[9];
				stream.GetFeature(late);
				Console.WriteLine($"0x8F after 1s: {BitConverter.ToString(late)}");
			}
		}

		// Interrogates the firmware over GetFeature (the PDF's A1 setup packets show
		// replies to 80H/81H/82H return as 9-byte feature reads, not EP5 data):
		// 80H = firmware version + mcu_type + sensor_type + key_sum, 81H = current
		// profile, 82H/83H = parameter-read ACK headers.
		public void Interrogate()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				(byte cmd, string name)[] queries =
				[
					(0x80, "firmware version (H-Ver, L-Ver, mcu, sensor, key_sum)"),
					(0x81, "current profile"),
					(0x82, "param_0 read ACK"),
					(0x83, "param_1 read ACK"),
				];

				foreach (var (cmd, name) in queries)
				{
					try
					{
						SendCommand(stream, cmd);
						Thread.Sleep(50);
						var buf = new byte[9];
						stream.GetFeature(buf);
						Console.WriteLine($"cmd 0x{cmd:X2} ({name}):");
						Console.WriteLine($"    {BitConverter.ToString(buf)}");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"cmd 0x{cmd:X2} ({name}): {ex.GetType().Name} — {ex.Message}");
					}
					Thread.Sleep(200);
				}
			}
		}

		// Replays the EXACT write sequence that lit the LED throughout June 30–July 1:
		// a full 3-block param_1 write (status=0x07, times=3) followed by param_0 with
		// effect 0x02 / brightness 150 / slot 7. The working-era production code sent
		// all-blocks for every slot-7 effect; every recovery attempt today used
		// single-block writes, which may never (re)start the firmware's LED engine.
		// Afterwards immediately re-sends a full 3-block write with CORRECT content
		// (DPI table + colors + key matrix) so nothing is left zeroed in flash.
		public void June30Exact()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				Console.WriteLine("STEP 1: EXACT June-30 sequence — all-blocks zeros + param_0 (0x02, bri150, slot7)");
				var p1 = new byte[192];
				WriteParameter1(stream, p1); // status=0x07, times=3 — the working-era write
				Thread.Sleep(50);

				var p0 = new byte[64];
				p0[2] = 0x02;
				p0[3] = 150;
				p0[4] = 3;
				p0[5] = 7;
				WriteParameter0(stream, p0);
				Console.WriteLine("        watch the LED for 6 seconds...");
				Thread.Sleep(6000);

				Console.WriteLine("STEP 2: full 3-block write with CORRECT content + rainbow param_0");
				var full = BuildFullParam1();
				WriteParameter1(stream, full);
				Thread.Sleep(50);
				p0[0] = 1;   // 1 ms report rate
				p0[3] = 255;
				WriteParameter0(stream, p0);
				Console.WriteLine("        watch the LED for 6 seconds...");
				Thread.Sleep(6000);

				Console.WriteLine("Finished. Blocks contain correct data (DPI/colors/buttons).");
			}
		}

		// Complete 192-byte param_1: block 0 = DPI table defaults, block 1 = color
		// tables, block 2 = key matrix (slots 0-4 factory + DPI loop on slot 5).
		private byte[] BuildFullParam1()
		{
			var p1 = new byte[192];

			// Block 0 — same defaults as WriteBlock0.
			p1[0] = 8;    // btn_debounce
			p1[1] = 8;    // wheel_debounce
			p1[3] = 2;    // sensor_lift
			p1[4] = 8;    // dpiStage_Max
			p1[5] = 100;  // scale_x = 1.0
			p1[7] = 100;  // scale_y = 1.0
			ushort[] dpi = [800, 1200, 1600, 2000, 2400, 3200, 4800, 6400];
			for (int i = 0; i < 8; i++)
			{
				p1[9  + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[10 + i * 2] = (byte)(dpi[i] >> 8);
				p1[25 + i * 2] = (byte)(dpi[i] & 0xFF);
				p1[26 + i * 2] = (byte)(dpi[i] >> 8);
			}

			// Block 1 — color palettes (offsets 64-87 DPI, 88-111 LED).
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

			// Block 2 — key matrix (offset 128): factory 0-4 plus DPI loop at slot 5.
			p1[128] = 0xF0; p1[131] = 0x02; // Left
			p1[132] = 0xF1; p1[135] = 0x02; // Right
			p1[136] = 0xF2; p1[139] = 0x02; // Middle (wheel)
			p1[140] = 0xF4; p1[143] = 0x02; // top side -> Forward
			p1[144] = 0xF3; p1[147] = 0x02; // bottom side -> Back
			p1[148] = 0x03; p1[151] = 0x0B; // DPI button -> DPI loop
			p1[152] = 0x03; p1[154] = 0x02; p1[155] = 0xF0; // polling -> rate loop (type 02 per PDF)
			p1[156] = 0x03; p1[159] = 0x0A; // RGB switch -> LED effect cycle (0A/03 per PDF)

			return p1;
		}

		// Vendor-prescribed reset (docs/mouse/blaze/blaze-vendor-qa-2026-07-27.md): there is NO
		// hardware key-combo reset. Resetting the firmware from software means writing a known-good
		// DEFAULT PROFILE back into the 0x02 (param_0) and 0x03 (param_1, all three 64-byte blocks)
		// tables. This rebuilds the DPI table, colour tables and key matrix, then sets a default LED
		// effect. Block-2 (key-matrix) writes are the brick-prone op; if a button/DPI misbehaves
		// afterwards, RestoreAll()/RestoreFactoryMatrix() are the recovery paths.
		public void ResetToDefault()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				// 0x03 table — the DEFAULT PROFILE that reproduces the OEM VS750 capture documented in
				// docs/mouse/blaze/protocol.md byte-for-byte (all three blocks together, status 0x07,
				// times 3). This is the "preset spec profile" the vendor Q&A says to write for a reset.
				var param1 = BuildCaptureDefaultParam1();
				WriteParameter1(stream, param1);
				Console.WriteLine("0x03 table written: capture-exact default (DPI table + colours + key matrix).");
				Thread.Sleep(50);

				// 0x02 table — default param_0 per protocol.md layout: report rate = raw ms (0x01 =
				// 1000 Hz), active DPI stage 0, then the LED header. ledEffect uses a documented vendor
				// effect (0x02 Breathing) — NOT 0x04 (ColorCycle isn't in the vendor's four-effect set).
				var param0 = new byte[64];
				param0[0] = 0x01; // reportrate: 1 ms (1000 Hz)
				param0[1] = 0x00; // active DPI stage 0
				param0[2] = 0x02; // ledEffect: Breathing (vendor effect 0x02)
				param0[3] = 0xFF; // ledBrightness
				param0[4] = 0x03; // ledSpeed
				param0[5] = 0x00; // ledColor slot 0
				param0[6] = 0x00; // ledOption
				WriteParameter0(stream, param0);
				Console.WriteLine("0x02 table written: reportrate 1000 Hz, DPI stage 0, Breathing.");

				Console.WriteLine("Reset-to-default sequence complete.");
			}
		}

		// 192-byte default profile that reproduces the OEM VS750 capture documented in
		// docs/mouse/blaze/protocol.md byte-for-byte — the authoritative "spec default profile" to
		// write for a reset (the same bytes EasenMouseService.CreateDefaultBlock0 / CreateDefaultKeyMatrix
		// send). Unlike BuildFullParam1 (older, diverges), this matches the ground-truth capture.
		private static byte[] BuildCaptureDefaultParam1()
		{
			var p1 = new byte[192];

			// ── Block 0 (index 0) — DPI table & sensor config ──
			// Capture wrote debounce/wheel/sensor/lift/scale = 0; dpiStage_Max = 7; 7 DPI stages.
			p1[4] = 7; // dpiStage_Max
			ushort[] dpi = [200, 800, 1600, 3200, 6400, 9600, 12000]; // 7 stages; slot 8 stays 0
			for (int i = 0; i < dpi.Length; i++)
			{
				p1[9  + i * 2] = (byte)(dpi[i] & 0xFF);  // dpi_x_value[i]
				p1[10 + i * 2] = (byte)(dpi[i] >> 8);
				p1[25 + i * 2] = (byte)(dpi[i] & 0xFF);  // dpi_y_value[i]
				p1[26 + i * 2] = (byte)(dpi[i] >> 8);
			}

			// ── Block 1 (index 1) — colours (base offset 64) ──
			// DPI-indicator RGB (64..87) all 0 per capture; LED-effect slots (88..111) =
			// red, blue, green, yellow, cyan, magenta, orange, white.
			byte[][] led =
			[
				[0xFF, 0x00, 0x00], [0x00, 0x00, 0xFF], [0x00, 0xFF, 0x00], [0xFF, 0xFF, 0x00],
				[0x00, 0xFF, 0xFF], [0xFF, 0x00, 0xFF], [0xFF, 0x80, 0x00], [0xFF, 0xFF, 0xFF],
			];
			for (int s = 0; s < 8; s++)
			{
				p1[88 + s * 3] = led[s][0];
				p1[89 + s * 3] = led[s][1];
				p1[90 + s * 3] = led[s][2];
			}

			// ── Block 2 (index 2) — key matrix, base offset 128, entry [code, b1, b2, mode] ──
			void Key(int slot, byte b0, byte b1, byte b2, byte mode)
			{
				int o = 128 + slot * 4;
				p1[o] = b0; p1[o + 1] = b1; p1[o + 2] = b2; p1[o + 3] = mode;
			}
			Key(0, 0xF0, 0x00, 0x00, 0x02); // Left
			Key(1, 0xF1, 0x00, 0x00, 0x02); // Right
			Key(2, 0xF2, 0x00, 0x00, 0x02); // Middle (wheel)
			Key(3, 0xF4, 0x00, 0x00, 0x02); // B5 / Forward
			Key(4, 0xF3, 0x00, 0x00, 0x02); // B4 / Back
			Key(5, 0x03, 0x00, 0x00, 0x0B); // DPI+ loop
			Key(6, 0x03, 0x00, 0x00, 0x06); // Report-rate loop (mode 0x06 — the working binding)
			Key(7, 0x03, 0x00, 0x00, 0x0A); // LED effect cycle
			for (int s = 8; s <= 13; s++)
				Key(s, 0x00, 0x00, 0x00, 0x02); // null mouse buttons
			Key(14, 0x00, 0x01, 0x00, 0x04); // ACPI
			Key(15, 0x00, 0x02, 0x00, 0x04); // ACPI

			return p1;
		}

		// LED isolation test built on the vendor Q&A: the underside buttons are a polling-rate switch
		// and a LIGHTING-EFFECT switch, so the LED's native control is that physical switch (slot 7).
		// Binding slot 7 (as the full default does) overrides it. This writes the full default but
		// leaves slot 7 NULL — DPI (slot 5) stays bound so the device is usable — sets a bright,
		// visible Neon effect, and sends cmd 0x01 to ACTIVATE the profile AFTER the param writes (the
		// only sequence that ever lit the LED activated last). Then the user works the underside
		// switches by hand. If the LED never lights even physically, the engine is gated in hardware
		// on this unit (isolate against the OEM software next).
		public void LedNativeSwitchTest()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				var p1 = BuildFullParam1();
				Array.Clear(p1, 156, 4); // slot 7 NULL — leave the lighting-effect switch native
				WriteParameter1(stream, p1);
				Console.WriteLine("0x03 table written (slot 7 left NULL for the native lighting-effect switch).");
				Thread.Sleep(50);

				var p0 = new byte[64];
				p0[0] = 1;    // 1 ms report rate
				p0[2] = 0x03; // Neon (vendor effect 0x03)
				p0[3] = 255;  // full brightness
				p0[4] = 3;    // speed
				p0[5] = 0;    // colour slot 0 (red from block 1)
				WriteParameter0(stream, p0);
				Console.WriteLine("0x02 table written: Neon, full brightness, slot 0.");
				Thread.Sleep(50);

				SendCommand(stream, 0x01, 0x00); // activate profile AFTER the param writes
				Console.WriteLine("Profile activated (cmd 0x01).");

				Console.WriteLine();
				Console.WriteLine(">>> Now press the UNDERSIDE lighting-effect switch several times and watch the LED.");
				Console.WriteLine(">>> Also try the polling-rate switch. Report if the LED EVER lights, even briefly.");
			}
		}

		// Writes the EXACT factory key matrix from the X43 PDF (page 6):
		// KEY0=Left KEY1=Right KEY2=Middle KEY3=B4 KEY4=B5, KEY5-15 = reserved (null).
		// CRITICAL: slots 5-7 (DPI button, polling button, RGB slide switch) must stay
		// NULL — the firmware handles those buttons natively ONLY while their matrix
		// entry is null. Any binding there overrides and disables the built-in
		// function; that is what killed the RGB switch (and with it the firmware's
		// LED-enable toggle) after our restores bound guessed codes to slot 7.
		public void RestoreFactoryMatrix()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				var keyMatrix = new byte[64];
				keyMatrix[0]  = 0xF0; keyMatrix[3]  = 0x02; // KEY0: Left
				keyMatrix[4]  = 0xF1; keyMatrix[7]  = 0x02; // KEY1: Right
				keyMatrix[8]  = 0xF2; keyMatrix[11] = 0x02; // KEY2: Middle
				keyMatrix[12] = 0xF3; keyMatrix[15] = 0x02; // KEY3: B4 (back)
				keyMatrix[16] = 0xF4; keyMatrix[19] = 0x02; // KEY4: B5 (forward)
				// KEY5-15: null (factory 保留) — DPI/polling/RGB handled natively.

				SendCommand(stream, 0x03, 0x00, 2, 0, 0, 0x04, 1);
				var report = new byte[65];
				Buffer.BlockCopy(keyMatrix, 0, report, 1, 64);
				stream.Write(report);
				Console.WriteLine("FACTORY key matrix written: KEY0-4 bound, KEY5-15 null.");
				Thread.Sleep(100);

				// Valid effect in param_0 so the LED shows as soon as the switch enables it.
				var p0 = new byte[64];
				p0[0] = 1;    // 1 ms report rate
				p0[2] = 0x04; // Breathing per PDF effect table (呼吸)
				p0[3] = 255;
				p0[4] = 3;
				p0[5] = 7;    // color loop
				SendCommandViaParam0(stream, p0);
				Console.WriteLine("param_0 written: breathing (0x04), bri 255, color loop.");
			}
		}

		// Hunts for factory-intact state. Every write today targeted profile 0 only,
		// so profiles 1–5 should still hold untouched factory data (params + key
		// matrix + whatever makes the LED work). Cycles through them first; then
		// tries undocumented reset-command candidates (the PDF documents a vendor
		// notification 0xFF/00 = "profile restored to defaults" but no command for
		// it, so 0x01 with out-of-range profile values and the unused command range
		// 0x06–0x0F are the plausible triggers). Ends back on profile 0.
		public void ProfileAndResetHunt()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				Console.WriteLine("=== PHASE 1: factory profiles (watch LED + test wheel click after each) ===");
				for (byte p = 1; p <= 5; p++)
				{
					SendCommand(stream, 0x01, p);
					Console.WriteLine($"  Switched to profile {p} — watch the mouse (4s)");
					Thread.Sleep(4000);
				}

				Console.WriteLine("  Back to profile 0.");
				SendCommand(stream, 0x01, 0x00);
				Thread.Sleep(2000);

				Console.WriteLine("=== PHASE 2: reset-command candidates (watch for LED / reconnect) ===");
				(byte cmd, byte p1, string name)[] candidates =
				[
					(0x01, 0xFF, "set profile 0xFF (restore-defaults convention)"),
					(0x01, 0x06, "set profile 6 (out of range)"),
					(0x06, 0x00, "undocumented cmd 0x06"),
					(0x07, 0x00, "undocumented cmd 0x07"),
					(0x08, 0x00, "undocumented cmd 0x08"),
					(0x09, 0x00, "undocumented cmd 0x09"),
					(0x0F, 0x00, "undocumented cmd 0x0F"),
					(0xFF, 0x00, "cmd 0xFF"),
				];
				foreach (var (cmd, p1, name) in candidates)
				{
					try
					{
						SendCommand(stream, cmd, p1);
						Console.WriteLine($"  Sent {name} — watch the mouse (3s)");
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  {name} -> {ex.GetType().Name}: {ex.Message} (device may be resetting!)");
					}
					Thread.Sleep(3000);
				}

				Console.WriteLine("Hunt finished. Device left on profile 0 (unless a reset re-enumerated it).");
			}
		}

		// Dumps param_0 and all three param_1 blocks by issuing the protocol read
		// commands (0x82/0x83) and polling every readable HID interface of the mouse
		// for the reply. Do not move the mouse while this runs — movement reports
		// would interleave with the parameter data.
		public void DumpParams()
		{
			var all = DeviceList.Local.GetHidDevices(VID, PID).ToList();
			Console.WriteLine($"{all.Count} interfaces found:");
			foreach (var dev in all)
				Console.WriteLine($"  in={dev.GetMaxInputReportLength()} out={dev.GetMaxOutputReportLength()} feat={dev.GetMaxFeatureReportLength()}");

			// Open every readable interface BEFORE sending the read command, so the
			// reply is buffered no matter which interface carries it.
			var streams = new List<(HidStream s, int inLen)>();
			foreach (var dev in all)
			{
				int inLen = dev.GetMaxInputReportLength();
				if (inLen <= 0) continue;
				if (dev.TryOpen(out var s))
				{
					s.ReadTimeout = 600;
					streams.Add((s, inLen));
				}
				else
				{
					Console.WriteLine($"  (could not open interface inLen={inLen} for reading)");
				}
			}
			Console.WriteLine($"Opened {streams.Count} readable interfaces.");

			try
			{
				if (!_device.TryOpen(out var w))
					throw new Exception("Unable to open write interface.");

				using (w)
				{
					Console.WriteLine("--- read param_0 (cmd 0x82) ---");
					SendCommand(w, 0x82, 0x00, 0, 0, 0, 0, 1);
					PollStreams(streams);

					Console.WriteLine("--- read param_1 (cmd 0x83, 3 blocks) ---");
					SendCommand(w, 0x83, 0x00, 0, 0, 0, 0x07, 3);
					PollStreams(streams);
				}
			}
			finally
			{
				foreach (var (s, _) in streams) s.Dispose();
			}
		}

		private static void PollStreams(List<(HidStream s, int inLen)> streams)
		{
			foreach (var (s, inLen) in streams)
			{
				for (int i = 0; i < 4; i++)
				{
					try
					{
						var buf = new byte[inLen];
						int n = s.Read(buf, 0, inLen);
						Console.WriteLine($"  iface(inLen={inLen}) pkt{i}: {BitConverter.ToString(buf, 0, n)}");
					}
					catch (TimeoutException)
					{
						Console.WriteLine($"  iface(inLen={inLen}) pkt{i}: timeout");
						break;
					}
					catch (Exception ex)
					{
						Console.WriteLine($"  iface(inLen={inLen}) pkt{i}: {ex.GetType().Name} {ex.Message}");
						break;
					}
				}
			}
		}

		// Restores the confirmed key matrix (buttons only, no LED/color writes).
		public void RestoreButtons()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				WriteKeyMatrix(stream, [0x01, 0x00, 0x00, 0x0A]);
				Console.WriteLine("Key matrix restored: L/R/Middle/Forward/Back/DPI-loop/rate-loop.");
			}
		}

		// Full recovery of all three param_1 blocks: DPI table (block 0), colors
		// (block 1), and key matrix (block 2). Needed after an old build with the
		// all-blocks bug zeroes everything.
		public void RestoreAll()
		{
			if (!_device.TryOpen(out var stream))
				throw new Exception("Unable to open HID stream.");

			using (stream)
			{
				WriteBlock0(stream, reserveFill: 0x00);
				Console.WriteLine("Block 0 restored: 8 DPI stages 800-6400, debounce 8ms, lift-off 2mm, scale 100.");
				Thread.Sleep(100);

				WriteColorBlock(stream, reserveFill: 0x00);
				Console.WriteLine("Block 1 restored: DPI indicator + LED color tables.");
				Thread.Sleep(100);

				WriteKeyMatrix(stream, [0x01, 0x00, 0x00, 0x0A]);
				Console.WriteLine("Block 2 restored: L/R/Middle/Forward/Back/DPI-loop/rate-loop.");
				Thread.Sleep(100);

				// param_0: active stage 1600 DPI (index 2), rainbow breathing, 1ms rate.
				var p0 = new byte[64];
				p0[0] = 1;
				p0[1] = 2;
				p0[2] = 0x02;
				p0[3] = 255;
				p0[4] = 3;
				p0[5] = 7;
				SendCommandViaParam0(stream, p0);
				Console.WriteLine("param_0 written: active DPI stage 3 (1600), rainbow breathing.");
			}
		}

		private void SendCommandViaParam0(HidStream stream, byte[] p0)
		{
			SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
			var report = new byte[65];
			Buffer.BlockCopy(p0, 0, report, 1, 64);
			stream.Write(report);
		}

		// Writes the full confirmed key matrix with a caller-supplied entry in slot 7.
		private void WriteKeyMatrix(HidStream stream, byte[] slot7Entry)
		{
			var keyMatrix = new byte[64];
			keyMatrix[0]  = 0xF0; keyMatrix[3]  = 0x02; // Left
			keyMatrix[4]  = 0xF1; keyMatrix[7]  = 0x02; // Right
			keyMatrix[8]  = 0xF2; keyMatrix[11] = 0x02; // wheel -> Middle
			keyMatrix[12] = 0xF4; keyMatrix[15] = 0x02; // top side -> Forward
			keyMatrix[16] = 0xF3; keyMatrix[19] = 0x02; // bottom side -> Back
			keyMatrix[20] = 0x03; keyMatrix[23] = 0x0B; // DPI button -> DPI loop
			keyMatrix[24] = 0x03; keyMatrix[26] = 0x01; keyMatrix[27] = 0xF0; // polling -> rate loop
			Buffer.BlockCopy(slot7Entry, 0, keyMatrix, 28, 4); // RGB button candidate

			SendCommand(stream, 0x03, 0x00, 2, 0, 0, 0x04, 1);
			var report = new byte[65];
			Buffer.BlockCopy(keyMatrix, 0, report, 1, 64);
			stream.Write(report);
		}

		// Full block-0 restore: every documented field gets a sane value.
		private void WriteBlock0(HidStream stream, byte reserveFill)
		{
			var b = new byte[64];
			b[0] = 8;    // btn_debounce ms
			b[1] = 8;    // wheel_debounce ms
			b[2] = 0;    // sensor_config
			b[3] = 2;    // sensor_lift mm
			b[4] = 8;    // dpiStage_Max
			b[5] = 100; b[6] = 0;   // scale_x = 100 → 1.0
			b[7] = 100; b[8] = 0;   // scale_y = 100 → 1.0
			ushort[] dpi = [800, 1200, 1600, 2000, 2400, 3200, 4800, 6400];
			for (int i = 0; i < 8; i++)
			{
				b[9  + i * 2] = (byte)(dpi[i] & 0xFF);
				b[10 + i * 2] = (byte)(dpi[i] >> 8);
				b[25 + i * 2] = (byte)(dpi[i] & 0xFF);
				b[26 + i * 2] = (byte)(dpi[i] >> 8);
			}
			for (int i = 41; i < 64; i++)
				b[i] = reserveFill;

			SendCommand(stream, 0x03, 0x00, 0, 0, 0, 0x01, 1);
			var report = new byte[65];
			Buffer.BlockCopy(b, 0, report, 1, 64);
			stream.Write(report);
		}

		// Writes param_1 block 1: DPI indicator colors + LED colors (slot 0 = red,
		// remaining slots a distinct palette) and the reserve bytes 48–63 set to fill.
		private void WriteColorBlock(HidStream stream, byte reserveFill)
		{
			var block1 = new byte[64];
			byte[][] palette =
			[
				[0xFF, 0x00, 0x00], [0x00, 0xFF, 0x00], [0x00, 0x00, 0xFF], [0xFF, 0xFF, 0x00],
				[0x00, 0xFF, 0xFF], [0xFF, 0x00, 0xFF], [0xFF, 0xFF, 0xFF], [0xFF, 0x80, 0x00],
			];
			for (int s = 0; s < 8; s++)
			{
				block1[s * 3]          = palette[s][0];
				block1[s * 3 + 1]      = palette[s][1];
				block1[s * 3 + 2]      = palette[s][2];
				block1[24 + s * 3]     = palette[s][0];
				block1[24 + s * 3 + 1] = palette[s][1];
				block1[24 + s * 3 + 2] = palette[s][2];
			}
			for (int i = 48; i < 64; i++)
				block1[i] = reserveFill;

			SendCommand(stream, 0x03, 0x00, 1, 0, 0, 0x02, 1);
			var report = new byte[65];
			Buffer.BlockCopy(block1, 0, report, 1, 64);
			stream.Write(report);
		}

		private void WriteEffect(HidStream stream, byte effect, byte brightness, byte speed, byte colorSlot, byte option)
		{
			var p0 = new byte[64];
			p0[2] = effect;
			p0[3] = brightness;
			p0[4] = speed;
			p0[5] = colorSlot;
			p0[6] = option;

			SendCommand(stream, 0x02, 0x00, 0, 0, 0, 0, 1);
			var report = new byte[65];
			Buffer.BlockCopy(p0, 0, report, 1, 64);
			stream.Write(report);
		}

		public void SetStaticColor(Color color)
		{
			if (!_device.TryOpen(out HidStream stream))
				throw new Exception("Unable to open HID stream.");
			var col = Color.Green;
			var p1 = new byte[192];

			//p1[88] = color.R;
			//p1[89] = color.G;
			//p1[90] = color.B;

			p1[88] = 0;
			p1[89] = 255;
			p1[90] = 0;

			//p1[88] = 0;
			//p1[89] = 0;
			//p1[90] = 0;

			WriteParameter1(stream, p1);
			Thread.Sleep(50);

			var p0 = new byte[64];

			p0[2] = 0x02;      // Effect
			p0[3] = 150;       // Brightness
			p0[4] = 3;         // Speed
			p0[5] = 0;         // Color0
			p0[6] = 0;

			WriteParameter0(stream, p0);
		}

		public void SetBreathing(
			Color color,
			byte brightness = 255,
			byte speed = 4)
		{
			SetEffect(
				LedEffect.Breathing,
				color,
				brightness,
				speed,
				option: 0);
		}

		public void SetNeon(
			Color color,
			byte brightness = 255,
			byte speed = 4)
		{
			SetEffect(
				LedEffect.Neon,
				color,
				brightness,
				speed,
				option: 0);
		}

		public void SetEffect(
			LedEffect effect,
			Color color,
			byte brightness,
			byte speed,
			byte option)
		{
			brightness = Math.Clamp(brightness, (byte)0, (byte)255);
			speed = Math.Clamp(speed, (byte)1, (byte)7);

			//------------------------------------------
			// Parameter1 (192 bytes)
			//------------------------------------------

			byte[] parameter1 = new byte[192];

			parameter1[88] = color.R;
			parameter1[89] = color.G;
			parameter1[90] = color.B;

			//UploadParameter1(parameter1);

			//------------------------------------------
			// Parameter0 (64 bytes)
			//------------------------------------------

			byte[] parameter0 = new byte[64];

			parameter0[2] = (byte)effect;
			parameter0[3] = brightness;
			parameter0[4] = speed;
			parameter0[5] = 0;
			parameter0[6] = option;

			//UploadParameter0(parameter0);
		}
	}
}
