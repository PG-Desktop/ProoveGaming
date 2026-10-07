namespace ProoveGaming.Infrastructure.CustomEvents;

public class BluetoothDeviceConnectionChangedEventArgs : EventArgs
{
    public required string DeviceId { get; set; }

    public required string DeviceName { get; set; }

    public bool IsConnected { get; set; }

    public bool IsPaired { get; set; }

    public DateTime Timestamp { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public int BatteryLevel { get; set; } = -1;
}
