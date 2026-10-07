using HidSharp;
using Microsoft.Extensions.Logging;
using Moq;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// Config-row resolution for device commands (RGB / button remap / macros). The wired row always
// wins; a Bluetooth row is only a fallback, and only for an RGB-capable keyboard — that is what
// lets a Bluetooth-connected Rampage light up while TAIPAN's dead-over-BT config stays blocked.
// The HID-interface half of CreateForDevice needs real HidSharp devices and is covered by the
// hardware probe (see docs/plans/add-rampage-bluetooth-rgb/), not here.
public class DeviceRgbServiceFactoryTests
{
	private const int RampageVid = 10473;
	private const int RampagePid = 12888;
	private const int RasterVid = 14000;
	private const int RasterPid = 12385;
	private const int X43Vid = 9639;
	private const int X43Pid = 12290;
	private const int TaipanVid = 9354;
	private const int TaipanPid = 64257;

	private static DeviceMap RampageWiredRow() => new()
	{
		Name = "RAMPAGE",
		VendorId = "10473",
		ProductId = "12888",
		HasBluetooth = false,
		SupportsRgb = true,
		DeviceType = "Wired Keyboard",
	};

	private static DeviceMap RasterWiredRow() => new()
	{
		Name = "Raster",
		VendorId = "14000",
		ProductId = "12385",
		HasBluetooth = false,
		SupportsRgb = true,
		DeviceType = "Wireless Keyboard",
	};

	private static DeviceMap RampageBluetoothRow() => new()
	{
		Name = "RAMPAGE",
		VendorId = "10473",
		ProductId = "12888",
		HasBluetooth = true,
		SupportsRgb = true,
		DeviceType = "Wireless Keyboard",
	};

	private static DeviceMap X43WiredRow() => new()
	{
		Name = "PG X43",
		VendorId = "9639",
		ProductId = "12290",
		HasBluetooth = false,
		SupportsRgb = true,
		DeviceType = "Wired Mouse",
	};

	// The dongle shape: button-only. SelectConfigRow's capability filter (RGB OR remap OR macros)
	// still serves it, and CreateForDevice dispatches to the Telink service by config Name.
	private static DeviceMap TaipanWiredRow() => new()
	{
		Name = "PG TAIPAN",
		VendorId = "9354",
		ProductId = "64257",
		HasBluetooth = false,
		SupportsButtonRemap = true,
		DeviceType = "Wireless Mouse",
	};

	[Fact]
	public void SelectConfigRow_WiredRowWins_WhenBothTransportsMatch()
	{
		var wired = RampageWiredRow();
		var maps = new List<DeviceMap> { RampageBluetoothRow(), wired };

		var selected = DeviceRgbServiceFactory.SelectConfigRow(maps, RampageVid, RampagePid);

		Assert.Same(wired, selected);
	}

	[Fact]
	public void SelectConfigRow_FallsBackToBluetoothRow_ForRgbKeyboard()
	{
		var bt = RampageBluetoothRow();
		var maps = new List<DeviceMap> { bt };

		var selected = DeviceRgbServiceFactory.SelectConfigRow(maps, RampageVid, RampagePid);

		Assert.Same(bt, selected);
	}

	[Fact]
	public void SelectConfigRow_IgnoresBluetoothRow_WithoutRgb()
	{
		// The T-Racer shape: cable and Bluetooth share PID 12370, only the wired row carries
		// SupportsRgb. Remove the wired row and the BT row must NOT be served.
		var bt = RampageBluetoothRow();
		bt.SupportsRgb = false;
		bt.SupportsMacros = true;

		var selected = DeviceRgbServiceFactory.SelectConfigRow([bt], RampageVid, RampagePid);

		Assert.Null(selected);
	}

	[Fact]
	public void SelectConfigRow_IgnoresBluetoothRow_ForAMouse()
	{
		// The TAIPAN shape: config writes over Bluetooth are accepted by the link and never
		// applied, so a mouse's BT row is never a fallback even if it claimed RGB.
		var bt = new DeviceMap
		{
			Name = "PG TAIPAN",
			VendorId = "9354",
			ProductId = "33382",
			HasBluetooth = true,
			SupportsRgb = true,
			SupportsButtonRemap = true,
			DeviceType = "Wireless Mouse",
		};

		var selected = DeviceRgbServiceFactory.SelectConfigRow([bt], 9354, 33382);

		Assert.Null(selected);
	}

