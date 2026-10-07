using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── Rampage over the 2.4G receiver — vendor-channel probe ────────────────────────────────────────
//
// The factory materials document exactly one wireless configuration path for the Smart Infinite
// family, and it is the 2.4G receiver (see
// docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md):
//   - same vendor interface rule as wired (usage 0x20 / page 0xFF87; 0xFF88/0x21 also named);
//   - receiver-channel commands use magic 0xAA (state query: AA D0 00 00 18 00 00 00);
//   - table transfers chunk as 64-byte packets with 24 payload bytes (wired uses 56) — C12.
//
// Run with the keyboard connected over 2.4G (receiver plugged in), DeviceHost STOPPED:
//
//   stage 1 (always, READ-ONLY): enumerates every HID interface, marking vendor-usage matches and
//     annotating known PIDs. Candidate = vendor-usage interface that is NOT a known wired keyboard
//     (Aress 0x326E / Cyrex 0x3272 are excluded so a probe run cannot repaint a board that happens
//     to be plugged in by cable).
//
//   stage 2 (always): per candidate — receiver state query (0xAA channel, documented as safe to
//     send at any time; decodes bonded VID/PID + link state), then a 0x14 function-block read
//     first with wired 56-byte chunking, then with the C12 24-byte chunking.
//
//   stage 3 (`write` arg only, WRITES): static RED via the production RampageKeyboardRgbService
//     (wired 56+8 framing), read-back; if that does not persist, a bare 0x15 write of the same
//     feature area in 24-byte chunks (no session bracket), static GREEN, read-back. Eyes on the
//     LEDs decide, as always.
//
// Only documented commands are sent: 0xAA 0xD0 (receiver state), 0x14 (read), 0x15 (RGB write),
// and the production service's 0x10/0x11/0x1D. 0x20 / 0x31 round-trips are never sent.
internal static class Rampage24gProbe
{
	private const int SmartInfiniteVid = 0x28E9;

	private const int VendorUsagePage = 0xFF87;
	private const int VendorUsageId = 0x20;
	// C12 additionally names this pair for wired/2.4G.
	private const int VendorUsagePageAlt = 0xFF88;
	private const int VendorUsageIdAlt = 0x21;

	private const byte ReceiverMagic = 0xAA;
	private const byte ReceiverStateQuery = 0xD0;

	// The RGB/feature-area write and the lighting sync — same values the production service uses.
	private const byte CmdSetGeneralInfo = 0x15;
	private const byte CmdSyncLighting = 0x1D;

	// C12: 2.4G table transfers carry 24 payload bytes per 64-byte packet.
	private const int Chunk24 = 24;

	// Boards that must never be repainted by this probe when they happen to be present by cable.
	private static string? KnownWiredBoard(int vid, int pid) => (vid, pid) switch
	{
		(SmartInfiniteVid, 0x326E) => "Aress (wired)",
		(SmartInfiniteVid, 0x3272) => "Cyrex (wired)",
		_ => null,
	};

	private static string Annotate(int vid, int pid) => (vid, pid) switch
	{
		(SmartInfiniteVid, 0x3258) => "Rampage USB identity (cable)",
		(SmartInfiniteVid, 0x3000) => "Rampage BLE identity",
		(SmartInfiniteVid, 0x326E) => "Aress (wired)",
		(SmartInfiniteVid, 0x3272) => "Cyrex (wired)",
		(0x056D, 0xC077) => "Rampage 2.4G receiver (observed 2026-08-13)",
		_ => "unknown — receiver candidate",
	};

