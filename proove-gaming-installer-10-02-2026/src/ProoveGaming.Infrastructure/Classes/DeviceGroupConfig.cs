namespace ProoveGaming.Infrastructure.Classes;

// One physical product in the device catalog. Carries the device-level defaults once, plus a list
// of transports (cable / 2.4G dongle / Bluetooth). DeviceMapExpander turns this into one DeviceMap
// row per transport, applying each transport's overrides over these defaults. Public settable
// properties so it binds from appsettings (AllowedDevices:Devices).
public class DeviceGroupConfig
{
    public string Name { get; set; } = string.Empty;
    // Optional: the name the App shows, when it differs from Name (which device matching, storage
    // and protocol choice key on). Absent = show Name.
    public string? DisplayName { get; set; }
    public string? DeviceName { get; set; }
    public string? CardType { get; set; }
    public string? ConnectionType { get; set; }
    public string? ImagePath { get; set; }
    public string? RgbImagePath { get; set; }
    public string? DeviceType { get; set; }
    public string? ProductName { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public bool SupportsRgb { get; set; }
    public bool SupportsButtonRemap { get; set; }
    public bool SupportsMacros { get; set; }
    // True for magnetic-axis keyboards whose actuation (key press force) is adjustable (T800,
    // Cyrex, Aress). Device-level: actuation capability describes the product, not a transport.
    public bool SupportsActuation { get; set; }
    // True for keyboards with an advanced-key storage area (Extended Keys) — Cyrex and Aress.
    // Device-level like actuation: the capability describes the product, not a transport.
    public bool SupportsExtendedKeys { get; set; }
    public byte? BatteryCommand { get; set; }
    public byte? BatteryReportId { get; set; }
    public bool? BatteryDirectScale { get; set; }
    public List<string>? SupportedMouseSettings { get; set; }
    // Which audio levels this device exposes ("OutputVolume", "MicrophoneVolume"). Empty/null on
    // every device that is not a headset, which is what hides the Audio section for them.
    public List<string>? SupportedAudioSettings { get; set; }
    // Where an audio level is applied: "OperatingSystem" (the device's own Windows endpoints, which
    // is what Cosmo does) or "Device" (a device-side level held in firmware). Null -> OperatingSystem.
    // A per-device capability because the two behave differently and the next headset may not match
    // this one.
    public string? AudioVolumeTarget { get; set; }
    public List<int>? DpiStages { get; set; }
    // Bounds for the user-editable DPI ladder (value range / quantum, and how many points the
    // device's stage table holds). Null -> the App's built-in bounds. See DeviceMap for detail.
    public int? DpiMin { get; set; }
    public int? DpiMax { get; set; }
    public int? DpiStep { get; set; }
    public int? DpiSlotMin { get; set; }
    public int? DpiSlotMax { get; set; }
    public double? ImageScale { get; set; }
    // Extra darkening scrim (0..1) laid over this board's artwork by the Key Assignments / Macros
    // overlay so the selection box reads on brightly-coloured keycaps (Rampage). Null -> none.
    public double? KeyOverlayArtDim { get; set; }
    public List<DeviceHotspotDefinition>? Hotspots { get; set; }
    // Per-device macro recorder limits (MaxDelayMs / MaxRepetitions); null -> App defaults.
    public DeviceMacrosConfig? Macros { get; set; }

    // Factory-sane starting values (DPI, report rate, debounce, lift-off, lighting); null -> the App
    // falls back to its built-in constants. Device-level only: defaults describe the product, not a
    // transport, so there is no per-transport override. See docs/device-defaults.md.
    //
    // This is the class appsettings actually binds to — a property missing here means the whole
    // JSON block is silently dropped before DeviceMapExpander ever sees it, which is exactly how
    // "Defaults" was lost while looking correct in appsettings.json AND in DeviceMap.
    public DeviceDefaultsConfig? Defaults { get; set; }

    // False when this product has no readable battery, even on a transport that normally implies one.
    // A wireless device is otherwise assumed to report battery purely from its transport, which would
    // give Cosmo a red "critical" indicator on its dongle — it has a battery, but nothing we can
    // read it with until the vendor documents report 0xBC.
    public bool SupportsBattery { get; set; } = true;

    // False when configuration writes do not reach the device on this product at all. Normally set
    // per TRANSPORT below rather than here, since it is usually the transport that cannot carry
    // configuration, not the product.
    public bool SupportsConfiguration { get; set; } = true;

    // True when this one physical device is reachable over several transports that must show as a
    // single card sharing one settings bucket (e.g. TAIPAN, T-Racer). Explicit, not implied from
    // the transport count: some products list multiple transports yet are intentionally not merged
    // (e.g. Rampage). See HidDeviceMapRegistry.
    public bool MergeTransports { get; set; }

    public List<DeviceTransportConfig> Transports { get; set; } = [];
}

// One transport of a DeviceGroupConfig. ConnectionType is required; every other field is an optional
// override that falls back to the device-level value when null. Only the fields that actually vary
// per transport in the catalog are overridable here.
public class DeviceTransportConfig
{
    // Substring of the Windows device name identifying this model when several products share one
    // Bluetooth VID/PID (Starlight and Cosmo both report 0x05D6/0x000A). Bluetooth transports only;
    // omitted elsewhere, which preserves VID/PID-only matching. See ClassicBluetoothDeviceResolver.
    public string? BluetoothName { get; set; }

    public string ConnectionType { get; set; } = string.Empty;
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? DeviceType { get; set; }
    public bool? SupportsRgb { get; set; }
    // False when host->device configuration does not work over THIS transport, so the App can stop
    // offering to save presets the device would never receive. TAIPAN's 2.4G and Bluetooth
    // transports set this; its cable does not. Null inherits the device-level value.
    public bool? SupportsConfiguration { get; set; }

    /// <summary>
    /// Whether THIS transport can report a usable battery level. Falls back to the device-level
    /// value when unset.
    /// </summary>
    /// <remarks>
    /// Per-transport because a battery reading is a property of the link, not only of the device.
    /// TAIPAN is the case that forced it: over the cable the mouse reports its charge VOLTAGE, which
    /// rises while charging (raw 1002 on the cable against 979-982 at rest ten minutes earlier —
    /// the charge state did not move 20 %), so it pins at 100 % and tells the user nothing.
    /// </remarks>
    public bool? SupportsBattery { get; set; }
    public byte? BatteryCommand { get; set; }
    public byte? BatteryReportId { get; set; }
    public bool? BatteryDirectScale { get; set; }
}
