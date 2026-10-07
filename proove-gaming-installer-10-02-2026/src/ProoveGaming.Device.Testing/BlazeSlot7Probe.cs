using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── BLAZE — does key-matrix slot 7 gate software RGB? ─────────────────────────────────────────
//
// The vendor said (2026-08-17) that "the 8th key" should be sent as `00 00 00 0E`, because the
// current value can block RGB. The 8th key counting from one is SLOT 7 — the physical LED slide
// switch on the mouse body — and we send it as the factory binding `03 00 00 0A`: mode 0x0A (LED
// control), code 0x03 = "LED effect cycle switch". That literally hands the LED to the switch.
//
// Our own testing already recorded the consequence (docs/mouse/blaze/key-matrix.md, 2026-07-13):
// "the switch cycles the LED purely in firmware and can't be detected or repurposed from software".
// If the firmware owns the effect, a software-set effect or colour can be overridden — which is the
// symptom described.
//
// This arm does NOT change slot 7 by itself. Slot 7 comes from X43KeyMatrix.CreateDefault(), a
// compile-time constant, so the two variants are compared by patching that constant and running
// this twice. The probe prints the bytes it is about to send, so each run states which variant it
// tested rather than relying on memory.
//
// SETTLED 2026-08-17: CreateDefault now ships 0x0E, so out of the box this runs the vendor variant
// only — reproducing the 0x0A baseline means patching the constant back first. Kept because slot 7
// is still the suspect in the intermittent button loss at connect, and this is the shortest path to
// putting one known configuration on the device and looking at it.
//
// WRITES, but only through EasenMouseService.ApplyConfigurationAsync — the single coordinated
// all-blocks write. Never a hand-rolled or per-block write: a burst of single-block writes is what
// corrupted this device's flash before (docs/mouse/blaze).
//
// Buttons are included in the SAME configuration on purpose. With only Led set, ChangedBlocksMask
// returns 0x02 and block 2 — the key matrix, where slot 7 lives — is never sent at all.
internal static class BlazeSlot7Probe
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA245;

	// A colour that is unmistakable on the mouse and unlike any factory effect, so "did the RGB
	// change" needs no interpretation.
	private const byte R = 0x00, G = 0xFF, B = 0x00;   // green

	// App-side effect byte for a steady colour; X43LedEncoder maps it to the device's pair.
	private const byte AppEffect = 0x01;

	public static async Task RunAsync(int holdSeconds)
	{
		Console.WriteLine("BLAZE — key-matrix slot 7 (the RGB slide switch) vs software RGB");
		Console.WriteLine();

		// Report the value this build will send, so the run is self-documenting.
		var matrix = X43KeyMatrix.CreateDefault();
		var slot7 = matrix.AsSpan(X43KeyMatrix.SlotOffset(7), 4).ToArray();

		Console.WriteLine($"  slot 7 in THIS build : {BitConverter.ToString(slot7).Replace('-', ' ')}");
		Console.WriteLine(slot7 is [0x03, 0x00, 0x00, 0x0A]
			? "  -> the FACTORY binding (mode 0x0A / code 0x03 = LED effect cycle). Baseline run."
			: slot7 is [0x00, 0x00, 0x00, 0x0E]
				? "  -> the VENDOR's proposed value (mode 0x0E). Patched run."
				: "  -> an unrecognised value; check the patch before trusting this run.");
		Console.WriteLine();

		var device = DeviceList.Local.GetHidDevices(Vid, Pid)
			.FirstOrDefault(d => Len(() => d.GetMaxFeatureReportLength()) >= 9);

		if (device is null)
		{
			Console.WriteLine("BLAZE not found (VID 0x04D9 / PID 0xA245). Plug it in.");
			return;
		}

		Console.WriteLine($"  device: {device.DevicePath}");
		Console.WriteLine();

		var service = new EasenMouseService(device, null, deviceKey: string.Empty);

		// The factory bindings for the four remappable slots, so nothing about the user's buttons
		// changes — the only difference between the two runs is slot 7, which BuildMatrix does not
		// touch and CreateDefault supplies.
		var bindings = new List<ButtonBinding>
		{
			new(MouseButton.Right, MouseButtonAction.RightClick),
			new(MouseButton.Wheel, MouseButtonAction.WheelClick),
			new(MouseButton.Forward, MouseButtonAction.Forward),
			new(MouseButton.Back, MouseButtonAction.Back),
		};

		Console.WriteLine("Applying key matrix + LED in ONE coordinated write...");

		var ok = await service.ApplyConfigurationAsync(new DeviceConfiguration
		{
			Buttons = bindings,
			Led = new LedConfig(R, G, B, AppEffect, Brightness: 0x04, Speed: 3),
		});

		Console.WriteLine($"  write reported: {(ok ? "OK" : "FAILED")}");
		Console.WriteLine();
		Console.WriteLine($">>> LOOK AT THE MOUSE for the next {holdSeconds}s. <<<");
		Console.WriteLine("    Expected if the vendor is right: the LED is GREEN and stays green.");
		Console.WriteLine();
		Console.WriteLine("    Then flip the physical LED slide switch once and watch again — that is");
		Console.WriteLine("    the case where a firmware-owned effect would take the LED back.");

		for (var s = holdSeconds; s > 0; s--)
		{
			Console.Write($"\r    {s,3}s ");
			await Task.Delay(1000);
		}

		Console.WriteLine("\r          ");
		Console.WriteLine();
		Console.WriteLine("Report for this run:");
		Console.WriteLine("  1. Did the LED turn green at all?");
		Console.WriteLine("  2. Did it STAY green, or did the firmware take it back?");
		Console.WriteLine("  3. After flipping the slide switch, did software RGB still work?");
		Console.WriteLine();
		Console.WriteLine("Compare against the other slot-7 variant. If green sticks only with 0x0E,");
		Console.WriteLine("the vendor is right and CreateDefault's slot 7 should change permanently.");
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}
}
