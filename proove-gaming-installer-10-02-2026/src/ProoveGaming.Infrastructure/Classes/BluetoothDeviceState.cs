namespace ProoveGaming.Infrastructure.Classes;

public class BluetoothDeviceState
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public bool IsPaired { get; set; }
    public string ClientName { get; set; } = string.Empty;

    public int BatteryLevel { get; set; } = -1;
}
