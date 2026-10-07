using System.Collections.Generic;

namespace ProoveGaming.App.Models.Settings;

public sealed record PerEffectSettings(string Hex, double Brightness, double Speed, bool IsRainbow = false);

public sealed record RgbDeviceSettings(
    bool IsEnabled,
    string SelectedEffectKey,
    Dictionary<string, PerEffectSettings> Effects);
