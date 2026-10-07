using ProoveGaming.GrpcDependencies.Device;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.DeviceHost.Services;

public class HidDeviceMapRegistry
{
	private readonly Dictionary<string, DeviceMap> _maps = new();
	private readonly Dictionary<string, DeviceMap> _mapsByPidVid = new();
	// Product name → its BLUETOOTH row. Separate from _mapsByPidVid because that one is keyed by
	// vid/pid and de-duplicates: Cosmo and Starlight both advertise 0x05D6/0x000A over Bluetooth, so
	// one of their rows was dropped there and the survivor answered for both. A by-name question
	// needs a by-name index.
	private readonly Dictionary<string, DeviceMap> _bluetoothMapsByName = new();
	// Product name → the product id its settings live under, for merged-transport products only.
	private readonly Dictionary<string, int> _settingsProductIdByName = new();
	// Product name → the vendor id its settings live under. The twin of the map above, pinned from
	// the SAME primary row so the two can never name different transports as canonical.
	private readonly Dictionary<string, int> _settingsVendorIdByName = new();
	// Product name → every distinct vendor id the product's transports enumerate under (RAMPAGE:
	// {10473, 1389}). Merged products only; the App uses it to find and heal alternate buckets.
	private readonly Dictionary<string, List<int>> _transportVendorIdsByName = new();

	public HidDeviceMapRegistry(IDeviceInfoProvider deviceInfoProvider)
	{
		ArgumentNullException.ThrowIfNull(deviceInfoProvider);

		// A merged-transport product (e.g. TAIPAN: cable + dongle + Bluetooth) reaches the host
		// under several product ids. Pin one id per product so switching transport doesn't move
		// its settings bucket. Prefer the wired entry; fall back to the product's first entry.
		foreach (var product in deviceInfoProvider.DevicesMaps
			.Where(m => m.MergeTransports && !string.IsNullOrEmpty(m.Name))
			.GroupBy(m => m.Name.ToUpper()))
		{
			// A transport may carry its own vendor id (RAMPAGE's 2.4G receiver enumerates under 1389
			// against the board's 10473; COSMO's and STARLIGHT's Bluetooth links under 1494 against
			// 3468). The VENDOR is therefore pinned here too, from the same primary row as the
			// product id, and the App keys its settings buckets by that pinned pair instead of by the
			// live transport's. Before this, such a transport kept a settings bucket of its own and
			// the user watched RGB, DPI, actuation, key remaps and presets disappear on a transport
			// switch. transport_vendor_ids carries the alternates so the App can heal rows already
			// written under them.
			// Wired first, then any NON-Bluetooth row, and only then whatever is first. The middle
			// step matters for a product with no cable (Starlight: dongle + Bluetooth): without it
			// the pin came from `product.First()`, i.e. from catalog ORDER, and listing the
			// Bluetooth transport first would pin settings to its product id — which is an ODM
			// constant shared with Cosmo, so both headsets would collapse into one settings bucket
			// and every stored preset would be orphaned under the old id.
			var primary = product.FirstOrDefault(m =>
					string.Equals(m.ConnectionType, "Wired", StringComparison.OrdinalIgnoreCase))
				?? product.FirstOrDefault(m => !m.HasBluetooth)
				?? product.First();

			if (int.TryParse(primary.ProductId, out var settingsProductId))
				_settingsProductIdByName[product.Key] = settingsProductId;

			if (int.TryParse(primary.VendorId, out var settingsVendorId))
				_settingsVendorIdByName[product.Key] = settingsVendorId;

			// Distinct, and deliberately including the pinned vendor: the App reads this as "every
			// bucket this product can own" and skips the pinned one itself. An unparseable or zero
			// vendor id is dropped — 0 is the "identity unknown" sentinel on the wire and must never
			// reach the App as a re-key source.
			var transportVendorIds = new List<int>();
			foreach (var transport in product)
			{
				if (int.TryParse(transport.VendorId, out var vid) && vid > 0 && !transportVendorIds.Contains(vid))
					transportVendorIds.Add(vid);
			}
			if (transportVendorIds.Count > 0)
				_transportVendorIdsByName[product.Key] = transportVendorIds;
		}

		foreach (var m in deviceInfoProvider.DevicesMaps)
		{
			if (string.IsNullOrEmpty(m.Name) || string.IsNullOrEmpty(m.DeviceName))
				continue;

			var key = BuildKey(m.Name, m.ProductName);
			if (_maps.TryGetValue(key, out var existing))
			{
				if (existing.HasBluetooth && !m.HasBluetooth)
					_maps[key] = m;
			}
			else
			{
				_maps[key] = m;
			}

			var pidVidKey = BuildPidVidKey(m.VendorId, m.ProductId, m.HasBluetooth);
			if (!_mapsByPidVid.ContainsKey(pidVidKey))
				_mapsByPidVid[pidVidKey] = m;

			if (m.HasBluetooth)
				_bluetoothMapsByName.TryAdd(m.Name.ToUpper(), m);
		}
	}

