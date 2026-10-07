namespace ProoveGaming.Infrastructure.Classes;

// Expands grouped device config into the flat per-transport DeviceMap rows the rest of the
// DeviceHost consumes (matched by VendorId/ProductId/HasBluetooth). One device × N transports → N
// rows. Each field is transport-override ?? device-default; HasBluetooth is derived from the
// transport's ConnectionType; MergeTransports is the device's explicit flag stamped on every row.
// Pure and stateless.
public static class DeviceMapExpander
{
    public static List<DeviceMap> Expand(IEnumerable<DeviceGroupConfig>? devices)
    {
        var rows = new List<DeviceMap>();
        if (devices is null)
            return rows;

        foreach (var device in devices)
        {
            if (device.Transports is not { Count: > 0 })
                continue;

            foreach (var t in device.Transports)
            {
                var connectionType = string.IsNullOrEmpty(t.ConnectionType)
                    ? device.ConnectionType
                    : t.ConnectionType;

                rows.Add(new DeviceMap
                {
                    Name = device.Name?.ToUpper() ?? string.Empty,
                    // transport override ?? device default
                    VendorId = t.VendorId ?? device.VendorId,
                    ProductId = t.ProductId ?? device.ProductId,
                    ProductName = t.ProductName ?? device.ProductName,
                    DeviceType = t.DeviceType ?? device.DeviceType,
                    SupportsRgb = t.SupportsRgb ?? device.SupportsRgb,
                    SupportsConfiguration = t.SupportsConfiguration ?? device.SupportsConfiguration,
                    SupportsBattery = t.SupportsBattery ?? device.SupportsBattery,
                    BatteryCommand = t.BatteryCommand ?? device.BatteryCommand,
                    BatteryReportId = t.BatteryReportId ?? device.BatteryReportId,
                    BatteryDirectScale = t.BatteryDirectScale ?? device.BatteryDirectScale,
                    ConnectionType = connectionType,
                    HasBluetooth = string.Equals(connectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase),
                    // Transport-only: no device-level fallback, because the token identifies a model
                    // on a shared BLUETOOTH id pair and means nothing on a cable or dongle row.
                    BluetoothName = t.BluetoothName,
                    // device-level, copied down (lists cloned so rows never alias)
                    DeviceName = device.DeviceName,
                    DisplayName = device.DisplayName,
                    CardType = device.CardType,
                    ImagePath = device.ImagePath,
                    RgbImagePath = device.RgbImagePath,
                    SupportsButtonRemap = device.SupportsButtonRemap,
                    SupportsMacros = device.SupportsMacros,
                    SupportsActuation = device.SupportsActuation,
                    SupportsExtendedKeys = device.SupportsExtendedKeys,
                    MergeTransports = device.MergeTransports,
                    ImageScale = device.ImageScale,
                    KeyOverlayArtDim = device.KeyOverlayArtDim,
                    SupportedMouseSettings = device.SupportedMouseSettings is null
                        ? null
                        : [.. device.SupportedMouseSettings],
                    SupportedAudioSettings = device.SupportedAudioSettings is null
                        ? null
                        : [.. device.SupportedAudioSettings],
                    AudioVolumeTarget = device.AudioVolumeTarget,
                    DpiStages = device.DpiStages is null ? null : [.. device.DpiStages],
                    DpiMin = device.DpiMin,
                    DpiMax = device.DpiMax,
                    DpiStep = device.DpiStep,
                    DpiSlotMin = device.DpiSlotMin,
                    DpiSlotMax = device.DpiSlotMax,
                    Hotspots = device.Hotspots is null ? null : [.. device.Hotspots],
                    Macros = device.Macros is null
                        ? null
                        : new DeviceMacrosConfig
                        {
                            MaxDelayMs = device.Macros.MaxDelayMs,
                            MaxRepetitions = device.Macros.MaxRepetitions,
                        },
                    // Cloned, not shared: every transport row gets its own instance so a later
                    // mutation of one row cannot reach the others (same rule as the lists above).
                    Defaults = device.Defaults is null
                        ? null
                        : new DeviceDefaultsConfig
                        {
                            Dpi = device.Defaults.Dpi,
                            ReportRateHz = device.Defaults.ReportRateHz,
                            ButtonDebounceMs = device.Defaults.ButtonDebounceMs,
                            LiftOffDistanceMm = device.Defaults.LiftOffDistanceMm,
                            RgbEnabled = device.Defaults.RgbEnabled,
                            RgbEffect = device.Defaults.RgbEffect,
                            RgbColorHex = device.Defaults.RgbColorHex,
                            RgbBrightness = device.Defaults.RgbBrightness,
                            RgbSpeed = device.Defaults.RgbSpeed,
                        },
                });
            }
        }

        return rows;
    }
}
