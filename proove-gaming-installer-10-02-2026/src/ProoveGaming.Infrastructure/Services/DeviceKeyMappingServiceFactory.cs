using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class DeviceKeyMappingServiceFactory : IDeviceKeyMappingServiceFactory
{
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IDeviceInfoProvider _deviceInfoProvider;
	private readonly ILogger<DeviceKeyMappingServiceFactory> _logger;

	public DeviceKeyMappingServiceFactory(
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		ILogger<DeviceKeyMappingServiceFactory> logger)
	{
		_hidDeviceProvider = hidDeviceProvider ?? throw new ArgumentNullException(nameof(hidDeviceProvider));
		_deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public IKeyboardKeyMappingService? CreateForDevice(int vendorId, int productId)
	{
		// Filter to the non-Bluetooth entry so PID collisions between wired and BT transports
		// resolve to the wired one (T-Racer PID 12370 is used for both). Deliberately stricter
		// than DeviceRgbServiceFactory.SelectConfigRow: RGB falls back to a Bluetooth keyboard row
		// (a single 0x15 write works over BLE), while key remap needs the full 384-byte 0x18
		// transfer, which no Bluetooth transport carries. The 2.4G receiver DOES carry it — such a
		// row is non-Bluetooth and matches here, with the receiver's own chunking below.
		var config = _deviceInfoProvider.DevicesMaps.FirstOrDefault(m =>
			!m.HasBluetooth &&
			m.VendorId == vendorId.ToString() &&
			m.ProductId == productId.ToString());

		if (config == null)
		{
			_logger.LogWarning("[KEY] No wired config entry for VID={VID} PID={PID}", vendorId, productId);
			return null;
		}

		var name = config.Name ?? string.Empty;

		if (name.Equals("Raster", StringComparison.OrdinalIgnoreCase))
			return CreateRasterKeyboardKeyMappingService(vendorId, productId);

		if (name.Equals("T-Racer", StringComparison.OrdinalIgnoreCase))
			return CreateTRacerKeyboardKeyMappingService(vendorId, productId);

		// Rampage keeps its dedicated service: its firmware reports a Device Info key count of 0,
		// so the shared service's key-count gate would refuse it — yet remapping works, provided
		// the write is the factory-confirmed shape (bare 0x18, full 384 bytes, no 0x10/0x11).
		if (name.Equals("Rampage", StringComparison.OrdinalIgnoreCase))
			return CreateRampageKeyboardKeyMappingService(vendorId, productId, TablePayloadFor(config));

		// The rest of the Smart Infinite family shares one protocol and one service; whether a
		// given unit can actually be remapped is decided at runtime from its Device Info key count.
		if (name.Equals("Cyrex", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Aress", StringComparison.OrdinalIgnoreCase))
			return CreateSmartInfiniteKeyMappingService(vendorId, productId, name);

		if (name.Equals("T800", StringComparison.OrdinalIgnoreCase))
			return CreateT800KeyMappingService(vendorId, productId);

		_logger.LogWarning("[KEY] Device Name='{Name}' (VID={VID} PID={PID}) is not a remap-supported keyboard",
			name, vendorId, productId);
		return null;
	}

	// Smart Infinite uses 64/64 reports, not the 65/65 the Raster family uses, and exposes several
	// interfaces of that shape — only one accepts writes. Selection is by the vendor's own rule
	// (usage 0x20 / page 0xFF87); see SmartInfiniteInterfaceSelector.
	private IKeyboardKeyMappingService? CreateSmartInfiniteKeyMappingService(int vendorId, int productId, string modelName)
	{
		var device = SmartInfiniteInterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, $"KEY {modelName}");

		return device is null ? null : new SmartInfiniteKeyMappingService(device, modelName, _logger);
	}

	// T800 speaks RY5088 over HID FEATURE reports on mi_02 — selection lives in
	// Ry5088InterfaceSelector, shared with the actuation and macro factories.
	private IKeyboardKeyMappingService? CreateT800KeyMappingService(int vendorId, int productId)
	{
		var device = Ry5088InterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, "KEY");

		return device is null ? null : new T800KeyboardKeyMappingService(device, "T800", _logger);
	}

	// Both Raster and T-Racer expose the same vendor-config interface: mi_01 raw HID with
	// outLen=65/inLen=65 (unnumbered report, usagePage 0xFF60). The 64/64 sibling interface on
	// this family is the FW-update channel and rejects vendor writes.
	private HidSharp.HidDevice? SelectVendorInterface(int vendorId, int productId, string logTag)
	{
		var candidates = _hidDeviceProvider.GetDevices()
			.Where(d => d.VendorID == vendorId && d.ProductID == productId &&
			            d.GetMaxOutputReportLength() == 65 && d.GetMaxInputReportLength() == 65)
			.ToList();

		foreach (var c in candidates)
			_logger.LogInformation("[KEY] {Tag} candidate outLen={O} inLen={I} path={P}",
				logTag, c.GetMaxOutputReportLength(), c.GetMaxInputReportLength(), c.DevicePath);

		var device = candidates
			.OrderBy(d => d.DevicePath, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();

		if (device == null)
		{
			_logger.LogWarning("[KEY] {Tag} vendor config interface (outLen=65/inLen=65) not found for VID={VID} PID={PID}",
				logTag, vendorId, productId);
			return null;
		}

		_logger.LogInformation("[KEY] {Tag} selected path={Path}", logTag, device.DevicePath);
		return device;
	}

	private IKeyboardKeyMappingService? CreateRasterKeyboardKeyMappingService(int vendorId, int productId)
	{
		var device = SelectVendorInterface(vendorId, productId, "Raster");
		return device is null ? null : new RasterKeyboardKeyMappingService(device, _logger);
	}

	private IKeyboardKeyMappingService? CreateTRacerKeyboardKeyMappingService(int vendorId, int productId)
	{
		var device = SelectVendorInterface(vendorId, productId, "T-Racer");
		return device is null ? null : new TRacerKeyboardKeyMappingService(device, _logger);
	}

	private IKeyboardKeyMappingService? CreateRampageKeyboardKeyMappingService(int vendorId, int productId, int maxPayload)
	{
		var device = SmartInfiniteInterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, "KEY Rampage");

		return device is null ? null : new RampageKeyboardKeyMappingService(device, _logger, maxPayload);
	}

	// The table-transfer chunk size for the transport a config row describes. Per the factory
	// clarification C12, hardware-confirmed on the Rampage receiver (2026-08-13): the 2.4G
	// receiver relays table transfers only in 24-byte payload packets — the wired 56-byte shape
	// reads back misaligned and 56-byte-chunked writes are acked and discarded.
	internal static int TablePayloadFor(DeviceMap config) =>
		string.Equals(config.ConnectionType, "Dongle2_4G", StringComparison.OrdinalIgnoreCase)
			? SmartInfiniteProtocol.Receiver24GPayloadLength
			: SmartInfiniteProtocol.MaxPayloadLength;
}
