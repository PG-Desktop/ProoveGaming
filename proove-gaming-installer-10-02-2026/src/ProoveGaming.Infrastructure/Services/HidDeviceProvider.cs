using ProoveGaming.Infrastructure.Interfaces;
using HidSharp;

namespace ProoveGaming.Infrastructure.Services;

public class HidDeviceProvider : IHidDeviceProvider
{
	public void SubscribeToDeviceListChanged(EventHandler<DeviceListChangedEventArgs> eventHandler)
	{
		if (eventHandler == null) throw new ArgumentNullException(nameof(eventHandler));

		DeviceList.Local.Changed += eventHandler;
	}

	public void UnsubscribeFromDeviceListChanged(EventHandler<DeviceListChangedEventArgs> eventHandler)
	{
		if (eventHandler == null) throw new ArgumentNullException(nameof(eventHandler));

		DeviceList.Local.Changed -= eventHandler;
	}

	public List<HidDevice> GetDevices() => DeviceList.Local.GetHidDevices().ToList();
}
