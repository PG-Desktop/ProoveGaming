using Google.Protobuf.WellKnownTypes;
using ProoveGaming.GrpcDependencies.Device;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;

namespace ProoveGaming.DeviceHost.Helpers;

public static class DevicesHelper
{
	public static IEnumerable<HidDeviceState> FilterDevices(this IEnumerable<HidDeviceState> fullList, IEnumerable<DeviceMap> allowedDevices)
	{
		ArgumentNullException.ThrowIfNull(fullList);
		ArgumentNullException.ThrowIfNull(allowedDevices);

		return fullList.Where(x => allowedDevices.Any(ad =>
			string.Equals(ad.ProductId, x.ProductId, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(ad.VendorId, x.VendorId, StringComparison.OrdinalIgnoreCase)));
	}

	// Collapses a device's several HID interfaces into one entry PER PHYSICAL UNIT. Grouping on
	// (ProductId, VendorId, DeviceKey) keeps two identical mice (same VID/PID, different Container
	// ID) as two entries while still merging one unit's own interfaces. A device whose Container
	// ID couldn't be read has DeviceKey = "", so all such interfaces of one (VID,PID) collapse to
	// a single entry — the legacy single-device behaviour.
	public static IEnumerable<HidDeviceState> GroupByDevice(this IEnumerable<HidDeviceState> list)
	{
		return list.GroupBy(c => new
		{
			c.ProductId,
			c.VendorId,
			c.DeviceKey,
		})
		.Select(g => new HidDeviceState()
		{
			ProductId = g.Key.ProductId,
			VendorId = g.Key.VendorId,
			DeviceKey = g.Key.DeviceKey,
			FriendlyName = g.First().FriendlyName,
			ClientName = g.First().ClientName,
			ReleaseNumber = g.First().ReleaseNumber,
			ReleaseNumberBcd = g.First().ReleaseNumberBcd,
			Manufacturer = g.First().Manufacturer,
			SerialPorts = g.SelectMany(x => x.SerialPorts).ToArray(),
			SerialNumber = g.First().SerialNumber,
			ProductName = g.First().ProductName,
		});
	}

	public static HidDeviceResponse Map(HidDeviceState hidDeviceState)
	{
		ArgumentNullException.ThrowIfNull(hidDeviceState);

		var message = new HidDeviceResponse
		{
			ProductId = hidDeviceState.ProductId,
			VendorId = hidDeviceState.VendorId,
			FriendlyName = hidDeviceState.FriendlyName,
			Manufacturer = hidDeviceState.Manufacturer,
			ProductName = hidDeviceState.ProductName,
			ReleaseNumber = hidDeviceState.ReleaseNumber,
			ReleaseNumberBcd = hidDeviceState.ReleaseNumberBcd,
			SerialNumber = hidDeviceState.SerialNumber,
			SerialPorts = { hidDeviceState.SerialPorts },
			ClientName = hidDeviceState.ClientName,
			DeviceKey = hidDeviceState.DeviceKey ?? string.Empty,
			LastTimeUpdated = Timestamp.FromDateTime(DateTime.UtcNow),
		};

		return message;
	}

	public static BluetoothDeviceState Map(BluetoothDeviceConnectionChangedEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);

		return new BluetoothDeviceState
		{
			DeviceId = e.DeviceId,
			Name = e.DeviceName,
			ClientName = e.ClientName,
			IsPaired = e.IsPaired,
			IsConnected = e.IsConnected,
			BatteryLevel = e.BatteryLevel,
		};
	}

	public static BluetoothDeviceResponse Map(BluetoothDeviceState bluetoothDeviceState)
	{
		ArgumentNullException.ThrowIfNull(bluetoothDeviceState);

		return new BluetoothDeviceResponse()
		{
			DeviceId = bluetoothDeviceState.DeviceId,
			Name = bluetoothDeviceState.Name,
			IsConnected = bluetoothDeviceState.IsConnected,
			IsPaired = bluetoothDeviceState.IsPaired,
			LastTimeUpdated = Timestamp.FromDateTime(DateTime.UtcNow),
			ClientName = bluetoothDeviceState.ClientName,
			BatteryLevel = bluetoothDeviceState.BatteryLevel,
		};
	}
}
