namespace ProoveGaming.Infrastructure.Interfaces;

// Shared RGB/LED capability for any lighting-capable device (mouse or keyboard).
// This is the common surface every RGB service implements; device-family specifics
// (DPI, report rate, debounce/lift-off, state sync) live in derived interfaces such
// as IMouseRgbService. Keyboards implement this directly and are not burdened with
// mouse-only members.
public interface IDeviceRgbService
{
	/// <summary>
	/// Set colour + effect for the device's LED(s).
	/// </summary>
	Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed);
}
