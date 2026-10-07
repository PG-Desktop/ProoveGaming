using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class HidDeviceStateFactory: IHidDeviceStateFactory
{
    private ILogger<IHidDeviceStateFactory> _logger;

    // Everything below is a property of the physical device at a given DevicePath, and none of it
    // changes while that path exists. Reading it is expensive: the seven Get* calls each OPEN the
    // device to pull a string/descriptor, and DeviceContainerId.TryGet is a CM_ query on top.
    //
    // Discovery re-creates state for EVERY HID interface on the machine — keyboard, touchpad,
    // webcam, everything — on every device-list change, so uncached this was ~8 device operations
    // per interface per change. Caching by path makes it once per device per session.
    private sealed record DeviceIdentity(
        string DeviceKey,
        string Manufacturer,
        byte[] RawReportDescriptor,
        string[] SerialPorts,
        string SerialNumber,
        string FileSystemName,
        string FriendlyName,
        string ProductName);

    // Keyed by DevicePath. Bounded by the number of distinct paths seen in a session (tens), and
    // the cached values are small, so entries are kept rather than pruned on unplug — a replugged
    // device reuses its path and hits the cache.
    private readonly ConcurrentDictionary<string, DeviceIdentity> _identityCache = new();

    public HidDeviceStateFactory(ILogger<IHidDeviceStateFactory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public HidDeviceState Create(HidSharp.HidDevice hidDevice) 
    {
        if (hidDevice == null) 
        {
            throw new ArgumentNullException(nameof(hidDevice));
        }

        var hidDeviceState = new HidDeviceState();

        var devicePath = hidDeviceState.DevicePath = hidDevice.DevicePath;
        hidDeviceState.ProductId = hidDevice.ProductID.ToString();
        hidDeviceState.ReleaseNumber = hidDevice.ReleaseNumber.ToString();
        hidDeviceState.ReleaseNumberBcd = hidDevice.ReleaseNumberBcd;
        hidDeviceState.VendorId = hidDevice.VendorID.ToString();

        // Read once per device path, then served from cache. A fresh HidDeviceState is still built
        // every call — only the expensive reads are shared, so callers never alias one another.
        var identity = _identityCache.GetOrAdd(devicePath, _ => ReadIdentity(hidDevice, devicePath));

        hidDeviceState.DeviceKey = identity.DeviceKey;
        hidDeviceState.Manufacturer = identity.Manufacturer;
        hidDeviceState.RawReportDescriptor = identity.RawReportDescriptor;
        hidDeviceState.SerialPorts = identity.SerialPorts;
        hidDeviceState.SerialNumber = identity.SerialNumber;
        hidDeviceState.FileSystemName = identity.FileSystemName;
        hidDeviceState.FriendlyName = identity.FriendlyName;
        hidDeviceState.ProductName = identity.ProductName;

        hidDeviceState.FullString = hidDevice.ToString();
        hidDeviceState.IsConnected = true;

        return hidDeviceState;
    }

    // Opens the device to read its descriptors. Called once per DevicePath — see _identityCache.
    private DeviceIdentity ReadIdentity(HidSharp.HidDevice hidDevice, string devicePath)
    {
        // The stable per-physical-device key. Empty when the Container ID can't be read — the whole
        // pipeline treats "" as single-device.
        var deviceKey = DeviceContainerId.TryGet(devicePath) ?? string.Empty;

        var rawDescriptor = ReadOptional(hidDevice.GetRawReportDescriptor, [], "raw report descriptor", devicePath);
        if (rawDescriptor.Length > 0)
        {
            // Debug, not Information: one long hex line per HID interface on the machine.
            _logger?.LogDebug("[RawDescriptor] VID=0x{VID:X4} PID=0x{PID:X4}: {Hex}",
                hidDevice.VendorID, hidDevice.ProductID, BitConverter.ToString(rawDescriptor));
        }

        return new DeviceIdentity(
            deviceKey,
            ReadOptional(hidDevice.GetManufacturer, string.Empty, "manufacturer", devicePath),
            rawDescriptor,
            ReadOptional(hidDevice.GetSerialPorts, [], "serial ports", devicePath),
            ReadOptional(hidDevice.GetSerialNumber, string.Empty, "serial number", devicePath),
            ReadOptional(hidDevice.GetFileSystemName, string.Empty, "file system name", devicePath),
            ReadOptional(hidDevice.GetFriendlyName, string.Empty, "friendly name", devicePath),
            ReadOptional(hidDevice.GetProductName, string.Empty, "product name", devicePath));
    }

    // Reads one optional descriptor, falling back when the device does not expose it.
    //
    // Logged at DEBUG, not Error. Every one of these is optional by the HID spec and a device
    // omitting one is normal, not a fault: BLAZE reports no serial number at all (confirmed on two
    // units), and plenty of built-in HID interfaces expose neither a manufacturer nor a friendly
    // name. Logging them as errors filled the log with failures nobody can act on, once per
    // descriptor per device. Nothing downstream treats an empty value as a problem.
    private T ReadOptional<T>(Func<T> read, T fallback, string what, string devicePath)
    {
        try
        {
            return read() ?? fallback;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Device does not expose {What}: {Path}", what, devicePath);
            return fallback;
        }
    }

    public HidDeviceState Clone(HidDeviceState deviceState)
    {
        if (deviceState == null)
        {
            throw new ArgumentNullException(nameof(deviceState));
        }

        var hidDeviceState = new HidDeviceState();

        hidDeviceState.DevicePath = deviceState.DevicePath;
        hidDeviceState.DeviceKey = deviceState.DeviceKey;
        hidDeviceState.ProductId = deviceState.ProductId;
        hidDeviceState.ReleaseNumber = deviceState.ReleaseNumber;
        hidDeviceState.ReleaseNumberBcd = deviceState.ReleaseNumberBcd;
        hidDeviceState.VendorId = deviceState.VendorId;
        hidDeviceState.Manufacturer = deviceState.Manufacturer;
        hidDeviceState.SerialNumber = deviceState.SerialNumber;
        hidDeviceState.FileSystemName = deviceState.FileSystemName;
        hidDeviceState.FriendlyName = deviceState.FriendlyName;
        hidDeviceState.ProductName = deviceState.ProductName;

        byte[] rawReportDescriptor;
        if (deviceState.RawReportDescriptor != null)
        {
            int rawReportLength = deviceState.RawReportDescriptor.Length;
            rawReportDescriptor = new byte[rawReportLength];
            Array.Copy(deviceState.RawReportDescriptor, rawReportDescriptor, rawReportLength);
            hidDeviceState.RawReportDescriptor = rawReportDescriptor;
        }

        string[] serialPorts;
        if (deviceState.SerialPorts != null)
        {
            int serialPortsLength = deviceState.SerialPorts.Length;
            serialPorts = new string[serialPortsLength];
            Array.Copy(deviceState.SerialPorts, serialPorts, serialPortsLength);
            hidDeviceState.SerialPorts = serialPorts;
        }

        hidDeviceState.IsConnected = deviceState.IsConnected;

        return hidDeviceState;
    }
}
