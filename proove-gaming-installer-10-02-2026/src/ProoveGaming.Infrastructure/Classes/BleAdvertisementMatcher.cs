namespace ProoveGaming.Infrastructure.Classes;

// Immutable snapshot of the tracked Bluetooth devices, in the shape the WinRT advertisement
// watcher's Received handler needs. That handler fires for EVERY advertisement in radio range,
// so the non-match path must be as close to free as possible: one ulong dictionary lookup,
// no allocation, nothing resolved. The name fallback exists only for a tracked device whose
// InTheHand Id failed to parse as an address (not expected on Windows, where BluetoothDevice.Id
// is the 12-hex-digit address string — but a fallback beats a dead reconnect path); the fallback
// list is empty in the normal case, so LocalName is never even read per advertisement.
//
// Instances are immutable by construction and swapped wholesale (a volatile field in the
// discovery service), so the handler never needs a lock. Pure/testable — no WinRT or InTheHand
// types. See docs/plans/fix-devicehost-ble-memory-leak/.
public sealed class BleAdvertisementMatcher
{
	private readonly Dictionary<ulong, string> _addressToDeviceId;
	private readonly (string NameUpper, string DeviceId)[] _nameFallbacks;

	// addressToDeviceId: parsed Bluetooth address → tracked device id.
	// nameFallbacks: (upper-cased catalog Bluetooth name, tracked device id) pairs for the ids
	// that could not be parsed; entries with a blank name are dropped so they can never match.
	public BleAdvertisementMatcher(
		IEnumerable<KeyValuePair<ulong, string>> addressToDeviceId,
		IEnumerable<(string NameUpper, string DeviceId)> nameFallbacks)
	{
		ArgumentNullException.ThrowIfNull(addressToDeviceId);
		ArgumentNullException.ThrowIfNull(nameFallbacks);

		_addressToDeviceId = new Dictionary<ulong, string>();
		foreach (var pair in addressToDeviceId)
			_addressToDeviceId[pair.Key] = pair.Value;

		_nameFallbacks = nameFallbacks
			.Where(f => !string.IsNullOrWhiteSpace(f.NameUpper))
			.ToArray();
	}

	// True when any tracked id failed to parse, i.e. the LocalName fallback can ever match
	// anything. The watcher's Received handler checks this BEFORE touching the advertisement's
	// LocalName, so the per-advertisement WinRT projection + HSTRING-to-string marshal is skipped
	// entirely in the normal (all-ids-parsed) case.
	public bool HasNameFallbacks => _nameFallbacks.Length > 0;

	// The hot-path decision: does this advertisement belong to a tracked device? Address lookup
	// first (the entire cost for a foreign advertisement); the LocalName Contains (ordinal, on
	// the upper-cased name) runs only when a fallback entry exists at all.
	public bool TryMatch(ulong address, string? localName, out string deviceId)
	{
		if (_addressToDeviceId.TryGetValue(address, out var byAddress))
		{
			deviceId = byAddress;
			return true;
		}

		if (_nameFallbacks.Length > 0 && !string.IsNullOrEmpty(localName))
		{
			var upper = localName.ToUpperInvariant();
			foreach (var (nameUpper, id) in _nameFallbacks)
			{
				if (upper.Contains(nameUpper, StringComparison.Ordinal))
				{
					deviceId = id;
					return true;
				}
			}
		}

		deviceId = string.Empty;
		return false;
	}
}
