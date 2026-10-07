using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;

namespace ProoveGaming.App.ViewModels;

public enum MouseSettingType { Slider }

public partial class MouseSettingViewModel : ObservableObject
{
	public string Key  { get; init; } = string.Empty;
	public MouseSettingType Type { get; init; }

	// ── Slider ────────────────────────────────────────────────────────────────
	public double Min  { get; init; }
	public double Max  { get; init; }
	public string Unit { get; init; } = string.Empty;

	// Where "Restore to default" lands when the device's catalog entry declares no default of
	// its own. Set from the factory below alongside the initial Value.
	public double DefaultValue { get; init; }

	// ── State ─────────────────────────────────────────────────────────────────
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SliderPct))]
	[NotifyPropertyChangedFor(nameof(DisplayValue))]
	private double _value;

	// ── Computed ──────────────────────────────────────────────────────────────
	public double SliderPct => Max > Min ? (Value - Min) / (Max - Min) * 100.0 : 0;
	public string DisplayValue => $"{(int)Math.Round(Value)}{Unit}";

	// ── Methods ───────────────────────────────────────────────────────────────
	public void SetSliderFromPct(double pct)
	{
		Value = Math.Clamp(Min + pct * (Max - Min), Min, Max);
	}

	// ── Factory ───────────────────────────────────────────────────────────────
	public static MouseSettingViewModel? Create(string key) => key switch
	{
		"ButtonDebounce"  => new MouseSettingViewModel { Key = key, Min = 2,  Max = 50, Unit = "ms",  Value = 8, DefaultValue = 8 },
		"LiftOffDistance" => new MouseSettingViewModel { Key = key, Min = 2,  Max = 3,  Unit = "mm",  Value = 2, DefaultValue = 2 },
		// Headset audio levels. Same slider shape as above — the type name is a mouse-era artifact,
		// not a restriction (see docs/plans/add-cosmo-headset).
		"OutputVolume"     => new MouseSettingViewModel { Key = key, Min = 0,  Max = 100, Unit = "%", Value = 50, DefaultValue = 50 },
		"MicrophoneVolume" => new MouseSettingViewModel { Key = key, Min = 0,  Max = 100, Unit = "%", Value = 50, DefaultValue = 50 },
		_ => null,
	};
}
