using ProoveGaming.Infrastructure.Classes;
namespace ProoveGaming.DeviceHost;

// Bound from appsettings "AllowedDevices". Each device carries its shared settings once plus a list
// of transports (cable / dongle / Bluetooth); DeviceMapExpander flattens it into the per-transport
// DeviceMap rows the rest of the DeviceHost consumes.
public class AllowedDevices
{
    public List<DeviceGroupConfig> Devices { get; set; } = [];
}
