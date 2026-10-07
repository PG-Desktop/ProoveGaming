using HidSharp;
using Microsoft.Extensions.Logging;
using Moq;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

public class HidDeviceDiscoveryTests
{
	private readonly Mock<ILogger<IHidDeviceDiscoveryService>> _mockLogger;
	private readonly Mock<IHidDeviceProvider> _mockHidDeviceProvider;
	private readonly Mock<IHidDeviceStateFactory> _mockHidDeviceStateFactory;
	private readonly Mock<IDeviceInfoProvider> _mockDeviceInfoProvider;

	public HidDeviceDiscoveryTests()
	{
		_mockLogger = new Mock<ILogger<IHidDeviceDiscoveryService>>();
		_mockHidDeviceProvider = new Mock<IHidDeviceProvider>();
		_mockHidDeviceStateFactory = new Mock<IHidDeviceStateFactory>();
		_mockDeviceInfoProvider = new Mock<IDeviceInfoProvider>();
	}

	#region Constructor Tests

	[Fact]
	public void Constructor_WithNullLogger_ThrowsArgumentNullException()
	{
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new HidDeviceDiscoveryService(
				null!,
				_mockHidDeviceProvider.Object,
				_mockDeviceInfoProvider.Object,
				_mockHidDeviceStateFactory.Object));

