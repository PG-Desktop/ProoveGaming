using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── BLAZE — walk the LED effects on real hardware ─────────────────────────────────────────────
//
// Verifies the gradient/rainbow fix: colour slot 7 sweeps the eight colours stored in block 1's
// palette, it does NOT generate its own. We used to leave that palette zeroed, so "gradient
// breathing" cycled eight blacks and looked exactly like the LED switching off. The palette now
// written is the OEM software's own, read out of cap-06.08.26-for-blaze.pcapng.
//
// Drives ProoveGaming.Infrastructure's EasenMouseService rather than composing frames here. That
// matters: BLAZE requires ONE coordinated all-blocks param_1 write, and a burst of partial writes
// is what corrupted its flash in July (docs/mouse/blaze). Reimplementing that sequence in a probe
// to save a project reference would be recreating the exact hazard.
//
// Run with ProoveGamingDeviceHost STOPPED — it holds the HID handle open.
internal static class BlazeEffectCycleProbe
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA245;

	// App-side effect bytes (see DeviceDetailsViewModel.MapEffectByte), which X43LedEncoder maps
	// onto the device's (protocol effect, colour slot) pair.
	private const byte AppRainbowBreathing = 0x00;
	private const byte AppBreathing = 0x01;
	private const byte AppNeon = 0x02;
	private const byte AppColorCycle = 0x03;

	private sealed record Step(string Name, byte AppEffect, byte R, byte G, byte B, byte Brightness, string Expected);

	public static async Task RunAsync(int holdSeconds = 10)
	{
		Console.WriteLine("BLAZE — LED effect cycle");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}, {holdSeconds}s per effect");
		Console.WriteLine();

		var service = CreateService();
		if (service is null) return;

		var steps = new[]
		{
			// Brightness is the value the OEM actually sent for each effect in the captures. It is
			// NOT cosmetic here: colour cycle failed through this probe at 0xFC while the byte-for-byte
			// replay at 0xBD worked, so byte 3 may not be plain brightness for protocol 0x04.
			new Step("Breathing — solid BLUE", AppBreathing, 0x00, 0x00, 0xFF, 0xFC,
				"blue, fading in and out"),
			new Step("Breathing — GRADIENT", AppRainbowBreathing, 0x00, 0x00, 0xFF, 0xFC,
				"breathes through 8 colours: light blue, blue, green, yellow, cyan, magenta, orange, white"),
			new Step("Neon", AppNeon, 0x00, 0x00, 0xFF, 0xBD,
				"neon sweeping the 8-colour palette (protocol 0x03, slot 0 — colour ignored)"),
			new Step("Colour cycle", AppColorCycle, 0x00, 0x00, 0xFF, 0xBD,
				"colour cycle through the same palette (protocol 0x04, slot 0)"),
		};

		for (var i = 0; i < steps.Length; i++)
		{
			var step = steps[i];
			var (protocolEffect, colorSlot) = X43LedEncoder.MapEffect(step.AppEffect);

			Console.WriteLine(new string('=', 78));
			Console.WriteLine($"[{i + 1}/{steps.Length}] {step.Name}");
			Console.WriteLine($"    app effect 0x{step.AppEffect:X2} -> protocol 0x{protocolEffect:X2}, colour slot {colorSlot}");
			Console.WriteLine($"    brightness 0x{step.Brightness:X2} (as the OEM sent it for this effect)");
			Console.WriteLine($"    EXPECT: {step.Expected}");

			if (X43LedEncoder.SweepsPalette(protocolEffect, colorSlot))
			{
				Console.Write("    palette written to block1[24..47]: ");
				Console.WriteLine(string.Join(" ", X43LedEncoder.AllColorsPalette
					.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")));
			}

			var config = new DeviceConfiguration
			{
				Led = new LedConfig(step.R, step.G, step.B, step.AppEffect, step.Brightness, Speed: 4),
			};

			var ok = await service.ApplyConfigurationAsync(config);
			Console.WriteLine(ok ? "    -> write OK" : "    -> WRITE FAILED");
			Console.WriteLine($"    holding {holdSeconds}s — watch the mouse...");

			await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 78));
		Console.WriteLine("Done. Step 4 (colour cycle) is the open question: it failed at brightness 0xFC");
		Console.WriteLine("but the byte-for-byte OEM replay at 0xBD worked. If it works now, byte 3 is not");
		Console.WriteLine("plain brightness for protocol 0x04 and the App must not send high values there.");
	}

	// Mirrors DeviceRgbServiceFactory's selection so the probe drives the same interfaces the
	// DeviceHost does: write on outLen=65/featLen=9, read back on any featLen=9 with an input.
	private static EasenMouseService? CreateService()
	{
		var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
		if (interfaces.Count == 0)
		{
			Console.WriteLine("BLAZE not found. Is it plugged in?");
			return null;
		}

		foreach (var iface in interfaces)
			Console.WriteLine($"  interface: in={Len(iface, false)} out={Len(iface, true)} feat={Feat(iface)}");

		var write = interfaces.FirstOrDefault(d => Len(d, true) == 65 && Feat(d) == 9);
		if (write is null)
		{
			Console.WriteLine("Easen vendor interface (outLen=65 / featLen=9) not found.");
			Console.WriteLine("If the DeviceHost is running, stop it — it holds the handle.");
			return null;
		}

		var read = interfaces.FirstOrDefault(d => Feat(d) == 9 && Len(d, false) > 0);
		Console.WriteLine($"  using write interface, read-back {(read is null ? "unavailable" : "available")}");
		Console.WriteLine();

		// Trace-level logging prints every frame the service actually puts on the wire, so the
		// comparison against the OEM capture uses our real bytes rather than recomposed ones.
		return new EasenMouseService(write, read, new ConsoleFrameLogger());
	}

	private static int Len(HidDevice d, bool output)
	{
		try { return output ? d.GetMaxOutputReportLength() : d.GetMaxInputReportLength(); }
		catch { return 0; }
	}

	private static int Feat(HidDevice d)
	{
		try { return d.GetMaxFeatureReportLength(); }
		catch { return 0; }
	}

	// Minimal ILogger so the probe can show the service's frame trace without pulling a logging
	// package into this project. Only [frame] lines are printed; the rest would drown them out.
	private sealed class ConsoleFrameLogger : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			var line = formatter(state, exception);
			if (line.StartsWith("[frame]", StringComparison.Ordinal))
				Console.WriteLine($"      {line}");
		}
	}
}
