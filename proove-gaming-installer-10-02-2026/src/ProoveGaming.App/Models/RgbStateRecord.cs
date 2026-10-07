using System.Collections.Generic;

namespace ProoveGaming.App.Models;

// Persisted RGB state for a device (SQLite). Mirrors the in-session RgbDeviceState so the
// chosen effect + per-effect colour/brightness/speed survive an App restart.
public sealed class RgbStateRecord
{
	public bool IsEnabled { get; set; } = true;
	public string SelectedEffect { get; set; } = string.Empty;
	public List<RgbEffectRecord> Effects { get; set; } = [];
}

public sealed class RgbEffectRecord
{
	public string EffectKey { get; set; } = string.Empty;
	public string Hex { get; set; } = string.Empty;
	public double Brightness { get; set; }
	public double Speed { get; set; }
	public bool IsRainbow { get; set; }
}
