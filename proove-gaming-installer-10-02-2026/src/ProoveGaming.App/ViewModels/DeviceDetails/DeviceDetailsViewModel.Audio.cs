using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;

namespace ProoveGaming.App.ViewModels;

// The Audio section — two 0-100 % sliders for a headset's output and microphone level.
//
// Where the level is APPLIED is a per-device capability (DeviceCardViewModel.AudioVolumeTarget),
// not a fixed rule: Cosmo moves the OS volume of its own Windows endpoints, while a future headset
// may hold its level in firmware. Only the OperatingSystem target is implemented; the Device target
// is deliberately inert rather than silently applying the wrong thing.
public partial class DeviceDetailsViewModel
{
	private IReadOnlyList<MouseSettingViewModel> _audioSettings = [];
	private CancellationTokenSource? _audioDebounceCts;
	// Suppresses the debounced apply while loading persisted values, so restoring state does not
	// immediately echo back to the endpoint. Same pattern as the mouse settings.
	private bool _suppressAudioSave;

	public IReadOnlyList<MouseSettingViewModel> AudioSettings => _audioSettings;

	public MouseSettingViewModel? OutputVolumeSetting => _audioSettings.FirstOrDefault(s => s.Key == "OutputVolume");
	public MouseSettingViewModel? MicrophoneVolumeSetting => _audioSettings.FirstOrDefault(s => s.Key == "MicrophoneVolume");

	public bool HasOutputVolume => OutputVolumeSetting is not null;
	public bool HasMicrophoneVolume => MicrophoneVolumeSetting is not null;

	// Mute state deliberately does NOT live here — it is on DeviceCardViewModel, so the home card
	// and the details connectivity bar render from one source. AudioMuteMonitor keeps it fresh.

	// Rebuilds the sliders for the current device and seeds them. Called from SetDeviceAsync, so it
	// runs for every device — it just produces an empty list for anything that declares no audio settings.
	//
	// forcePersisted mirrors LoadPersistedDeviceStateAsync's parameter of the same name and is set
	// by a preset apply: the preset's stored volumes must win over whatever the endpoint currently
	// reads, and be pushed back to the endpoint. Without it, applying a preset would restore every
	// other setting but silently leave the volumes where the user had dragged them.
	// Rebuilds are serialised by version, not by the device identity check alone: SetDeviceAsync
	// starts one fire-and-forget mid-chain, and a preset apply awaits another, so opening a page and immediately
	// picking a preset runs two rebuilds for the SAME device — which !ReferenceEquals(Device, device)
	// cannot tell apart. The older one would then subscribe a second OnAudioSettingChanged handler to
	// the current sliders, and its pre-preset endpoint read could overwrite the value the preset had
	// just restored.
	private int _audioRebuildVersion;

	internal async Task RebuildAudioSettingsAsync(bool forcePersisted = false)
	{
		var version = ++_audioRebuildVersion;

		foreach (var s in _audioSettings)
			s.PropertyChanged -= OnAudioSettingChanged;

		var device = Device;
		_audioSettings = device?.SupportedAudioSettings is { Count: > 0 } keys
			? keys.Select(MouseSettingViewModel.Create).OfType<MouseSettingViewModel>().ToList()
			: [];

		OnPropertyChanged(nameof(AudioSettings));
		OnPropertyChanged(nameof(OutputVolumeSetting));
		OnPropertyChanged(nameof(MicrophoneVolumeSetting));
		OnPropertyChanged(nameof(HasOutputVolume));
		OnPropertyChanged(nameof(HasMicrophoneVolume));

		var restoredFromStorage = false;
		if (_audioSettings.Count > 0)
			restoredFromStorage = await LoadAudioSettingsAsync(device, forcePersisted);

		// A newer rebuild started while the load was in flight; it owns the sliders now. Returning
		// here is what prevents the duplicate subscription and the stale-value overwrite.
		if (version != _audioRebuildVersion) return;

		// Subscribed only after loading, so the seeded values do not schedule a write.
		foreach (var s in _audioSettings)
			s.PropertyChanged += OnAudioSettingChanged;

		// A preset apply has to reach the endpoint. The load above deliberately runs under
		// suppression (no debounced echo), so the push is made explicitly here.
		if (forcePersisted && restoredFromStorage)
			await ApplyAudioSettingsAsync();

		if (version != _audioRebuildVersion) return;

		SyncVolumeTrackingTimer();
	}

