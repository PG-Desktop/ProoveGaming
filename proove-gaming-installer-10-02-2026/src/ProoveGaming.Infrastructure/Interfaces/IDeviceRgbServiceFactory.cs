namespace ProoveGaming.Infrastructure.Interfaces;

public interface IDeviceRgbServiceFactory
{
    // Returns null if the device isn't connected, isn't configured with SupportsRgb=true,
    // or has a DeviceType for which no RGB service implementation exists yet.
    // Returns the shared RGB surface; callers needing mouse-only operations cast to
    // IMouseRgbService (mice return an instance that implements it, keyboards do not).
    // deviceKey selects a specific physical unit among identical (VID,PID) devices (Container
    // ID); pass "" for the legacy single-device behaviour (first matching interface).
    IDeviceRgbService? CreateForDevice(int vendorId, int productId, string deviceKey);

    // True when the catalog resolves this VID/PID to a row CreateForDevice would serve with an
    // IMouseRgbService. Lets DeviceHost gate its DPI readers on the catalog without duplicating
    // the dispatch rule (any vendor's device can expose a Col04-shaped HID interface).
    bool IsCatalogMouse(int vendorId, int productId);
}
