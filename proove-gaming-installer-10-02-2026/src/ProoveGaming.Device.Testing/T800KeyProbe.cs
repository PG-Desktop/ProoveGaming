using HidSharp;

// T800 (Rongyuan RY5088) key-matrix reconnaissance — Phase 0 of
// docs/plans/add-t800-key-assignments/add-t800-key-assignments-plan.md.
//
// Answers the questions the plan gates all production code behind. Everything about this device's
// key matrix is undocumented except the command framing, and the failure mode is silent: a wrong
// record size or page geometry is discarded without an ack, and GetFeature then returns the
// driver's cached copy of our own request — which reads as a plausible reply. So every step here
// distinguishes "the firmware answered" from "we got our own bytes back".
//
// Transport is the same one the RGB work settled: HID FEATURE reports on the mi_02 vendor
// interface, report ID 0x00, 64 protocol bytes, CS = 0xFF - (sum & 0xFF).
//
// Read-only by default. The write test (R4) is opt-in via `keys write` and restores what it wrote.
public static class T800KeyProbe
{
	private const int VID = 0x3151;
	private const int PID = 0x502D;

	private const int ReportLen = 65;

	private const byte CmdSetKeyMatrix = 0x0A;
	private const byte CmdGetKeyMatrix = 0x8A;

	private const byte TypeWholeMatrix = 255;
	private const byte Profile0 = 0;
	private const byte Layer0 = 0;

	// A GET_KEYMATRIX reply is 64 bytes of raw matrix data with NO echoed header — the first record
	// starts at protocol byte 0. Measured, not assumed: page 0 comes back
	// `00 00 00 00 | 00 00 29 00 | 00 00 2B 00 | 00 00 39 00`, i.e. empty, Escape, Tab, CapsLock.
	// That also settles the page geometry the spec contradicts itself on — 8 pages of 64 bytes = 512.
	private const int PayloadOffset = 0;
	private const int PageBytes = 64;
	private const int MatrixPages = 8;

	public static void Run(bool includeWriteTest)
	{
		var control = DeviceList.Local.GetHidDevices(VID, PID)
			.FirstOrDefault(d => SafeFeatureLength(d) == ReportLen
				&& d.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true);

		if (control == null)
		{
			Console.WriteLine("mi_02 feature interface not found — is the T800 connected?");
			return;
		}

		Console.WriteLine($"control: {control.DevicePath}\n");

		// ── R0 / R2: does 0x8A answer, and how many pages are there? ──────────────────────────
		Console.WriteLine("### R0/R2 — GET_KEYMATRIX pages");
		var pages = new List<byte[]>();
		for (byte page = 0; page < MatrixPages; page++)
		{
			var request = BuildGet(page);
			var reply = Exchange(control, request);
			if (reply == null)
			{
				Console.WriteLine($"  page {page,2}: no reply");
				break;
			}

			bool echo = IsEcho(request, reply);
			var body = reply.Skip(1).Take(16).Select(x => x.ToString("X2"));
			Console.WriteLine($"  page {page,2}: {string.Join(' ', body)} {(echo ? "  <== ECHO (not parsed)" : "")}");

			if (echo) break;
			pages.Add(reply);
		}

		if (pages.Count == 0)
		{
			Console.WriteLine("\nNo page parsed. GET_KEYMATRIX is not answering under this framing —");
			Console.WriteLine("stop here and re-check the command layout before writing anything.");
			return;
		}

		// ── R1: record stride ─────────────────────────────────────────────────────────────────
		var matrix = Flatten(pages);
		Console.WriteLine($"\n### R1 — record stride  ({matrix.Length} payload bytes over {pages.Count} pages)");
		DumpHex(matrix);

		foreach (var stride in new[] { 3, 4 })
			ReportStride(matrix, stride);

		// ── R3: decode under whichever stride looks right ─────────────────────────────────────
		foreach (var stride in new[] { 3, 4 })
		{
			Console.WriteLine($"\n### R3 — decode at stride {stride}");
			DecodeSlots(matrix, stride);
		}

		if (!includeWriteTest)
		{
			Console.WriteLine("\nRead-only pass complete. Re-run with `keys write` for the R4 write/restore test.");
			return;
		}

		WriteTest(control, matrix);
	}

