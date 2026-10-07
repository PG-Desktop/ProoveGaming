using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;

namespace ProoveGaming.Infrastructure.Interfaces;

public interface IBluetoothDeviceDiscoveryService
{
    event EventHandler<BluetoothDeviceConnectionChangedEventArgs>? DeviceConnectionChanged;

    Task StartDiscoveringIfNotStartedAsync(CancellationToken cancellationToken = default);
    void StopDiscovering();

    Task<IReadOnlyList<BluetoothDeviceState>> GetBluetoothDevicesAsync();
}
