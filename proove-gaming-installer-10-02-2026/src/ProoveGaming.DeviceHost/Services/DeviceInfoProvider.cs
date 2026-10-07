using Microsoft.Extensions.Options;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.DeviceHost.Services
{
    // Collects device information from configuration and should collect a device id from user session in the future.
    internal class DeviceInfoProvider : IDeviceInfoProvider
    {
        private readonly List<DeviceMap> _deviceMaps;

        public DeviceInfoProvider(IOptions<AllowedDevices> configurationSettings)
        {
            if (configurationSettings == null) 
            {
                throw new ArgumentNullException(nameof(configurationSettings));
            }

            // One grouped device × N transports -> N per-transport DeviceMap rows (the shape the
            // rest of the DeviceHost matches on). See DeviceMapExpander.
            _deviceMaps = DeviceMapExpander.Expand(configurationSettings.Value.Devices);
        }

        public IReadOnlyList<DeviceMap> DevicesMaps => _deviceMaps;

        public string GetClientName(string productId, string vendorId, string? productName = null)
        {
            // Prefer entries that have a matching ProductName (specific match over generic)
            if (!string.IsNullOrEmpty(productName))
            {
                var specific = _deviceMaps.FirstOrDefault(d =>
                    d.ProductId == productId && d.VendorId == vendorId &&
                    string.Equals(d.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (specific != null)
                    return specific.Name;
            }

            // Fall back to entries with no ProductName restriction
            var fallback = _deviceMaps.FirstOrDefault(d =>
                d.ProductId == productId && d.VendorId == vendorId &&
                string.IsNullOrEmpty(d.ProductName));

            return fallback?.Name ?? string.Empty;
        }

        public string GetClientName(string deviceId)
        {
            if (!string.IsNullOrEmpty(deviceId) && 
                _deviceMaps.FirstOrDefault(d => d.DeviceId == deviceId) is DeviceMap btDevice)
            {
                return btDevice.Name;
            }
            
            return string.Empty;
        }
    }
}
