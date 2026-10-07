using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The path classification behind the selector's transport preference: with a board connected over
// USB and Bluetooth at once, both links can expose the vendor collection, and the non-Bluetooth
// path must sort first so writes stay on the cable. Ordering over real HidDevice instances needs
// hardware; the predicate is the part that can drift, so it is pinned here.
public class SmartInfiniteInterfaceSelectorTests
{
	[Theory]
	// Classic Bluetooth HID enumerates under BTHENUM.
	[InlineData(@"\\?\hid#bthenum{00001124-0000-1000-8000-00805f9b34fb}_vid&000228e9_pid&3258#8&2f1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	// Bluetooth LE (HID over GATT) enumerates under BTHLEDEVICE.
	[InlineData(@"\\?\hid#bthledevice&dev_vid&0228e9_pid&3258&col05#9&1b2c3d4e&1&0005#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	[InlineData(@"\\?\HID#BTHLEDevice&Dev_VID&0228e9_PID&3258#7&aa&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	// Current Windows 11 builds the HOGP path from the HID service UUID instead — this exact
	// shape was observed on the real Rampage over BLE (VID 0x28E9 / PID 0x3000, col06).
	[InlineData(@"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&0228e9_pid&3000_rev&0109_cc482ebf5532&col06#9&37efeaae&0&0005#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	public void IsBluetoothDevicePath_TrueForBluetoothStacks(string path)
	{
		Assert.True(SmartInfiniteInterfaceSelector.IsBluetoothDevicePath(path));
	}

	[Theory]
	[InlineData(@"\\?\hid#vid_28e9&pid_3258&mi_01&col05#8&2d0e2b2f&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	[InlineData(@"\\?\hid#vid_28e9&pid_3258&mi_01&col07#8&2d0e2b2f&0&0006#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	[InlineData("")]
	[InlineData(null)]
	public void IsBluetoothDevicePath_FalseForUsbPaths(string? path)
	{
		Assert.False(SmartInfiniteInterfaceSelector.IsBluetoothDevicePath(path));
	}
}
