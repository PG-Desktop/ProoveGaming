using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── PG TAIPAN — does CONFIGURATION apply over the 2.4G dongle? ─────────────────────────────────
//
// This repo recorded "the dongle only relays mouse->host; config never applies; cable is the only
// write path" (2026-08-04). That conclusion is now suspect, because its premise was disproven on
// 2026-08-07: a battery query sent as `0x48` on the dongle gets a reply every time. The dongle does
// carry host->mouse commands. The earlier battery test had used opcode `0x08`, and one wrong byte
// turned into a rule about the whole transport.
//
// The vendor's answer the same day: 2.4G needs no separate fragmentation or format — send the FULL
// data set, identical to wired. That is exactly what TelinkMouseService.SetButtonBindingsAsync
// already does (header -> entry chunks -> ValidBankID -> commit), so this probe drives that service
// unmodified rather than hand-rolling frames. Reimplementing the write to "test" it would test
// something the app does not do.
//
// WHAT IT DOES
//   1. Remaps the chosen button to a visibly different action, over the dongle.
//   2. Waits while you press it.
//   3. Restores that button to its normal function.
//
// The button is an argument, defaulting to RIGHT. Each target is picked to be unmistakable when
// pressed rather than merely different in the table:
//
//   right    -> LEFT CLICK    the right button left-clicks; no context menu appears
//   wheel    -> RIGHT CLICK   wheel click opens the context menu
//   forward  -> RIGHT CLICK   side button opens the context menu
//   back     -> RIGHT CLICK   side button opens the context menu
//
// Testing `right` costs you the context menu for the hold window, which is why a side button is
// gentler — but it is also the most direct answer to "can the app change my right click", so it is
// the default rather than a substitution made on your behalf.
//
// WRITES. The documented button-map command over the documented collection — the same bytes the app
// sends over the cable. Note the header packet carries SeqFirst (0xD1), which erases the button
// flash sector before rewriting it; that is how the wired path works too, and the restore step puts
// the default map back.
internal static class TaipanDongleConfigProbe
{
	private const int Vid = 0x248A;
	private const int Pid = 0x8373;   // 2.4G dongle

	// CMD_SetMouseState, read/ACK form. Echoes back and touches NO flash, which is what makes it
	// useful here: the button map is a multi-frame flash write, so if it fails there is no way to
	// tell "the dongle drops host->mouse commands" from "the flash sequence does not survive the
	// radio link". This command separates them.
	//
	// Para1 = 0x01 (enabled) is reused from TaipanDongleProbe rather than guessed — the parameter is
	// undocumented beyond "enable/disable", and a wrong value on a command that can DISABLE the
	// mouse is not worth the information.
	private const byte SetMouseStateCommand = 0x46;

	// Which button to remap, and to what. The pair is chosen so the remap is obvious on the desk.
	private static (MouseButton Button, MouseButtonAction Target) Resolve(string name) => name switch
	{
		"wheel" => (MouseButton.Wheel, MouseButtonAction.MyComputer),
		"forward" => (MouseButton.Forward, MouseButtonAction.MyComputer),
		"back" => (MouseButton.Back, MouseButtonAction.MyComputer),
		_ => (MouseButton.Right, MouseButtonAction.MyComputer),
	};

	// That button's normal function, for the restore step.
	private static MouseButtonAction Normal(MouseButton button) => button switch
	{
		MouseButton.Left => MouseButtonAction.LeftClick,
		MouseButton.Right => MouseButtonAction.RightClick,
		MouseButton.Wheel => MouseButtonAction.WheelClick,
		MouseButton.Forward => MouseButtonAction.Forward,
		_ => MouseButtonAction.Back,
	};

	public static void Run(string buttonName = "right", int holdSeconds = 25)
	{
		Console.WriteLine("PG TAIPAN — button remap over the 2.4G dongle");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();
		Console.WriteLine("UNPLUG THE USB CABLE FIRST. While cabled the mouse runs in wired mode and the radio");
		Console.WriteLine("link is idle, so a dongle test proves nothing.");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid).FirstOrDefault(d =>
			Len(() => d.GetMaxOutputReportLength()) == TaipanFrame.Length &&
			Len(() => d.GetMaxInputReportLength()) == TaipanFrame.Length);

		if (device is null)
		{
			Console.WriteLine($"No {TaipanFrame.Length}/{TaipanFrame.Length} vendor collection on the dongle PID.");
			Console.WriteLine("Is the dongle plugged in and the mouse switched on?");
			return;
		}

