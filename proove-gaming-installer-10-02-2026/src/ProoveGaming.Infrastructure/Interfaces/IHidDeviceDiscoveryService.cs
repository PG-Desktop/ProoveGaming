using System.Collections.Generic;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;

namespace ProoveGaming.Infrastructure.Interfaces;

public interface IHidDeviceDiscoveryService
{
	void Start();
	void Stop();

	event EventHandler<HidDeviceListChangedEventArgs> HidDeviceListUsbConnected;
	event EventHandler<HidDeviceListChangedEventArgs> HidDeviceListBluetoothAdapterConnected;

	IReadOnlyList<HidDeviceState> GetClientUsbDevicesList();
	(int batteryValue, bool isCharging) TryGetBatteryLevel(int vendorId, int productId);

	// Answered from the service's in-memory HID list — no enumeration, no allocation — so a
	// poll loop can gate its work on presence without touching any device API.
	bool IsHidDevicePresent(int vendorId, int productId);
}
