namespace ProoveGaming.Infrastructure.Classes;
public class HidDeviceState
{
    public string DevicePath { get; set; } = string.Empty;
    // Stable per-physical-device identity (Windows Container ID). Identical across a device's
    // interfaces, unique per physical unit. Empty when unknown (legacy / query failed) — treated
    // as the single-device fallback everywhere it is used.
    public string DeviceKey { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string ReleaseNumber { get; set; } = string.Empty;
    public int ReleaseNumberBcd { get; set; }
    public string VendorId { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public byte[] RawReportDescriptor { get; set; } = [];
    public string[] SerialPorts { get; set; } = [];
    public string SerialNumber { get; set; } = string.Empty;
    public string FileSystemName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string FullString { get; set; } = string.Empty;
    public bool IsBluetoothAdapter { get; set; }
    public bool IsConnected { get; set; }
}
