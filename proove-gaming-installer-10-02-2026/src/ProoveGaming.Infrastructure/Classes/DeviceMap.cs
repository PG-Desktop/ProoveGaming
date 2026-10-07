namespace ProoveGaming.Infrastructure.Classes
{
    public class DeviceMap
    {
        public required string Name { get; set; }
        public string? ProductId { get; set; }
        public string? VendorId { get; set; }
        public bool HasBluetooth { get; set; }

        // Substring of the Windows device name identifying THIS model on a shared Bluetooth
        // VID/PID. Starlight and Cosmo both report 0x05D6/0x000A — the pair is an ODM constant,
        // not an identity — so without a token the first row wins and one headset is drawn as the
        // other. Null/empty keeps the VID/PID-only behaviour every other device relies on.
        public string? BluetoothName { get; set; }
        public string? DeviceId { get; set; }
        public string? ProductName { get; set; }
        public byte? BatteryCommand { get; set; }
        public byte? BatteryReportId { get; set; }
        public bool? BatteryDirectScale { get; set; }
        public string? DeviceName { get; set; }
        public string? DisplayName { get; set; }
        public string? DeviceType { get; set; }
        public string? CardType { get; set; }
        public string? ConnectionType { get; set; }
        public string? ImagePath { get; set; }
        public string? RgbImagePath { get; set; }
        public bool SupportsRgb { get; set; }
        public List<string>? SupportedMouseSettings { get; set; }
        // Audio levels this device exposes ("OutputVolume", "MicrophoneVolume"); empty/null on
        // anything that is not a headset. See DeviceGroupConfig for the pair below.
        public List<string>? SupportedAudioSettings { get; set; }
        // "OperatingSystem" (the device's Windows endpoints) or "Device" (a firmware-held level).
        public string? AudioVolumeTarget { get; set; }
        public bool SupportsButtonRemap { get; set; }
        public bool SupportsMacros { get; set; }
        // True for magnetic-axis keyboards with adjustable actuation (T800, Cyrex, Aress).
        public bool SupportsActuation { get; set; }
        // True for keyboards with an advanced-key storage area (Extended Keys: Rapid Snap, SOCD,
        // Mod-Tap, Toggle) — Cyrex and Aress. The App additionally trusts the live probe.
        public bool SupportsExtendedKeys { get; set; }
        // Per-device DPI stage values shown in the UI and written by "Recommendations". Device-
        // specific (e.g. BLAZE vs Taipan); when omitted the App falls back to its default set.
        public List<int>? DpiStages { get; set; }
        // Bounds for the user-editable DPI ladder. DpiMin/DpiMax/DpiStep bound a single point's
        // value and its quantum (50 is TAIPAN's native register unit); DpiSlotMin/DpiSlotMax bound
        // how many points the stage table holds (1-8 on both mice). Null -> the App's built-in
        // bounds. See docs/plans/add-custom-dpi-values/.
        public int? DpiMin { get; set; }
        public int? DpiMax { get; set; }
        public int? DpiStep { get; set; }
        public int? DpiSlotMin { get; set; }
        public int? DpiSlotMax { get; set; }
        // Optional render scale for the device image in details views (artwork padding differs
        // per device); null -> the App renders at 1.0.
        public double? ImageScale { get; set; }
        // Extra darkening scrim (0..1) for the Key Assignments / Macros overlay artwork, so the
        // selection box reads on brightly-coloured keycaps (Rampage); null -> no scrim.
        public double? KeyOverlayArtDim { get; set; }
        // Set on every entry of a product that is ONE physical device reachable over several
        // transports (wired / dongle / Bluetooth), e.g. TAIPAN. Such a product shows a single
        // card and keeps one settings bucket across transports. Leave false for products where
        // two units can legitimately be connected at once (e.g. BLAZE).
        public bool MergeTransports { get; set; }
        // Button-remap hotspots drawn over the device image, tuned per device. Empty/null
        // -> the details view shows no hotspots for this device.
        public List<DeviceHotspotDefinition>? Hotspots { get; set; }
        // Per-device macro recorder limits. Null -> the App uses its built-in defaults.
        public DeviceMacrosConfig? Macros { get; set; }
        // Factory-sane starting values for this device: applied when it is first added, and what
        // the DPI "Recommendations" and Settings "Restore to default" buttons restore. Null -> the
        // App falls back to its built-in constants. See docs/device-defaults.md.
        public DeviceDefaultsConfig? Defaults { get; set; }
        // False when this device has no readable battery even on a transport that implies one.
        public bool SupportsBattery { get; set; } = true;
        // False when configuration writes do not reach the device over THIS transport; the App then
        // keeps Save Preset disabled rather than offering to save something the device cannot take.
        public bool SupportsConfiguration { get; set; } = true;
    }

    // The default settings a device ships with. Every value is optional; when null that aspect has
    // no default and is left alone, so a keyboard simply omits Dpi/ReportRateHz/ButtonDebounceMs/
    // LiftOffDistanceMm and a device without lighting omits the Rgb* fields. Dpi is a DPI VALUE,
    // not a stage index: the App resolves it against that device's own DpiStages ladder, so a
    // device with a different ladder still lands on a sensible stage.
    public class DeviceDefaultsConfig
    {
        public int? Dpi { get; set; }
        public int? ReportRateHz { get; set; }
        public int? ButtonDebounceMs { get; set; }
        public int? LiftOffDistanceMm { get; set; }
        // RgbEffect names one of the App's effect keys (Static / Breathing / Neon / ColorCycle /
        // Wave) and must be one this device can actually render — Static and Wave are keyboard-only.
        public bool? RgbEnabled { get; set; }
        public string? RgbEffect { get; set; }
        public string? RgbColorHex { get; set; }
        public int? RgbBrightness { get; set; }
        public int? RgbSpeed { get; set; }
    }

    // Per-device macro recorder limits (from the device's "Macros" config section). Each value is
    // optional; when null the App falls back to its default. MaxDelayMs bounds the longest recorded
    // inter-event pause (device-specific: BLAZE's 7-bit delay field caps ~1 s, TAIPAN's 16-bit delay
    // action allows much longer); MaxRepetitions bounds the macro repeat count the UI accepts.
    public class DeviceMacrosConfig
    {
        public int? MaxDelayMs { get; set; }
        public int? MaxRepetitions { get; set; }
    }

    // Geometry of one button-remap hotspot, in the 1236x756 hotspot-canvas space. Only the
    // per-device geometry is carried; the label text and whether it is bindable are derived
    // in the App from the id. Public settable props so it binds from appsettings.
    public class DeviceHotspotDefinition
    {
        public string Id { get; set; } = string.Empty;
        public double ButtonX { get; set; }
        public double ButtonY { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public string LabelSide { get; set; } = "Left";
        // True → the hotspot labels the part but its button can't be bound (fixed in
        // firmware, e.g. left-click). Default false = bindable.
        public bool Disabled { get; set; }
    }
}