	// ── R4: does a write stick? ───────────────────────────────────────────────────────────────
	//
	// Deliberately conservative. A malformed matrix write scrambles every key on the board and no
	// factory-reset command has been verified on this device, so:
	//   * the verbatim matrix is saved to disk BEFORE anything is written,
	//   * the target slot is checked to hold exactly what we expect before being touched,
	//   * a SINGLE-key write (Type = slot) is tried first — it cannot disturb any other slot,
	//   * every write is followed by a full re-read and a byte-level diff,
	//   * the original is restored and re-verified at the end.
	private static void WriteTest(HidDevice control, byte[] before)
	{
		const byte TargetSlot = 71;                                   // Right Ctrl
		var original = new byte[] { 0x00, 0x00, 0xE4, 0x00 };         // RightCtrl usage
		var probeValue = new byte[] { 0x00, 0x00, 0xE6, 0x00 };       // RightAlt usage

		Console.WriteLine("\n### R4 — write test");

		var backup = Path.Combine(Path.GetTempPath(), "t800-keymatrix-backup.bin");
		File.WriteAllBytes(backup, before);
		Console.WriteLine($"  backup of the full 512-byte matrix: {backup}");

		var current = before.Skip(TargetSlot * 4).Take(4).ToArray();
		if (!current.SequenceEqual(original))
		{
			Console.WriteLine($"  slot {TargetSlot} holds {Hex(current)}, expected {Hex(original)} — ABORTING.");
			Console.WriteLine("  The board is not in its factory state; pick another slot rather than guessing.");
			return;
		}
		Console.WriteLine($"  slot {TargetSlot} = {Hex(current)} (RightCtrl), will try -> {Hex(probeValue)} (RightAlt)");

		// Attempt 1 — single-key write. Safest possible probe: touches one slot by construction.
		Console.WriteLine("\n  [1] single-key write (Type = slot)");
		Send(control, BuildSetSingleKey(TargetSlot, probeValue, flash: true), "SET_KEYMATRIX single");
		Thread.Sleep(400);

		var after = ReadMatrix(control);
		if (after == null) { Console.WriteLine("      re-read failed — STOPPING, restore by hand from the backup."); return; }

		var changed = Diff(before, after);
		Report(changed, TargetSlot, after);

		if (changed.Count == 1 && changed[0] == TargetSlot)
		{
			Console.WriteLine("\n  RESULT: single-key write WORKS and is surgical. Restoring...");
			Send(control, BuildSetSingleKey(TargetSlot, original, flash: true), "restore");
			Thread.Sleep(400);
			Verify(control, before, TargetSlot);
			return;
		}

		if (changed.Count == 0)
			Console.WriteLine("\n  single-key write had no effect. Not escalating to a whole-matrix write");
		Console.WriteLine("  automatically — that is the write that can scramble the board, and it should only");
		Console.WriteLine($"  run deliberately. The matrix is unchanged and the backup is at {backup}.");
	}

	private static void Report(List<int> changed, byte slot, byte[] after)
	{
		if (changed.Count == 0) { Console.WriteLine("      diff: nothing changed"); return; }

		Console.WriteLine($"      diff: {changed.Count} slot(s) changed");
		foreach (var s in changed.Take(12))
			Console.WriteLine($"        slot {s,3} -> {Hex(after.Skip(s * 4).Take(4).ToArray())}"
				+ (s == slot ? "   <== the one we asked for" : "   <== UNEXPECTED"));
	}

	private static void Verify(HidDevice control, byte[] expected, byte slot)
	{
		var restored = ReadMatrix(control);
		if (restored == null) { Console.WriteLine("  restore verify: re-read failed"); return; }

		var diff = Diff(expected, restored);
		Console.WriteLine(diff.Count == 0
			? "  restore verified: the matrix is byte-for-byte back to its original state."
			: $"  restore INCOMPLETE: {diff.Count} slot(s) still differ ({string.Join(", ", diff.Take(8))}).");
	}

