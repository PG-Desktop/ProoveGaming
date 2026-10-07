using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// Default values for the generic app-scoped preferences block (used by
	// ResetSettings and as the pre-load baseline).
	private const double DefaultLiftOffDistance = 0;
	private const bool DefaultScrollOneScreenAtATime = false;
	private const double DefaultScrollLines = 3;
	private const double DefaultDoubleClickSpeed = 550;
	private const double DefaultSleepTimer = 120;

	// ── Settings backing fields ───────────────────────────────────────────────
	private double _liftOffDistance        = DefaultLiftOffDistance;
	private bool   _scrollOneScreenAtATime = DefaultScrollOneScreenAtATime;
	private double _scrollLines            = DefaultScrollLines;
	private double _doubleClickSpeed       = DefaultDoubleClickSpeed;
	private double _sleepTimer             = DefaultSleepTimer;
	private IReadOnlyList<MouseSettingViewModel> _deviceSettings = [];
	private CancellationTokenSource? _mouseSettingsDebounceCts;
	private CancellationTokenSource? _appPrefsDebounceCts;
	// Suppresses the debounced save while pre-loading persisted values so a load
	// does not immediately schedule an echo write.
	private bool _suppressPreferencesSave;
	private bool _suppressMouseSettingsSave;
	private bool _appPreferencesLoaded;

	// ── Device-specific settings (e.g. BLAZE) ────────────────────────────────
	public IReadOnlyList<MouseSettingViewModel> DeviceSettings => _deviceSettings;
	public bool HasDeviceSpecificSettings => _deviceSettings.Count > 0;

	// Named helpers used by AXAML to conditionally show individual controls
	public MouseSettingViewModel? ButtonDenounceSetting  => _deviceSettings.FirstOrDefault(s => s.Key == "ButtonDebounce");
	public MouseSettingViewModel? LiftOffSetting         => _deviceSettings.FirstOrDefault(s => s.Key == "LiftOffDistance");

	public bool HasButtonDebounce  => ButtonDenounceSetting  is not null;
	public bool HasLiftOffDistance => LiftOffSetting         is not null;

	// Returns the persisted-settings load task so a caller that then reads the values (the preset
	// apply) can await it — otherwise the apply can read the previous preset's stale debounce/lift.
	internal Task RebuildDeviceSettings()
	{
		foreach (var s in _deviceSettings)
			s.PropertyChanged -= OnMouseSettingChanged;

		_deviceSettings = Device?.SupportedMouseSettings is { Count: > 0 } keys
			? keys.Select(MouseSettingViewModel.Create).OfType<MouseSettingViewModel>().ToList()
			: [];

		// Seed with the device's catalog defaults, so a device being used for the first time starts
		// at its factory-sane values rather than the sliders' generic ones. Safe to write directly:
		// nothing is subscribed yet, and the load below overwrites these if the user has values saved.
		foreach (var s in _deviceSettings)
		{
			s.Value = s.Key switch
			{
				"ButtonDebounce"  => ClampToSetting(s, CurrentDefaults.ButtonDebounceMs),
				"LiftOffDistance" => ClampToSetting(s, CurrentDefaults.LiftOffDistanceMm),
				_ => s.Value,
			};
		}

		// Pre-load persisted values BEFORE subscribing so restoring stored state
		// does not schedule a debounced echo write back to the device.
		var loadTask = LoadPersistedMouseSettingsAsync(Device);

		foreach (var s in _deviceSettings)
			s.PropertyChanged += OnMouseSettingChanged;

		OnPropertyChanged(nameof(DeviceSettings));
		OnPropertyChanged(nameof(HasDeviceSpecificSettings));
		OnPropertyChanged(nameof(ButtonDenounceSetting));
		OnPropertyChanged(nameof(LiftOffSetting));
		OnPropertyChanged(nameof(HasButtonDebounce));
		OnPropertyChanged(nameof(HasLiftOffDistance));

		return loadTask;
	}

	private async Task LoadPersistedMouseSettingsAsync(DeviceCardViewModel? device)
	{
		if (_settings == null || device == null || _deviceSettings.Count == 0) return;

		var stored = await _settings.LoadMouseSettingsAsync(device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		if (stored is null || !ReferenceEquals(Device, device)) return;

		_suppressMouseSettingsSave = true;
		try
		{
			if (ButtonDenounceSetting is { } debounce)
				debounce.Value = Math.Clamp(stored.ButtonDebounce, debounce.Min, debounce.Max);
			if (LiftOffSetting is { } lift)
				lift.Value = Math.Clamp(stored.LiftOffDistance, lift.Min, lift.Max);
		}
		finally
		{
			_suppressMouseSettingsSave = false;
		}
	}

	private void OnMouseSettingChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is not nameof(MouseSettingViewModel.Value))
			return;

		if (_suppressMouseSettingsSave)
			return;

		_mouseSettingsDebounceCts?.Cancel();
		var cts = new CancellationTokenSource();
		_mouseSettingsDebounceCts = cts;
		_ = DebounceApplyMouseSettingsAsync(cts.Token);
	}

	private async Task DebounceApplyMouseSettingsAsync(CancellationToken token)
	{
		try { await Task.Delay(300, token); }
		catch (TaskCanceledException) { return; }
		await ApplyMouseSettingsAsync();
	}

	private async Task ApplyMouseSettingsAsync()
	{
		if (Device == null) return;
		if (!IsDeviceConfigurable) return;

		var btnDebounce = (byte)Math.Clamp(Math.Round(ButtonDenounceSetting?.Value ?? 8), 0, 255);
		var liftOffMm   = (byte)Math.Clamp(Math.Round(LiftOffSetting?.Value ?? 2), 2, 3);

		// Persist the device-specific settings; StageChangeAndApply below marks the preset dirty
		// and (debounced) pushes the change to the device.
		if (_settings != null)
		{
			await _settings.SaveMouseSettingsAsync(
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey,
				new MouseSettingsPayload(btnDebounce, liftOffMm));
		}

		StageChangeAndApply();
	}

	// ── DPI ───────────────────────────────────────────────────────────────────
	// Sets the mouse's active DPI stage from the UI. Both mouse families support it — BLAZE via the
	// OEM's 0x0B SetCurrentDPI, TAIPAN via the DpiSwitch byte of MsConfig — and the physical DPI
	// button still drives the stage too, with HighlightActiveDpiStage reflecting whichever moved it.
	private void SelectDpiPreset(DpiPresetViewModel selected)
	{
		if (Device?.SupportsDpiStageSwitch != true) return;
		if (!IsDeviceConfigurable) { HighlightActiveDpiStage(selected.StageIndex); return; }

		HighlightActiveDpiStage(selected.StageIndex);
		_dpiStageKnown = true;

		// Persists the active stage; StageChangeAndApply below marks the preset dirty and
		// (debounced) pushes it to the device. The physical DPI button still drives the stage live.
		if (_settings != null)
			_ = _settings.SaveActiveDpiStageAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, selected.StageIndex);

		StageChangeAndApply();
	}

	// Reflects the device's current active DPI stage in the UI. Called when the
	// DeviceHost reports a stage change (physical DPI button press) or on refresh.
	internal void HighlightActiveDpiStage(int stageIndex)
	{
		for (int i = 0; i < DpiPresets.Count; i++)
			DpiPresets[i].IsSelected = i == stageIndex;

		// The customization block edits whichever point is selected, so it follows every writer of
		// the selection — the user's click, a physical DPI-button press, a preset switch.
		RefreshDpiPointState();
	}

	// "Recommendations" restores the DPI section to this device's catalog defaults: the stage ladder,
	// the active stage, and the polling rate — the whole of what that section shows. Buttons and the
	// per-device sliders belong to the Settings "Restore to default" button (see ResetSettings),
	// which restores this section too, as part of restoring everything.
	[RelayCommand]
	private Task ApplyDpiRecommendations() => RestoreDpiSectionDefaultsAsync();

	// Shared by both restore buttons. Ends in StageChangeAndApply, so the caller does not need its
	// own — the debounce coalesces this with anything the caller restored alongside it into one
	// device write, which is the only write shape BLAZE tolerates.
	private async Task RestoreDpiSectionDefaultsAsync()
	{
		if (Device == null) return;
		if (!IsDeviceConfigurable) return;

		// Rebuild the ladder from the catalog so a user who edited stage values gets them back, then
		// select the default stage.
		//
		// Falls back to the ladder's FIRST stage, never to the stage the user was on. This is a
		// restore button: landing on a defined stage is the whole point, and preserving the current
		// one is indistinguishable from the button doing nothing — which is exactly how it behaved
		// for a device whose catalog declares no default DPI (the documented fallback for `Dpi` is
		// "the first stage on the device's DPI ladder", see docs/device-defaults.md).
		RebuildDpiPresets(Device);
		var restoredStage = ResolveDefaultDpiStageIndex() ?? DpiPresets.FirstOrDefault()?.StageIndex;
		if (restoredStage is { } stage)
		{
			HighlightActiveDpiStage(stage);
			_dpiStageKnown = true;
		}

		// The polling rate is part of the DPI section, so "Recommendations" restores it too — on a
		// device whose ladder and debounce already sit on their defaults, this is often the only
		// visible change the button makes.
		if (Device.SupportsDpiStageSwitch)
		{
			HighlightPollingRate(DefaultReportRateHz);
			_reportRateKnown = true;
		}

		// Persist the restored table + stage + rate; StageChangeAndApply below marks the preset dirty
		// and (debounced) pushes it all to the device.
		if (_settings != null)
		{
			var snapshot = new DpiPresetsSettings(
				DpiPresets.Select(p => new DpiPresetSetting(p.StageIndex, p.DpiValue, p.R, p.G, p.B)).ToList());
			await _settings.SaveDpiPresetsAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, snapshot);

			if (Device.SupportsDpiStageSwitch)
			{
				if (_dpiStageKnown && DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex is { } activeStage)
					await _settings.SaveActiveDpiStageAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, activeStage);

				await _settings.SavePollingRateAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, DefaultReportRateHz);
			}
		}

		StageChangeAndApply();
	}

	// ── Polling Rate ──────────────────────────────────────────────────────────
	// Built-in fallback (X43 spec sheet 3311) for a device whose catalog config declares no
	// ReportRateHz default, and the value a revert falls back to when the preset being restored
	// predates PersistDisplayedDeviceState and holds no rate of its own. Prefer
	// DefaultReportRateHz, which layers the device's catalog value over this.
	internal const int FallbackPollingRateHz = 1000;

	// User clicked a rate in the UI → highlight AND write it to the device.
	private void SelectPollingRate(PollingRateOptionViewModel selected)
	{
		foreach (var option in PollingRateOptions)
			option.IsSelected = ReferenceEquals(option, selected);
		_reportRateKnown = true;

		if (Device == null) return;
		if (!IsDeviceConfigurable) return;

		// Persists the report rate; StageChangeAndApply below marks the preset dirty and
		// (debounced) pushes it to the device. The physical polling button still drives the rate
		// live.
		if (_settings != null)
			_ = _settings.SavePollingRateAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, selected.Hz);

		StageChangeAndApply();
	}

	// Reflects the device's current report rate in the UI WITHOUT writing back — used
	// when the DeviceHost reports a rate (physical polling button, or the startup state
	// query). Must not call SetReportRateAsync, or a hardware-driven update would echo
	// back to the device. Mirrors HighlightActiveDpiStage.
	internal void HighlightPollingRate(int hz)
	{
		foreach (var option in PollingRateOptions)
			option.IsSelected = option.Hz == hz;
	}

	// ── Generic Mouse Settings (fallback for devices without specific settings) ─
	public double LiftOffDistance
	{
		get => _liftOffDistance;
		set
		{
			var clamped = Math.Clamp(Math.Round(value), 0, 5);
			if (SetProperty(ref _liftOffDistance, clamped))
			{
				OnPropertyChanged(nameof(LiftOffDistancePct));
				ScheduleAppPreferencesSave();
			}
		}
	}
	public double LiftOffDistancePct => _liftOffDistance / 5.0 * 100.0;

	public bool ScrollOneScreenAtATime
	{
		get => _scrollOneScreenAtATime;
		set
		{
			if (SetProperty(ref _scrollOneScreenAtATime, value))
				ScheduleAppPreferencesSave();
		}
	}

	public double ScrollLines
	{
		get => _scrollLines;
		set
		{
			var clamped = Math.Clamp(Math.Round(value), 1, 100);
			if (SetProperty(ref _scrollLines, clamped))
			{
				OnPropertyChanged(nameof(ScrollLinesPct));
				ScheduleAppPreferencesSave();
			}
		}
	}
	public double ScrollLinesPct => (_scrollLines - 1) / 99.0 * 100.0;

	public double DoubleClickSpeed
	{
		get => _doubleClickSpeed;
		set
		{
			var clamped = Math.Clamp(Math.Round(value), 100, 900);
			if (SetProperty(ref _doubleClickSpeed, clamped))
			{
				OnPropertyChanged(nameof(DoubleClickSpeedPct));
				ScheduleAppPreferencesSave();
			}
		}
	}
	public double DoubleClickSpeedPct => (_doubleClickSpeed - 100) / 800.0 * 100.0;

	public double SleepTimer
	{
		get => _sleepTimer;
		set
		{
			var clamped = Math.Clamp(Math.Round(value / 30) * 30, 30, 300);
			if (SetProperty(ref _sleepTimer, clamped))
			{
				OnPropertyChanged(nameof(SleepTimerPct));
				OnPropertyChanged(nameof(SleepTimerLabel));
				ScheduleAppPreferencesSave();
			}
		}
	}
	public double SleepTimerPct   => (_sleepTimer - 30) / 270.0 * 100.0;
	public string SleepTimerLabel => _sleepTimer < 60 ? $"{_sleepTimer:0}s" : $"{_sleepTimer / 60:0}min";

	// "Restore to default" puts the WHOLE device back on its catalog defaults: the app-scoped
	// preferences, the per-device sliders, the remappable buttons, and the DPI section (stage ladder,
	// active stage, polling rate). It is the broad reset; the DPI section's own "Recommendations"
	// button restores just that section.
	//
	// Every part below stages its change synchronously and the debounce coalesces them, so the whole
	// reset reaches the hardware as ONE coordinated write.
	[RelayCommand]
	private async Task ResetSettings()
	{
		ScrollOneScreenAtATime = DefaultScrollOneScreenAtATime;
		ScrollLines            = DefaultScrollLines;
		DoubleClickSpeed       = DefaultDoubleClickSpeed;
		SleepTimer             = DefaultSleepTimer;
		LiftOffDistance        = DefaultLiftOffDistance;

		await ResetDeviceSettingsToDefaultAsync();
		ResetButtonBindingsToDefault();

		// DPI stage / ladder / polling rate. Last because it is the one part that awaits, and doing
		// it here means a device with defaults already on its sliders and buttons still shows a
		// visible change — which is what made this button look like it did nothing.
		await RestoreDpiSectionDefaultsAsync();
	}

	// Restores this device's own sliders (debounce / lift-off) to the catalog defaults, falling back
	// to each slider's own default when the device declares none. The individual writes run under
	// _suppressMouseSettingsSave so they don't each schedule a debounced save; one explicit
	// ApplyMouseSettingsAsync then persists both values and stages the device apply.
	//
	// AWAITS that apply rather than firing it off. The preset Reset flow snapshots device_settings
	// immediately after calling this, and ApplyMouseSettingsAsync is what writes those rows — left
	// unawaited, the snapshot could capture the values from BEFORE the reset.
	private async Task ResetDeviceSettingsToDefaultAsync()
	{
		if (_deviceSettings.Count == 0) return;

		_suppressMouseSettingsSave = true;
		try
		{
			if (ButtonDenounceSetting is { } debounce)
				debounce.Value = ClampToSetting(debounce, CurrentDefaults.ButtonDebounceMs);
			if (LiftOffSetting is { } lift)
				lift.Value = ClampToSetting(lift, CurrentDefaults.LiftOffDistanceMm);
		}
		finally
		{
			_suppressMouseSettingsSave = false;
		}

		await ApplyMouseSettingsAsync();
	}

	// A catalog default outside the slider's range would silently distort the UI, so clamp it;
	// no configured default means "keep the slider's own default".
	private static double ClampToSetting(MouseSettingViewModel setting, int? configured) =>
		configured is { } value
			? Math.Clamp(value, setting.Min, setting.Max)
			: setting.DefaultValue;

	[RelayCommand]
	private Task SaveSettings() => WriteAppPreferencesAsync();

	// Loads persisted app-scoped preferences on first entry into the Settings section.
	// Runs at most once per VM instance — subsequent visits already have the values.
	internal async Task EnsureAppPreferencesLoadedAsync()
	{
		if (_appPreferencesLoaded || _settings == null) return;
		_appPreferencesLoaded = true;

		var stored = await _settings.LoadAppPreferencesAsync();
		if (stored is null) return;

		_suppressPreferencesSave = true;
		try
		{
			ScrollOneScreenAtATime = stored.ScrollOneScreenAtATime;
			ScrollLines            = stored.ScrollLines;
			DoubleClickSpeed       = stored.DoubleClickSpeed;
			SleepTimer             = stored.SleepTimer;
			LiftOffDistance        = stored.GenericLiftOffDistance;
		}
		finally
		{
			_suppressPreferencesSave = false;
		}
	}

	private void ScheduleAppPreferencesSave()
	{
		if (_suppressPreferencesSave || _settings == null) return;

		_appPrefsDebounceCts?.Cancel();
		var cts = new CancellationTokenSource();
		_appPrefsDebounceCts = cts;
		_ = DebounceSaveAppPreferencesAsync(cts.Token);
	}

	private async Task DebounceSaveAppPreferencesAsync(CancellationToken token)
	{
		try { await Task.Delay(300, token); }
		catch (TaskCanceledException) { return; }
		await WriteAppPreferencesAsync();
	}

	private Task WriteAppPreferencesAsync()
	{
		if (_settings == null) return Task.CompletedTask;
		var payload = new AppPreferencesPayload(
			ScrollOneScreenAtATime,
			ScrollLines,
			DoubleClickSpeed,
			SleepTimer,
			LiftOffDistance);
		return _settings.SaveAppPreferencesAsync(payload);
	}
}