		Console.WriteLine($"Vendor collection: {device.DevicePath}");
		Console.WriteLine();

		// PRE-FLIGHT. The 2026-08-04 dongle finding had exactly one confound: with the cable
		// plugged in the mouse runs wired and the radio link is idle, so every dongle test fails
		// for a trivial reason. We can now prove the state instead of trusting a reminder — the
		// battery query works over the dongle and its reply carries a USB flag.
		Console.WriteLine("PRE-FLIGHT — querying battery to prove the radio link is live");
		var before = QueryBattery(device);

		if (before is null)
		{
			Console.WriteLine("  NO REPLY. The mouse is not answering over the dongle at all, so a write test");
			Console.WriteLine("  would be meaningless. Switch the mouse on, or re-pair the dongle.");
			return;
		}

		if (before.Value.Usb)
		{
			Console.WriteLine($"  Mouse reports USB CONNECTED (battery raw={before.Value.Raw}).");
			Console.WriteLine("  ABORTING: the cable is plugged in, so the mouse is in wired mode and this would");
			Console.WriteLine("  test the cable, not the dongle. Unplug the USB cable and re-run.");
			return;
		}

		Console.WriteLine($"  Reply received, usb=False, battery raw={before.Value.Raw} " +
			$"({TaipanStateReport.PercentFromRaw(before.Value.Raw)}%).");
		Console.WriteLine("  The mouse is live on the radio link and host->mouse commands reach it.");
		Console.WriteLine();

		// ECHO TEST. Runs before the flash write because it answers the more fundamental question,
		// and because its answer changes what a failed remap means.
		Console.WriteLine($"ECHO TEST — CMD_SetMouseState (0x{SetMouseStateCommand:X2}), no flash involved");
		var echoed = Echo(device);
		Console.WriteLine(echoed
			? "  Echo received. Host->mouse commands are processed, so anything that fails below is\n" +
			  "  specific to the multi-frame FLASH write, not to the transport."
			: "  No echo. The mouse answers the battery read but not this command, so whether WRITES\n" +
			  "  reach the device at all is still unproven.");
		Console.WriteLine();

		// The same service the DeviceHost uses over the cable. Driving it directly is the point:
		// if this works, the app works, with no protocol change.
		var service = new TelinkMouseService(device, null, deviceKey: string.Empty);

		var (button, target) = Resolve(buttonName.ToLowerInvariant());
		var normal = Normal(button);

