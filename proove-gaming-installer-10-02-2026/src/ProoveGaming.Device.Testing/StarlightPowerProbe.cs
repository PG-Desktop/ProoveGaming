using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.Device.Testing;

// ── PG Starlight — is "headset off" visible to the host? ───────────────────────────────────────
//
// This is the question Phase 0c was written for and never answered, and it decides whether the app
// needs any device-specific code at all for power state.
//
// Cosmo needed a vendor report because its DONGLE stays enumerated when the headset is switched
// off: USB still sees a device, so "connected" was a lie without reading report 0xBC byte 19.
// Starlight might behave the same way, or it might simply vanish from USB — in which case the app
// already handles it, because a device that stops enumerating is a device that shows as
// disconnected. No new code, no vendor channel, nothing to decode.
//
// Also samples report 0xFD each second. It reads all zeros while the headset is ON; if it reads
// something different while OFF, that is a state bit worth having even though the payload looked
// empty in the earlier survey.
//
// READ-ONLY. Enumeration and control-transfer reads only.
internal static class StarlightPowerProbe
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x036D;

	private const byte VendorReportId = 0xFD;
	private const int VendorReportLength = 32;

	private static readonly (string Label, int Seconds, string Instruction)[] Phases =
	[
		("baseline-on", 10,
			"Headset ON. Leave it alone — this records what 'present and powered' looks like."),
		("power-off", 20,
			"HOLD BUTTON 1 until the headset switches OFF. Then leave it off for the rest of this phase."),
		("stayed-off", 10,
			"Keep it OFF. This separates a slow disconnect from a device that never disconnects."),
		("power-on", 20,
			"Switch the headset back ON."),
	];

	public static void Run()
	{
		Console.WriteLine("PG Starlight — power-state visibility (READ-ONLY)");
		Console.WriteLine($"VID 0x{Vid:X4} PID 0x{Pid:X4}");
		Console.WriteLine();
		Console.WriteLine("Samples USB enumeration and report 0xFD once a second, through a power cycle.");
		Console.WriteLine();

		var samples = new List<(string Phase, int Interfaces, string Payload)>();
		var phase = "startup";
		var gate = new object();

		using var cts = new CancellationTokenSource();

		var sampler = Task.Run(() =>
		{
			while (!cts.IsCancellationRequested)
			{
				var interfaces = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
				var payload = "-";

				if (interfaces.Count > 0)
				{
					var vendor = interfaces.FirstOrDefault(d => Len(() => d.GetMaxOutputReportLength()) == VendorReportLength);
					if (vendor is not null)
					{
						var frame = TryGetInputReport(vendor.DevicePath);
						payload = frame is null ? "read-failed" : BitConverter.ToString(frame);
					}
				}

				lock (gate) samples.Add((phase, interfaces.Count, payload));

				try { Task.Delay(1000, cts.Token).Wait(cts.Token); }
				catch { break; }
			}
		}, cts.Token);

		foreach (var (label, seconds, instruction) in Phases)
		{
			lock (gate) phase = label;

			Console.WriteLine(new string('-', 96));
			Console.WriteLine($"PHASE: {label}   ({seconds}s)");
			Console.WriteLine($"  {instruction}");
			Thread.Sleep(seconds * 1000);

			lock (gate)
			{
				var inPhase = samples.Where(s => s.Phase == label).ToList();
				var counts = inPhase.Select(s => s.Interfaces).Distinct().OrderBy(c => c);
				Console.WriteLine($"  -> interfaces seen: {string.Join(", ", counts)}");
			}
		}

		cts.Cancel();
		sampler.Wait(TimeSpan.FromSeconds(3));

		Report(samples);
	}

	private static void Report(List<(string Phase, int Interfaces, string Payload)> samples)
	{
		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("RESULT");
		Console.WriteLine();

		foreach (var (label, _, _) in Phases)
		{
			var inPhase = samples.Where(s => s.Phase == label).ToList();
			if (inPhase.Count == 0) continue;

			var minIf = inPhase.Min(s => s.Interfaces);
			var maxIf = inPhase.Max(s => s.Interfaces);
			var payloads = inPhase.Select(s => s.Payload).Distinct().ToList();

			Console.WriteLine($"{label,-12} interfaces {minIf}..{maxIf}   distinct 0xFD payloads: {payloads.Count}");
			foreach (var p in payloads.Take(3))
				Console.WriteLine($"                 {Shorten(p)}");
		}

		var offSamples = samples.Where(s => s.Phase is "power-off" or "stayed-off").ToList();
		var onSamples = samples.Where(s => s.Phase is "baseline-on" or "power-on").ToList();

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));

		if (offSamples.Count == 0 || onSamples.Count == 0)
		{
			Console.WriteLine("Not enough samples to conclude.");
			return;
		}

		var disappears = offSamples.Any(s => s.Interfaces == 0);
		var payloadDiffers = offSamples.Select(s => s.Payload).Distinct()
			.Except(onSamples.Select(s => s.Payload).Distinct()).Any();

		if (disappears)
		{
			Console.WriteLine("THE DEVICE DISAPPEARS FROM USB WHEN SWITCHED OFF.");
			Console.WriteLine();
			Console.WriteLine("  This is the good outcome: power state IS connection state, so the app already");
			Console.WriteLine("  handles it. The card greys out on power-off and returns on power-on with no");
			Console.WriteLine("  device-specific code — no vendor report, nothing to decode, nothing like the");
			Console.WriteLine("  CosmoStateMonitor needed for this device.");
		}
		else if (payloadDiffers)
		{
			Console.WriteLine("THE DEVICE STAYS ENUMERATED, BUT REPORT 0xFD CHANGES WHEN IT IS OFF.");
			Console.WriteLine();
			Console.WriteLine("  Same shape as Cosmo: the payload carries a power bit. Compare the payloads");
			Console.WriteLine("  above to find the byte that moved — that is the state flag, and a small");
			Console.WriteLine("  monitor modelled on CosmoStateMonitor can surface it.");
		}
		else
		{
			Console.WriteLine("THE DEVICE STAYS ENUMERATED AND 0xFD NEVER CHANGES.");
			Console.WriteLine();
			Console.WriteLine("  Power state is not observable at all. The app would show the headset as");
			Console.WriteLine("  connected while it is switched off, and there is no honest way to correct");
			Console.WriteLine("  that from the host. Worth telling the vendor, since it is a user-visible");
			Console.WriteLine("  wrong state rather than a missing nice-to-have.");
		}
	}

	private static string Shorten(string payload) =>
		payload.Length <= 71 ? payload : payload[..71] + "...";

	private static byte[]? TryGetInputReport(string devicePath)
	{
		if (string.IsNullOrEmpty(devicePath) || !OperatingSystem.IsWindows())
			return null;

		SafeFileHandle? handle = null;
		try
		{
			handle = CreateFileW(devicePath, GenericRead | GenericWrite,
				FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

			if (handle.IsInvalid) return null;

			var buffer = new byte[VendorReportLength];
			buffer[0] = VendorReportId;

			return HidD_GetInputReport(handle, buffer, buffer.Length) ? buffer : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			handle?.Dispose();
		}
	}

	private static int Len(Func<int> get)
	{
		try { return get(); }
		catch { return 0; }
	}

	private const uint GenericRead = 0x80000000;
	private const uint GenericWrite = 0x40000000;
	private const uint FileShareRead = 1;
	private const uint FileShareWrite = 2;
	private const uint OpenExisting = 3;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFileW(
		string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
		uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] buffer, int bufferLength);
}
