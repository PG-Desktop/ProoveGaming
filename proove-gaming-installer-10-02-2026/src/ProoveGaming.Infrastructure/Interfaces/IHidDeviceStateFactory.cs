using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces;

public interface IHidDeviceStateFactory
{
    HidDeviceState Create(HidSharp.HidDevice hidDevice);
    HidDeviceState Clone(HidDeviceState dto);
}
