using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class DeviceExtendedKeysServiceFactory : IDeviceExtendedKeysServiceFactory
{
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IDeviceInfoProvider _deviceInfoProvider;
	private readonly ILogger<DeviceExtendedKeysServiceFactory> _logger;

	public DeviceExtendedKeysServiceFactory(
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		ILogger<DeviceExtendedKeysServiceFactory> logger)
	{
		_hidDeviceProvider = hidDeviceProvider ?? throw new ArgumentNullException(nameof(hidDeviceProvider));
		_deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public IKeyboardExtendedKeysService? CreateForDevice(int vendorId, int productId)
	{
		// Same wired-entry lookup as DeviceActuationServiceFactory: the vendor dongle strips the
		// vendor HID channel, so the advanced-key area is reachable over the cable only.
		var config = _deviceInfoProvider.DevicesMaps.FirstOrDefault(m =>
			!m.HasBluetooth &&
			m.VendorId == vendorId.ToString() &&
			m.ProductId == productId.ToString());

		if (config == null)
		{
			_logger.LogWarning("[EXT] No wired config entry for VID={VID} PID={PID}", vendorId, productId);
			return null;
		}

		var name = config.Name ?? string.Empty;

		if (name.Equals("Cyrex", StringComparison.OrdinalIgnoreCase) ||
			name.Equals("Aress", StringComparison.OrdinalIgnoreCase))
		{
			var device = SmartInfiniteInterfaceSelector.Select(
				_hidDeviceProvider.GetDevices(), vendorId, productId, _logger, $"EXT {name}");

			return device is null ? null : new SmartInfiniteExtendedKeysService(device, name, _logger);
		}

		_logger.LogWarning("[EXT] Device Name='{Name}' (VID={VID} PID={PID}) has no advanced-key area",
			name, vendorId, productId);
		return null;
	}
}
