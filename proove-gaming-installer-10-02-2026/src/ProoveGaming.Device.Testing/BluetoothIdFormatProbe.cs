using InTheHand.Bluetooth;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Device.Testing;

// ── What does InTheHand's BluetoothDevice.Id actually look like? ───────────────────────────────
//
// The BLE presence poll decides liveness by matching a tracked device's address against the Windows
// device tree. That match is only as good as our ability to pull a 48-bit address out of
// `BluetoothDevice.Id` — and that format was ASSUMED rather than measured, which is exactly how it
// went wrong: when the address does not resolve, LowEnergyTreeSaysConnected returns null meaning
// "no opinion", the code falls back to the stale `Gatt.IsConnected`, and the card both flaps on
// removal and never comes back after a re-pair.
//
// So: print the real ids, next to what the current parser makes of them and what the device tree
// holds, and let the transcript settle it.
//
// READ-ONLY: enumerates paired devices and reads their properties. No GATT connection is opened,
// nothing is written, nothing is paired or unpaired.
internal static class BluetoothIdFormatProbe
{
	public static async Task RunAsync()
	{
		Console.WriteLine("InTheHand BluetoothDevice.Id — real format vs what we parse (READ-ONLY)");
		Console.WriteLine();

		IReadOnlyCollection<BluetoothDevice> paired;
		try
		{
			paired = await Bluetooth.GetPairedDevicesAsync();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"GetPairedDevicesAsync failed: {ex.GetType().Name}: {ex.Message}");
			Console.WriteLine("Is Bluetooth switched on?");
			return;
		}

		Console.WriteLine($"{paired.Count} paired device(s) as InTheHand sees them:");
		Console.WriteLine();

		foreach (var device in paired)
		{
			string id, name;
			try { id = device.Id; } catch (Exception ex) { id = $"<threw: {ex.GetType().Name}>"; }
			try { name = device.Name; } catch (Exception ex) { name = $"<threw: {ex.GetType().Name}>"; }

			bool? gatt = null;
			try { gatt = device.Gatt.IsConnected; } catch { /* reported as unknown below */ }

			bool? isPaired = null;
			try { isPaired = device.IsPaired; } catch { /* ditto */ }

			Console.WriteLine($"  {name}");
			Console.WriteLine($"    Id            : {id}");
			Console.WriteLine($"    Id length     : {id.Length}");
			Console.WriteLine($"    Gatt.IsConnected: {gatt?.ToString() ?? "(threw)"}   IsPaired: {isPaired?.ToString() ?? "(threw)"}");

			// What the SHIPPING parser makes of it. A null here is the whole bug: it means the poll
			// has no opinion about this device and silently defers to Gatt.IsConnected.
			var parsed = BluetoothDeviceDiscoveryService.NormalizeBluetoothAddress(id);
			Console.WriteLine($"    parsed address: {parsed ?? "NULL  <-- no opinion; falls back to Gatt.IsConnected"}");

			// And the two cheaper interpretations, so the transcript shows which rule would work
			// rather than leaving it to another guess.
			Console.WriteLine($"    all hex digits: {Hex(id)} (len {Hex(id).Length})");
			var tail = id.Contains('-') ? id[(id.LastIndexOf('-') + 1)..] : id;
			Console.WriteLine($"    after last '-': '{tail}' -> hex '{Hex(tail)}' (len {Hex(tail).Length})");
			Console.WriteLine();
		}

		Console.WriteLine(new string('=', 96));
		Console.WriteLine("DEVICE TREE — the addresses the presence poll is matching AGAINST");
		Console.WriteLine();

		var nodes = DeviceNodeQuery.EnumerateLowEnergyNodes();
		if (nodes is null)
		{
			Console.WriteLine("  BTHLE enumeration FAILED (returned null).");
		}
		else
		{
			foreach (var node in nodes)
			{
				var address = ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId);
				Console.WriteLine($"  {node.FriendlyName ?? "(no name)",-34} IsConnected={node.IsConnected,-6} " +
					$"address={address ?? "(unparsed)"}");
				Console.WriteLine($"    {node.InstanceId}");
			}

			Console.WriteLine();
			Console.WriteLine("  live set (present AND connected): " +
				string.Join(", ", BluetoothDeviceDiscoveryService.LiveLowEnergyAddresses(nodes)));
		}

		Console.WriteLine();
		Console.WriteLine(new string('=', 96));
		Console.WriteLine("READING THE RESULT");
		Console.WriteLine();
		Console.WriteLine("  Every device shows a parsed address that appears in the tree list above");
		Console.WriteLine("     -> matching works and the flapping is coming from somewhere else.");
		Console.WriteLine();
		Console.WriteLine("  parsed address is NULL, or does not match any tree address");
		Console.WriteLine("     -> this is the bug. The poll has no opinion, defers to the stale");
		Console.WriteLine("        Gatt.IsConnected, and the card flaps on removal and never returns after a");
		Console.WriteLine("        re-pair. The 'after last dash' line shows the rule that would work.");
	}

	private static string Hex(string value) =>
		new([.. value.Where(Uri.IsHexDigit)]);
}