	public DeviceMap? Find(string? clientName, string? productName)
	{
		if (string.IsNullOrEmpty(clientName)) return null;

		if (!string.IsNullOrEmpty(productName) &&
			_maps.TryGetValue(BuildKey(clientName, productName), out var exact))
			return exact;

		return _maps.TryGetValue(BuildKey(clientName, null), out var fallback) ? fallback : null;
	}

	public DeviceCatalogEntry? FindCatalogEntry(string? clientName, string? productName)
	{
		var map = Find(clientName, productName);
		return map == null ? null : ToCatalogEntry(map);
	}

	// Bluetooth lookup. Find() resolves by NAME, and that index deliberately prefers the non-Bluetooth
	// row (see the constructor), so for a multi-transport product it returns the WIRED entry — whose
	// per-transport fields then describe the wrong transport. That is how a Bluetooth discovery
	// stamped SupportsConfiguration = true onto a TAIPAN card whose live connection is the dongle,
	// re-enabling Save Preset at app start until some later USB event corrected it.
	//
	// Prefers a Bluetooth row for the same product name, falling back to the name lookup so a device
	// that has no Bluetooth entry behaves exactly as before.
	public DeviceCatalogEntry? FindBluetoothCatalogEntry(string? clientName)
	{
		if (string.IsNullOrEmpty(clientName)) return null;

		// Indexed by NAME. Reading _mapsByPidVid here silently lost every product that shares a
		// Bluetooth id pair with another: only the first row for 0x05D6/0x000A survived that
		// dictionary, so a Starlight fell through to the name lookup below and was handed the
		// DONGLE row — no battery indicator, and Save Preset enabled on a link that cannot
		// configure. See docs/plans/add-starlight-bluetooth/ (review round 1, B1).
		return _bluetoothMapsByName.TryGetValue(clientName.ToUpper(), out var bt)
			? ToCatalogEntry(bt)
			: FindCatalogEntry(clientName, productName: null);
	}

	public DeviceCatalogEntry? FindCatalogEntryByPidVid(string? vendorId, string? productId)
	{
		if (string.IsNullOrEmpty(vendorId) || string.IsNullOrEmpty(productId))
			return null;

		// Prefer non-Bluetooth (dongle/wired) entry for HID USB matching
		var nonBtKey = BuildPidVidKey(vendorId, productId, hasBluetooth: false);
		if (_mapsByPidVid.TryGetValue(nonBtKey, out var nonBtMap))
			return ToCatalogEntry(nonBtMap);

		var btKey = BuildPidVidKey(vendorId, productId, hasBluetooth: true);
		if (_mapsByPidVid.TryGetValue(btKey, out var btMap))
			return ToCatalogEntry(btMap);

		return null;
	}

	private static string BuildKey(string name, string? productName) =>
		string.IsNullOrEmpty(productName)
			? name.ToUpper()
			: $"{name.ToUpper()}|{productName.ToUpper()}";

	private static string BuildPidVidKey(string? vendorId, string? productId, bool hasBluetooth) =>
		$"{vendorId}:{productId}:{(hasBluetooth ? "bt" : "usb")}";

