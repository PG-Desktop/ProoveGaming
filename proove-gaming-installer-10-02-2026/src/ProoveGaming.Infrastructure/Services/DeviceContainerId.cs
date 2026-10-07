using System.Runtime.InteropServices;

namespace ProoveGaming.Infrastructure.Services;

// Resolves the Windows Container ID for a HID interface path. The Container ID is a GUID that is
// IDENTICAL across every interface/collection of ONE physical device and UNIQUE per physical
// device — so it is the stable per-device key that groups a single mouse's MI_00/MI_01 collections
// together and cleanly separates a second identical unit. Returns null when the property can't be
// read (non-Windows, or the query fails) so the caller can fall back to the legacy single-device
// path. Pure P/Invoke, no state; runs once per interface during discovery (never on a hot path).
public static class DeviceContainerId
{
    public static string? TryGet(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath) || !OperatingSystem.IsWindows())
            return null;

        try
        {
            var key = DevpkeyDeviceContainerId;
            var buffer = new byte[16]; // a Container ID is a 16-byte GUID
            var size = (uint)buffer.Length;

            var cr = CM_Get_Device_Interface_PropertyW(devicePath, ref key, out var propType, buffer, ref size, 0);
            if (cr != CrSuccess || propType != DevpropTypeGuid || size != 16)
                return null;

            return new Guid(buffer).ToString();
        }
        catch
        {
            return null;
        }
    }

    private const int CrSuccess = 0;
    private const uint DevpropTypeGuid = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    // DEVPKEY_Device_ContainerId = {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}, 2
    private static DevPropKey DevpkeyDeviceContainerId => new()
    {
        Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        Pid = 2,
    };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int CM_Get_Device_Interface_PropertyW(
        string pszDeviceInterface,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);
}
