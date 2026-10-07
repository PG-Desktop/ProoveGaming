using HidSharp;

namespace ProoveGaming.Device.Testing
{
	public static class X43Finder
	{
		public const int VendorId = 0x04D9;
		public const int ProductId = 0xA245;

		// Null when the device isn't plugged in — FirstOrDefault, not First.
		public static HidDevice? Find()
		{
			return DeviceList.Local
				.GetHidDevices(VendorId, ProductId)
				.FirstOrDefault();
		}
	}
}