	// ── Live volume tracking ─────────────────────────────────────────────────
	//
	// The headset's own +/- buttons are standard consumer-control keys that WINDOWS handles: the
	// app never sees them, it only sees the resulting endpoint level (measured on hardware, they
	// step by 6 — 52 -> 46 -> 58). So the slider has to follow the endpoint, or it drifts out of
	// sync with the volume the user is actually hearing the moment they touch the headset.
	//
	// Polled because Core Audio gives us no cheap change notification on a specific endpoint here.
	// One control-transfer read per endpoint per second, only while an audio-capable device is open.
	private DispatcherTimer? _volumeTrackTimer;
	private DateTime _lastLocalVolumeChangeUtc = DateTime.MinValue;

	// How long a local change (dragging the slider) suppresses polled updates. Without this the
	// poll would fight the user mid-drag: it reads the pre-write value and snaps the thumb back.
	private static readonly TimeSpan LocalChangeGrace = TimeSpan.FromMilliseconds(1200);

	// Also called when ActiveSection changes: the sliders are the only thing this feeds, so there is
	// nothing to track while the user is on Presets or Firmware. Each poll costs a full Core Audio
	// endpoint enumeration per direction, so gating on visibility takes the steady-state cost from
	// ~4 enumerations/second down to the mute monitor's alone.
	internal void SyncVolumeTrackingTimer()
	{
		var wanted = _audioSettings.Count > 0
			&& IsAudioSection
			&& Device?.AudioVolumeTarget == AudioVolumeTarget.OperatingSystem
			&& _audioEndpoints is not null;

		if (!wanted)
		{
			StopVolumeTracking();
			return;
		}

		if (_volumeTrackTimer is not null) return;

		// Created on the UI thread (this runs from SetDeviceAsync's chain), which is where a
		// DispatcherTimer must be started.
		_volumeTrackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_volumeTrackTimer.Tick += (_, _) => _ = TrackEndpointVolumeAsync();
		_volumeTrackTimer.Start();
	}

	internal void StopVolumeTracking()
	{
		if (_volumeTrackTimer is null) return;
		_volumeTrackTimer.Stop();
		_volumeTrackTimer = null;
	}

	private async Task TrackEndpointVolumeAsync()
	{
		var device = Device;
		if (device is null || _audioEndpoints is null || _audioSettings.Count == 0) return;
		if (device.AudioVolumeTarget != AudioVolumeTarget.OperatingSystem) return;

		// The user is mid-drag (or a debounced write is still in flight): their value wins.
		if (DateTime.UtcNow - _lastLocalVolumeChangeUtc < LocalChangeGrace) return;

		var output = await _audioEndpoints.GetOutputVolumeAsync(device.AudioContainerId);
		var mic = await _audioEndpoints.GetMicrophoneVolumeAsync(device.AudioContainerId);

		if (!ReferenceEquals(Device, device)) return;
		// Re-checked after the awaits: the user may have grabbed the slider while they were running.
		if (DateTime.UtcNow - _lastLocalVolumeChangeUtc < LocalChangeGrace) return;

		// Suppressed so adopting the device's own value does not schedule a write back to it, which
		// would be a pointless round-trip and could fight the physical button.
		_suppressAudioSave = true;
		try
		{
			if (output is { } o && OutputVolumeSetting is { } outputSetting)
				outputSetting.Value = Math.Clamp(o, outputSetting.Min, outputSetting.Max);
			if (mic is { } m && MicrophoneVolumeSetting is { } micSetting)
				micSetting.Value = Math.Clamp(m, micSetting.Min, micSetting.Max);
		}
		finally
		{
			_suppressAudioSave = false;
		}
	}

