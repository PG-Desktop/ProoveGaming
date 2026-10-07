namespace ProoveGaming.Infrastructure.Interfaces;

public interface IDeviceKeyMappingServiceFactory
{
	/// <summary>
	/// Returns null if the device isn't a keyboard we can remap, or the required HID
	/// interface isn't currently enumerated (e.g. dongle unplugged).
	/// </summary>
	IKeyboardKeyMappingService? CreateForDevice(int vendorId, int productId);
}