	[Fact]
	public void SelectConfigRow_ReturnsNull_WhenNothingMatches()
	{
		Assert.Null(DeviceRgbServiceFactory.SelectConfigRow([RampageWiredRow()], 1, 2));
	}

	[Fact]
	public void SelectConfigRow_StillServesButtonOnlyWiredRow()
	{
		// A button-only mouse (TAIPAN over cable, SupportsRgb=false) must keep resolving for its
		// button write — the capability filter is RGB OR remap OR macros, not RGB alone.
		var wired = new DeviceMap
		{
			Name = "PG TAIPAN",
			VendorId = "9354",
			ProductId = "64257",
			HasBluetooth = false,
			SupportsButtonRemap = true,
			DeviceType = "Wireless Mouse",
		};

		Assert.Same(wired, DeviceRgbServiceFactory.SelectConfigRow([wired], 9354, 64257));
	}

	[Fact]
	public void CreateForDevice_UnknownDevice_ReturnsNull()
	{
		var factory = CreateFactory([RampageWiredRow(), RampageBluetoothRow()]);

		Assert.Null(factory.CreateForDevice(1, 2, string.Empty));
	}

	[Fact]
	public void CreateForDevice_BluetoothOnlyRow_WithNoHidInterfaces_ReturnsNullWithoutThrowing()
	{
		// The BT row resolves to the Rampage keyboard service, whose interface selection then
		// finds no candidates — the factory must degrade to null, exactly like the wired path
		// does when the vendor interface is absent.
		var factory = CreateFactory([RampageBluetoothRow()]);

		Assert.Null(factory.CreateForDevice(RampageVid, RampagePid, string.Empty));
	}