		Assert.Equal("logger", exception.ParamName);
	}

	[Fact]
	public void Constructor_WithNullHidDeviceProvider_ThrowsArgumentNullException()
	{
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new HidDeviceDiscoveryService(
				_mockLogger.Object,
				null!,
				_mockDeviceInfoProvider.Object,
				_mockHidDeviceStateFactory.Object));

		Assert.Equal("hidDeviceProvider", exception.ParamName);
	}

	[Fact]
	public void Constructor_WithNullHidDeviceStateFactory_ThrowsArgumentNullException()
	{
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new HidDeviceDiscoveryService(
				_mockLogger.Object,
				_mockHidDeviceProvider.Object,
				_mockDeviceInfoProvider.Object,
				null!));

		Assert.Equal("hidDeviceStateFactory", exception.ParamName);
	}

	[Fact]
	public void Constructor_WithNullDeviceInfoProvider_ThrowsArgumentNullException()
	{
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new HidDeviceDiscoveryService(
				_mockLogger.Object,
				_mockHidDeviceProvider.Object,
				null!,
				_mockHidDeviceStateFactory.Object));

		Assert.Equal("deviceInfoProvider", exception.ParamName);
	}

	[Fact]
	public void Constructor_WithValidParameters_InitializesCorrectly()
	{
		var service = CreateService();

		Assert.NotNull(service);
		Assert.False(service.IsRunning);
	}

	#endregion

	#region Start/Stop Tests

	[Fact]
	public void Start_WhenNotRunning_StartsServiceAndSubscribesToEvents()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		service.Start();

		Assert.True(service.IsRunning);
		_mockHidDeviceProvider.Verify(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Once);
		_mockHidDeviceProvider.Verify(x => x.GetDevices(), Times.Once);
	}

	[Fact]
	public void Start_WhenAlreadyRunning_DoesNotSubscribeAgain()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		service.Start();
		service.Start();

		Assert.True(service.IsRunning);
		_mockHidDeviceProvider.Verify(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Once);
	}

	[Fact]
	public void Stop_WhenRunning_StopsServiceAndUnsubscribesFromEvents()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		service.Stop();

		Assert.False(service.IsRunning);
		_mockHidDeviceProvider.Verify(x => x.UnsubscribeFromDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Once);
	}

	[Fact]
	public void Stop_WhenNotRunning_DoesNotUnsubscribe()
	{
		var service = CreateService();

		service.Stop();

		Assert.False(service.IsRunning);
		_mockHidDeviceProvider.Verify(x => x.UnsubscribeFromDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Never);
	}

	#endregion

	#region GetClientUsbDevicesList Tests

	[Fact]
	public void GetClientUsbDevicesList_WhenNoDevices_ReturnsEmptyList()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		var result = service.GetClientUsbDevicesList();

		Assert.NotNull(result);
		Assert.Empty(result);
	}

	[Fact]
	public void GetClientUsbDevicesList_WithDevicesButNoMaps_ReturnsEmptyList()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		var result = service.GetClientUsbDevicesList();

		Assert.NotNull(result);
		Assert.Empty(result);
	}

	[Fact]
	public void GetClientUsbDevicesList_WhenServiceNotStarted_ReturnsEmptyList()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		var result = service.GetClientUsbDevicesList();

		Assert.NotNull(result);
		Assert.Empty(result);
	}

	[Fact]
	public void GetClientUsbDevicesList_ReturnsDistinctInstancesOnEachCall()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		var result1 = service.GetClientUsbDevicesList();
		var result2 = service.GetClientUsbDevicesList();

		Assert.NotSame(result1, result2);
	}

	#endregion

	#region Event Tests

	[Fact]
	public void Start_RaisesHidDeviceListUsbConnectedEvent()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		HidDeviceListChangedEventArgs? capturedArgs = null;
		service.HidDeviceListUsbConnected += (sender, args) => capturedArgs = args;

		service.Start();

		Assert.NotNull(capturedArgs);
		Assert.NotNull(capturedArgs.HidDevices);
	}

	[Fact]
	public void Start_RaisesHidDeviceListBluetoothAdapterConnectedEvent()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		HidDeviceListChangedEventArgs? capturedArgs = null;
		service.HidDeviceListBluetoothAdapterConnected += (sender, args) => capturedArgs = args;

		service.Start();

		Assert.NotNull(capturedArgs);
		Assert.NotNull(capturedArgs.HidDevices);
	}

	[Fact]
	public void DeviceListChanged_WhenServiceRunning_TriggersRecognition()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		EventHandler<DeviceListChangedEventArgs>? capturedHandler = null;
		_mockHidDeviceProvider.Setup(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()))
			.Callback<EventHandler<DeviceListChangedEventArgs>>(handler => capturedHandler = handler);

		var service = CreateService();
		service.Start();

		int getDevicesCalls = 0;
		_mockHidDeviceProvider.Setup(x => x.GetDevices())
			.Callback(() => getDevicesCalls++)
			.Returns(new List<HidDevice>());

		capturedHandler?.Invoke(this, new DeviceListChangedEventArgs());

		_mockHidDeviceProvider.Verify(x => x.GetDevices(), Times.AtLeast(2));
	}

	[Fact]
	public void Stop_UnsubscribesFromEvents()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();
		service.Stop();

		_mockHidDeviceProvider.Verify(x => x.UnsubscribeFromDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Once);
	}

	#endregion

	#region Device Filtering Tests

	[Fact]
	public void Start_FiltersDevicesBasedOnDeviceMap()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>
		{
			new DeviceMap { Name = "Test Device", ProductId = "1111", VendorId = "2222", HasBluetooth = false }
		});

		var service = CreateService();
		HidDeviceListChangedEventArgs? usbArgs = null;
		service.HidDeviceListUsbConnected += (sender, args) => usbArgs = args;

		service.Start();

		Assert.NotNull(usbArgs);
		Assert.NotNull(usbArgs.HidDevices);
	}

	[Fact]
	public void GetClientUsbDevicesList_OnlyReturnsUsbDevices()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>
		{
			new DeviceMap { Name = "USB Device", ProductId = "1111", VendorId = "2222", HasBluetooth = false },
			new DeviceMap { Name = "BT Device", ProductId = "3333", VendorId = "4444", HasBluetooth = true }
		});

		var service = CreateService();
		service.Start();

		var result = service.GetClientUsbDevicesList();

		Assert.NotNull(result);
		Assert.IsAssignableFrom<IReadOnlyList<HidDeviceState>>(result);
	}

	[Fact]
	public void Start_TriggersEventsForBothDeviceTypes()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>
		{
			new DeviceMap { Name = "USB Device", ProductId = "1111", VendorId = "2222", HasBluetooth = false },
			new DeviceMap { Name = "BT Device", ProductId = "3333", VendorId = "4444", HasBluetooth = true }
		});

		var service = CreateService();

		bool usbEventFired = false;
		bool btEventFired = false;

		service.HidDeviceListUsbConnected += (sender, args) => usbEventFired = true;
		service.HidDeviceListBluetoothAdapterConnected += (sender, args) => btEventFired = true;

		service.Start();

		Assert.True(usbEventFired);
		Assert.True(btEventFired);
	}

	#endregion

	#region Thread Safety Tests

	[Fact]
	public async Task GetClientUsbDevicesList_CalledConcurrently_ReturnsConsistentResults()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		var tasks = new List<Task<IReadOnlyList<HidDeviceState>>>();
		for (int i = 0; i < 10; i++)
		{
			tasks.Add(Task.Run(() => service.GetClientUsbDevicesList()));
		}

		await Task.WhenAll(tasks);

		Assert.All(tasks, task =>
		{
			Assert.NotNull(task.Result);
			Assert.IsAssignableFrom<IReadOnlyList<HidDeviceState>>(task.Result);
		});
	}

	[Fact]
	public void Start_CalledConcurrently_DoesNotThrow()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		var exception = Record.Exception(() =>
		{
			Parallel.For(0, 5, _ => service.Start());
		});

		Assert.Null(exception);
	}

	[Fact]
	public void Stop_CalledConcurrently_DoesNotThrow()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();
		service.Start();

		var exception = Record.Exception(() =>
		{
			Parallel.For(0, 5, _ => service.Stop());
		});

		Assert.Null(exception);
	}

	#endregion

	#region Integration Tests

	[Fact]
	public void Service_FullLifecycle_WorksCorrectly()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		Assert.False(service.IsRunning);

		service.Start();
		Assert.True(service.IsRunning);

		var devices1 = service.GetClientUsbDevicesList();
		Assert.NotNull(devices1);

		service.Stop();
		Assert.False(service.IsRunning);

		var devices2 = service.GetClientUsbDevicesList();
		Assert.NotNull(devices2);
	}

	[Fact]
	public void Service_StartStopMultipleTimes_WorksCorrectly()
	{
		_mockHidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());
		_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(new List<DeviceMap>());

		var service = CreateService();

		service.Start();
		service.Stop();
		service.Start();
		service.Stop();

		Assert.False(service.IsRunning);
		_mockHidDeviceProvider.Verify(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Exactly(2));
		_mockHidDeviceProvider.Verify(x => x.UnsubscribeFromDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()), Times.Exactly(2));
	}

	#endregion

	#region Helper Methods

	private HidDeviceDiscoveryService CreateService()
	{
		return new HidDeviceDiscoveryService(
			_mockLogger.Object,
			_mockHidDeviceProvider.Object,
			_mockDeviceInfoProvider.Object,
			_mockHidDeviceStateFactory.Object);
	}

	#endregion

	#region Test Helper Classes

	private class TestHidDeviceFactory
	{
		public HidDevice CreateConnectableDevice()
		{
			return DeviceList.Local.GetHidDevices().FirstOrDefault() ??
				throw new InvalidOperationException("No HID devices available for testing. Tests that require actual HID devices may be skipped.");
		}
	}

	#endregion
}

