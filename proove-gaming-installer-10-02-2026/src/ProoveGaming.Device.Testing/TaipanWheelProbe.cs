using HidSharp;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — restore wheel scroll direction ─────────────────────────────────────────────────
//
// Symptom: after a button remap over the cable, wheel up/down scroll in the wrong direction.
//
// Why a remap can do that at all: TaipanButtonMap.BuildTable starts from CreateDefault(), which
// populates slots 23 and 24 with Wheel Up (0x8B) and Wheel Down (0x8C). So EVERY button write
// rewrites the wheel slots, even when the binding being changed is right-click. If our slot
// assignment is reversed relative to the firmware, any remap silently inverts the wheel — and
// nothing else in the codebase touches scroll direction (MsConfig has wheel *speed*, not
// direction, and ButtonToIndex never returns 23 or 24).
//
// protocol.md line 136 says slots 23/24 are wheel up/down "by convention" — an assumption carried
// from the vendor address table, never confirmed on hardware. This arm settles it by writing the
// table three ways and letting the wheel answer:
//
//   fixed   23=0x8C down, 24=0x8B up   hardware-correct order; what we ship since the fix
//   legacy  23=0x8B,      24=0x8C      the old buggy order, kept to reproduce the inversion
//   clear   23 and 24 zeroed           leave the wheel entirely to firmware defaults
//
// The slots are written as ABSOLUTE numbers, deliberately not through TaipanButtonMap's
// WheelUpIndex / WheelDownIndex. Those constants were swapped by the fix, so going through them
// would silently swap these modes too — "legacy" would emit the fixed order and vice versa, and a
// diagnostic that lies about which order it just wrote is worse than no diagnostic.
//
// Everything sent here is a DOCUMENTED function code in a documented slot, over the same write
// sequence TelinkMouseService already uses. No unknown opcodes, no guessed parameters.
internal static class TaipanWheelProbe
{
	private const int Vid = 0x248A;
	private const int EntryPacketBytes = 24;
	private const int PacketDelayMs = 12;

	private const byte FnWheelUp = 0x8B;
	private const byte FnWheelDown = 0x8C;

	public static async Task RunAsync(string mode)
	{
		Console.WriteLine("PG TAIPAN — wheel scroll direction");
		Console.WriteLine($"mode: {mode}");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid)
			.FirstOrDefault(d =>
				Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
				Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null)
		{
			Console.WriteLine("No TAIPAN config interface found. Connect the mouse (cable preferred —");
			Console.WriteLine("configuration writes are only proven to land over the cable).");
			return;
		}

		Console.WriteLine($"writing to {device.DevicePath}");
		Console.WriteLine();

		// (slot 23, slot 24) — absolute slots, see the header note.
		var (slot23, slot24) = mode switch
		{
			"legacy" => (FnWheelUp, FnWheelDown),
			"clear" => ((byte)0x00, (byte)0x00),
			_ => (FnWheelDown, FnWheelUp),
		};

		// Factory table, then the wheel pair overridden per mode. Every other slot keeps its
		// default so this arm only ever changes the thing under test.
		var table = TaipanButtonMap.CreateDefault();
		SetEntry(table, 23, slot23);
		SetEntry(table, 24, slot24);

		Console.WriteLine($"  slot 23 <- 0x{slot23:X2}   slot 24 <- 0x{slot24:X2}");
		Console.WriteLine();

		if (!device.TryOpen(out var stream))
		{
			Console.WriteLine("Could not open the config interface for writing.");
			return;
		}

		using (stream)
		{
			byte cmd = TaipanButtonMap.ButtonCommand;

			// Same sequence as TelinkMouseService.SetButtonBindingsAsync: 0xD1 header (erases the
			// sector) -> 0xD2 entry chunks -> ValidBankID -> 0xD3 commit, with the same pacing.
			await Write(stream, TaipanFrame.Build(
				cmd, 0x00, 0x00, TaipanButtonMap.HeaderLength, TaipanFrame.SeqFirst,
				table.AsSpan(0, TaipanButtonMap.HeaderLength)));

			int entriesLength = table.Length - TaipanButtonMap.HeaderLength;
			for (int written = 0; written < entriesLength; written += EntryPacketBytes)
			{
				int len = Math.Min(EntryPacketBytes, entriesLength - written);
				int offset = TaipanButtonMap.HeaderLength + written;
				await Write(stream, TaipanFrame.Build(
					cmd, (byte)(offset & 0xFF), (byte)(offset >> 8), (byte)len, TaipanFrame.SeqContinue,
					table.AsSpan(offset, len)));
			}

			await Write(stream, TaipanFrame.Build(
				cmd, TaipanButtonMap.ValidBankIdOffset & 0xFF, TaipanButtonMap.ValidBankIdOffset >> 8, 0x01,
				TaipanFrame.SeqContinue, [TaipanButtonMap.ValidBankId]));
			await Write(stream, TaipanFrame.Build(
				cmd, 0x00, 0x00, 0x00, TaipanFrame.SeqCommit, ReadOnlySpan<byte>.Empty));
		}

		Console.WriteLine("Written. Scroll the wheel now.");
		Console.WriteLine();
		Console.WriteLine("  Scrolls the RIGHT way -> this mode is correct; tell me which one and I will");
		Console.WriteLine("                           make it the shipped default.");
		Console.WriteLine("  Still inverted        -> run the next mode:  fixed -> legacy -> clear");
	}

	private static async Task Write(HidStream stream, byte[] frame)
	{
		stream.Write(frame);
		await Task.Delay(PacketDelayMs);
	}

	private static void SetEntry(byte[] table, int index, byte fn)
	{
		int o = TaipanButtonMap.EntryOffset(index);
		table[o] = fn;
		table[o + 1] = 0x00;
		table[o + 2] = 0x00;
		table[o + 3] = 0x00;
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
