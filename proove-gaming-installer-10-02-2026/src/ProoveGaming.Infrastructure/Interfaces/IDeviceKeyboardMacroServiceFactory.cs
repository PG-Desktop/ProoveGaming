namespace ProoveGaming.Infrastructure.Interfaces;

public interface IDeviceKeyboardMacroServiceFactory
{
	/// <summary>
	/// Returns null if the device is not a keyboard we can upload macros to, or the required
	/// HID interface isn't currently enumerated (e.g. dongle unplugged).
	/// </summary>
	IKeyboardMacroService? CreateForDevice(int vendorId, int productId);
}
