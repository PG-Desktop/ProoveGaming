using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class DeviceKeyboardMacroServiceFactory : IDeviceKeyboardMacroServiceFactory
{
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IDeviceInfoProvider _deviceInfoProvider;
	private readonly ILogger<DeviceKeyboardMacroServiceFactory> _logger;

	public DeviceKeyboardMacroServiceFactory(
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		ILogger<DeviceKeyboardMacroServiceFactory> logger)
	{
		_hidDeviceProvider = hidDeviceProvider ?? throw new ArgumentNullException(nameof(hidDeviceProvider));
		_deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public IKeyboardMacroService? CreateForDevice(int vendorId, int productId)
	{
		// Mirror DeviceKeyMappingServiceFactory: filter to the non-Bluetooth entry so PID
		// collisions between wired and BT transports resolve to the wired one. Bluetooth is
		// excluded because that transport does not expose the vendor config interface; the 2.4G
		// dongle does, and macro upload is verified working over it (Raster's only configured
		// transport is Dongle2_4G).
		var config = _deviceInfoProvider.DevicesMaps.FirstOrDefault(m =>
			!m.HasBluetooth &&
			m.VendorId == vendorId.ToString() &&
			m.ProductId == productId.ToString());

		if (config == null)
		{
			_logger.LogWarning("[MACRO] No wired config entry for VID={VID} PID={PID}", vendorId, productId);
			return null;
		}

		var name = config.Name ?? string.Empty;

		// Raster and T-Racer are the same vendor family and share this wire format, so both go to
		// one service rather than a copy each — see the remarks on RasterKeyboardMacroService.
		// The model name is a literal, not `name`: DeviceMapExpander upper-cases the config name,
		// which would render the log tags as RASTER / T-RACER.
		if (name.Equals("Raster", StringComparison.OrdinalIgnoreCase))
			return CreateRasterFamilyMacroService(vendorId, productId, "Raster");

		if (name.Equals("T-Racer", StringComparison.OrdinalIgnoreCase))
			return CreateRasterFamilyMacroService(vendorId, productId, "T-Racer");

		// The Smart Infinite family runs its own framing, so these need the other transport even
		// though the packed buffer is the same. Rampage is included: its Device Info reports 2048
		// bytes of macro space, and the full-table transfer discipline that unblocked its key
		// writes (RM1 factory update) is exactly what this service now uses. The chunk size is
		// per-transport: the 2.4G receiver relays table transfers only in 24-byte packets (C12).
		if (name.Equals("Cyrex", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Aress", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Rampage", StringComparison.OrdinalIgnoreCase))
			return CreateSmartInfiniteMacroService(vendorId, productId, name,
				DeviceKeyMappingServiceFactory.TablePayloadFor(config));

		if (name.Equals("T800", StringComparison.OrdinalIgnoreCase))
			return CreateT800MacroService(vendorId, productId);

		_logger.LogWarning("[MACRO] Device Name='{Name}' (VID={VID} PID={PID}) is not a macro-supported keyboard",
			name, vendorId, productId);
		return null;
	}

	// Both Raster and T-Racer expose the vendor-config interface on mi_01 with outLen=65/inLen=65
	// (unnumbered report, usagePage 0xFF60). Same interface the key-mapping service picks — the
	// 64/64 sibling on this family is FW-update-only and rejects vendor writes.
	private HidSharp.HidDevice? SelectVendorInterface(int vendorId, int productId, string logTag)
	{
		var candidates = _hidDeviceProvider.GetDevices()
			.Where(d => d.VendorID == vendorId && d.ProductID == productId &&
			            d.GetMaxOutputReportLength() == 65 && d.GetMaxInputReportLength() == 65)
			.ToList();

		foreach (var c in candidates)
			_logger.LogInformation("[MACRO] {Tag} candidate outLen={O} inLen={I} path={P}",
				logTag, c.GetMaxOutputReportLength(), c.GetMaxInputReportLength(), c.DevicePath);

		var device = candidates
			.OrderBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();

		if (device == null)
		{
			_logger.LogWarning("[MACRO] {Tag} vendor config interface (outLen=65/inLen=65) not found for VID={VID} PID={PID}",
				logTag, vendorId, productId);
			return null;
		}

		_logger.LogInformation("[MACRO] {Tag} selected path={Path}", logTag, device.DevicePath);
		return device;
	}

	private IKeyboardMacroService? CreateRasterFamilyMacroService(int vendorId, int productId, string modelName)
	{
		var device = SelectVendorInterface(vendorId, productId, modelName);
		return device is null ? null : new RasterKeyboardMacroService(device, modelName, _logger);
	}

	// The Smart Infinite boards expose their vendor interface differently from Raster (usage 0x20 /
	// page 0xFF87), so interface selection goes through the shared selector the key-mapping factory
	// uses rather than the outLen==65 predicate above.
	private IKeyboardMacroService? CreateSmartInfiniteMacroService(int vendorId, int productId, string modelName, int maxPayload)
	{
		var device = SmartInfiniteInterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, $"MACRO {modelName}");

		return device is null ? null : new SmartInfiniteMacroService(device, modelName, _logger, maxPayload);
	}

	private IKeyboardMacroService? CreateT800MacroService(int vendorId, int productId)
	{
		var device = Ry5088InterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, "MACRO");

		return device is null ? null : new T800KeyboardMacroService(device, "T800", _logger);
	}
}