	private static List<int> Diff(byte[] a, byte[] b)
	{
		var slots = new List<int>();
		for (int s = 0; s * 4 + 4 <= Math.Min(a.Length, b.Length); s++)
			for (int i = 0; i < 4; i++)
				if (a[s * 4 + i] != b[s * 4 + i]) { slots.Add(s); break; }
		return slots;
	}

	private static byte[]? ReadMatrix(HidDevice control)
	{
		var pages = new List<byte[]>();
		for (byte page = 0; page < MatrixPages; page++)
		{
			var request = BuildGet(page);
			var reply = Exchange(control, request);
			if (reply == null || IsEcho(request, reply)) return null;
			pages.Add(reply);
		}
		return Flatten(pages);
	}

	// SET_KEYMATRIX, single key:
	//   0x0A | Profile | Type=slot | Page | Len | FlashEnable | Index | CS | record(4)
	// Type 0..125 addresses one key rather than the whole table, so this cannot touch another slot
	// however wrong the rest of the framing turns out to be.
	private static byte[] BuildSetSingleKey(byte slot, byte[] record, bool flash)
	{
		var p = new byte[ReportLen];
		p[1] = CmdSetKeyMatrix;
		p[2] = Profile0;
		p[3] = slot;
		p[4] = 0;
		p[5] = (byte)record.Length;
		p[6] = flash ? (byte)1 : (byte)0;
		p[7] = Layer0;
		p[8] = Checksum(p, 1, 7);
		for (int i = 0; i < record.Length; i++) p[9 + i] = record[i];
		return p;
	}

