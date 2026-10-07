using HidSharp;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — write the button map to EVERY interface on the dongle ──────────────────────────
//
// The vendor maintains that configuration over 2.4G works and suggests we may be on the wrong HID
// interface. Every write this repo has made went to the single 33/33 collection, chosen by
// TelinkMouseService's own filter. This arm removes that filter entirely and tries the full
// button-map write on every interface the dongle exposes, pausing after each so the button can be
// tested by hand before the next attempt.
//
// What the enumeration already says (taipan paths), and why the run is still worth doing:
//
//   mi_00&col01   in=7  out=0   LOCKED     mouse — Windows owns it exclusively
//   mi_00&col02   in=3  out=0   openable   consumer control
//   mi_00&col03   in=2  out=0   openable   system controller
//   mi_00&col04   in=33 out=33  openable   the one we have always used
//   mi_01         in=9  out=2   LOCKED     keyboard — Windows owns it exclusively
//
// An out=0 interface has no output report to write to, so a 33-byte write should fail outright,
// and LOCKED interfaces should refuse to open. Those are predictions, not reasons to skip them —
// the point of the run is to let the device contradict us if it can. Each attempt reports exactly
// how it failed, so "cannot open", "write rejected" and "write accepted but no effect" stay
// distinct outcomes rather than collapsing into "didn't work".
//
// Binding: RIGHT -> MY PC (Win+E), which opens File Explorer. Unmistakable when it lands, and it
// cannot be confused with the button's normal behaviour. Restored after each attempt.
internal static class TaipanAllInterfacesProbe
{
	private const int Vid = 0x248A;
	private const int DonglePid = 0x8373;
	private const int EntryPacketBytes = 24;
	private const int PacketDelayMs = 12;

	public static async Task RunAsync(int testSeconds)
	{
		Console.WriteLine("PG TAIPAN — button map write across every dongle interface");
		Console.WriteLine($"binding: RIGHT -> MY PC (Win+E), {testSeconds}s to test after each attempt");
		Console.WriteLine();

		var devices = DeviceList.Local.GetHidDevices(Vid).ToList();
		if (devices.Count == 0)
		{
			Console.WriteLine("No TAIPAN present. Plug the dongle in and switch the mouse on.");
			return;
		}

		// Cable check: a wired unit would make every result meaningless, since configuration is
		// already known to work over USB.
		var pids = devices.Select(d => d.ProductID).Distinct().ToList();
		if (pids.Any(p => p != DonglePid))
		{
			Console.WriteLine($"PID(s) present: {string.Join(", ", pids.Select(p => $"0x{p:X4}"))}");
			Console.WriteLine("That is not the dongle alone — unplug the USB cable, or the result proves nothing.");
			return;
		}

		var remap = new[] { new ButtonBinding(MouseButton.Right, MouseButtonAction.MyComputer) };
		var restore = new[] { new ButtonBinding(MouseButton.Right, MouseButtonAction.RightClick) };

		Console.WriteLine($"{devices.Count} interface(s) to try");
		Console.WriteLine();

		var accepted = new List<string>();

		for (var i = 0; i < devices.Count; i++)
		{
			var d = devices[i];
			var inLen = Len(() => d.GetMaxInputReportLength());
			var outLen = Len(() => d.GetMaxOutputReportLength());

			Console.WriteLine(new string('=', 96));
			Console.WriteLine($"[{i + 1}/{devices.Count}] in={inLen} out={outLen}");
			Console.WriteLine($"  {d.DevicePath}");

			var result = await TryWrite(d, remap);
			Console.WriteLine($"  -> {result}");

			if (!result.StartsWith("WRITE ACCEPTED"))
			{
				Console.WriteLine("  (no test pause — nothing was sent)");
				Console.WriteLine();
				continue;
			}

			accepted.Add(d.DevicePath);

			Console.WriteLine();
			Console.WriteLine($"  >>> PRESS THE RIGHT BUTTON NOW — {testSeconds}s <<<");
			Console.WriteLine("      File Explorer opening = THIS interface works.");

			for (var s = testSeconds; s > 0; s--)
			{
				Console.Write($"\r      {s,2}s remaining ");
				await Task.Delay(1000);
			}

			Console.WriteLine("\r                        ");

			// Restore before moving on, so a later attempt cannot be credited with an earlier
			// interface's success.
			var restored = await TryWrite(d, restore);
			Console.WriteLine($"  restored: {restored}");
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"{accepted.Count} interface(s) accepted the write at the transport level:");
		foreach (var path in accepted) Console.WriteLine($"  {path}");
		Console.WriteLine();
		Console.WriteLine("NOTE: 'accepted' means Windows took the bytes — NOT that the mouse applied them.");
		Console.WriteLine("Only the button actually changing proves that. If every interface accepted the");
		Console.WriteLine("write and none changed the button, interface choice is not the problem, and the");
		Console.WriteLine("question for the vendor becomes what the 2.4G link does to the multi-frame flash");
		Console.WriteLine("sequence: 0xD1 erase -> 0xD2 chunks -> ValidBankID -> 0xD3 commit.");
	}

	// Returns a human-readable outcome rather than a bool: *how* a write fails is the finding here.
	private static async Task<string> TryWrite(HidDevice device, IReadOnlyList<ButtonBinding> bindings)
	{
		if (!device.TryOpen(out var stream))
			return "CANNOT OPEN — Windows holds this interface exclusively";

		try
		{
			using (stream)
			{
				var table = TaipanButtonMap.BuildTable(bindings);
				byte cmd = TaipanButtonMap.ButtonCommand;

				// Same sequence as TelinkMouseService.SetButtonBindingsAsync, same pacing.
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

			return "WRITE ACCEPTED by the HID stack";
		}
		catch (Exception ex)
		{
			return $"WRITE REJECTED — {ex.GetType().Name}: {ex.Message}";
		}
	}

	private static async Task Write(HidStream stream, byte[] frame)
	{
		stream.Write(frame);
		await Task.Delay(PacketDelayMs);
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
