using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.CustomEvents;

public class HidDeviceListChangedEventArgs : EventArgs
{
    public List<HidDeviceState> HidDevices { get; private set; }

    public HidDeviceListChangedEventArgs()
    {
        HidDevices = new List<HidDeviceState>();
    }
}
