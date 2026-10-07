namespace ProoveGaming.App.Services;

// Leaving the device details page on sign-out. Implemented by DeviceDetailsViewModel
// (docs/plans/refactor-auth-session-flow).
public interface IDeviceDetailsExit
{
    void ClearDeviceForExit();
}