		// The FULL desired set, every button named — the vendor's "full data set identical to wired".
		var remapped = Bindings(button, target);
		var restored = Bindings(button, normal);

		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"STEP 1 — remapping {button.ToString().ToUpperInvariant()} -> {target}");

		if (!Write(service, remapped)) return;

		var expectation = target == MouseButtonAction.MyComputer
			? "FILE EXPLORER / THIS PC OPENS"
			: $"it behaves as {target}";

		Console.WriteLine();
		Console.WriteLine($"  Now press the {button.ToString().ToUpperInvariant()} button. {holdSeconds}s.");
		Console.WriteLine();
		Console.WriteLine($"    {expectation}  -> configuration over the dongle WORKS.");
		Console.WriteLine($"    still behaves as {normal}  -> the write did not take.");
		Console.WriteLine();

		Thread.Sleep(holdSeconds * 1000);

		// POST-FLIGHT. If the button did not change, this separates "the write was ignored" from
		// "the link died mid-test" — without it, a dropped link looks exactly like a rejected write.
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("POST-FLIGHT — querying battery again to prove the link survived the write");
		var after = QueryBattery(device);
		Console.WriteLine(after is null
			? "  NO REPLY — the link dropped during the test, so its result is inconclusive. Re-run."
			: $"  Reply received, usb={after.Value.Usb}, battery raw={after.Value.Raw}. Link still live.");
		Console.WriteLine();

		Console.WriteLine(new string('=', 96));
		Console.WriteLine($"STEP 2 — restoring {button.ToString().ToUpperInvariant()} -> {normal}");

		if (!Write(service, restored))
		{
			Console.WriteLine();
			Console.WriteLine("  RESTORE FAILED. Re-run this arm, or apply any preset from the app over the");
			Console.WriteLine("  cable to rewrite the button map.");
			return;
		}

		Console.WriteLine();
		Console.WriteLine($"  Press {button.ToString().ToUpperInvariant()} again — it should behave normally now.");
		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("READING THE RESULT");
		Console.WriteLine();
		Console.WriteLine("  Button CHANGED            -> configuration over the dongle works. The catalog's");
		Console.WriteLine("                               SupportsConfiguration:false on the Dongle2_4G");
		Console.WriteLine("                               transport can be lifted.");
		Console.WriteLine();
		Console.WriteLine(echoed
			? "  Button unchanged, echo OK -> commands ARE processed, so the failure is specific to the\n" +
			  "                               multi-frame flash write (0xD1 erase / 0xD2 chunks /\n" +
			  "                               ValidBankID / 0xD3 commit). That is a precise question for\n" +
			  "                               the vendor: does the radio link break its sequencing?"
			: "  Button unchanged, no echo -> only reads are proven to work. Report to the vendor that\n" +
			  "                               0x48 replies but 0x46 does not echo, which contradicts\n" +
			  "                               'send the full data set identical to wired'.");
	}

	// Every remappable button named explicitly. SetButtonBindingsAsync builds the whole table from
	// this list, so anything omitted would be written as its default rather than left alone — which
	// is also the vendor's "full data set" requirement for 2.4G.
	private static List<ButtonBinding> Bindings(MouseButton changed, MouseButtonAction action) =>
	[
		new(MouseButton.Left, changed == MouseButton.Left ? action : MouseButtonAction.LeftClick),
		new(MouseButton.Right, changed == MouseButton.Right ? action : MouseButtonAction.RightClick),
		new(MouseButton.Wheel, changed == MouseButton.Wheel ? action : MouseButtonAction.WheelClick),
		new(MouseButton.Forward, changed == MouseButton.Forward ? action : MouseButtonAction.Forward),
		new(MouseButton.Back, changed == MouseButton.Back ? action : MouseButtonAction.Back),
	];

	// Sends the documented battery request (cmd 0x48, zero parameters) and waits for the single
	// reply. Proven to work over the dongle on 2026-08-07; used here purely as a liveness and
	// cable-state check, because its reply carries the USB flag.
	private static (int Raw, bool Usb)? QueryBattery(HidDevice device)
	{
		try
		{
			if (!device.TryOpen(out var stream)) return null;

			using (stream)
			{
				stream.ReadTimeout = 400;
				stream.Write(TaipanFrame.Build(0x48, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty));

				var buf = new byte[TaipanFrame.Length];
				var deadline = DateTime.UtcNow.AddSeconds(3);

				while (DateTime.UtcNow < deadline)
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { return null; }

					if (n > 0 && TaipanStateReport.TryParseBattery(buf.AsSpan(0, n), out var raw, out var usb))
						return (raw, usb);
				}
			}
		}
		catch
		{
			// Fall through — a failed query is itself the answer the caller checks for.
		}

		return null;
	}

	// Sends CMD_SetMouseState and waits for the device to echo it back. Returns false on any
	// failure — a missing echo is the result being measured, not an error to report.
	private static bool Echo(HidDevice device)
	{
		try
		{
			if (!device.TryOpen(out var stream)) return false;

			using (stream)
			{
				stream.ReadTimeout = 400;
				stream.Write(TaipanFrame.Build(SetMouseStateCommand, 0x01, 0, 0, 0, ReadOnlySpan<byte>.Empty));

				var buf = new byte[TaipanFrame.Length];
				var deadline = DateTime.UtcNow.AddSeconds(3);

				while (DateTime.UtcNow < deadline)
				{
					int n;
					try { n = stream.Read(buf); }
					catch (TimeoutException) { continue; }
					catch { return false; }

					// Byte 3 is driverCMD; the device echoes the command it accepted.
					if (n > 3 && buf[3] == SetMouseStateCommand) return true;
				}
			}
		}
		catch
		{
			// Treated as "no echo" — the caller reports it as a measurement, not a crash.
		}

		return false;
	}

	private static bool Write(TelinkMouseService service, List<ButtonBinding> bindings)
	{
		foreach (var b in bindings)
			Console.WriteLine($"    {b.Button,-8} -> {b.Action}");

		var ok = service.SetButtonBindingsAsync(bindings).GetAwaiter().GetResult();
		Console.WriteLine(ok
			? "  write reported OK (this only means the frames left the host)"
			: "  WRITE FAILED at the transport level");
		return ok;
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
