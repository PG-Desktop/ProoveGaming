using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class DeviceActuationServiceFactory : IDeviceActuationServiceFactory
{
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IDeviceInfoProvider _deviceInfoProvider;
	private readonly ILogger<DeviceActuationServiceFactory> _logger;

	public DeviceActuationServiceFactory(
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		ILogger<DeviceActuationServiceFactory> logger)
	{
		_hidDeviceProvider = hidDeviceProvider ?? throw new ArgumentNullException(nameof(hidDeviceProvider));
		_deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public IKeyboardActuationService? CreateForDevice(int vendorId, int productId)
	{
		// Same wired-entry lookup as DeviceKeyMappingServiceFactory: actuation is wired-only for
		// the same reason key remap is — the vendor dongle strips vendor HID channels.
		var config = _deviceInfoProvider.DevicesMaps.FirstOrDefault(m =>
			!m.HasBluetooth &&
			m.VendorId == vendorId.ToString() &&
			m.ProductId == productId.ToString());

		if (config == null)
		{
			_logger.LogWarning("[ACT] No wired config entry for VID={VID} PID={PID}", vendorId, productId);
			return null;
		}

		var name = config.Name ?? string.Empty;

		if (name.Equals("Cyrex", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Aress", StringComparison.OrdinalIgnoreCase))
			return CreateSmartInfiniteActuationService(vendorId, productId, name);

		if (name.Equals("T800", StringComparison.OrdinalIgnoreCase))
			return CreateT800ActuationService(vendorId, productId);

		_logger.LogWarning("[ACT] Device Name='{Name}' (VID={VID} PID={PID}) is not an actuation-capable keyboard",
			name, vendorId, productId);
		return null;
	}

	private IKeyboardActuationService? CreateSmartInfiniteActuationService(int vendorId, int productId, string modelName)
	{
		var device = SmartInfiniteInterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, $"ACT {modelName}");

		return device is null ? null : new SmartInfiniteActuationService(device, modelName, _logger);
	}

	// T800 speaks RY5088 over HID FEATURE reports on mi_02 — selection lives in
	// Ry5088InterfaceSelector, shared with the key-mapping and macro factories.
	private IKeyboardActuationService? CreateT800ActuationService(int vendorId, int productId)
	{
		var device = Ry5088InterfaceSelector.Select(
			_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, "ACT");

		return device is null ? null : new T800KeyboardActuationService(device, _logger);
	}
}
