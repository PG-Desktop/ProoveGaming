using InTheHand.Bluetooth;

namespace ProoveGaming.Device.Testing;

// ── PG Cosmo over BLUETOOTH — can the App address its audio endpoints? ────────────────────────
//
// Phase 0 of docs/plans/add-cosmo-bluetooth. Everything else in that plan is mechanical; this is
// the one genuine unknown.
//
// `IAudioEndpointService` addresses endpoints by DEVPKEY_Device_ContainerId on every method, never
// by name. Over the 2.4G dongle that container is the USB composite device's, and the App gets it
// from the HID pipeline as the card's DeviceKey. Over Bluetooth there is no USB device at all, so
// the question is simply: what container do Cosmo's Bluetooth audio endpoints report, and can the
// App arrive at it from what the Bluetooth stack tells it?
//
// If the answer is "the same container the BT device is known by", the Audio section needs nothing
// but a different identity handed to it. If not, the plan's Option B/C decision needs this data.
//
// READ-ONLY. Enumerates audio endpoints and reads properties; enumerates paired Bluetooth devices
// and reads name/id. No volume is set, no GATT write, nothing is paired or unpaired.
internal static class CosmoBluetoothAudioProbe
{
	// Matched case-insensitively against endpoint friendly names and Bluetooth device names. Cosmo's
	// USB endpoints are "Headphones (PG Cosmo)" / "Microphone (PG Cosmo)"; the Bluetooth ones are
	// expected to differ (commonly a "Stereo" and a "Hands-Free" variant), which is itself a result.
	private const string MatchName = "cosmo";

	public static async Task RunAsync()
	{
		Console.WriteLine("PG Cosmo over Bluetooth — audio endpoint identity probe (read-only)");
		Console.WriteLine();
		Console.WriteLine("Connect the headset over BLUETOOTH before running. If its 2.4G dongle is also");
		Console.WriteLine("plugged in, both sets of endpoints will appear — that is worth seeing.");
		Console.WriteLine();

		var endpoints = AudioEndpointInterop.ReadAll();
		var matching = endpoints
			.Where(e => e.Name.Contains(MatchName, StringComparison.OrdinalIgnoreCase))
			.ToList();

		Console.WriteLine(new string('=', 92));
		Console.WriteLine($"AUDIO ENDPOINTS matching \"{MatchName}\"  ({matching.Count} of {endpoints.Count} total)");
		Console.WriteLine();

		if (matching.Count == 0)
		{
			Console.WriteLine("  None. Either the headset is not connected, or Windows names its endpoints");
			Console.WriteLine("  something other than \"Cosmo\". Full list follows so the name can be read off.");
			Console.WriteLine();
			foreach (var e in endpoints)
				Console.WriteLine($"    [{e.Direction,-7}] {e.State,-10} {e.Name}");
		}
		else
		{
			foreach (var e in matching)
			{
				Console.WriteLine($"  [{e.Direction}] {e.Name}");
				Console.WriteLine($"      state       : {e.State}");
				Console.WriteLine($"      ContainerId : {e.ContainerId?.ToString() ?? "(none reported)"}");
				Console.WriteLine($"      endpoint id : {e.EndpointId}");
				Console.WriteLine($"      volume      : {(e.VolumePercent?.ToString() ?? "?")}%   muted={(e.Muted?.ToString() ?? "?")}");
				Console.WriteLine();
			}

			// Grouped by container, NOT counted. A headset present on two transports reports two
			// containers — one per transport — which is a completely different situation from render
			// and capture disagreeing within one transport. Counting alone cannot tell them apart,
			// and reading "2 containers" as "render and capture differ" is wrong.
			var groups = matching
				.Where(e => e.ContainerId is not null)
				.GroupBy(e => e.ContainerId!.Value)
				.ToList();

			Console.WriteLine($"  CONTAINERS ({groups.Count}), with the endpoints each one covers:");
			Console.WriteLine();
			foreach (var g in groups)
			{
				var directions = g.Select(e => e.Direction).Distinct().OrderBy(d => d).ToList();
				var live = g.Any(e => e.State == "ACTIVE");
				Console.WriteLine($"    {g.Key}");
				Console.WriteLine($"      covers   : {string.Join(" + ", directions)}");
				Console.WriteLine($"      state    : {(live ? "ACTIVE — this is the live transport" : "not present")}");
				foreach (var e in g)
					Console.WriteLine($"                 [{e.Direction}] {e.Name}");
				Console.WriteLine();
			}

			var liveGroup = groups.FirstOrDefault(g => g.Any(e => e.State == "ACTIVE"));
			if (liveGroup is null)
			{
				Console.WriteLine("  -> Nothing is ACTIVE, so no transport is live. Connect the headset and re-run.");
			}
			else
			{
				var liveDirections = liveGroup.Select(e => e.Direction).Distinct().Count();
				Console.WriteLine(liveDirections >= 2
					? "  -> The live transport's render AND capture share ONE container, so a single\n" +
					  "     identity addresses both. IAudioEndpointService works unchanged — it only\n" +
					  "     needs this container instead of the USB one (plan Option A/C).\n" +
					  $"     Live container: {liveGroup.Key}"
					: "  -> The live transport reports only one direction. Check whether the other is\n" +
					  "     merely NOTPRESENT (headset profile not engaged) before concluding.");
			}
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 92));
		Console.WriteLine("PAIRED BLUETOOTH DEVICES (what the App's discovery has to work from)");
		Console.WriteLine();

		try
		{
			var devices = await Bluetooth.GetPairedDevicesAsync();
			if (devices is null || devices.Count == 0)
			{
				Console.WriteLine("  none reported");
			}
			else
			{
				foreach (var d in devices)
				{
					var mark = (d.Name ?? string.Empty).Contains(MatchName, StringComparison.OrdinalIgnoreCase)
						? "  <-- Cosmo"
						: string.Empty;
					Console.WriteLine($"  name : {d.Name}{mark}");
					Console.WriteLine($"  id   : {d.Id}");
					Console.WriteLine();
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  Bluetooth enumeration failed: {ex.Message}");
		}

		Console.WriteLine(new string('=', 92));
		Console.WriteLine("WHAT TO COMPARE");
		Console.WriteLine();
		Console.WriteLine("  1. Does the Bluetooth `id` above contain, or map to, the endpoint ContainerId?");
		Console.WriteLine("     A Windows BT id often embeds the address (BTHENUM\\...\\7&...&...) rather than");
		Console.WriteLine("     a container GUID, in which case the App must resolve container from the device");
		Console.WriteLine("     tree (CfgMgr32, as DeviceContainerId already does) rather than string-matching.");
		Console.WriteLine("  2. Is the render endpoint's container the same as the capture endpoint's?");
		Console.WriteLine("  3. Are there TWO render endpoints (Stereo + Hands-Free)? Which one is ACTIVE?");
		Console.WriteLine();
		Console.WriteLine("Record the answers in docs/plans/add-cosmo-bluetooth/add-cosmo-bluetooth-plan.md");
		Console.WriteLine("under Phase 0, then the Option A/B/C decision can be made on data.");
	}
}
