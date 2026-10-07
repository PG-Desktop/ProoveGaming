using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class DeviceRgbServiceFactory : IDeviceRgbServiceFactory
{
	private readonly IHidDeviceProvider hidDeviceProvider;
	private readonly IDeviceInfoProvider deviceInfoProvider;
	private readonly ILogger<EasenMouseService> logger;

	// Cache service instances so their internal caches (_lastParam0, _lastBlock0,
	// _cachedLed*) survive across gRPC calls. Keyed by (VID, PID, deviceKey) so two identical
	// units get their own instance opening their own physical interface. The factory is a
	// singleton, so this dictionary lives until the next HID device-list change clears it (see
	// OnDeviceListChanged): a cached service is bound to a specific DevicePath, and a mouse
	// replugged into a different USB port would otherwise keep being served an instance bound
	// to the old, dead path until DeviceHost restarts.
	private readonly ConcurrentDictionary<(int, int, string), EasenMouseService> _serviceCache = new();
	// Most-recent EVICTED Easen instance per unit, parked as a state donor: eviction must not
	// throw away what the service knows the device holds (_lastParam0, _lastBlock0, _cachedLed*,
	// the flash-commit record) — an unrelated USB plug event changes none of that, and losing it
	// meant the next narrow apply wrote factory defaults (report rate / active DPI stage) over the
	// user's and re-committed an unchanged key matrix to flash. CreateEasenMouseService adopts the
	// donor's state into the freshly-bound instance and removes it. Bounded by the number of
	// distinct units ever seen, so no cleanup path is needed.
	private readonly ConcurrentDictionary<(int, int, string), EasenMouseService> _evictedServiceCache = new();
	// Separate cache for the Telink (TAIPAN) service — same keying rationale and the same
	// device-list-change invalidation as the Easen cache.
	private readonly ConcurrentDictionary<(int, int, string), TelinkMouseService> _telinkCache = new();
	// One cache for every keyboard RGB service, mirroring the mouse caches above: constructing one
	// runs a full HID enumeration and logs its interface selection, and the DPI monitor's poll and
	// gRPC calls both hit CreateForDevice constantly. Null (device absent / interface not found) is
	// never cached so a keyboard plugged in later is created on the next call. Cleared on every
	// HID device-list change together with the mouse caches (see OnDeviceListChanged), for the
	// same DevicePath-staleness reason.
	// Keyed by (VID, PID) ONLY, unlike the mouse caches: keyboard creation is not unit-aware yet
	// (Create*KeyboardRgbService ignores deviceKey and selects the interface by the vendor rule
	// alone), so including deviceKey in the key would let mixed call sites (gRPC's Container ID vs
	// "") cache duplicate instances bound to the same physical interface.
	private readonly ConcurrentDictionary<(int, int), IDeviceRgbService> _keyboardCache = new();
	// VID/PIDs already reported as non-RGB, so the "not in config" note is logged once instead of
	// on every DPI-monitor poll of a non-RGB Col04 device.
	private readonly ConcurrentDictionary<(int, int), bool> _loggedMissingConfig = new();

	public DeviceRgbServiceFactory(
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		ILogger<EasenMouseService> logger)
	{
		this.hidDeviceProvider = hidDeviceProvider;
		this.deviceInfoProvider = deviceInfoProvider;
		this.logger = logger;

		// Invalidate every service cache whenever the HID device list changes (same signal
		// DpiStageMonitor refreshes its readers on), so a replugged device gets a fresh service
		// bound to its new DevicePath on the next call. Subscribed once here and never
		// unsubscribed: the factory is a DI singleton that lives for the process lifetime, so no
		// unsubscribe path is needed.
		hidDeviceProvider.SubscribeToDeviceListChanged(OnDeviceListChanged);
	}

	private void OnDeviceListChanged(object? sender, HidSharp.DeviceListChangedEventArgs e)
	{
		// Wholesale clear (audit C7/C11): any list change — even an unrelated USB device — drops
		// the cached service instances, so the next call re-resolves interfaces against the live
		// DevicePath. List changes are human-scale (plug/unplug) events. A creation in flight can
		// re-cache a just-staled instance for milliseconds — the same accepted race the keyboard
		// cache has; the next list change (or a failed write's retry) clears it again.
		//
		// The Easen instances are parked (not dropped) before the clear: their in-memory write
		// caches describe the device, not the interface binding, and must survive the recreation —
		// see _evictedServiceCache. The same in-flight-creation race applies to the park: an
		// instance cached between the loop and Clear() is lost without donating, costing one
		// defaults-based apply, same as a DeviceHost restart.
		foreach (var entry in _serviceCache)
			_evictedServiceCache[entry.Key] = entry.Value;
		_serviceCache.Clear();
		_telinkCache.Clear();
		_keyboardCache.Clear();
	}

	public IDeviceRgbService? CreateForDevice(int vendorId, int productId, string deviceKey)
	{
		deviceKey ??= string.Empty;

		var config = SelectConfigRow(deviceInfoProvider.DevicesMaps, vendorId, productId);

		if (config == null)
		{
			// Normal for a non-RGB device (e.g. the DPI monitor probing every Col04 interface).
			// Log once per VID/PID at debug level so it doesn't spam the console on every poll.
			if (_loggedMissingConfig.TryAdd((vendorId, productId), true))
				logger.LogDebug("[RGB] VID={VID} PID={PID} not found in config or SupportsRgb=false", vendorId, productId);
			return null;
		}

		var deviceType = config.DeviceType ?? string.Empty;
		var name = config.Name ?? string.Empty;

		if (deviceType.Contains("Mouse", StringComparison.OrdinalIgnoreCase))
		{
			// TAIPAN speaks the Telink 33-byte protocol, not Easen. Dispatch by config Name so
			// adding another Telink mouse is just a name check; every other mouse stays on Easen.
			if (name.Equals("PG TAIPAN", StringComparison.OrdinalIgnoreCase))
				return CreateTelinkMouseService(vendorId, productId, deviceKey);
			return CreateEasenMouseService(vendorId, productId, deviceKey);
		}

		// Aress and Cyrex share Rampage's SmartInfinite wire format but each has firmware quirks
		// (5-step brightness, etc.) tuned in their own services. Dispatch by the config Name so
		// adding a new SmartInfinite keyboard is a matter of listing it in appsettings.
		if (deviceType.Contains("Keyboard", StringComparison.OrdinalIgnoreCase))
		{
			var key = (vendorId, productId);
			if (_keyboardCache.TryGetValue(key, out var cachedKeyboard))
				return cachedKeyboard;

			var service = CreateKeyboardRgbService(name, config, vendorId, productId);
			if (service != null)
				_keyboardCache[key] = service;
			return service;
		}

		logger.LogWarning("[RGB] No RGB service implementation for device type '{Type}' VID={VID} PID={PID}",
			deviceType, vendorId, productId);
		return null;
	}

	// The keyboard name dispatch, split out of CreateForDevice so the cache above wraps every
	// keyboard family the same way.
	private IDeviceRgbService? CreateKeyboardRgbService(string name, DeviceMap config, int vendorId, int productId)
	{
		if (name.Equals("Aress", StringComparison.OrdinalIgnoreCase))
			return CreateAressKeyboardRgbService(vendorId, productId);
		if (name.Equals("Cyrex", StringComparison.OrdinalIgnoreCase))
			return CreateCyrexKeyboardRgbService(vendorId, productId);
		if (name.Equals("Raster", StringComparison.OrdinalIgnoreCase))
			return CreateRasterKeyboardRgbService(vendorId, productId);
		if (name.Equals("T-Racer", StringComparison.OrdinalIgnoreCase))
			return CreateTRacerKeyboardRgbService(vendorId, productId);
		if (name.Equals("T800", StringComparison.OrdinalIgnoreCase))
			return CreateT800KeyboardRgbService(vendorId, productId);
		// The 2.4G receiver relays SetGeneralInfo in its own 24-byte framing (C12) — the
		// same per-transport chunk size the key-mapping factory resolves.
		return CreateRampageKeyboardRgbService(vendorId, productId,
			DeviceKeyMappingServiceFactory.TablePayloadFor(config));
	}

	public bool IsCatalogMouse(int vendorId, int productId)
	{
		// The same row and the same DeviceType test CreateForDevice dispatches on, so this stays
		// true exactly when CreateForDevice would serve the device with an IMouseRgbService.
		var config = SelectConfigRow(deviceInfoProvider.DevicesMaps, vendorId, productId);
		return config?.DeviceType?.Contains("Mouse", StringComparison.OrdinalIgnoreCase) == true;
	}

	// Resolves the config row a device command should be served under. Wired/dongle rows win — for
	// PID collisions between transports (T-Racer uses 12370 for both cable and BT) the non-Bluetooth
	// row keeps describing the proven channel. A Bluetooth row is only a fallback, and only for an
	// RGB-capable keyboard: Rampage's RGB write works over its BLE vendor channel, while mouse
	// config over Bluetooth is known dead (TAIPAN — writes acked by the link, never applied).
	// Matches any capability this factory serves — RGB, button remap or macros — not only
	// RGB-capable ones, so a button-only mouse (e.g. TAIPAN, SupportsRgb=false) is still resolved
	// for its button write.
	internal static DeviceMap? SelectConfigRow(IEnumerable<DeviceMap> maps, int vendorId, int productId)
	{
		var vid = vendorId.ToString();
		var pid = productId.ToString();

		var wired = maps.FirstOrDefault(m =>
			!m.HasBluetooth &&
			m.VendorId == vid &&
			m.ProductId == pid &&
			(m.SupportsRgb || m.SupportsButtonRemap || m.SupportsMacros));
		if (wired != null)
			return wired;

		return maps.FirstOrDefault(m =>
			m.HasBluetooth &&
			m.VendorId == vid &&
			m.ProductId == pid &&
			m.SupportsRgb &&
			m.DeviceType?.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) == true);
	}

	// Easen protocol (Holtek HT68FB571 MCU) — identified by vendor data interface characteristics:
	// outLen=65 (64-byte output report + report-ID prefix) and featLen=9 (9-byte feature report).
	private IMouseRgbService? CreateEasenMouseService(int vendorId, int productId, string deviceKey)
	{
		var key = (vendorId, productId, deviceKey);

		// Return the cached instance so all internal state (_lastParam0, _lastBlock0,
		// _cachedLed*) persists across gRPC calls without requiring a device read.
		if (_serviceCache.TryGetValue(key, out var cached))
			return cached;

		var allInterfaces = hidDeviceProvider.GetDevices()
			.Where(d => d.VendorID == vendorId && d.ProductID == productId)
			.ToList();

		// When a specific unit is requested, keep only that physical device's interfaces
		// (Container ID match) so the right unit is opened among two identical mice. Empty
		// deviceKey = legacy/single-device: keep them all and pick the first, as before.
		if (!string.IsNullOrEmpty(deviceKey))
		{
			var forUnit = allInterfaces
				.Where(d => DeviceContainerId.TryGet(d.DevicePath) == deviceKey)
				.ToList();
			if (forUnit.Count > 0)
				allInterfaces = forUnit;
			else
				logger.LogWarning("[RGB] No interfaces matched deviceKey={Key} for VID={VID} PID={PID}; using first matching unit",
					deviceKey, vendorId, productId);
		}

		// Log all interfaces so we can diagnose read capability
		foreach (var iface in allInterfaces)
		{
			logger.LogInformation("[HID] VID={VID} PID={PID} interface: inLen={In} outLen={Out} featLen={Feat}",
				vendorId, productId,
				iface.GetMaxInputReportLength(),
				iface.GetMaxOutputReportLength(),
				iface.GetMaxFeatureReportLength());
		}

		var device = allInterfaces.FirstOrDefault(d =>
			d.GetMaxOutputReportLength() == 65 && d.GetMaxFeatureReportLength() == 9);

		if (device == null)
		{
			logger.LogWarning("[RGB] Easen vendor interface (outLen=65/featLen=9) not found for VID={VID} PID={PID}",
				vendorId, productId);
			return null;
		}

		// Find a readable companion interface (same device, featLen=9, has an input endpoint).
		// Used for read-before-write in ApplyConfigurationAsync to preserve DPI and other bytes.
		var readDevice = allInterfaces.FirstOrDefault(d =>
			d.GetMaxFeatureReportLength() == 9 &&
			d.GetMaxInputReportLength() > 0);

		if (readDevice != null)
			logger.LogInformation("[HID] Readable interface found: inLen={In}", readDevice.GetMaxInputReportLength());
		else
			logger.LogWarning("[HID] No readable interface found — param_1 read-back will be skipped");

		// The inLen=7 vendor collection (Col04) carries the DPI/state notification
		// (report 0x05) and is owned by DeviceHost's DpiStageMonitor. Its reports are DPI
		// state — not an LED switch — so this service does not open it.
		var service = new EasenMouseService(device, readDevice, logger, deviceKey);
		// A predecessor evicted by a device-list change donates its caches (last param_0/block 0,
		// LED colour, flash-commit record) so the rebind is invisible to the device: without it the
		// first apply after any USB plug event started from factory defaults. See AdoptStateFrom.
		if (_evictedServiceCache.TryRemove(key, out var evicted))
			service.AdoptStateFrom(evicted);
		_serviceCache[key] = service;
		return service;
	}

	// TAIPAN (Telink) vendor interface — the config collection carries the 33-byte report (id 0x05)
	// as an OUTPUT/INPUT report (out=33/in=33) with NO feature report, confirmed by hardware
	// enumeration. Same deviceKey filtering as the Easen path so the right unit is opened among
	// identical mice.
	private TelinkMouseService? CreateTelinkMouseService(int vendorId, int productId, string deviceKey)
	{
		var key = (vendorId, productId, deviceKey);
		if (_telinkCache.TryGetValue(key, out var cached))
			return cached;

		var allInterfaces = hidDeviceProvider.GetDevices()
			.Where(d => d.VendorID == vendorId && d.ProductID == productId)
			.ToList();

		if (!string.IsNullOrEmpty(deviceKey))
		{
			var forUnit = allInterfaces
				.Where(d => DeviceContainerId.TryGet(d.DevicePath) == deviceKey)
				.ToList();
			if (forUnit.Count > 0)
				allInterfaces = forUnit;
			else
				logger.LogWarning("[Taipan] No interfaces matched deviceKey={Key} for VID={VID} PID={PID}; using first matching unit",
					deviceKey, vendorId, productId);
		}

		foreach (var iface in allInterfaces)
		{
			logger.LogInformation("[HID] TAIPAN VID={VID} PID={PID} interface: inLen={In} outLen={Out} featLen={Feat}",
				vendorId, productId,
				iface.GetMaxInputReportLength(),
				iface.GetMaxOutputReportLength(),
				iface.GetMaxFeatureReportLength());
		}

		var device = allInterfaces.FirstOrDefault(d =>
			d.GetMaxOutputReportLength() == TaipanFrame.Length &&
			d.GetMaxInputReportLength() == TaipanFrame.Length);
		if (device == null)
		{
			logger.LogWarning("[Taipan] vendor interface (out={Len}/in={Len}) not found for VID={VID} PID={PID}",
				TaipanFrame.Length, TaipanFrame.Length, vendorId, productId);
			return null;
		}

		var service = new TelinkMouseService(device, logger, deviceKey);
		_telinkCache[key] = service;
		return service;
	}

	// Rampage vendor interface — identified by 64-byte Output and Input reports (report ID 0x06
	// followed by 63 bytes of SmartInfinite v1.4 protocol data).
	private RampageKeyboardRgbService? CreateRampageKeyboardRgbService(int vendorId, int productId, int maxPayload)
	{
		// Selected by the vendor's own rule (usage 0x20 / page 0xFF87), not by report shape: these boards
		// expose several 64/64 interfaces and only one accepts writes, so picking by shape reduces to
		// enumeration order and yields ERROR_INVALID_FUNCTION when an input-only collection comes first.
		// Key mapping selects the same way, so both subsystems land on one DevicePath — which is what
		// makes the shared DeviceGate meaningful.
		var device = SmartInfiniteInterfaceSelector.Select(
			hidDeviceProvider.GetDevices(), vendorId, productId, logger, "RGB Rampage");

		if (device == null)
		{
			return null;
		}

		return new RampageKeyboardRgbService(device, logger, maxPayload);
	}

	// Aress vendor interface — same 64-byte Output/Input report shape as Rampage.
	private AressKeyboardRgbService? CreateAressKeyboardRgbService(int vendorId, int productId)
	{
		// Selected by the vendor's own rule (usage 0x20 / page 0xFF87), not by report shape: these boards
		// expose several 64/64 interfaces and only one accepts writes, so picking by shape reduces to
		// enumeration order and yields ERROR_INVALID_FUNCTION when an input-only collection comes first.
		// Key mapping selects the same way, so both subsystems land on one DevicePath — which is what
		// makes the shared DeviceGate meaningful.
		var device = SmartInfiniteInterfaceSelector.Select(
			hidDeviceProvider.GetDevices(), vendorId, productId, logger, "RGB Aress");

		if (device == null)
		{
			return null;
		}

		return new AressKeyboardRgbService(device, logger);
	}

	// Cyrex vendor interface — same 64-byte Output/Input report shape as Rampage.
	private IDeviceRgbService? CreateCyrexKeyboardRgbService(int vendorId, int productId)
	{
		// Selected by the vendor's own rule (usage 0x20 / page 0xFF87), not by report shape: these boards
		// expose several 64/64 interfaces and only one accepts writes, so picking by shape reduces to
		// enumeration order and yields ERROR_INVALID_FUNCTION when an input-only collection comes first.
		// Key mapping selects the same way, so both subsystems land on one DevicePath — which is what
		// makes the shared DeviceGate meaningful.
		var device = SmartInfiniteInterfaceSelector.Select(
			hidDeviceProvider.GetDevices(), vendorId, productId, logger, "RGB Cyrex");

		if (device == null)
		{
			return null;
		}

		return new CyrexKeyboardRgbService(device, logger);
	}

	// Raster's control interface — mi_01 raw-HID with outLen=65/inLen=65 and report ID 0x00.
	// The 64/64 interface on this device is the FW-update channel (report ID 0x3F) and rejects
	// writes with ERROR_INVALID_FUNCTION.
	private RasterKeyboardRgbService? CreateRasterKeyboardRgbService(int vendorId, int productId)
	{
		var device = hidDeviceProvider.GetDevices().FirstOrDefault(d =>
			d.VendorID == vendorId && d.ProductID == productId &&
			d.GetMaxOutputReportLength() == 65 && d.GetMaxInputReportLength() == 65);

		if (device == null)
		{
			logger.LogWarning("[RGB] Raster raw-HID interface (outLen=65/inLen=65) not found for VID={VID} PID={PID}",
				vendorId, productId);
			return null;
		}

		return new RasterKeyboardRgbService(device, logger);
	}

	// T-Racer — same vendor family as Raster (VID 14000), assumed same mi_01 raw-HID
	// interface with report ID 0x00 and 65-byte reports.
	private TRacerKeyboardRgbService? CreateTRacerKeyboardRgbService(int vendorId, int productId)
	{
		var device = hidDeviceProvider.GetDevices().FirstOrDefault(d =>
			d.VendorID == vendorId && d.ProductID == productId &&
			d.GetMaxOutputReportLength() == 65 && d.GetMaxInputReportLength() == 65);

		if (device == null)
		{
			logger.LogWarning("[RGB] T-Racer raw-HID interface (outLen=65/inLen=65) not found for VID={VID} PID={PID}",
				vendorId, productId);
			return null;
		}

		return new TRacerKeyboardRgbService(device, logger);
	}

	// T800 (Rongyuan RY5088) — the vendor channel is a FEATURE report, not an output report, so
	// this selects on feature-report length rather than the out/in shape every other keyboard here
	// uses. The spec pins the transfer down exactly: `21 09 00 03 02 00 40 00` = SET_REPORT,
	// type 3 (Feature), report ID 0, **interface 2**, 64 bytes — hence featLen 65 (64 + report ID)
	// and the mi_02 preference below.
	//
	// The board also exposes vendor-defined collections under mi_01 (COL05), which can present a
	// feature report of the same size. Path order alone would pick between them arbitrarily, the
	// same trap SmartInfiniteInterfaceSelector exists to avoid, so mi_02 is matched explicitly and
	// the bare length match is kept only as a fallback.
	private T800KeyboardRgbService? CreateT800KeyboardRgbService(int vendorId, int productId)
	{
		var candidates = hidDeviceProvider.GetDevices()
			.Where(d => d.VendorID == vendorId && d.ProductID == productId)
			.Select(d => new { Device = d, FeatureLength = SafeFeatureReportLength(d) })
			.Where(c => c.FeatureLength == T800KeyboardRgbService.ReportLen)
			.OrderBy(c => c.Device.DevicePath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (var candidate in candidates)
			logger.LogDebug("[LED] T800 candidate featLen={Feat} path={Path}",
				candidate.FeatureLength, candidate.Device.DevicePath);

		var device = candidates
			.FirstOrDefault(c => c.Device.DevicePath?.Contains("mi_02", StringComparison.OrdinalIgnoreCase) == true)
			?.Device;

		if (device == null && candidates.Count > 0)
		{
			device = candidates[0].Device;
			logger.LogWarning("[LED] T800 no mi_02 interface matched; falling back to {Path}", device.DevicePath);
		}

		if (device == null)
		{
			logger.LogWarning("[LED] T800 vendor feature interface (featLen={Len}) not found for VID={VID} PID={PID}",
				T800KeyboardRgbService.ReportLen, vendorId, productId);
			return null;
		}

		return new T800KeyboardRgbService(device, logger);
	}

	private static int SafeFeatureReportLength(HidSharp.HidDevice device)
	{
		try { return device.GetMaxFeatureReportLength(); }
		catch { return -1; }
	}
}