	// Live endpoint first, persisted value second. The endpoint is the truth for an OS-target
	// device — the user may have moved the Windows slider since we last saw them — so reading it
	// back is what makes the section agree with the volume mixer on every open. A preset apply
	// (forcePersisted) inverts that: the preset's stored value is the whole point, so the live
	// reads are skipped entirely.
	//
	// Returns true when stored values were applied, so the caller knows whether to push them out.
	private async Task<bool> LoadAudioSettingsAsync(DeviceCardViewModel? device, bool forcePersisted)
	{
		if (device is null) return false;

		var stored = _settings is null
			? null
			: await _settings.LoadAudioSettingsAsync(device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);

		int? liveOutput = null;
		int? liveMic = null;
		if (!forcePersisted && device.AudioVolumeTarget == AudioVolumeTarget.OperatingSystem && _audioEndpoints is not null)
		{
			liveOutput = await _audioEndpoints.GetOutputVolumeAsync(device.AudioContainerId);
			liveMic = await _audioEndpoints.GetMicrophoneVolumeAsync(device.AudioContainerId);
		}

		// The device may have been swapped while the reads above were in flight.
		if (!ReferenceEquals(Device, device)) return false;

		_suppressAudioSave = true;
		try
		{
			if (OutputVolumeSetting is { } output && (liveOutput ?? stored?.OutputVolume) is { } outputValue)
				output.Value = Math.Clamp(outputValue, output.Min, output.Max);
			if (MicrophoneVolumeSetting is { } mic && (liveMic ?? stored?.MicrophoneVolume) is { } micValue)
				mic.Value = Math.Clamp(micValue, mic.Min, mic.Max);
		}
		finally
		{
			_suppressAudioSave = false;
		}

		return stored is not null;
	}

	private void OnAudioSettingChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is not nameof(MouseSettingViewModel.Value))
			return;

		if (_suppressAudioSave)
			return;

		// Marks this as a USER change so the endpoint tracker backs off and does not snap the
		// thumb back to the pre-write value mid-drag.
		_lastLocalVolumeChangeUtc = DateTime.UtcNow;

		_audioDebounceCts?.Cancel();
		var cts = new CancellationTokenSource();
		_audioDebounceCts = cts;
		_ = DebounceApplyAudioSettingsAsync(cts.Token);
	}

	// Dragging a slider fires per pixel; without this each tick would be a COM round-trip.
	private async Task DebounceApplyAudioSettingsAsync(CancellationToken token)
	{
		try { await Task.Delay(300, token); }
		catch (TaskCanceledException) { return; }
		await ApplyAudioSettingsAsync();
	}

	// internal, not private: the debounce path is a timer the tests cannot wait on deterministically,
	// so they drive the apply directly (InternalsVisibleTo ProoveGaming.App.Tests).
	internal async Task ApplyAudioSettingsAsync()
	{
		var device = Device;
		if (device is null || _audioSettings.Count == 0) return;

		var output = (int)Math.Round(OutputVolumeSetting?.Value ?? 0);
		var mic = (int)Math.Round(MicrophoneVolumeSetting?.Value ?? 0);

		// Persisted regardless of target, so the value survives a restart and joins preset snapshots
		// through device_settings exactly like debounce and lift-off do.
		if (_settings is not null)
		{
			await _settings.SaveAudioSettingsAsync(
				device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey,
				new AudioSettingsPayload(output, mic));

			// Strictly AFTER the save: the dirty comparison reads the persisted device_settings
			// rows, so staging first would compare against the previous values and miss the change.
			//
			// Dirty-only, not StageChangeAndApply: these levels go to the OS endpoints, not into the
			// device's configuration frame, so there is nothing to write to the device.
			StagePresetDirtyOnly();
		}

		if (device.AudioVolumeTarget != AudioVolumeTarget.OperatingSystem || _audioEndpoints is null)
			return;

		// A disconnected headset resolves no endpoint and the service does nothing — importantly it
		// never falls back to the default endpoint, which would move the user's speakers instead.
		if (HasOutputVolume)
			await _audioEndpoints.SetOutputVolumeAsync(device.AudioContainerId, output);
		if (HasMicrophoneVolume)
			await _audioEndpoints.SetMicrophoneVolumeAsync(device.AudioContainerId, mic);

		// Update the indicators straight away rather than waiting up to 2 s for AudioMuteMonitor's
		// next poll: dragging a slider to 0 should flip the icon as the thumb lands, not later.
		//
		// The mute flag is re-read rather than assumed. Deriving silence from the level alone would
		// CLEAR a genuine mute the moment the user nudged the volume above 0, because muted-at-40 %
		// and unmuted-at-40 % are indistinguishable from the level. The monitor stays authoritative
		// and corrects this on its next tick either way.
		if (HasOutputVolume)
		{
			var muted = await _audioEndpoints.GetOutputMutedAsync(device.AudioContainerId);
			device.IsOutputSilent = AudioMuteMonitor.IsSilent(muted, output);
		}

		if (HasMicrophoneVolume)
		{
			var micMuted = await _audioEndpoints.GetMicrophoneMutedAsync(device.AudioContainerId);
			device.IsMicrophoneSilent = AudioMuteMonitor.IsSilent(micMuted, mic);
		}
	}
}