	[Fact]
	public void CreateForDevice_KeyboardWithNoHidInterfaces_DoesNotCacheTheNullResult()
	{
		// The keyboard cache must copy the Easen/Telink contract: a null creation result (device
		// enumerated by config but its vendor interface absent) is never cached, so a keyboard
		// plugged in later is created on the next call. Both calls must reach the HID enumeration
		// — a cached null would short-circuit the second one.
		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([RampageWiredRow()]);

		var factory = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		Assert.Null(factory.CreateForDevice(RampageVid, RampagePid, string.Empty));
		Assert.Null(factory.CreateForDevice(RampageVid, RampagePid, string.Empty));

		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void IsCatalogMouse_CatalogMouseRow_IsTrue()
	{
		var mouse = new DeviceMap
		{
			Name = "PG X43",
			VendorId = "9639",
			ProductId = "12290",
			HasBluetooth = false,
			SupportsRgb = true,
			DeviceType = "Wired Mouse",
		};
		var factory = CreateFactory([mouse, RampageWiredRow()]);

		Assert.True(factory.IsCatalogMouse(9639, 12290));
	}

	[Fact]
	public void IsCatalogMouse_CatalogKeyboardRow_IsFalse()
	{
		var factory = CreateFactory([RampageWiredRow()]);

		Assert.False(factory.IsCatalogMouse(RampageVid, RampagePid));
	}

	[Fact]
	public void IsCatalogMouse_ForeignVidPid_IsFalse()
	{
		// The DpiStageMonitor repro from the leak investigation: a Logitech unit (VID 0x056D,
		// PID 0xC077) exposing an &mi_01&col04 interface must never get a DPI reader.
		var factory = CreateFactory([RampageWiredRow(), RampageBluetoothRow()]);

		Assert.False(factory.IsCatalogMouse(0x056D, 0xC077));
	}

	[Fact]
	public void CreateForDevice_Keyboard_SameInstanceForSameVidPid_RegardlessOfDeviceKey()
	{
		// The keyboard cache is keyed (vid, pid) ONLY: keyboard creation is not unit-aware (it
		// ignores deviceKey), so mixed call sites — gRPC passing a Container ID vs internal
		// callers passing "" — must share one instance instead of caching duplicates bound to the
		// same physical interface. The single GetDevices() verification is the churn fix itself:
		// the full HID enumeration runs once per device, not once per gRPC call / DPI poll.
		// Raster is the fabricable keyboard here — its interface selection needs only the report
		// lengths (65/65), all virtual on HidSharp's HidDevice, and its constructor just stores
		// the device.
		var rasterInterface = new Mock<HidDevice>();
		rasterInterface.SetupGet(d => d.VendorID).Returns(RasterVid);
		rasterInterface.SetupGet(d => d.ProductID).Returns(RasterPid);
		rasterInterface.Setup(d => d.GetMaxOutputReportLength()).Returns(65);
		rasterInterface.Setup(d => d.GetMaxInputReportLength()).Returns(65);

		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice> { rasterInterface.Object });

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([RasterWiredRow()]);

		var factory = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		var first = factory.CreateForDevice(RasterVid, RasterPid, string.Empty);
		var second = factory.CreateForDevice(RasterVid, RasterPid, "usb-container-id-of-the-unit");

		Assert.NotNull(first);
		Assert.Same(first, second);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Once);
	}

	[Fact]
	public void Constructor_SubscribesToDeviceListChanged_ExactlyOnce()
	{
		// The keyboard-cache invalidation (PR #115 review M1) hangs off exactly one constructor
		// subscription; a second subscription would clear the cache twice per change (harmless) but
		// more importantly signal a lifecycle change nobody planned — the factory is a DI singleton
		// with no unsubscribe path, so the count is the whole contract.
		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([RasterWiredRow()]);

		_ = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		hidDeviceProvider.Verify(
			x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()),
			Times.Once);
	}

	[Fact]
	public void CreateForDevice_Keyboard_DeviceListChange_ClearsCacheAndReEnumerates()
	{
		// The M1 invalidation contract: a HID device-list change (unplug/replug) clears the keyboard
		// cache, so the next CreateForDevice runs a fresh HID enumeration and constructs a new
		// service bound to the current DevicePath — instead of forever serving an instance bound to
		// the old dead path. Raster-shaped mock as in the cache-hit test above.
		EventHandler<DeviceListChangedEventArgs>? capturedHandler = null;

		var rasterInterface = new Mock<HidDevice>();
		rasterInterface.SetupGet(d => d.VendorID).Returns(RasterVid);
		rasterInterface.SetupGet(d => d.ProductID).Returns(RasterPid);
		rasterInterface.Setup(d => d.GetMaxOutputReportLength()).Returns(65);
		rasterInterface.Setup(d => d.GetMaxInputReportLength()).Returns(65);

		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice> { rasterInterface.Object });
		hidDeviceProvider.Setup(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()))
			.Callback<EventHandler<DeviceListChangedEventArgs>>(handler => capturedHandler = handler);

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([RasterWiredRow()]);

		var factory = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		Assert.NotNull(capturedHandler);

		var beforeChange = factory.CreateForDevice(RasterVid, RasterPid, string.Empty);
		Assert.NotNull(beforeChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Once);

		capturedHandler!.Invoke(hidDeviceProvider.Object, new DeviceListChangedEventArgs());

		var afterChange = factory.CreateForDevice(RasterVid, RasterPid, string.Empty);

		// A cache hit would have skipped enumeration and returned the same instance — the change
		// event must force both: a second GetDevices() and a freshly constructed service.
		Assert.NotNull(afterChange);
		Assert.NotSame(beforeChange, afterChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void CreateForDevice_Keyboard_AfterDeviceListChange_FreshServiceIsCachedAgain()
	{
		// The invalidation must not degrade the cache to per-call construction: after one clear, the
		// next successful creation re-populates the cache, so the call after that is a hit again
		// (same instance, no third enumeration).
		EventHandler<DeviceListChangedEventArgs>? capturedHandler = null;

		var rasterInterface = new Mock<HidDevice>();
		rasterInterface.SetupGet(d => d.VendorID).Returns(RasterVid);
		rasterInterface.SetupGet(d => d.ProductID).Returns(RasterPid);
		rasterInterface.Setup(d => d.GetMaxOutputReportLength()).Returns(65);
		rasterInterface.Setup(d => d.GetMaxInputReportLength()).Returns(65);

		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice> { rasterInterface.Object });
		hidDeviceProvider.Setup(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()))
			.Callback<EventHandler<DeviceListChangedEventArgs>>(handler => capturedHandler = handler);

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([RasterWiredRow()]);

		var factory = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		factory.CreateForDevice(RasterVid, RasterPid, string.Empty);
		capturedHandler!.Invoke(hidDeviceProvider.Object, new DeviceListChangedEventArgs());

		var fresh = factory.CreateForDevice(RasterVid, RasterPid, string.Empty);
		var hit = factory.CreateForDevice(RasterVid, RasterPid, string.Empty);

		Assert.NotNull(fresh);
		Assert.Same(fresh, hit);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void CreateForDevice_EasenMouse_DeviceListChange_ClearsCacheAndReEnumerates()
	{
		// The audit C7 contract: the Easen mouse cache shares the keyboard cache's invalidation —
		// a HID device-list change (unplug/replug) clears it, so the next CreateForDevice runs a
		// fresh enumeration and constructs a new service bound to the live DevicePath instead of
		// forever serving one bound to the old dead path until DeviceHost restarts.
		var (factory, hidDeviceProvider, handler) = CreateMouseFactory(EasenInterface(), X43WiredRow());

		var beforeChange = factory.CreateForDevice(X43Vid, X43Pid, string.Empty);
		Assert.NotNull(beforeChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Once);

		handler.Invoke(hidDeviceProvider.Object, new DeviceListChangedEventArgs());

		var afterChange = factory.CreateForDevice(X43Vid, X43Pid, string.Empty);

		// A cache hit would have skipped enumeration and returned the same instance — the change
		// event must force both: a second GetDevices() and a freshly constructed service.
		Assert.NotNull(afterChange);
		Assert.NotSame(beforeChange, afterChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void CreateForDevice_TelinkMouse_DeviceListChange_ClearsCacheAndReEnumerates()
	{
		// The audit C11 contract: same invalidation for the TAIPAN path's separate _telinkCache.
		var (factory, hidDeviceProvider, handler) = CreateMouseFactory(TaipanInterface(), TaipanWiredRow());

		var beforeChange = factory.CreateForDevice(TaipanVid, TaipanPid, string.Empty);
		Assert.NotNull(beforeChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Once);

		handler.Invoke(hidDeviceProvider.Object, new DeviceListChangedEventArgs());

		var afterChange = factory.CreateForDevice(TaipanVid, TaipanPid, string.Empty);

		Assert.NotNull(afterChange);
		Assert.NotSame(beforeChange, afterChange);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void CreateForDevice_EasenMouse_AfterDeviceListChange_FreshServiceIsCachedAgain()
	{
		// The invalidation must not degrade the cache to per-call construction: after one clear,
		// the next successful creation re-populates the cache, so the call after that is a hit
		// again (same instance, no third enumeration) — mirrors the keyboard variant above.
		var (factory, hidDeviceProvider, handler) = CreateMouseFactory(EasenInterface(), X43WiredRow());

		factory.CreateForDevice(X43Vid, X43Pid, string.Empty);
		handler.Invoke(hidDeviceProvider.Object, new DeviceListChangedEventArgs());

		var fresh = factory.CreateForDevice(X43Vid, X43Pid, string.Empty);
		var hit = factory.CreateForDevice(X43Vid, X43Pid, string.Empty);

		Assert.NotNull(fresh);
		Assert.Same(fresh, hit);
		hidDeviceProvider.Verify(x => x.GetDevices(), Times.Exactly(2));
	}

	[Fact]
	public void IsCatalogMouse_ButtonOnlyWiredMouseRow_IsTrue()
	{
		// The TAIPAN dongle shape: SupportsRgb=false, SupportsButtonRemap=true. SelectConfigRow's
		// capability filter is RGB OR remap OR macros, so a capability-any row must still count as
		// a catalog mouse — otherwise the DPI gate would starve a real catalog mouse of its reader.
		var dongle = new DeviceMap
		{
			Name = "PG TAIPAN",
			VendorId = "9354",
			ProductId = "64257",
			HasBluetooth = false,
			SupportsButtonRemap = true,
			DeviceType = "Wireless Mouse",
		};
		var factory = CreateFactory([dongle]);

		Assert.True(factory.IsCatalogMouse(9354, 64257));
	}

	[Fact]
	public void IsCatalogMouse_BluetoothOnlyMouseRow_IsFalse()
	{
		// Mirrors SelectConfigRow's mouse-over-BT exclusion: a mouse row that only exists over
		// Bluetooth resolves to no config row, so it is not a catalog mouse either.
		var bt = new DeviceMap
		{
			Name = "PG TAIPAN",
			VendorId = "9354",
			ProductId = "33382",
			HasBluetooth = true,
			SupportsRgb = true,
			SupportsButtonRemap = true,
			DeviceType = "Wireless Mouse",
		};
		var factory = CreateFactory([bt]);

		Assert.False(factory.IsCatalogMouse(9354, 33382));
	}

	// An Easen vendor interface as CreateEasenMouseService selects it: outLen=65, featLen=9.
	// GetMaxInputReportLength defaults to 0, so no readable companion interface is found — the
	// service degrades to write-only exactly like a real unit without a read interface.
	private static HidDevice EasenInterface()
	{
		var mouseInterface = new Mock<HidDevice>();
		mouseInterface.SetupGet(d => d.VendorID).Returns(X43Vid);
		mouseInterface.SetupGet(d => d.ProductID).Returns(X43Pid);
		mouseInterface.Setup(d => d.GetMaxOutputReportLength()).Returns(65);
		mouseInterface.Setup(d => d.GetMaxFeatureReportLength()).Returns(9);
		return mouseInterface.Object;
	}

	// The TAIPAN vendor interface: 33-byte OUTPUT/INPUT report (id 0x05), no feature report.
	private static HidDevice TaipanInterface()
	{
		var mouseInterface = new Mock<HidDevice>();
		mouseInterface.SetupGet(d => d.VendorID).Returns(TaipanVid);
		mouseInterface.SetupGet(d => d.ProductID).Returns(TaipanPid);
		mouseInterface.Setup(d => d.GetMaxOutputReportLength()).Returns(TaipanFrame.Length);
		mouseInterface.Setup(d => d.GetMaxInputReportLength()).Returns(TaipanFrame.Length);
		return mouseInterface.Object;
	}

	// The mouse-cache tests' shared rig: one HID interface, one config row, and the device-list
	// handler captured at construction — the same shape the keyboard invalidation tests build
	// inline above.
	private static (DeviceRgbServiceFactory Factory, Mock<IHidDeviceProvider> HidDeviceProvider, EventHandler<DeviceListChangedEventArgs> Handler)
		CreateMouseFactory(HidDevice hidInterface, DeviceMap configRow)
	{
		EventHandler<DeviceListChangedEventArgs>? capturedHandler = null;

		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice> { hidInterface });
		hidDeviceProvider.Setup(x => x.SubscribeToDeviceListChanged(It.IsAny<EventHandler<DeviceListChangedEventArgs>>()))
			.Callback<EventHandler<DeviceListChangedEventArgs>>(handler => capturedHandler = handler);

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns([configRow]);

		var factory = new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);

		Assert.NotNull(capturedHandler);
		return (factory, hidDeviceProvider, capturedHandler!);
	}

	private static DeviceRgbServiceFactory CreateFactory(List<DeviceMap> maps)
	{
		var hidDeviceProvider = new Mock<IHidDeviceProvider>();
		hidDeviceProvider.Setup(x => x.GetDevices()).Returns(new List<HidDevice>());

		var deviceInfoProvider = new Mock<IDeviceInfoProvider>();
		deviceInfoProvider.Setup(x => x.DevicesMaps).Returns(maps);

		return new DeviceRgbServiceFactory(
			hidDeviceProvider.Object,
			deviceInfoProvider.Object,
			new Mock<ILogger<EasenMouseService>>().Object);
	}
}
