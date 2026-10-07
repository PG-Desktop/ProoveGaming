using HidSharp;

namespace ProoveGaming.Infrastructure.Interfaces;

public interface IHidDeviceProvider
{
    void SubscribeToDeviceListChanged(EventHandler<DeviceListChangedEventArgs> eventHandler);
    void UnsubscribeFromDeviceListChanged(EventHandler<DeviceListChangedEventArgs> eventHandler);
    List<HidDevice> GetDevices();
}
