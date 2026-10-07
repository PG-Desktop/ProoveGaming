using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces
{
    public interface IDeviceInfoProvider
    {
        IReadOnlyList<DeviceMap> DevicesMaps { get; }
        string GetClientName(string productId, string vendorId, string? productName = null);
        string GetClientName(string deviceId);
    }
}