	public static async Task RunAsync(bool allowWrites)
	{
		Console.WriteLine("Rampage 2.4G receiver vendor-channel probe");
		Console.WriteLine("Expected state: keyboard on 2.4G (receiver plugged), cable unplugged, DeviceHost stopped.");
		Console.WriteLine();

		var all = DeviceList.Local.GetHidDevices()
			.OrderBy(d => d.VendorID).ThenBy(d => d.ProductID)
			.ThenBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		Console.WriteLine($"HID interfaces visible to the OS: {all.Count}");
		Console.WriteLine();

		var candidates = new List<HidDevice>();
		foreach (var d in all)
		{
			bool vendorUsage = HasVendorUsage(d, out var alt);
			bool bluetooth = IsBluetoothPath(d.DevicePath);
			string marker = vendorUsage ? (alt ? "VENDOR* " : "VENDOR  ") : "        ";

			Console.WriteLine(
				$"  {(bluetooth ? "BT " : "   ")}{marker}" +
				$"VID 0x{d.VendorID:X4} PID 0x{d.ProductID:X4} out={Len(d, true),3} in={Len(d, false),3}  {d.DevicePath}");

			// Any VID: the receiver does NOT reuse the Smart Infinite USB VID — observed on real
			// hardware as 0x056D:0xC077 with the exact wired interface layout (col05 = FF87:20).
			if (vendorUsage && !bluetooth)
				candidates.Add(d);
		}

		Console.WriteLine();
		Console.WriteLine("Non-Bluetooth vendor-usage candidates:");
		foreach (var c in candidates)
			Console.WriteLine($"  VID 0x{c.VendorID:X4} PID 0x{c.ProductID:X4} — {Annotate(c.VendorID, c.ProductID)}");

		// Stage 2/3 need a writable 64-byte channel; the FF88:21 companion collection is input-only
		// (out=0) and only exists as a marker, so it is listed above but not probed.
		var targets = candidates
			.Where(c => KnownWiredBoard(c.VendorID, c.ProductID) == null && Len(c, output: true) == 64)
			.ToList();
		if (targets.Count == 0)
		{
			Console.WriteLine();
			Console.WriteLine(">>> RESULT: no receiver candidate found (only known wired boards, or nothing).");
			Console.WriteLine(">>> Is the receiver plugged in and the keyboard switched to 2.4G?");
			return;
		}

		foreach (var device in targets)
		{
			Console.WriteLine();
			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"Candidate: VID 0x{device.VendorID:X4} PID 0x{device.ProductID:X4} — {Annotate(device.VendorID, device.ProductID)}");
			Console.WriteLine($"  {device.DevicePath}");
			Console.WriteLine(new string('=', 78));

			if (!device.TryOpen(out var stream))
			{
				Console.WriteLine("  Could not open the interface. Is ProoveGamingDeviceHost stopped?");
				continue;
			}

			using (stream)
			{
				stream.ReadTimeout = 1500;
				stream.WriteTimeout = 1500;

				QueryReceiverState(stream);

				var block = ReadFuncBlock(stream, "56-byte (wired) chunking", 56)
					?? ReadFuncBlock(stream, "24-byte (C12 2.4G) chunking", Chunk24);

				if (block == null)
				{
					Console.WriteLine("  0x14 read failed with both chunkings — the keyboard channel does not answer here.");
					continue;
				}

				if (allowWrites)
					await WriteTestAsync(device, stream);
				else
					Console.WriteLine("\n  Stage 3 skipped — re-run with `rampage-24g write` to attempt the 0x15 RGB write.");
			}
		}
	}

	// ── keys arm — key matrix over the receiver ──────────────────────────────

	// Q's slot in the Rampage factory table; the write test remaps it to W and restores it.
	private const byte TestSlot = 33;

	/// <summary>
	/// K1: read the key matrix (0x17) through the receiver, wired 56-byte shape first, then the
	/// C12 24-byte chunking; validate alignment via the plausibility gate and the FN canary
	/// (slot 90 = 60-01-00). Also reads 0x16 for comparison and the first macro-region packet.
	/// K2 (allowWrites): remap slot 33 via the production RampageKeyboardKeyMappingService,
	/// verify, then restore the original record whatever the outcome; if the production shape is
	/// discarded, retry the whole read-modify-write with 24-byte chunks.
	/// </summary>
	public static async Task RunKeysAsync(bool allowWrites)
	{
		Console.WriteLine("Rampage 2.4G receiver — key matrix probe");
		Console.WriteLine("Expected state: keyboard on 2.4G (receiver plugged), cable unplugged, DeviceHost stopped.");
		Console.WriteLine();

		var targets = DeviceList.Local.GetHidDevices()
			.Where(d => !IsBluetoothPath(d.DevicePath) &&
				KnownWiredBoard(d.VendorID, d.ProductID) == null &&
				HasVendorUsage(d, out _) &&
				Len(d, output: true) == 64)
			.OrderBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (targets.Count == 0)
		{
			Console.WriteLine(">>> No receiver vendor interface found. Is the keyboard on 2.4G and the DeviceHost stopped?");
			return;
		}

		foreach (var device in targets)
		{
			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"Target: VID 0x{device.VendorID:X4} PID 0x{device.ProductID:X4} — {Annotate(device.VendorID, device.ProductID)}");
			Console.WriteLine($"  {device.DevicePath}");
			Console.WriteLine(new string('=', 78));

			byte[]? current;
			int workingPayload;

			if (!device.TryOpen(out var stream))
			{
				Console.WriteLine("  Could not open the interface.");
				continue;
			}

			using (stream)
			{
				stream.ReadTimeout = 1500;
				stream.WriteTimeout = 1500;

				(current, workingPayload) = ReadKeyTableBothShapes(stream, RampageProtocol.CmdReadKeyInfo, "current (0x17)");
				if (current == null)
					continue;

				var (factory, _) = ReadKeyTableBothShapes(stream, RampageProtocol.CmdReadDefaultKeyInfo, "factory (0x16)", workingPayload);
				if (factory != null)
					ReportTableDiff(current, factory);

				ReadMacroRegionHead(stream);
			}

			if (!allowWrites)
			{
				Console.WriteLine("\n  K2 skipped — re-run with `rampage-24g keys write` to attempt the slot write.");
				continue;
			}

			await KeyWriteTestAsync(device, current);
		}
	}

	// Tries the wired shape first (or only the shape that already worked, when told), reporting
	// which one the receiver answers.
	private static (byte[]? Table, int Payload) ReadKeyTableBothShapes(
		HidStream stream, byte cmd, string label, int? knownPayload = null)
	{
		int[] payloads = knownPayload is { } p ? [p] : [56, Chunk24];
		foreach (var payload in payloads)
		{
			Console.WriteLine();
			Console.WriteLine($"  Key table {label} — {payload}-byte chunking");
			var table = ReadKeyTable(stream, cmd, payload);
			if (table == null)
			{
				Console.WriteLine("    read FAILED with this chunking");
				continue;
			}

			bool plausible = RampageProtocol.IsPlausibleKeyTable(table, out var validSlots);
			var fn = RampageProtocol.ReadSlot(table, 90);
			Console.WriteLine($"    plausibility: {validSlots}/{RampageProtocol.KeyCount} known TYPE bytes; FN slot 90 = {fn}");
			if (plausible && fn.Type == 0x60 && fn.CodeLow == 0x01)
			{
				Console.WriteLine($"    >>> READ WORKS with {payload}-byte chunking (aligned, FN canary OK).");
				return (table, payload);
			}
			Console.WriteLine("    table read but MISALIGNED — trying the other chunking");
		}
		Console.WriteLine("    >>> table unreadable over this transport with either chunking.");
		return (null, 0);
	}

	// Reads the 288 meaningful bytes (96 keys x 3) in full-`maxPayload` packets, address-matched
	// like the production service. The 384-byte buffer's reserve stays zero.
	private static byte[]? ReadKeyTable(HidStream stream, byte cmd, int maxPayload)
	{
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		int packets = (RampageProtocol.KeyDataSize + maxPayload - 1) / maxPayload;
		var filled = new bool[packets];
		int remaining = packets;

		for (int pass = 0; pass < 2 && remaining > 0; pass++)
		{
			for (int i = 0; i < packets; i++)
			{
				if (filled[i]) continue;
				var addr = (ushort)(i * maxPayload);
				try
				{
					stream.Write(RampageProtocol.BuildReadCommand(cmd, addr, (byte)maxPayload));
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    write of read request failed at addr {addr}: {ex.Message}");
					return null;
				}

				var reply = ReplyFor(stream, cmd, addr);
				if (reply != null)
				{
					int len = Math.Min(maxPayload, table.Length - addr);
					Buffer.BlockCopy(reply, RampageProtocol.ResponsePayloadOffset, table, addr, len);
					filled[i] = true;
					remaining--;
				}
				Thread.Sleep(4);
			}
		}

		return remaining == 0 ? table : null;
	}

	private static void ReportTableDiff(byte[] current, byte[] factory)
	{
		int diffs = 0;
		for (int slot = 0; slot < RampageProtocol.KeyCount; slot++)
		{
			if (RampageProtocol.ReadSlot(current, slot) != RampageProtocol.ReadSlot(factory, slot))
				diffs++;
		}
		Console.WriteLine($"    current vs factory: {(diffs == 0 ? "identical (nothing remapped on this unit)" : $"{diffs} slot(s) differ")}");
	}

	// One packet of the macro region (0x1B) — enough to see whether the region answers over 2.4G
	// and whether its AA 55 usage marker is present. Read-only.
	private static void ReadMacroRegionHead(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine("  Macro region head (0x1B, first 56 bytes)");
		try
		{
			stream.Write(RampageProtocol.BuildReadCommand(SmartInfiniteProtocol.CmdReadMacroInfo, 0, 56));
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    request write failed: {ex.Message}");
			return;
		}

		var reply = ReplyFor(stream, SmartInfiniteProtocol.CmdReadMacroInfo, 0);
		if (reply == null)
		{
			Console.WriteLine("    no reply — macro region does not answer over 2.4G (record for the macro follow-up).");
			return;
		}

		var head = reply.AsSpan(RampageProtocol.ResponsePayloadOffset, 16).ToArray();
		Console.WriteLine("    raw: " + string.Join("-", head.Select(x => x.ToString("X2"))));
		Console.WriteLine(head[0] == 0xAA && head[1] == 0x55
			? "    >>> macro region ANSWERS and carries the AA 55 usage marker."
			: "    >>> macro region answers (marker absent = no macros stored; still a valid read).");
	}

	private static async Task KeyWriteTestAsync(HidDevice device, byte[] current)
	{
		var original = RampageProtocol.ReadSlot(current, TestSlot);
		var wanted = new KeyMatrixRecord(0x10, 0x00, 0x1A); // W

		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine($"  K2 — slot {TestSlot} {original} -> {wanted} via the production key-mapping service");
		Console.WriteLine(new string('-', 78));

		// Both framings go through the REAL service — the second is exactly what the DeviceHost
		// runs for a Dongle2_4G-matched row, complete with its retry, gate and read-back verify.
		var wired = new RampageKeyboardKeyMappingService(device);
		var receiver = new RampageKeyboardKeyMappingService(device, null, RampageProtocol.Receiver24GPayloadLength);

		var wrote56 = await wired.SetKeyMappingAsync(TestSlot, wanted.Type, wanted.CodeLow, wanted.CodeHigh);
		Console.WriteLine($"    production write, wired 56-byte chunking: {(wrote56 ? "PERSISTED" : "did NOT persist")}");

		bool wrote24 = false;
		if (!wrote56)
		{
			wrote24 = await receiver.SetKeyMappingAsync(TestSlot, wanted.Type, wanted.CodeLow, wanted.CodeHigh);
			Console.WriteLine($"    production write, {Chunk24}-byte chunking: {(wrote24 ? "PERSISTED" : "did NOT persist either")}");
		}
		if (wrote56 || wrote24)
			Console.WriteLine("    -> if you are at the keyboard: the physical Q key should now type 'w'.");
		Thread.Sleep(1500);

		Console.WriteLine();
		Console.WriteLine("    restoring the original record ...");
		var restoreService = wrote24 ? receiver : wired;
		var restored = await restoreService.SetKeyMappingAsync(TestSlot, original.Type, original.CodeLow, original.CodeHigh);
		if (!restored)
			restored = await (wrote24 ? wired : receiver).SetKeyMappingAsync(TestSlot, original.Type, original.CodeLow, original.CodeHigh);
		Console.WriteLine(restored
			? $"    slot {TestSlot} restored to {original} and verified."
			: $"    RESTORE DID NOT CONFIRM — check the keyboard; slot {TestSlot} should be {original}.");
	}

	// ── stage 2a: receiver state (0xAA channel) ──────────────────────────────

	// The sheet's exact frame — note bytes 5-6 (checksum position on the keyboard channel) are
	// zero in the vendor's own example: the receiver channel does not use the additive checksum.
	private static void QueryReceiverState(HidStream stream)
	{
		Console.WriteLine();
		Console.WriteLine("  Receiver state query (AA D0 00 00 18 00 00 00)");

		var frame = new byte[RampageProtocol.ReportLength];
		frame[0] = ReceiverMagic;
		frame[1] = ReceiverStateQuery;
		frame[4] = 0x18;

		try
		{
			stream.Write(frame);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"    query write failed: {ex.Message}");
			return;
		}

		for (int attempt = 0; attempt < 4; attempt++)
		{
			byte[] reply;
			try
			{
				reply = new byte[RampageProtocol.ReportLength];
				if (stream.Read(reply) < 15) continue;
			}
			catch (TimeoutException)
			{
				Console.WriteLine("    no reply (timeout) — receiver channel silent");
				return;
			}

			if (reply[0] != ReceiverMagic || reply[1] != ReceiverStateQuery) continue;

			Console.WriteLine("    raw: " + string.Join("-", reply.Take(20).Select(x => x.ToString("X2"))));
			Console.WriteLine($"    bonded devices : {reply[9]}");
			Console.WriteLine($"    bonded VID     : 0x{(reply[11] << 8) | reply[10]:X4}");
			Console.WriteLine($"    bonded PID     : 0x{(reply[13] << 8) | reply[12]:X4}");
			Console.WriteLine($"    link state     : {reply[14]} " +
				"(0 invalid, 1 idle, 2 reconnecting, 3 pairing, 4 connected, 5 sleeping)");
			return;
		}
		Console.WriteLine("    no matching reply in 4 reads");
	}

	// ── stage 2b: 0x14 read with a given chunking ────────────────────────────

	private static byte[]? ReadFuncBlock(HidStream stream, string label, int chunkSize)
	{
		Console.WriteLine();
		Console.WriteLine($"  Function/config block (0x14) — {label}");

		var block = new byte[64];
		for (int off = 0; off < block.Length; off += chunkSize)
		{
			int len = Math.Min(chunkSize, block.Length - off);
			try
			{
				stream.Write(RampageProtocol.BuildReadCommand(RampageProtocol.CmdReadFuncInfo, (ushort)off, (byte)len));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"    write of the read request failed at addr {off}: {ex.Message}");
				return null;
			}

			var reply = ReplyFor(stream, RampageProtocol.CmdReadFuncInfo, (ushort)off);
			if (reply == null)
			{
				Console.WriteLine($"    no valid reply for addr {off}");
				return null;
			}
			Buffer.BlockCopy(reply, RampageProtocol.ResponsePayloadOffset, block, off, len);
			Thread.Sleep(4);
		}

		Console.WriteLine("    raw: " + string.Join("-", block.Take(24).Select(x => x.ToString("X2"))));
		Console.WriteLine($"    decoded: mode={block[1]} level={block[2]} speed={block[3]} RGB={block[5]} " +
			$"colour=#{block[6]:X2}{block[7]:X2}{block[8]:X2} colourIndex={block[9]}");
		Console.WriteLine("    >>> 0x14 READ WORKS with this chunking.");
		return block;
	}

	// ── stage 3: 0x15 write, production framing then C12 chunking ────────────

	private static async Task WriteTestAsync(HidDevice device, HidStream stage2Stream)
	{
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine("  WRITE TEST 1 — production RampageKeyboardRgbService (56+8 framing, bracketed)");
		Console.WriteLine(new string('-', 78));

		// The production service opens its own stream; release stage 2's handle first.
		stage2Stream.Dispose();

		var service = new RampageKeyboardRgbService(device);
		Console.WriteLine("    sending: Static RED, full brightness ...");
		var redOk = await service.SetLedColorAsync(0xFF, 0x00, 0x00, effect: 0x04, brightness: 0xFF, speed: 3);
		Console.WriteLine($"    write sequence reported: {(redOk ? "OK" : "FAILED")}   -> LOOK AT THE KEYBOARD");
		Thread.Sleep(2500);

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("    could not reopen the interface for the read-back / second framing");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = 1500;
			stream.WriteTimeout = 1500;

			var after = ReadFuncBlock(stream, "read-back after RED", 56)
				?? ReadFuncBlock(stream, "read-back after RED (24-byte chunking)", Chunk24);
			bool redStuck = after != null && after[1] == 10 && after[6] == 0xFF && after[7] == 0x00;
			Console.WriteLine(redStuck
				? "    >>> RED PERSISTED — the wired framing works over 2.4G as-is."
				: "    >>> red did not persist with the production framing.");

			Console.WriteLine();
			Console.WriteLine(new string('-', 78));
			Console.WriteLine("  WRITE TEST 2 — bare 0x15, C12 24-byte chunking, no session bracket");
			Console.WriteLine(new string('-', 78));

			var feature = BuildFeatureArea(r: 0x00, g: 0xFF, b: 0x00);
			bool acked = true;
			for (int off = 0; off < feature.Length && acked; off += Chunk24)
			{
				int len = Math.Min(Chunk24, feature.Length - off);
				try
				{
					stream.Write(RampageProtocol.BuildCommand(CmdSetGeneralInfo, (ushort)off, feature.AsSpan(off, len)));
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    write failed at addr {off}: {ex.Message}");
					acked = false;
					break;
				}
				var ack = ReplyFor(stream, CmdSetGeneralInfo, (ushort)off);
				Console.WriteLine($"    packet addr={off} len={len}: {(ack != null ? "acked 0x55" : "NO ACK")}");
				Thread.Sleep(6);
			}

			if (acked)
			{
				Console.WriteLine("    sending Sync Lighting (0x1D) ...");
				try { stream.Write(RampageProtocol.BuildCommand(CmdSyncLighting)); } catch (Exception ex) { Console.WriteLine($"    0x1D failed: {ex.Message}"); }
				Thread.Sleep(300);

				var after2 = ReadFuncBlock(stream, "read-back after GREEN", Chunk24)
					?? ReadFuncBlock(stream, "read-back after GREEN (56-byte chunking)", 56);
				bool greenStuck = after2 != null && after2[1] == 10 && after2[6] == 0x00 && after2[7] == 0xFF;
				Console.WriteLine(greenStuck
					? "    >>> GREEN PERSISTED — 2.4G needs the C12 24-byte chunking; note it for the service."
					: "    >>> green did not persist either — record both results in the findings doc.");
			}
		}
	}

	// Same 64-byte feature area BuildFeatureArea in RampageKeyboardRgbService produces for
	// Static (mode 10, monochrome), full brightness — kept in step manually; this is probe code.
	private static byte[] BuildFeatureArea(byte r, byte g, byte b)
	{
		var block = new byte[64];
		block[1] = 10;   // light_mode = Steady On
		block[2] = 99;   // light_level (0-99)
		block[3] = 2;    // light_speed
		block[5] = 0;    // monochrome
		block[6] = r;
		block[7] = g;
		block[8] = b;
		block[9] = 7;    // colour index: White (ignored when RGB=0)
		for (int i = 15; i <= 24; i++) block[i] = 0xFF; // OFF_LED / SLEEP timers pinned
		return block;
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	private static byte[]? ReplyFor(HidStream stream, byte cmd, ushort addr)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			var reply = new byte[RampageProtocol.ReportLength];
			try
			{
				if (stream.Read(reply) < RampageProtocol.HeaderLength) continue;
			}
			catch (TimeoutException) { return null; }
			catch (Exception ex)
			{
				Console.WriteLine($"    read failed: {ex.Message}");
				return null;
			}

			int replyAddr = reply[2] | (reply[3] << 8);
			if (reply[1] == cmd && replyAddr == addr &&
				reply[RampageProtocol.ResponseStatusOffset] == RampageProtocol.ResponseSuccess)
				return reply;
		}
		return null;
	}

	private static bool IsBluetoothPath(string? path) =>
		path != null &&
		(path.Contains("bthenum", StringComparison.OrdinalIgnoreCase) ||
		 path.Contains("bthle", StringComparison.OrdinalIgnoreCase) ||
		 path.Contains("{00001812-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase));

	private static bool HasVendorUsage(HidDevice d, out bool viaAltPair)
	{
		viaAltPair = false;
		try
		{
			var usages = d.GetReportDescriptor().DeviceItems
				.SelectMany(i => i.Usages.GetAllValues())
				.ToList();
			if (usages.Any(u => (u >> 16) == VendorUsagePage && (u & 0xFFFF) == VendorUsageId))
				return true;
			viaAltPair = usages.Any(u => (u >> 16) == VendorUsagePageAlt && (u & 0xFFFF) == VendorUsageIdAlt);
			return viaAltPair;
		}
		catch { return false; }
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return -1; }
	}
}
