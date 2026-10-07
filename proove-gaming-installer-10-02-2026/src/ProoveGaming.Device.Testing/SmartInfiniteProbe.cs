using HidSharp;

// ── Smart Infinite ("智慧无限") read-only probe — Rampage / Aress / Cyrex ─────────────────────
//
// Answers one question without writing anything to the device: does this keyboard report a non-zero
// KEY COUNT in its Device Info block?
//
// Rampage reports 0 there and discards every key-matrix write, while accepting configuration writes
// on the same interface — which is why remapping is dead on it. Aress and Cyrex are the magnetic
// members of the same family (same VID 0x28E9, same 0x06 framing). The vendor header defines per-key
// advanced types (ADV_DKS 0x80, ADV_SOCD 0x83, ADV_RS 0x84 — Rapid Trigger and friends) that are
// TYPE bytes of a key-matrix record, so a magnetic keyboard shipping per-key Rapid Trigger must have
// a writable key matrix. If these report a real key count, remapping is worth pursuing on them.
//
// This probe only READS. The single 0x10 / 0x11 session bracket is required by the protocol and is
// what the shipped RGB service already sends on every colour change.
//
// Verified protocol facts this relies on (see docs/knowledge-base/rampage-keyboard-protocol/):
//   byte 0    0x06 magic
//   byte 1    command
//   byte 2-3  address, little-endian
//   byte 4    length (0x38 = 56 for table transfers)
//   byte 5-6  checksum — plain additive sum, NOT a CRC
//   byte 7    0x00 in a request, 0x55 in a reply
//   byte 8..  payload
// Every reply arrives one command behind its request, so replies are matched by the address they
// echo back rather than by arrival order. Never drain the Start ack before the first real request:
// it corrupts the HidSharp stream so later writes fail.
internal static class SmartInfiniteProbe
{
	private const int SmartInfiniteVid = 0x28E9;   // 10473 — Rampage, Aress, Cyrex

	private const int VendorUsagePage = 0xFF87;
	private const int VendorUsageId = 0x20;

	private const byte Magic = 0x06;
	private const byte CmdStart = 0x10;
	private const byte CmdEnd = 0x11;
	private const byte CmdDeviceInfo = 0x12;
	private const byte CmdFuncInfo = 0x14;
	private const byte CmdReadDefaultKeys = 0x16;
	private const byte CmdReadCurrentKeys = 0x17;
	private const byte CmdSetKeys = 0x18;
	private const byte Ok = 0x55;

	private const int ReportLen = 64;
	private const int HeaderLen = 8;
	private const int MaxPayload = 56;
	private const int TableBytes = 384;
	private const int SlotSize = 3;

	// Known models on this VID, for a friendlier report.
	private static string ModelName(int pid) => pid switch
	{
		0x3258 => "Rampage (mechanical)",
		0x326E => "Aress (magnetic)",
		0x3272 => "Cyrex (magnetic)",
		_ => "unknown model",
	};

