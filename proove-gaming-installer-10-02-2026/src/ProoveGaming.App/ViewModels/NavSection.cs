namespace ProoveGaming.App.ViewModels;

// Order matters only for readability; nothing persists these by numeric value. Actuation (keyboard
// key-press force, from main) and Audio (headset volumes) are independent sections that happen to
// have landed in parallel — both are kept.
public enum NavSection { Presets, RgbLighting, Buttons, Macros, Dpi, Settings, Actuation, ExtendedKeys, Audio, Firmware }