	private DeviceCatalogEntry ToCatalogEntry(DeviceMap m)
	{
		var entry = new DeviceCatalogEntry
		{
			Name = m.Name.ToUpper(),
			ProductName = m.ProductName ?? string.Empty,
			DeviceName = m.DeviceName ?? string.Empty,
			DisplayName = m.DisplayName ?? string.Empty,
			DeviceType = m.DeviceType ?? string.Empty,
			CardType = m.CardType ?? string.Empty,
			ConnectionType = m.ConnectionType ?? string.Empty,
			ImagePath = m.ImagePath ?? string.Empty,
			RgbImagePath = m.RgbImagePath ?? string.Empty,
			HasBluetooth = m.HasBluetooth,
			SupportsRgb = m.SupportsRgb,
			SupportsButtonRemap = m.SupportsButtonRemap,
			SupportsMacros = m.SupportsMacros,
			SupportsActuation = m.SupportsActuation,
			SupportsExtendedKeys = m.SupportsExtendedKeys,
			MergeTransports = m.MergeTransports,
			SettingsProductId = ResolveSettingsProductId(m),
			SettingsVendorId = ResolveSettingsVendorId(m),
			// 0 = unset in the proto; the App maps that to a 1.0 render scale.
			ImageScale = m.ImageScale ?? 0,
			// 0 = unset in the proto; the App draws no overlay scrim.
			KeyOverlayArtDim = m.KeyOverlayArtDim ?? 0,
			// 0 = unset in the proto; the App falls back to its default macro limits.
			MacroMaxDelayMs = m.Macros?.MaxDelayMs ?? 0,
			MacroMaxRepetitions = m.Macros?.MaxRepetitions ?? 0,
			// 0 / "" = unset in the proto; the App falls back to its built-in constant for that
			// aspect. See docs/device-defaults.md.
			DefaultDpi = m.Defaults?.Dpi ?? 0,
			DefaultReportRateHz = m.Defaults?.ReportRateHz ?? 0,
			DefaultButtonDebounceMs = m.Defaults?.ButtonDebounceMs ?? 0,
			DefaultLiftOffDistanceMm = m.Defaults?.LiftOffDistanceMm ?? 0,
			DefaultRgbEnabled = m.Defaults?.RgbEnabled ?? false,
			DefaultRgbEffect = m.Defaults?.RgbEffect ?? string.Empty,
			DefaultRgbColorHex = m.Defaults?.RgbColorHex ?? string.Empty,
			DefaultRgbBrightness = m.Defaults?.RgbBrightness ?? 0,
			DefaultRgbSpeed = m.Defaults?.RgbSpeed ?? 0,
			// Negative on the wire: see the proto comment. Config says what IS supported.
			ConfigurationUnsupported = !m.SupportsConfiguration,
			// "" = unset on the wire; the App reads that as OperatingSystem.
			AudioVolumeTarget = m.AudioVolumeTarget ?? string.Empty,
			// Negative on the wire, like ConfigurationUnsupported. Config says what IS supported.
			BatteryUnsupported = !m.SupportsBattery,
			// 0 = unparseable/unset; the App treats that as "identity unknown" (pre-existing rows
			// without VID/PID behave exactly as before these fields existed).
			VendorId = int.TryParse(m.VendorId, out var vendorId) ? vendorId : 0,
			ProductId = int.TryParse(m.ProductId, out var productId) ? productId : 0,
			// 0 = unset on all five, which is what the App reads as "use your built-in bounds".
			DpiMin = m.DpiMin ?? 0,
			DpiMax = m.DpiMax ?? 0,
			DpiStep = m.DpiStep ?? 0,
			DpiSlotMin = m.DpiSlotMin ?? 0,
			DpiSlotMax = m.DpiSlotMax ?? 0,
		};
		// Merged products only: a single-transport device has exactly one vendor and nothing to heal,
		// and sending it a one-element list would invite the App to consider a migration for it.
		if (m.MergeTransports && !string.IsNullOrEmpty(m.Name) &&
			_transportVendorIdsByName.TryGetValue(m.Name.ToUpper(), out var transportVendorIds))
			entry.TransportVendorIds.AddRange(transportVendorIds);
		if (m.SupportedMouseSettings is { Count: > 0 })
			entry.SupportedMouseSettings.AddRange(m.SupportedMouseSettings);
		if (m.SupportedAudioSettings is { Count: > 0 })
			entry.SupportedAudioSettings.AddRange(m.SupportedAudioSettings);
		if (m.DpiStages is { Count: > 0 })
			entry.DpiStages.AddRange(m.DpiStages);
		if (m.Hotspots is { Count: > 0 })
			entry.Hotspots.AddRange(m.Hotspots.Select(h => new DeviceHotspot
			{
				Id = h.Id,
				ButtonX = h.ButtonX,
				ButtonY = h.ButtonY,
				TargetX = h.TargetX,
				TargetY = h.TargetY,
				LabelSide = h.LabelSide ?? "Left",
				Disabled = h.Disabled,
			}));
		return entry;
	}

	// The product id a device persists settings under: the product-wide id for a merged-transport
	// product (so cable/dongle/BT share one bucket), otherwise the device's own id.
	private int ResolveSettingsProductId(DeviceMap m)
	{
		if (m.MergeTransports && !string.IsNullOrEmpty(m.Name) &&
			_settingsProductIdByName.TryGetValue(m.Name.ToUpper(), out var shared))
			return shared;

		return int.TryParse(m.ProductId, out var own) ? own : 0;
	}

	// The vendor id a device persists settings under: the product-wide id for a merged-transport
	// product (so a transport that enumerates under its own vendor — RAMPAGE's receiver, COSMO's
	// and STARLIGHT's Bluetooth links — shares the product's one bucket), otherwise the device's
	// own id. Pinned from the same primary row as ResolveSettingsProductId, by construction.
	private int ResolveSettingsVendorId(DeviceMap m)
	{
		if (m.MergeTransports && !string.IsNullOrEmpty(m.Name) &&
			_settingsVendorIdByName.TryGetValue(m.Name.ToUpper(), out var shared))
			return shared;

		return int.TryParse(m.VendorId, out var own) ? own : 0;
	}
}