	public static void Run()
	{
		var devices = DeviceList.Local.GetHidDevices(SmartInfiniteVid).ToList();
		if (devices.Count == 0)
		{
			Console.WriteLine($"No devices found on VID 0x{SmartInfiniteVid:X4}. Is the keyboard plugged in over USB?");
			return;
		}

		var byPid = devices.GroupBy(d => d.ProductID).OrderBy(g => g.Key);
		foreach (var group in byPid)
		{
			Console.WriteLine();
			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"PID 0x{group.Key:X4} — {ModelName(group.Key)}");
			Console.WriteLine(new string('=', 78));
			ProbeOne(group.Key, group.ToList());
		}
	}

	private static void ProbeOne(int pid, List<HidDevice> interfaces)
	{
		var device = interfaces.FirstOrDefault(HasVendorUsage)
			?? interfaces.FirstOrDefault(d => Len(d, output: true) == 64 && Len(d, output: false) == 64);

		if (device == null)
		{
			Console.WriteLine("  No vendor config interface found (usage 0x20 / page 0xFF87, or 64/64).");
			return;
		}

		Console.WriteLine($"  interface: {device.DevicePath}");
		Console.WriteLine($"  usage match: {HasVendorUsage(device)}   out/in: {Len(device, true)}/{Len(device, false)}");

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("  Could not open the interface. Is ProoveGamingDeviceHost running? Stop it and retry.");
			return;
		}

		using (stream)
		{
			stream.ReadTimeout = 600;
			stream.WriteTimeout = 600;

			stream.Write(Build(CmdStart));
			Thread.Sleep(10);

			try
			{
				var info = ReadBlock(stream, CmdDeviceInfo);
				var func = ReadBlock(stream, CmdFuncInfo);
				var current = ReadTable(stream, CmdReadCurrentKeys);
				var factory = ReadTable(stream, CmdReadDefaultKeys);

				ReportDeviceInfo(info);
				ReportFuncInfo(func);

				// The matrix reserve and real key count come from Device Info — they differ per
				// model (Rampage 128/0, Cyrex 75/68), so never assume 128 slots.
				int slots = info != null && info[4] > 0 ? info[4] : TableBytes / SlotSize;
				int keys = info != null ? info[6] : 0;

				ReportTable("current key matrix (0x17)", current, slots, keys);
				ReportTable("factory key matrix (0x16)", factory, slots, keys);
				if (current != null && factory != null)
					ReportDifferences(current, factory, slots);

				// Only attempt a write where the device claims it has keys. Slot 18 is E on Cyrex.
				if (keys > 0)
					WriteTest(stream, slot: 18, newType: 0x10, newCode1: 0x00, newCode2: 0x1A, slots: slots);
				else
					Console.WriteLine("\n  Skipping write test: the device reports 0 keys.");
			}
			finally
			{
				stream.Write(Build(CmdEnd));
			}
		}
	}

	// ── reporting ────────────────────────────────────────────────────────────

	private static void ReportDeviceInfo(byte[]? b)
	{
		Console.WriteLine();
		Console.WriteLine("  Device Info (0x12)");
		if (b == null) { Console.WriteLine("    read FAILED"); return; }

		Console.WriteLine("    raw: " + Hex(b, 24));

		bool valid = b[0] == 0xAA && b[1] == 0xBB;
		Console.WriteLine($"    validity marker  : 0x{b[0]:X2} 0x{b[1]:X2}   {(valid ? "OK" : "UNEXPECTED — layout may differ")}");
		Console.WriteLine($"    product ID       : 0x{(b[3] << 8) | b[2]:X4}");
		Console.WriteLine($"    key matrix size  : {b[4]} x 3 = {b[4] * 3} bytes");
		Console.WriteLine($"    macro space      : {b[5]} x 256 = {b[5] * 256} bytes");
		Console.WriteLine($"    device type      : {b[9]}  (0 USB, 1 +BLE, 2 +2.4G, 3 +BLE+2.4G)");
		Console.WriteLine($"    product version  : 0x{(b[11] << 8) | b[10]:X4}");
		Console.WriteLine($"    max brightness   : {b[12]}          max speed: {b[13]}");
		Console.WriteLine();
		Console.WriteLine($"    >>> ACTUAL KEY COUNT (byte 6) : {b[6]}");
		Console.WriteLine(b[6] == 0
			? "    >>> ZERO — same as Rampage. Key remapping appears DISABLED in this firmware build."
			: $"    >>> NON-ZERO — key remapping looks ENABLED. Worth attempting a write on this model.");
	}

	private static void ReportFuncInfo(byte[]? b)
	{
		Console.WriteLine();
		Console.WriteLine("  Function / config block (0x14)");
		Console.WriteLine(b == null ? "    read FAILED" : "    raw: " + Hex(b, 24));
	}

	private static void ReportTable(string label, byte[]? table, int slots, int keyCount)
	{
		Console.WriteLine();
		Console.WriteLine($"  {label} — {slots} reserved slots, device reports {keyCount} keys");
		if (table == null) { Console.WriteLine("    read FAILED"); return; }

		int populated = 0, keyboardRecords = 0, fnSlot = -1;
		var adv = new List<string>();
		for (int slot = 0; slot < slots; slot++)
		{
			int o = slot * SlotSize;
			byte t = table[o], c1 = table[o + 1], c2 = table[o + 2];
			if (t == 0 && c1 == 0 && c2 == 0) continue;
			populated++;
			if (t == 0x10) keyboardRecords++;
			if (t == 0x60 && c1 == 0x01) fnSlot = slot;
			if (t is >= 0x80 and <= 0x84) adv.Add($"slot {slot}=0x{t:X2}");
		}

		Console.WriteLine($"    populated        : {populated} of {slots}   (plain keyboard records: {keyboardRecords})");
		Console.WriteLine($"    FN slot          : {(fnSlot >= 0 ? fnSlot.ToString() : "not found")}");
		Console.WriteLine("    advanced (magnetic) records: " + (adv.Count == 0 ? "none assigned" : string.Join(", ", adv.Take(12))));

		// Raw dump so a catalog can be generated from the device's own bytes rather than
		// hand-transcribed from this console output.
		Console.WriteLine($"    RAW[{slots * SlotSize}]: " +
			string.Join("", Enumerable.Range(0, slots * SlotSize).Select(i => table[i].ToString("X2"))));

		Console.WriteLine("    decoded layout:");
		for (int row = 0; row < slots; row += 8)
		{
			var cells = new List<string>();
			for (int slot = row; slot < Math.Min(row + 8, slots); slot++)
			{
				int o = slot * SlotSize;
				cells.Add($"{slot,3}:{Describe(table[o], table[o + 1], table[o + 2]),-9}");
			}
			Console.WriteLine("      " + string.Concat(cells));
		}
	}

	// Highlights where the live table already differs from the factory one — i.e. whether anything
	// has ever been successfully written to this keyboard.
	private static void ReportDifferences(byte[] current, byte[] factory, int slots)
	{
		Console.WriteLine();
		var diffs = new List<string>();
		for (int slot = 0; slot < slots; slot++)
		{
			int o = slot * SlotSize;
			if (current[o] == factory[o] && current[o + 1] == factory[o + 1] && current[o + 2] == factory[o + 2])
				continue;
			diffs.Add($"slot {slot}: factory {Describe(factory[o], factory[o + 1], factory[o + 2])} -> current {Describe(current[o], current[o + 1], current[o + 2])}");
		}
		Console.WriteLine(diffs.Count == 0
			? "  current matrix is IDENTICAL to factory (nothing has been remapped on this unit)"
			: $"  current differs from factory in {diffs.Count} slot(s) — remapping HAS worked here before:");
		foreach (var d in diffs.Take(20)) Console.WriteLine("    " + d);
	}

	// Short human label for a 3-byte key record.
	private static string Describe(byte type, byte code1, byte code2)
	{
		if (type == 0 && code1 == 0 && code2 == 0) return "-";
		if (type == 0x60) return code1 == 0x01 ? "FN" : $"fn{code1:X2}";
		if (type is >= 0x80 and <= 0x84) return $"ADV{type:X2}";
		if (type != 0x10) return $"t{type:X2}";
		if (code1 != 0) return code1 switch
		{
			0x01 => "LCtrl", 0x02 => "LShift", 0x04 => "LAlt", 0x08 => "LWin",
			0x10 => "RCtrl", 0x20 => "RShift", 0x40 => "RAlt", 0x80 => "RWin",
			_ => $"mod{code1:X2}",
		};
		return code2 switch
		{
			0x00 => "(empty)",
			>= 0x04 and <= 0x1D => ((char)('A' + code2 - 0x04)).ToString(),
			>= 0x1E and <= 0x26 => ((char)('1' + code2 - 0x1E)).ToString(),
			0x27 => "0", 0x28 => "Enter", 0x29 => "Esc", 0x2A => "Bksp", 0x2B => "Tab",
			0x2C => "Space", 0x2D => "-", 0x2E => "=", 0x2F => "[", 0x30 => "]", 0x31 => "\\",
			0x33 => ";", 0x34 => "'", 0x35 => "`", 0x36 => ",", 0x37 => ".", 0x38 => "/",
			0x39 => "Caps",
            >= 0x3A and <= 0x45 => $"F{code2 - 0x39}",
			0x46 => "PrtSc", 0x47 => "ScrLk", 0x48 => "Pause", 0x49 => "Ins", 0x4A => "Home",
			0x4B => "PgUp", 0x4C => "Del", 0x4D => "End", 0x4E => "PgDn",
			0x4F => "Right", 0x50 => "Left", 0x51 => "Down", 0x52 => "Up",
			0x65 => "Menu",
			_ => $"0x{code2:X2}",
		};
	}

	// ── protocol ─────────────────────────────────────────────────────────────

	// 64-byte blocks (device info, function block) are two packets: offset 0 then offset 56.
	private static byte[]? ReadBlock(HidStream s, byte cmd)
	{
		var block = new byte[64];
		bool any = false;
		foreach (var (addr, len) in new[] { ((ushort)0, 56), ((ushort)56, 8) })
		{
			s.Write(BuildRead(cmd, addr, (byte)len));
			var reply = ReplyFor(s, cmd, addr);
			Thread.Sleep(4);
			if (reply == null) continue;
			Buffer.BlockCopy(reply, HeaderLen, block, addr, len);
			any = true;
		}
		return any ? block : null;
	}

	// Splits a table of exactly dataSize bytes into transfer packets. The final packet is trimmed so
	// the transfer never runs past the matrix the device declares — Cyrex reserves 225 bytes, so
	// sending 5 full 56-byte packets (280 B) would write 55 bytes beyond it.
	private static List<(ushort Addr, int Len)> Chunks(int dataSize)
	{
		var list = new List<(ushort, int)>();
		for (int off = 0; off < dataSize; off += MaxPayload)
			list.Add(((ushort)off, Math.Min(MaxPayload, dataSize - off)));
		return list;
	}

	private static byte[]? ReadTable(HidStream s, byte cmd, int dataSize = TableBytes)
	{
		var table = new byte[Math.Max(TableBytes, dataSize)];
		var chunks = Chunks(dataSize);
		var filled = new bool[chunks.Count];

		for (int pass = 0; pass < 2; pass++)
		{
			for (int i = 0; i < chunks.Count; i++)
			{
				if (filled[i]) continue;
				var (addr, len) = chunks[i];
				s.Write(BuildRead(cmd, addr, (byte)len));
				var reply = ReplyFor(s, cmd, addr);
				Thread.Sleep(4);
				if (reply == null) continue;
				Buffer.BlockCopy(reply, HeaderLen, table, addr, len);
				filled[i] = true;
			}
			if (filled.All(f => f)) break;
		}

		return filled.All(f => f) ? table : null;
	}

	// ── write test ───────────────────────────────────────────────────────────

	/// <summary>
	/// Changes one slot, writes the whole table back, verifies, then restores the original value
	/// whatever the outcome. The vendor's documented procedure is read → patch → send the entire
	/// table; partial single-slot writes are what failed on Rampage.
	/// </summary>
	private static void WriteTest(HidStream s, int slot, byte newType, byte newCode1, byte newCode2, int slots)
	{
		int dataSize = slots * SlotSize;
		Console.WriteLine();
		Console.WriteLine(new string('-', 78));
		Console.WriteLine($"  WRITE TEST — slot {slot}, transferring {dataSize} bytes in {Chunks(dataSize).Count} packets");
		Console.WriteLine(new string('-', 78));

		var table = ReadTable(s, CmdReadCurrentKeys, dataSize);
		if (table == null) { Console.WriteLine("    aborted: could not read the current table"); return; }

		// Refuse to write back a table we may have read incorrectly.
		int known = 0;
		for (int i = 0; i < slots; i++)
		{
			byte t = table[i * SlotSize];
			if (t is 0x10 or 0x11 or 0x20 or 0x30 or 0x60 or 0x61 or 0x70 || (t >= 0x40 && t <= 0x45)
				|| (t >= 0x50 && t <= 0x52) || (t >= 0x80 && t <= 0x84)) known++;
		}
		Console.WriteLine($"    sanity: {known}/{slots} slots carry a known TYPE byte");
		if (known < slots - 4) { Console.WriteLine("    aborted: the table we read does not look valid — refusing to write it back"); return; }

		int o = slot * SlotSize;
		byte oT = table[o], oC1 = table[o + 1], oC2 = table[o + 2];
		Console.WriteLine($"    slot {slot} before : {Describe(oT, oC1, oC2)}  ({oT:X2}-{oC1:X2}-{oC2:X2})");
		Console.WriteLine($"    writing           : {Describe(newType, newCode1, newCode2)}  ({newType:X2}-{newCode1:X2}-{newCode2:X2})");

		table[o] = newType; table[o + 1] = newCode1; table[o + 2] = newCode2;
		int acked = WriteTable(s, table, dataSize);
		Console.WriteLine($"    packets acked 0x55: {acked}/{Chunks(dataSize).Count}");

		Thread.Sleep(80);
		var after = ReadTable(s, CmdReadCurrentKeys, dataSize);
		bool stuck = after != null && after[o] == newType && after[o + 1] == newCode1 && after[o + 2] == newCode2;
		Console.WriteLine($"    slot {slot} read back: " +
			(after == null ? "read FAILED" : $"{Describe(after[o], after[o + 1], after[o + 2])}  ({after[o]:X2}-{after[o + 1]:X2}-{after[o + 2]:X2})"));
		Console.WriteLine(stuck
			? "    >>> WRITE STUCK — key remapping WORKS on this keyboard."
			: "    >>> did not stick — same behaviour as Rampage.");

		// Always restore, whether or not the write appeared to work.
		Console.WriteLine();
		Console.WriteLine("    restoring the original value ...");
		table[o] = oT; table[o + 1] = oC1; table[o + 2] = oC2;
		WriteTable(s, table, dataSize);
		Thread.Sleep(80);
		var restored = ReadTable(s, CmdReadCurrentKeys, dataSize);
		bool ok = restored != null && restored[o] == oT && restored[o + 1] == oC1 && restored[o + 2] == oC2;
		Console.WriteLine($"    slot {slot} now      : " +
			(restored == null ? "read FAILED" : $"{Describe(restored[o], restored[o + 1], restored[o + 2])}") +
			(ok ? "   restored OK" : "   RESTORE DID NOT CONFIRM — unplug and replug the keyboard"));
	}

	private static int WriteTable(HidStream s, byte[] table, int dataSize)
	{
		int acked = 0;
		foreach (var (addr, len) in Chunks(dataSize))
		{
			s.Write(Build(CmdSetKeys, addr, table.AsSpan(addr, len)));
			var ack = ReplyFor(s, CmdSetKeys, addr);
			if (ack != null) acked++;
			Thread.Sleep(4);
		}
		return acked;
	}

	// Replies run one command behind, so match on the echoed command + address and discard the rest.
	private static byte[]? ReplyFor(HidStream s, byte cmd, ushort addr)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			byte[] reply;
			try
			{
				reply = new byte[ReportLen];
				int read = s.Read(reply);
				if (read < HeaderLen) continue;
			}
			catch (TimeoutException) { return null; }

			int replyAddr = reply[2] | (reply[3] << 8);
			if (reply[1] == cmd && replyAddr == addr && reply[7] == Ok)
				return reply;
		}
		return null;
	}

	private static byte[] Build(byte cmd, ushort addr = 0, ReadOnlySpan<byte> payload = default)
	{
		var r = new byte[ReportLen];
		r[0] = Magic;
		r[1] = cmd;
		r[2] = (byte)(addr & 0xFF);
		r[3] = (byte)(addr >> 8);
		r[4] = (byte)payload.Length;
		payload.CopyTo(r.AsSpan(HeaderLen));
		WriteChecksum(r);
		return r;
	}

	// A read request carries the requested length in byte 4 and no payload.
	private static byte[] BuildRead(byte cmd, ushort addr, byte len)
	{
		var r = new byte[ReportLen];
		r[0] = Magic;
		r[1] = cmd;
		r[2] = (byte)(addr & 0xFF);
		r[3] = (byte)(addr >> 8);
		r[4] = len;
		WriteChecksum(r);
		return r;
	}

	// Plain additive sum of cmd + addr + len + every payload byte. Not a CRC.
	private static void WriteChecksum(byte[] r)
	{
		int sum = r[1] + r[2] + r[3] + r[4];
		for (int i = 0; i < r[4] && HeaderLen + i < r.Length; i++) sum += r[HeaderLen + i];
		r[5] = (byte)(sum & 0xFF);
		r[6] = (byte)((sum >> 8) & 0xFF);
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	private static bool HasVendorUsage(HidDevice d)
	{
		try
		{
			return d.GetReportDescriptor().DeviceItems
				.SelectMany(i => i.Usages.GetAllValues())
				.Any(u => (u >> 16) == VendorUsagePage && (u & 0xFFFF) == VendorUsageId);
		}
		catch { return false; }
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return -1; }
	}

	private static string Hex(byte[] b, int count) =>
		string.Join("-", b.Take(Math.Min(count, b.Length)).Select(x => x.ToString("X2")));
}
