using System.Globalization;

namespace ProoveGaming.Infrastructure.Classes;

// Parses the InTheHand Windows BluetoothDevice.Id format — the bare 12-hex-digit Bluetooth
// address string (e.g. "E0F1A2B3C4D5") — into the ulong form the WinRT advertisement watcher
// reports in BluetoothLEAdvertisementReceivedEventArgs.BluetoothAddress. Pure/testable: this is
// the seam that lets the watcher's Received handler match advertisements by address without
// resolving any device. See docs/plans/fix-devicehost-ble-memory-leak/.
public static class BluetoothAddressParser
{
	// A Bluetooth address is 48 bits = exactly 12 hex digits, no separators.
	private const int AddressLength = 12;

	// Returns false (address left at 0) for null/empty input, any length other than 12, or any
	// non-hex character. AllowHexSpecifier alone — no whitespace styles — so a padded string of
	// the right length cannot smuggle in a shorter address.
	public static bool TryParse(string? deviceId, out ulong address)
	{
		address = 0;

		if (deviceId is null || deviceId.Length != AddressLength)
			return false;

		return ulong.TryParse(deviceId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
	}
}