	private static void Send(HidDevice device, byte[] packet, string label)
	{
		Console.WriteLine($"      {label,-22} {string.Join(' ', packet.Skip(1).Take(12).Select(x => x.ToString("X2")))}");
		try
		{
			if (!device.TryOpen(out var stream)) { Console.WriteLine("      TryOpen failed"); return; }
			using (stream) stream.SetFeature(packet);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"      SetFeature failed: {ex.Message}");
		}
	}

	private static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));

	// ── framing ───────────────────────────────────────────────────────────────────────────────

	// GET_KEYMATRIX: 0x8A | Profile | Type | Page | Index | 00 | 00 | CS
	private static byte[] BuildGet(byte page)
	{
		var p = new byte[ReportLen];
		p[1] = CmdGetKeyMatrix;
		p[2] = Profile0;
		p[3] = TypeWholeMatrix;
		p[4] = page;
		p[5] = Layer0;
		p[8] = Checksum(p, 1, 7);
		return p;
	}

	private static byte Checksum(byte[] packet, int from, int to)
	{
		int sum = 0;
		for (int i = from; i <= to; i++) sum += packet[i];
		return (byte)(0xFF - (sum & 0xFF));
	}

	// A reply identical to the request is the driver's cached copy, not the firmware answering.
	private static bool IsEcho(byte[] request, byte[] reply)
	{
		for (int i = 1; i < 12; i++)
			if (request[i] != reply[i]) return false;
		return true;
	}

	private static byte[] Flatten(List<byte[]> pages)
	{
		var all = new List<byte>();
		foreach (var page in pages)
			all.AddRange(page.Skip(1 + PayloadOffset).Take(PageBytes - PayloadOffset));
		return all.ToArray();
	}

	// ── analysis ──────────────────────────────────────────────────────────────────────────────

	// The 26 letters sit somewhere in any QWERTY factory table. If the stride and the byte offset
	// within the record are both right, all 26 land on the same offset; a wrong stride scatters
	// them. Scanning every offset rather than assuming one is what caught the record layout here —
	// the usage code is at index 2, not the last byte.
	private static void ReportStride(byte[] matrix, int stride)
	{
		for (int offset = 0; offset < stride; offset++)
		{
			var letters = new HashSet<byte>();
			for (int i = offset; i < matrix.Length; i += stride)
				if (matrix[i] >= 0x04 && matrix[i] <= 0x1D) letters.Add(matrix[i]);

			Console.WriteLine($"  stride {stride} offset {offset}: {letters.Count}/26 distinct letters"
				+ (letters.Count == 26 ? "   <== ALL 26" : ""));
		}
	}

	private static void DecodeSlots(byte[] matrix, int stride)
	{
		int slots = matrix.Length / stride;
		int named = 0;

		for (int slot = 0; slot < slots; slot++)
		{
			var rec = matrix.Skip(slot * stride).Take(stride).ToArray();
			if (rec.All(b => b == 0)) continue;

			string name = NameFor(rec, stride);
			if (name != "?") named++;
			Console.WriteLine($"  slot {slot,3}: {string.Join(' ', rec.Select(x => x.ToString("X2")))}  {name}");
		}

		Console.WriteLine($"  -> {named} slots decoded to a known key at stride {stride}");
	}

	// Minimal USB HID usage table, enough to recognise a factory QWERTY layout.
	//
	// Record layout, measured: [Type, Code1, Code2, Code3]. A plain keyboard key is
	// [0x00, 0x00, usage, 0x00] and FN is [0x0A, 0x01, 0x00, 0x00] — which matches the YC3123
	// action table exactly (Special Key / Fn = 0A 01 00 00, multimedia = 03 00 B5 00).
	//
	// Note this family stores modifiers as ordinary HID usages 0xE0..0xE7, NOT as the
	// modifier-bitmask-with-zero-usage form Cyrex uses. A modifier here is identified by its usage
	// code alone.
	private static string NameFor(byte[] rec, int stride)
	{
		byte type = rec[0];
		byte usage = stride >= 3 ? rec[2] : rec[stride - 1];

		if (type == 0x0A && rec[1] == 0x01) return "FN";
		if (type != 0x00 && type != 0x10) return $"type=0x{type:X2}";

		if (usage >= 0xE0 && usage <= 0xE7)
			return usage switch
			{
				0xE0 => "LeftCtrl", 0xE1 => "LeftShift", 0xE2 => "LeftAlt", 0xE3 => "LeftWin",
				0xE4 => "RightCtrl", 0xE5 => "RightShift", 0xE6 => "RightAlt", _ => "RightWin",
			};

		if (usage >= 0x04 && usage <= 0x1D) return ((char)('A' + usage - 0x04)).ToString();
		if (usage >= 0x1E && usage <= 0x26) return ((char)('1' + usage - 0x1E)).ToString();

		return usage switch
		{
			0x27 => "0", 0x28 => "Enter", 0x29 => "Escape", 0x2A => "Backspace", 0x2B => "Tab",
			0x2C => "Space", 0x2D => "Minus", 0x2E => "Equal", 0x2F => "BracketLeft",
			0x30 => "BracketRight", 0x31 => "Backslash", 0x33 => "Semicolon", 0x34 => "Quote",
			0x35 => "Grave", 0x36 => "Comma", 0x37 => "Period", 0x38 => "Slash", 0x39 => "CapsLock",
			0x4A => "Home", 0x4B => "PageUp", 0x4C => "Delete", 0x4D => "End", 0x4E => "PageDown",
			0x4F => "Right", 0x50 => "Left", 0x51 => "Down", 0x52 => "Up",
			_ => "?",
		};
	}

	private static void DumpHex(byte[] data)
	{
		for (int i = 0; i < data.Length; i += 16)
			Console.WriteLine($"    {i,4:D4}: {string.Join(' ', data.Skip(i).Take(16).Select(x => x.ToString("X2")))}");
	}

	// ── plumbing ──────────────────────────────────────────────────────────────────────────────

	private static byte[]? Exchange(HidDevice device, byte[] request)
	{
		try
		{
			if (!device.TryOpen(out var stream)) return null;
			using (stream)
			{
				stream.SetFeature(request);
				Thread.Sleep(20);
				var reply = new byte[ReportLen];
				stream.GetFeature(reply);
				return reply;
			}
		}
		catch
		{
			return null;
		}
	}

	private static int SafeFeatureLength(HidDevice d)
	{
		try { return d.GetMaxFeatureReportLength(); }
		catch { return -1; }
	}
}
