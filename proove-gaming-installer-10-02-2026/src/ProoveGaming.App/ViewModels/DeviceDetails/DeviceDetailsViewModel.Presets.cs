using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// Character cap for the preset name field (design spec: counter shown as "N/max").
	// Description is retained on the record (Edit preserves it, Duplicate copies it) but
	// is not user-editable in the current dialog.
	public const int NewPresetNameMaxLength = 20;

	// One row per saved preset on the current device (Presets section list).
	public ObservableCollection<PresetViewModel> Presets { get; } = [];

	// Currently active preset — surfaced through the top-panel "Current preset" dropdown.
	// Maintained in lock-step with the row's IsSelected flag so the row list and the
	// dropdown reflect the same selection.
	private PresetViewModel? _selectedPreset;
	public PresetViewModel? SelectedPreset
	{
		get => _selectedPreset;
		private set
		{
			if (SetProperty(ref _selectedPreset, value))
			{
				// A fresh selection is never dirty: either we just loaded the preset's
				// snapshot into the live device state (SelectPreset) or the caller is
				// seeding an initial pick (LoadPresetsForDevice).
				SettlePresetClean();
				SavePresetCommand.NotifyCanExecuteChanged();
			}
		}
	}

	// True while the user has made changes to RGB / button bindings that haven't been
	// captured into the SelectedPreset's snapshot yet. Drives the SAVE PRESET button's
	// enabled state — nothing to save means the button is disabled.
	private bool _hasUnsavedPresetChanges;
	public bool HasUnsavedPresetChanges
	{
		get => _hasUnsavedPresetChanges;
		private set
		{
			if (SetProperty(ref _hasUnsavedPresetChanges, value))
				SavePresetCommand.NotifyCanExecuteChanged();
		}
	}

	// Marks the preset dirty, synchronously, with no device write. Two guards suppress it during
	// load-like sequences:
	//   - _suppressAutoApply — set inside LoadRgbStateForCurrentDevice's try/finally to
	//     stop mid-load property setters from cascading into device writes.
	//   - _suppressPresetDirty — wraps ApplyPresetToDevice (and other preset-apply /
	//     device-load sequences) because the section reloads inside them can reach
	//     SaveRgbStateForCurrentDevice → StageChangeAndApply AFTER _suppressAutoApply has
	//     been cleared. Without this second flag, applying a preset would immediately
	//     re-dirty it AND fire a redundant device write mid-load.
	// Kept as its own method (rather than folded into StageChangeAndApply) because it is also
	// used directly wherever tests need to simulate "dirty" without a Device set and without
	// scheduling the async apply below.
	internal void NotifyPresetChanged()
	{
		if (_suppressAutoApply) return;
		if (_suppressPresetDirty) return;
		if (SelectedPreset is null) return;
		HasUnsavedPresetChanges = true;
	}

	// Called by the RGB / Buttons / Settings / KeyAssignments partials whenever the user mutates
	// staged device state (the value has already been written to its own live table by the
	// caller, synchronously — which is what lets the dirty check below run right here).
	//
	// The SAVE PRESET button's state is decided IMMEDIATELY, comparing live state against the
	// selected preset's saved snapshot, so it flips the instant the user changes a value — in
	// both directions, including back to "disabled" when an edit is reverted to what the preset
	// already holds. It deliberately does not wait on the device write scheduled below: that is
	// debounced and can take real time on the hardware, and gating the button on it made the UI
	// visibly lag the user's own click.
	// `aspects` says which parts of the configuration this edit actually touched. It defaults to
	// everything, so a caller that does not care keeps the whole-state push it always had; the RGB
	// section opts into Led-only.
	//
	// Why it matters: on BLAZE every block of a configuration write is a FLASH commit. Sending the
	// whole state on a colour change re-committed the key matrix and DPI table each time, and a user
	// clicking "Random lighting" nine times in half a minute ended up with left/right click dead
	// (2026-08-17 host log; docs/mouse/blaze/key-matrix.md). EasenMouseService now also drops blocks
	// whose content has not changed, so this is the second of two guards — but not sending what the
	// user did not touch is the honest fix, and it keeps the payload small on every transport.
	internal void StageChangeAndApply(DeviceApplyAspects aspects = DeviceApplyAspects.All)
	{
		if (!StagePresetDirtyOnly()) return;

		// Accumulated, not replaced: the debounce below coalesces edits from DIFFERENT sections, and
		// an RGB tweak arriving after a button remap must not narrow that pending apply to the LED.
		_pendingApplyAspects |= aspects;

		_deviceApplyDebounceCts?.Cancel();
		var cts = new CancellationTokenSource();
		_deviceApplyDebounceCts = cts;
		_ = DebounceApplyAsync(cts.Token);
	}

	// Marks the preset dirty WITHOUT scheduling a device write, and reports whether staging applied.
	//
	// Split out for settings that are not carried in the device's configuration frame — the headset
	// audio levels, which are applied to the operating system's endpoints rather than to the device.
	// Routing those through StageChangeAndApply would send an all-null configuration write at
	// Cosmo's undocumented HID interface every time a volume slider moved: pointless, and precisely
	// the kind of unsolicited write this device's protocol work has been careful to avoid.
	//
	// The dirty state itself still works, because the comparison reads the persisted device_settings
	// rows and the audio levels are stored there alongside debounce and lift-off.
	internal bool StagePresetDirtyOnly()
	{
		if (_suppressAutoApply) return false;
		if (_suppressPresetDirty) return false;
		if (SelectedPreset is null) return false;
		// Read-only transport: no device write, and nothing is marked dirty because nothing was
		// persisted for the comparison to differ on. See IsDeviceConfigurable.
		if (!IsDeviceConfigurable) return false;

		// Settled in two steps. The synchronous compare gives the instant answer for edits
		// whose staging write is itself synchronous (key remaps and other direct row writes) —
		// the button must not lag the click in either direction. Edits whose staging write went
		// through the storage executor are not visible to that read yet, so the authoritative
		// recompute is ALSO enqueued behind them (FIFO) and corrects the flag the moment the
		// queue settles — which is what clears it again when an edit nets out to the snapshot
		// (800 -> 1200 -> 800) via a queued save.
		// Probed BEFORE the sync compare, deliberately: a queued save can land (and decrement the
		// counter) in the gap between the compare reading the pre-write rows and this probe, and
		// probing second would then see neither the pending write nor its effect — the button
		// stays dark for a click that changed the device (Release suite-load flake:
		// PhysicalDpiButtonPress_WhilePresetSelected_MarksPresetDirty). Probe-first closes it:
		// the staging enqueue happens on this same thread before this method runs, so every
		// staged write is either still pending at the probe or already visible to the compare.
		var hadPendingDeviceWrites = Device is { } device &&
			_settings?.HasPendingDeviceWrites(device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey) == true;
		if (!TryRecomputeHasUnsavedPresetChangesNow())
		{
			HasUnsavedPresetChanges = true; // no storage to compare against — assume changed
			return true;
		}
		if (!HasUnsavedPresetChanges && hadPendingDeviceWrites)
		{
			// The sync compare said "clean" while THIS device's staged writes were still queued —
			// it read the pre-write rows. An edit definitely just happened, so light the button
			// now; the queued recompute below settles the truth once those writes land. Scoped to
			// this device's pending saves, not the executor-wide idle probe: the queue is shared
			// app-wide (notifications, session flush, other devices), and unrelated traffic must
			// not light SAVE for an edit that nets out to the snapshot (800 -> 1200 -> 800).
			HasUnsavedPresetChanges = true;
		}
		TryScheduleUnsavedPresetChangesRecompute();

		return true;
	}

	private const int DeviceApplyDebounceMs = 250;
	private CancellationTokenSource? _deviceApplyDebounceCts;

	// What the debounced apply owes the device: the union of every staged edit since the last one
	// fired. Only the debounced path narrows an apply — every other caller (preset switch, Save
	// Preset, connect, Discard) pushes the whole state, which is what those actions mean.
	private DeviceApplyAspects _pendingApplyAspects = DeviceApplyAspects.None;

	// Coalesces a burst of edits — even across different sections — into one device write.
	// Intentionally does NOT touch HasUnsavedPresetChanges: that is settled synchronously in
	// StageChangeAndApply, and an apply never mutates the live tables the comparison reads, so
	// re-deciding it here could only re-assert the same answer later and make the button lag.
	private async Task DebounceApplyAsync(CancellationToken token)
	{
		try { await Task.Delay(DeviceApplyDebounceMs, token); }
		catch (TaskCanceledException) { return; }

		// Claimed here, not after the apply: a cancelled run leaves the pending set alone for the run
		// that superseded it, and this one owns exactly what it is about to send.
		var aspects = _pendingApplyAspects;
		_pendingApplyAspects = DeviceApplyAspects.None;

		// Cancellation is not instantaneous: a run whose delay had already elapsed still claims the
		// pending set, including bits the run that superseded it had just added. The superseding run
		// then arrives holding nothing. Applying that would put a configuration frame carrying no
		// user intent on the wire — the unsolicited write the audio path takes care to avoid.
		if (aspects == DeviceApplyAspects.None) return;

		await ApplyCurrentStateToDeviceAsync(aspects: aspects);
	}

	// Bumped per scheduled recompute; only the newest one may write the flag. UI-thread only.
	private int _dirtyRecomputeGeneration;

	// Explicitly settles the flag to "clean" AND invalidates every recompute scheduled before
	// this decision. A save / reset / discard / selection change makes the live state the
	// snapshot by construction — but recomputes scheduled by the staging that led here were
	// enqueued BEFORE the snapshot write, compare against the old snapshot, and their
	// continuations can land after this assignment and resurrect a stale "dirty" (CI flake:
	// ConfirmResetPreset_RestoresDefaultsAndSavesThemIntoThePreset, run 32644137293). Bumping
	// the generation makes those continuations no-ops. UI-thread only, like the counter.
	private void SettlePresetClean()
	{
		_dirtyRecomputeGeneration++;
		HasUnsavedPresetChanges = false;
	}

	// The synchronous half of the dirty check: one indexed SQLite read on the calling thread
	// (audit-accepted; the button must flip within the click for synchronously-staged edits).
	// May be stale for queue-staged edits — the scheduled recompute below corrects those.
	// False when the scope needed to compare isn't available.
	private bool TryRecomputeHasUnsavedPresetChangesNow()
	{
		if (SelectedPreset is null || Device is null || _localStorage is null) return false;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return false;

		HasUnsavedPresetChanges = !_localStorage.PresetMatchesLiveState(
			SelectedPreset.Id, owner.Value.Kind, owner.Value.Id,
			Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
		return true;
	}

	// The authoritative dirty check: live per-device state vs. the selected preset's own saved
	// snapshot — what makes an edit that nets out to "back where it started" (e.g. DPI
	// 800 -> 1200 -> 800) correctly clear HasUnsavedPresetChanges again. The staging writes are
	// queued on the storage executor now, so the comparison is enqueued BEHIND them (never read
	// synchronously — it would see the pre-write rows) and its continuation resumes on the
	// captured (UI) context, guarded by generation + identity so a newer edit, open or preset
	// switch owns the flag. Returns false when the scope needed to compare isn't available.
	private bool TryScheduleUnsavedPresetChangesRecompute()
	{
		if (SelectedPreset is null || Device is null || _localStorage is null) return false;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return false;

		var localStorage = _localStorage;
		var preset = SelectedPreset;
		var device = Device;
		var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);
		var generation = ++_dirtyRecomputeGeneration;

		_ = RecomputeAsync();
		return true;

		async Task RecomputeAsync()
		{
			var matches = await localStorage.RunOnStorageAsync(() => localStorage.PresetMatchesLiveState(
				preset.Id, ownerKind, ownerId,
				device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey));
			if (generation != _dirtyRecomputeGeneration) return;
			if (!ReferenceEquals(Device, device) || !ReferenceEquals(SelectedPreset, preset)) return;
			if (_suppressPresetDirty) return;
			HasUnsavedPresetChanges = !matches;
		}
	}

	// Runs the authoritative dirty check once a device open settles (SetDeviceAsync's follow-up
	// chain), with the SQLite read off the calling thread. The SelectedPreset setter clears the
	// flag on open, which MASKED divergence left over from an earlier session (e.g. an RGB edit
	// never saved into the preset): Save Preset sat disabled until the first edit's recompute
	// suddenly revealed the old truth — reported as "assign a key + Undo enables Save Preset with
	// no actual changes" (PR #100 QA). Recomputing here makes the button truthful from the start,
	// so an assign + undo cycle lands back exactly where it began.
	internal async Task RecomputeUnsavedPresetChangesAsync()
	{
		var device = Device;
		var preset = SelectedPreset;
		if (device is null || preset is null || _localStorage is null) return;
		// Read-only transport: same policy as StagePresetDirtyOnly — nothing can be saved or
		// applied here, and the live rows belong to another transport's session, so divergence
		// against them must not raise the flag.
		if (!IsDeviceConfigurable) return;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		// Enqueued on the storage executor, not Task.Run: SetDeviceAsync's own chain
		// (BackfillSelectedPresetSettings) has just queued the backfill writes this comparison
		// depends on, and on a re-open of the same device the Ensure* steps in between exit early
		// and give no FIFO barrier. A direct read races those writes for the storage lock and can
		// compare live rows against a snapshot the backfill has not written yet — lighting SAVE
		// PRESET on an untouched page. Queuing puts the read behind them (the
		// TryScheduleUnsavedPresetChangesRecompute pattern) and keeps SQLite off this thread.
		var localStorage = _localStorage;
		var matches = await localStorage.RunOnStorageAsync(() => localStorage.PresetMatchesLiveState(
			preset.Id, owner.Value.Kind, owner.Value.Id,
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey));

		// A newer open or a preset switch owns the flag now.
		if (!ReferenceEquals(Device, device) || !ReferenceEquals(SelectedPreset, preset)) return;
		HasUnsavedPresetChanges = !matches;
	}

	private bool _suppressPresetDirty;

	// Two-way to the top-panel dropdown's Popup.IsOpen. Toggled by the drop caret button
	// and closed on light-dismiss / after selecting a preset.
	private bool _isPresetsDropdownOpen;
	public bool IsPresetsDropdownOpen
	{
		get => _isPresetsDropdownOpen;
		set => SetProperty(ref _isPresetsDropdownOpen, value);
	}

	[RelayCommand]
	private void TogglePresetsDropdown() => IsPresetsDropdownOpen = !IsPresetsDropdownOpen;

	// Opens the "Save Preset Settings" dialog: user picks the target preset (defaulted to
	// the currently active one) and confirms. The actual snapshot happens in
	// ConfirmSavePreset. Disabled when there's nothing meaningful to save.
	[RelayCommand(CanExecute = nameof(CanSavePreset))]
	private void SavePreset()
	{
		if (SelectedPreset is null || Device is null || _localStorage is null) return;
		OpenSavePresetDialog();
	}

	// Seeds the dialog to a known state and shows it. EVERY path that opens the dialog must
	// come through here: the target radio and search query persist on the ViewModel between
	// openings, and since ConfirmSavePreset both saves into AND switches to the target, a
	// stale target from a previously cancelled dialog would make one careless SAVE click
	// write into — and jump to — a preset the user never picked this time.
	private void OpenSavePresetDialog()
	{
		foreach (var p in Presets)
			p.IsSaveTarget = p == SelectedPreset;
		SavePresetTarget = SelectedPreset;
		// Reset the filter so re-opening the dialog doesn't inherit a stale query
		// from the previous session. Setter triggers RefreshFilteredSavePresets.
		SavePresetSearchQuery = string.Empty;
		IsSavePresetOpen = true;
	}

	// ── Save Preset dialog search ─────────────────────────────────────────────
	// The Save-Preset picker has a search TextBox above the row list. The query
	// is a case-insensitive substring match on preset name. FilteredSavePresets is
	// what the dialog's ItemsControl binds to; the top-panel dropdown and the
	// PresetsSectionView still use the unfiltered Presets collection.
	private string _savePresetSearchQuery = string.Empty;
	public string SavePresetSearchQuery
	{
		get => _savePresetSearchQuery;
		set
		{
			if (SetProperty(ref _savePresetSearchQuery, value ?? string.Empty))
				RefreshFilteredSavePresets();
		}
	}

	public ObservableCollection<PresetViewModel> FilteredSavePresets { get; } = [];

	private void RefreshFilteredSavePresets()
	{
		var q = _savePresetSearchQuery.Trim();
		FilteredSavePresets.Clear();
		if (q.Length == 0)
		{
			foreach (var p in Presets)
				FilteredSavePresets.Add(p);
			return;
		}
		foreach (var p in Presets)
			if (p.Name.Contains(q, System.StringComparison.OrdinalIgnoreCase))
				FilteredSavePresets.Add(p);
	}

	// Device.SupportsConfiguration is false when host->device configuration does not reach the
	// hardware over the transport it is currently on — TAIPAN over its 2.4G receiver or Bluetooth,
	// where writes are accepted by the receiver and never applied (docs/mouse/taipan). Saving a
	// preset there would record settings the device never received, so the button stays disabled
	// until the user connects the cable.
	// Unsaved changes are only worth prompting about if the user could actually save them. On a
	// transport that cannot carry configuration (TAIPAN over its 2.4G receiver or Bluetooth) the
	// Save Preset button is disabled, so the "save or discard?" dialog would offer a choice with
	// one dead option — and there is nothing to lose by leaving, since the change never reached the
	// device either. Both prompts gate on this rather than on HasUnsavedPresetChanges alone.
	public bool HasPromptableUnsavedChanges
		=> HasUnsavedPresetChanges && Device?.SupportsConfiguration != false;

	private bool CanSavePreset()
		=> SelectedPreset is not null && Device is not null && _localStorage is not null
			&& Device.SupportsConfiguration && HasUnsavedPresetChanges;

	// Snapshots the current device state into the chosen target preset and closes the
	// dialog. Called from the SAVE button in the "Save Preset Settings" popup. Async command
	// (AsyncRelayCommand blocks re-entry while executing): the snapshot and applied-preset
	// writes run on the storage executor, so the dispatcher thread performs zero storage
	// work here; queue FIFO guarantees the snapshot observes the stage/rate/RGB rows
	// PersistDisplayedDeviceState enqueued just before it.
	[RelayCommand(CanExecute = nameof(CanConfirmSavePreset))]
	private async Task ConfirmSavePreset()
	{
		var target = SavePresetTarget;
		if (target is null || Device is null || _localStorage is null)
		{
			IsSavePresetOpen = false;
			return;
		}
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null)
		{
			IsSavePresetOpen = false;
			return;
		}

		// Captured before the awaits: leaving the page mid-save nulls Device.
		var localStorage = _localStorage;
		var (vid, pid, key) = (Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
		var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);

		PersistDisplayedDeviceState();
		await localStorage.RunOnStorageAsync(() => localStorage.SavePresetSnapshot(
			target.Id, ownerKind, ownerId, vid, pid, key));

		// Cancel any pending debounced apply first — its captured state is already current (staging
		// is synchronous; only the device push is debounced), so this isn't fixing a bug, just
		// removing a redundant duplicate write if one happened to be in flight.
		_deviceApplyDebounceCts?.Cancel();

		// Push the just-saved state to the hardware and record it as the device's applied preset.
		_ = ApplyCurrentStateToDeviceAsync(pushActuation: true);
		await localStorage.RunOnStorageAsync(() => localStorage.SetAppliedPreset(
			ownerKind, ownerId, vid, pid, key, target.Id));

		// The working state is now the target's snapshot — no unsaved changes remain either
		// way. Saving onto a DIFFERENT preset also makes that preset the active one: the
		// snapshot was taken FROM the live state, so a plain selection switch is enough — no
		// LoadPresetSnapshotToDevice / section reload / second device write, unlike
		// SelectAndApplyPresetAsync. Leaving the old preset selected-and-dirty here is what
		// made a cross-preset save look like it never happened.
		if (!ReferenceEquals(target, SelectedPreset))
		{
			foreach (var p in Presets)
				p.IsSelected = p == target;
			SelectedPreset = target; // setter clears HasUnsavedPresetChanges
			// Same courtesy as SelectAndApplyPresetAsync: the user may have reached this
			// dialog from the top-panel dropdown — don't leave it hanging open over the
			// just-switched selection.
			IsPresetsDropdownOpen = false;
		}
		else
		{
			SettlePresetClean();
		}

		IsSavePresetOpen = false;
	}

	// A preset is a COMPLETE unit of device settings, so its snapshot must carry the DPI stage,
	// report rate and RGB state it represents — including when the user never explicitly picked any
	// of them and the UI is simply showing its default. Those tables only get rows when the user
	// actively changes something, so without this the snapshot has no stage/rate/RGB at all, and
	// reverting to the preset later (Discard / preset switch) finds nothing to restore and silently
	// leaves the abandoned edit on the hardware. Called immediately before every SavePresetSnapshot.
	//
	// RGB matters here for a second reason: the live device_rgb rows are now written whenever the
	// App resolves a device's RGB state (PersistRgbStateForCurrentDevice), so a snapshot that
	// skipped RGB would leave preset_rgb empty against a populated device_rgb — and
	// PresetMatchesLiveState compares those two directly, so Save Preset would light up on a page
	// the user never edited.
	private void PersistDisplayedDeviceState()
	{
		if (Device is null || _settings is null) return;

		// RGB comes first, and outside the DPI gate below: keyboards support lighting but not DPI
		// stages, and they are precisely the devices whose device_rgb rows the App populates on its
		// own. Returning early on the capability check would leave their snapshot RGB-less and put
		// Save Preset back on for an untouched page.
		if (IsRgbSupported)
			PersistRgbStateForCurrentDevice();

		// Stage and rate are gated on SupportsDpiStageSwitch — the capability, deliberately not a
		// device-type check. They are the same class of mouse-style tuning: a device that cannot
		// switch DPI stages has no report rate to snapshot either, and the apply path only ever
		// sends either one down its non-keyboard branch. Gating on the capability means a new
		// device type is onboarded by getting that capability right in DeviceCardViewModel, with
		// nothing to update here.
		//
		// Needed because RebuildDpiPresets falls back to DefaultDpiStages for any device that
		// declares none, highlights row 0, and the restoreUnsetToDefaults fallback in
		// ApplyPresetToDeviceCoreAsync then marks that fabricated stage (and a 1000 Hz rate) as
		// "known". Without these checks a keyboard preset would snapshot a DPI stage and report
		// rate the device has no concept of — harmless to the hardware, which is never sent either,
		// but real garbage in the snapshot that PresetMatchesLiveState then compares on.
		if (!Device.SupportsDpiStageSwitch) return;

		// Both writes are additionally gated on the SAME _dpiStageKnown / _reportRateKnown flags
		// the device-write path uses (ApplyCurrentStateToDeviceCoreAsync), and for the same reason:
		// a highlighted row is not a fact about the hardware. RebuildDpiPresets leaves
		// DpiPresets[0].IsSelected = true with _dpiStageKnown = false, and ApplyKnownDeviceState
		// deliberately keeps stage 0 highlighted as a placeholder when the host has not reported
		// one — its comment explicitly says that must never be read as "the device is on stage 0"
		// by a later write. Persisting it here would launder that placeholder into the preset, and
		// from then on it IS treated as known: the next page open reads it back, sets
		// _dpiStageKnown = true, and AssertActiveDpiStageAsync physically writes stage 0 — silently
		// dropping a mouse the user had put on 12000 via the physical button back to 800. When the
		// stage genuinely is not known, storing nothing is the honest answer; restoreUnsetToDefaults
		// already gives such a preset a sane revert target.
		if (_dpiStageKnown && DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex is { } stage)
			_ = _settings.SaveActiveDpiStageAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, stage);

		if (_reportRateKnown && PollingRateOptions.FirstOrDefault(o => o.IsSelected)?.Hz is { } hz)
			_ = _settings.SavePollingRateAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, hz);
	}

	private bool CanConfirmSavePreset() => SavePresetTarget is not null;

	[RelayCommand]
	private void CancelSavePreset() => IsSavePresetOpen = false;

	[RelayCommand]
	private async Task SelectPreset(PresetViewModel? preset)
	{
		if (preset is null) return;

		// Switching to a different preset while the current one has unsaved changes
		// pops the confirmation dialog first. The dialog's Discard command re-invokes
		// SelectPreset on the parked target once the dirty flag is cleared.
		// HasPromptableUnsavedChanges, not HasUnsavedPresetChanges: where saving is impossible the
		// dialog cannot help, so switching just applies the newly picked preset — which is exactly
		// what its Discard branch would have done anyway.
		if (HasPromptableUnsavedChanges && !ReferenceEquals(preset, SelectedPreset))
		{
			OpenUnsavedChangesForSwitch(preset);
			return;
		}

		await SelectAndApplyPresetAsync(preset, resetUnsetActuationToDefaults: false);
	}

	// resetUnsetActuationToDefaults is true only when arriving from the unsaved-changes Discard —
	// see the parameter's remarks on ApplyPresetToDeviceAsync.
	private async Task SelectAndApplyPresetAsync(PresetViewModel preset, bool resetUnsetActuationToDefaults)
	{
		foreach (var existing in Presets)
			existing.IsSelected = false;
		preset.IsSelected = true;
		SelectedPreset = preset;
		IsPresetsDropdownOpen = false;

		await ApplyPresetToDeviceAsync(preset, resetUnsetActuationToDefaults: resetUnsetActuationToDefaults);
	}

	// Copies the preset's snapshot over the (owner, device) rows in device_rgb /
	// device_rgb_effect / button_bindings, drops the in-memory RGB cache so the reload
	// hits the DB, then re-runs the section-load methods and pushes the fresh state to
	// hardware. An empty snapshot (e.g. after Reset) legitimately clears the device rows
	// and the sections fall back to their defaults.
	// restoreUnsetToDefaults: true for every user-driven revert (Discard / preset switch / delete) —
	// the preset's values must fully replace whatever is live, falling back to the defaults it
	// visually represents when its snapshot predates PersistDisplayedDeviceState. False only for
	// EnsureDefaultPresetAppliedAsync's first-connect push, where an unset stage/rate must stay
	// unset rather than resetting a DPI the user set outside the app.
	// resetUnsetActuationToDefaults: true ONLY from the unsaved-changes Discard (both its variants),
	// deliberately narrower than restoreUnsetToDefaults: a preset with no actuation rows normally
	// writes no actuation, so a plain switch to a pre-actuation preset cannot reset a tuned board —
	// but a Discard must not leave the abandoned actuation edit on the board either, so that one
	// path returns the board to the state it reported at page open (see
	// ApplyStoredActuationToDeviceAsync).
	// target: the card the preset was resolved for, set only by a caller that reads its preset from
	// a background step and can therefore be overtaken by a device switch (the pulled-preset
	// materialization). When it no longer matches what is open, the whole apply is dropped rather
	// than landing one device's configuration on another's rows and hardware — the ordinary
	// user-driven callers leave it null and keep targeting whatever is open, as before.
	private async Task ApplyPresetToDeviceAsync(
		PresetViewModel preset, bool restoreUnsetToDefaults = true, bool resetUnsetActuationToDefaults = false,
		DeviceCardViewModel? target = null)
	{
		// Serialises the WHOLE revert, not just its device write. Reverts genuinely can overlap:
		// DiscardUnsavedChanges closes its dialog BEFORE awaiting, so the page stays interactive and
		// a second revert (another preset click, a delete, or the fire-and-forget
		// EnsureDefaultPresetAppliedAsync) can start while this one is running — AsyncRelayCommand
		// only stops a command re-entering ITSELF, and these arrive from different commands and from
		// a non-command path.
		//
		// HARDENING, not a fix for an observed bug: today the reload below is effectively
		// synchronous up to the device write (every SettingsRepository load returns an already
		// completed task), so a second revert cannot actually interleave mid-reload, and correct
		// ordering falls out incidentally from _applyGate. This gate makes that ordering explicit
		// and keeps it correct if any of those loads later becomes genuinely async — at which point
		// an overlap WOULD interleave two reloads and race SetAppliedPreset. Cheap insurance in the
		// area this feature has repeatedly regressed in.
		//
		// Lock order is always this gate then _applyGate (taken inside ApplyCurrentStateToDeviceAsync)
		// and never the reverse, so the two cannot deadlock.
		await _presetApplyGate.WaitAsync();
		try
		{
			await ApplyPresetToDeviceCoreAsync(preset, restoreUnsetToDefaults, resetUnsetActuationToDefaults, target);
		}
		finally
		{
			_presetApplyGate.Release();
		}
	}

	private readonly SemaphoreSlim _presetApplyGate = new(1, 1);

	private async Task ApplyPresetToDeviceCoreAsync(
		PresetViewModel preset, bool restoreUnsetToDefaults, bool resetUnsetActuationToDefaults,
		DeviceCardViewModel? target)
	{
		// Re-checked after the gate wait, not before it — Device can be nulled (page exit) while
		// this call was queued behind another revert.
		if (Device is null || _localStorage is null) return;
		// Same reason the check above is here and not before the wait: the gate is an await, so a
		// device switch can land while this call is queued. A caller that named its target wants
		// the apply dropped in that case, not re-aimed at the new device (see the parameter's
		// remarks on ApplyPresetToDeviceAsync).
		if (target is not null && !ReferenceEquals(Device, target)) return;
		// Applying a preset rewrites the live tables and pushes to the hardware; on a read-only
		// transport it would do neither usefully and would overwrite what the cable last stored.
		if (!IsDeviceConfigurable) return;

		// Captured once. Everything below runs across several awaits, and leaving the details page
		// sets Device to null — re-reading it after any of them crashed the app with a
		// NullReferenceException (reported on TAIPAN over the dongle, at the SetAppliedPreset call
		// at the end). This whole method is about ONE device: the one the preset is being applied
		// to, which is the correct target regardless of what is on screen when it finishes.
		var card = Device;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		// Cancel any pending debounced apply from an abandoned edit BEFORE reloading — otherwise
		// that stale task can fire mid-reload or right after it, re-pushing the abandoned value
		// (e.g. a DPI stage the user picked but never saved) on top of the snapshot we're about to
		// load here, undoing the revert. See StageChangeAndApply.
		_deviceApplyDebounceCts?.Cancel();

		// Suppress the dirty flag while we overwrite the live rows from the snapshot and reload the
		// sections — those loads would otherwise re-mark the just-applied preset as unsaved.
		// Save/restore rather than a blind reset to false: a synchronous suppressor (SetDevice's
		// outgoing-RGB flush) can run on the UI thread inside one of the await gaps below, and
		// clearing the flag on its way out would drop suppression for the rest of this reload.
		var previousSuppressPresetDirty = _suppressPresetDirty;
		_suppressPresetDirty = true;
		try
		{
			// Queued: the multi-table snapshot copy runs off the dispatcher; awaiting the
			// executor task is the rows-in-place barrier the section reloads below depend on.
			var localStorage = _localStorage;
			var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);
			await localStorage.RunOnStorageAsync(() => localStorage.LoadPresetSnapshotToDevice(
				preset.Id, ownerKind, ownerId,
				card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey));

			// Only THIS owner's entry — the key carries the owner, so another account's cached state
			// for the same device is neither removed nor reachable from here.
			_rgbStateByDevice.Remove(RgbCacheKey(owner.Value, card));

			LoadButtonBindings(card);
			LoadRgbStateForCurrentDevice();
			LoadKeyLayoutForDevice();
			// Await the mouse-settings load so the apply below sends THIS preset's debounce/lift,
			// not the previous preset's stale (cached) values.
			await RebuildDeviceSettings();
			// Same reason, for a headset's volumes: forcePersisted so the PRESET's levels win over
			// whatever the endpoint currently reads, and get pushed back to it.
			await RebuildAudioSettingsAsync(forcePersisted: true);
			// Preset switch: the preset's DPI stage / report rate win over the device's live value
			// (unless the user just pressed the physical DPI button — that path is unaffected).
			// assertStageToDevice: false — ApplyCurrentStateToDeviceAsync below immediately writes the
			// full correct state (new DPI table + stage); asserting the stage here first would race
			// that write against the OLD preset's still-cached table. This ALSO updates
			// _dpiStageKnown/DpiPresets/_reportRateKnown/PollingRateOptions for display purposes, but
			// the apply below does not rely on that translation — see dpiRateOverride below.
			await LoadPersistedDeviceStateAsync(card, forcePersisted: true, assertStageToDevice: false);

			// Read this preset's own DPI stage / report rate straight from storage — the same table
			// LoadPresetSnapshotToDevice just reverted above — rather than trusting
			// _dpiStageKnown/DpiPresets.IsSelected/_reportRateKnown/PollingRateOptions.IsSelected.
			// Those VM fields exist to track live device state across a whole session and have
			// repeatedly been the source of revert bugs: an edit the user made but never saved could
			// survive in them across this exact reload and get sent as if it belonged to the preset
			// just loaded. A plain, direct read has no such state to get out of sync.
			(int? Stage, int? Rate)? dpiRateOverride = null;
			if (_settings != null)
			{
				var stage = await _settings.LoadActiveDpiStageAsync(
					card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
				var rate = await _settings.LoadPollingRateAsync(
					card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);

				// A preset saved before PersistDisplayedDeviceState existed can hold no stage/rate at
				// all. Sending null then means "don't touch it" — which on a revert leaves the
				// abandoned edit running on the hardware, the exact symptom this fixes. Such a preset
				// still visually represents the first DPI stage and the default rate, so restore
				// those. Deliberately NOT persisted: writing them into device_settings would make the
				// live rows differ from the (stage/rate-less) snapshot and re-enable Save Preset.
				if (restoreUnsetToDefaults)
				{
					stage ??= ResolveDefaultDpiStageIndex() ?? DpiPresets.FirstOrDefault()?.StageIndex;
					rate ??= PollingRateOptions.FirstOrDefault(o => o.Hz == DefaultReportRateHz)?.Hz;
				}

				dpiRateOverride = (stage, rate);
			}

			// Reflect what we're about to send, so the UI shows the reverted value rather than the
			// abandoned selection LoadPersistedDeviceStateAsync had nothing stored to overwrite.
			if (dpiRateOverride?.Stage is { } revertedStage)
			{
				HighlightActiveDpiStage(revertedStage);
				_dpiStageKnown = true;
			}
			if (dpiRateOverride?.Rate is { } revertedRate)
			{
				HighlightPollingRate(revertedRate);
				_reportRateKnown = true;
			}

			// Push the reloaded (now-active preset) state to the hardware and record it as applied.
			await ApplyCurrentStateToDeviceAsync(dpiRateOverride, pushActuation: true,
				restoreUnsetActuationToDefaults: resetUnsetActuationToDefaults);
			await localStorage.RunOnStorageAsync(() => localStorage.SetAppliedPreset(
				ownerKind, ownerId,
				card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey, preset.Id));
		}
		finally
		{
			_suppressPresetDirty = previousSuppressPresetDirty;
		}
	}

	// Pushes the full current (staged) state to the hardware, device-type-aware. Mice go through the
	// single ApplyDeviceConfiguration action (one coordinated device write); keyboards get RGB +
	// key remaps via their per-aspect paths. Used on Save Preset, preset switch, and connect-default.
	// Serializes every ApplyCurrentStateToDeviceAsync call. Without this, a debounced apply that's
	// already past its Task.Delay and mid-flight (its own gRPC/device write actually in progress)
	// cannot be stopped by cancelling _deviceApplyDebounceCts — that only pre-empts a call that
	// hasn't started yet. Two concurrent device writes (e.g. the abandoned edit's stale apply and
	// Discard's own revert) racing to completion have no guaranteed order, so whichever finishes
	// last silently wins — which is how an abandoned edit can outlive a Discard. Gating every call
	// through one semaphore forces strict ordering: a call issued after another is already running
	// waits for it to finish, then runs with whatever is CURRENT at that point (already reverted,
	// for Discard) — so the most-recently-issued apply is always the one that actually lands.
	private readonly SemaphoreSlim _applyGate = new(1, 1);

	// dpiRateOverride is set ONLY by ApplyPresetToDeviceAsync (preset switch / discard): the active
	// stage and report rate to send, read directly from the just-reverted preset snapshot in
	// storage — bypassing _dpiStageKnown/DpiPresets.IsSelected/_reportRateKnown/
	// PollingRateOptions.IsSelected entirely. Those VM-cached fields track the device's live state
	// across the whole session and are exactly what let an abandoned, never-saved DPI/rate
	// selection survive a reload and get re-sent as if it belonged to the reverted preset. null
	// (the default, used by Save Preset / a plain apply) keeps the existing VM-state-based logic.
	// Re-applies a device's last-applied preset when it connects, for a device that is NOT the one
	// currently open. Sibling of ReapplyPersistedRgbAsync, and bound by the same rule: everything it
	// pushes comes from its `card` argument and storage, never from Device or any UI collection —
	// this ViewModel is a singleton bound to whichever device the user has open, and a stray read
	// here would push one device's configuration onto another. Device is read for exactly one
	// purpose (IsOpenDeviceCard), and only to DECLINE work: the pulled-preset materialization below
	// is the one thing this method writes, and it belongs to the page when the page has the unit.
	//
	// Source is the LIVE per-device tables, not the preset's snapshot. Applying a preset copies its
	// snapshot into those tables, so they already hold the last-applied preset's values — and they
	// are what the details page pushes, so connect-time and page-time applies agree by construction.
	// Reading the snapshot instead would discard a legitimately applied-but-unsaved state.
	//
	// Only CUSTOM presets are re-applied. A device on its auto-created default preset represents "as
	// it came", so re-pushing it on every connect would be a hardware write that changes nothing the
	// user asked for.
	//
	// Returns whether it actually wrote to the device. ReapplyOnConnectAsync uses that to decide
	// whether the lighting still needs a restore of its own: the configuration built here ALREADY
	// carries the stored LED (BuildStoredLedConfig), so when this runs it is the whole restore and a
	// separate RGB push would be a second transaction sending bytes the device just received.
	public async Task<bool> ReapplyLastCustomPresetAsync(DeviceCardViewModel card)
	{
		ArgumentNullException.ThrowIfNull(card);

		if (_usbHidDeviceService is null || _localStorage is null || _settings is null || _ownerProvider is null)
			return false;

		// Transports that cannot carry configuration are skipped before any read or write, so this
		// path cannot touch the device or the database for them (TAIPAN on its 2.4G receiver / BT).
		if (!card.SupportsConfiguration) return false;

		// Keyboards are out of scope: their apply is an RGB push, a key-override push and a macro
		// resync, and the key push needs layout state only the details page builds. Their lighting is
		// still restored on connect by the RGB fallback in ReapplyOnConnectAsync.
		if (card.DeviceTypeLabel?.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true)
			return false;

		// A device with nothing in the configuration frame has nothing to re-apply. Without this the
		// Cosmo headset — which has no RGB, no button remap, no mouse settings and no DPI stages, but
		// CAN now hold a custom preset because its audio levels are preset state — sent an all-null
		// ApplyDeviceConfiguration at its undocumented vendor interface on every power-on. The
		// DeviceHost rejects it (no matching config service), so this is wasted work and a warning per
		// power cycle rather than a device risk, but the correct number of those calls is zero.
		var hasConfigurableState = card.SupportsRgb
			|| card.SupportsButtonRemap
			|| card.SupportedMouseSettings.Count > 0
			|| card.SupportsDpiStageSwitch;
		if (!hasConfigurableState) return false;

		var owner = _ownerProvider.GetCurrent();
		if (owner is null) return false;

		var appliedId = _localStorage.GetAppliedPreset(
			owner.Value.Kind, owner.Value.Id, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
		if (appliedId is not { } presetId) return false;

		var preset = _localStorage
			.GetPresets(owner.Value.Kind, owner.Value.Id, card.SettingsVendorId, card.SettingsProductId)
			.FirstOrDefault(p => p.Id == presetId);
		if (preset is null || preset.IsDefault) return false;

		// The live rows this method reads below are empty for a preset that arrived through the
		// sign-in pull — it was never applied on this machine. The pull leaves a pending-apply mark
		// for the first path holding a unit (docs/plans/add-extended-keys-api-sync), and for a mouse
		// that is here: copy the snapshot into this unit's rows first and the connect push below
		// carries the pulled preset without its page ever being opened. Consumed before the device
		// write, so a failing write cannot re-trigger it on every reconnect.
		//
		// ...unless this unit's details page is the one open. Then SetDeviceAsync's own
		// MaterializePulledPresetAsync owns the mark, and a snapshot copy landing here mid-open
		// diverges the live rows from what the page holds: SetDeviceAsync's awaited
		// PersistRgbStateForCurrentDeviceAsync writes the page's pre-materialization RGB straight
		// back over the rows this would have replaced, the board wears the snapshot colour, and
		// Save Preset lights up on a page the user never touched (review round 1, S2). Skipping is
		// safe by construction: the mark survives for the page to consume. The rest of the method
		// is unaffected — the connect-time push for an open device is the same read-only restore it
		// has always been.
		if (!IsOpenDeviceCard(card))
		{
			var localStorage = _localStorage;
			var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);
			await ConsumePendingApplyMarkAsync(
				localStorage, owner.Value, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey,
				adoptedPresetId => localStorage.LoadPresetSnapshotToDevice(
					adoptedPresetId, ownerKind, ownerId,
					card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey));
		}

		var led = BuildStoredLedConfig(card, owner.Value);
		var bindings = card.SupportsButtonRemap ? BuildStoredBindings(card, owner.Value) : null;

		(byte debounce, byte liftOff)? mouseSettings = null;
		if (card.SupportedMouseSettings.Count > 0 &&
			await _settings.LoadMouseSettingsAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey)
				is { } stored)
		{
			mouseSettings = (stored.ButtonDebounce, stored.LiftOffDistance);
		}

		IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages = null;
		int? activeStage = null;
		int? reportRate = null;
		if (card.SupportsDpiStageSwitch)
		{
			var table = await _settings.LoadDpiPresetsAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
			if (table is { Stages.Count: > 0 })
				dpiStages = table.Stages.Select(x => (x.DpiValue, x.R, x.G, x.B)).ToList();

			// Nothing stored stays null rather than becoming a guess: sending a fabricated stage is
			// how a mouse the user had on 12000 silently drops back to 800. Same rule the on-page
			// apply expresses through _dpiStageKnown / _reportRateKnown.
			activeStage = await _settings.LoadActiveDpiStageAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
			reportRate = await _settings.LoadPollingRateAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
		}

		// Shares the details page's gate so a connect-triggered apply and a page-triggered one cannot
		// interleave two writes at the hardware.
		await _applyGate.WaitAsync();
		try
		{
			// ONE coordinated write — BLAZE bricks its buttons on separate per-aspect writes.
			await _usbHidDeviceService.ApplyDeviceConfigurationAsync(
				card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty,
				led, bindings, mouseSettings, dpiStages, activeStage, reportRate);
		}
		finally
		{
			_applyGate.Release();
		}

		return true;
	}

	// Whether `card` is the unit whose details page is open. Compared on the SETTINGS identity, not
	// by reference: HomeViewModel hands the connect path the roster's card, which for a reconnect
	// need not be the same instance the page was opened with, and it is the settings identity that
	// decides which rows the two would be writing.
	private bool IsOpenDeviceCard(DeviceCardViewModel card) =>
		Device is { } open
		&& open.SettingsVendorId == card.SettingsVendorId
		&& open.SettingsProductId == card.SettingsProductId
		&& string.Equals(open.SettingsDeviceKey, card.SettingsDeviceKey, StringComparison.Ordinal);

	// The whole connect-time restore, as ONE hardware transaction.
	//
	// Until 2026-08-17 this was two calls fired back to back from HomeViewModel — a persisted-RGB
	// push and then the preset apply — even though the preset apply's configuration already carries
	// the same stored LED. On BLAZE that second transaction is not free: the firmware tolerates a
	// paced sequence and not a burst, and the mouse intermittently came up on a cold start with
	// left/right click dead (docs/mouse/blaze/key-matrix.md). Spacing the two apart was the first
	// mitigation; sending one is the fix.
	//
	// The RGB push is kept only as the FALLBACK, for the devices the preset path deliberately does
	// not serve: keyboards, transports that cannot carry configuration, and any device sitting on its
	// default preset. Those still need their lighting back, and for them the RGB push is itself a
	// single transaction.
	//
	// resetWhenUnset is passed straight through to the lighting fallback and is set only by the
	// owner-change sweep — see ReapplyPersistedRgbAsync.
	public async Task ReapplyOnConnectAsync(DeviceCardViewModel card, bool resetWhenUnset = false)
	{
		ArgumentNullException.ThrowIfNull(card);

		if (await ReapplyLastCustomPresetAsync(card))
			return;

		if (card.SupportsRgb)
			await ReapplyPersistedRgbAsync(card, resetWhenUnset);
	}

	// The stored lighting as wire values, or null when the device has none or has it switched off —
	// mirrors ReapplyPersistedRgbAsync's rules so both connect paths agree.
	private (byte r, byte g, byte b, byte effect, byte brightness, byte speed)? BuildStoredLedConfig(
		DeviceCardViewModel card, SettingsOwner owner)
	{
		if (!card.SupportsRgb || _localStorage is null) return null;

		var record = _localStorage.GetRgbState(
			owner.Kind, owner.Id, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
		if (record is null || !record.IsEnabled) return null;

		var effect = record.Effects.FirstOrDefault(e => e.EffectKey == record.SelectedEffect)
					 ?? record.Effects.FirstOrDefault();
		if (effect is null) return null;

		Color.TryParse(effect.Hex, out var color);
		var isRainbow = effect.IsRainbow && record.SelectedEffect == "Breathing";
		return (color.R, color.G, color.B,
			isRainbow ? MapEffectByte("RainbowBreathing") : MapEffectByte(record.SelectedEffect),
			ToProtocolBrightness(effect.Brightness),
			ToProtocolSpeed(effect.Speed));
	}

	// The stored button map, with each Macro binding's body attached from the unit's macro library.
	// A Macro binding whose macro is missing is dropped rather than sent bodiless — the device
	// composer has no Macro case and would silently leave that button on its factory function.
	private IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? BuildStoredBindings(
		DeviceCardViewModel card, SettingsOwner owner)
	{
		if (_localStorage is null) return null;

		var stored = _localStorage.GetButtonBindings(
			owner.Kind, owner.Id, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
		if (stored.Count == 0) return null;

		var macros = _localStorage.GetMacros(
			owner.Kind, owner.Id, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);

		var result = new List<(MouseButton, MouseButtonAction, MacroDefinition?)>(stored.Count);
		foreach (var (buttonInt, actionInt, macroId) in stored)
		{
			var button = (MouseButton)buttonInt;
			var action = (MouseButtonAction)actionInt;
			if (action == MouseButtonAction.Unspecified) continue;

			MacroDefinition? macro = null;
			if (action == MouseButtonAction.Macro)
			{
				var record = macroId.HasValue ? macros.FirstOrDefault(m => m.Id == macroId.Value) : null;
				if (record is null) continue;
				macro = ToProtoMacro(record);
			}

			result.Add((button, action, macro));
		}

		return result.Count > 0 ? result : null;
	}

	/// <param name="pushActuation">
	/// True only on the preset paths (switch, Save Preset, new preset, reset). Defaults to FALSE
	/// because this method is also the target of the staged-edit debounce, and an RGB or key-remap
	/// edit must not drag an actuation write along with it — on T800 that write is a flash commit
	/// with a 300 ms settle, which is exactly what the actuation queue's own coalescing exists to
	/// avoid. A new caller that wants the push has to ask for it.
	/// </param>
	/// <param name="restoreUnsetActuationToDefaults">
	/// True only from the unsaved-changes Discard (see ApplyPresetToDeviceAsync's
	/// resetUnsetActuationToDefaults): a preset with no actuation rows then returns the board to
	/// the state it reported at page open instead of leaving the abandoned actuation edit in
	/// place. A plain preset switch keeps the no-opinion rule, so old presets cannot reset a
	/// tuned board.
	/// </param>
	private async Task ApplyCurrentStateToDeviceAsync(
		(int? Stage, int? Rate)? dpiRateOverride = null, bool pushActuation = false,
		bool restoreUnsetActuationToDefaults = false,
		DeviceApplyAspects aspects = DeviceApplyAspects.All)
	{
		await _applyGate.WaitAsync();
		try
		{
			await ApplyCurrentStateToDeviceCoreAsync(dpiRateOverride, pushActuation, restoreUnsetActuationToDefaults, aspects);
		}
		finally
		{
			_applyGate.Release();
		}
	}

	// No default for `aspects`: its one caller passes it explicitly, and a default here would let a
	// future call site silently fall back to sending everything.
	private async Task ApplyCurrentStateToDeviceCoreAsync(
		(int? Stage, int? Rate)? dpiRateOverride, bool pushActuation, bool restoreUnsetActuationToDefaults,
		DeviceApplyAspects aspects)
	{
		if (Device is null || _usbHidDeviceService is null) return;
		if (!IsDeviceConfigurable) return;

		if (IsKeyboardDevice)
		{
			if (IsRgbSupported)
				await PushRgbToDeviceAsync();
			await ApplyKeyOverridesToDeviceAsync();

			// Macro bindings live in key_macro_bindings, per device rather than per preset, so the
			// key push above cannot carry them. This is where they reach the keyboard — on the
			// debounced auto-apply after an edit as well as on Save Preset / switch / connect.
			//
			// Strictly AFTER the key push: that push writes the factory keycode into any slot the
			// device reports as overridden but the active preset does not stage, which is exactly
			// what a macro-bound slot looks like to it. Without this ordering, a push would leave
			// the keyboard holding a plain keycode where the App still shows a macro.
			// Self-gating on macro support and idempotent, so it is safe to call unconditionally.
			await ResyncKeyMacroBindingsAsync();

			// Actuation on the magnetic boards (T800 / Cyrex / Aress) — preset paths only, see the
			// pushActuation parameter. Self-gating beyond that: it writes nothing for a board
			// without actuation, and normally nothing for a preset that holds no actuation rows,
			// which is every preset saved before actuation was preset-scoped — so a preset switch
			// cannot reset a board the user has tuned. The one exception is the unsaved-changes
			// Discard (see restoreUnsetActuationToDefaults), which returns the board to the state
			// it reported at page open.
			if (pushActuation)
			{
				await ApplyStoredActuationToDeviceAsync(restoreUnsetActuationToDefaults);

				// Extended keys (Cyrex / Aress), strictly AFTER the key and macro pushes, which skip
				// the slots the live rows claim. When the write removed records, their slots went back
				// to the factory triplets, so the two pushes run once more to land any remap or macro
				// the incoming preset stages on them — the skip set is the new set by then.
				if (await ApplyStoredExtendedKeysToDeviceAsync())
				{
					await ApplyKeyOverridesToDeviceAsync();
					await ResyncKeyMacroBindingsAsync();
				}
			}
		}
		else
		{
			// An aspect the caller did not stage is sent as null — the configuration frame's existing
			// contract for "leave this alone" — so the device never re-commits a block the user did
			// not touch. Every non-debounced caller passes All and is unaffected.
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led =
				IsRgbSupported && aspects.HasFlag(DeviceApplyAspects.Led) ? BuildLedConfig() : null;
			var bindings = IsButtonRemapSupported && aspects.HasFlag(DeviceApplyAspects.Buttons)
				? CurrentBindingSet()
				: null;

			(byte debounce, byte liftOff)? mouseSettings = null;
			if (aspects.HasFlag(DeviceApplyAspects.Settings) && (HasButtonDebounce || HasLiftOffDistance))
			{
				var debounce = (byte)Math.Clamp(Math.Round(ButtonDenounceSetting?.Value ?? 8), 0, 255);
				var liftOff = (byte)Math.Clamp(Math.Round(LiftOffSetting?.Value ?? 2), 2, 3);
				mouseSettings = (debounce, liftOff);
			}

			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages = null;
			int? activeStage = null;
			if (aspects.HasFlag(DeviceApplyAspects.DpiStages) && Device.SupportsDpiStageSwitch && DpiPresets.Count > 0)
			{
				dpiStages = DpiPresets.Select(p => ((int)p.DpiValue, p.R, p.G, p.B)).ToList();
				if (dpiRateOverride is { } ov)
				{
					activeStage = ov.Stage;
				}
				// Only send an active stage when one is actually known (persisted, a confirmed host
				// push, or the user's own click) — DpiPresets[0].IsSelected is the UI's structural
				// default, not a fact about the device, and writing stage 0 from that default would
				// silently reset the user's real DPI on a first-connect default-preset apply.
				else if (_dpiStageKnown)
				{
					activeStage = DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex;
				}

				// Never address a slot the table being written does not have. The stage can come from
				// a preset snapshot (dpiRateOverride) that was saved against a LONGER ladder than the
				// one now in hand, and the firmwares index this table positionally — a stage past its
				// end selects an entry written as disabled. Clamping here is the last barrier before
				// the wire, independent of whichever UI path resolved the stage.
				if (activeStage is { } requested && requested >= dpiStages.Count)
					activeStage = dpiStages.Count - 1;
			}

			// Only send a report rate when one is actually known (persisted, a confirmed host push,
			// or the user's own click) — same rule as activeStage above. PollingRateOptions is a
			// fixed list reused across devices, so an unguarded read here would leak whichever
			// option was last highlighted (even the constructor's 1000 Hz default) onto a device
			// the user never touched. Gated by the aspect too, so "anything not flagged is sent as
			// null" holds for every field rather than three out of four.
			int? reportRate = !aspects.HasFlag(DeviceApplyAspects.ReportRate)
				? null
				: dpiRateOverride is { } rov
					? rov.Rate
					: (_reportRateKnown ? PollingRateOptions.FirstOrDefault(o => o.IsSelected)?.Hz : null);

			// Captured BEFORE the await. Leaving the details page sets Device to null, and this
			// write can take real time on the hardware (seconds on TAIPAN over its 2.4G receiver),
			// so reading Device again in the continuation crashed with a NullReferenceException
			// when the user navigated back mid-apply. The caches belong to the device the write was
			// actually for, so the captured card is the correct value regardless of what is on
			// screen by the time it completes.
			var applied = Device;

			await _usbHidDeviceService.ApplyDeviceConfigurationAsync(
				applied.VendorId, applied.ProductId, applied.DeviceKey,
				led, bindings, mouseSettings, dpiStages, activeStage, reportRate);

			// Keep the asserted-stage / asserted-rate caches aligned with what was just written, so
			// re-opening this device's page afterward doesn't see a stale value and fire a redundant
			// assert write, and so the DeviceHost's own confirmation push for this write doesn't get
			// mistaken for a fresh physical change (see OnDpiStageChanged / OnReportRateChanged).
			if (activeStage is { } appliedStage)
				NoteAssertedDpiStage(applied, appliedStage);
			if (reportRate is { } appliedRate)
				NoteAssertedReportRate(applied, appliedRate);
		}
	}

	// Header label the section shows on the top line, e.g. "Total: 4". Localizable via the
	// Presets_Header_TotalFormat resource. Recomputed on every Presets collection change,
	// which is wired the first time LoadPresetsForDevice runs so the hook survives ViewModel
	// reuse across device swaps.
	public string PresetsCountLabel =>
		string.Format(LocalizationManager.GetString("Presets_Header_TotalFormat"), Presets.Count);

	private bool _presetsCountHooked;

	// True between the open prologue's Presets.Clear() and the loader's populate. The
	// empty-state hint gates on it: the async open otherwise flashed "No presets yet" for the
	// load's duration on every device open (PR #100 QA) — a wrong claim, not an empty list.
	private bool _isPresetsLoading;
	internal bool IsPresetsLoading
	{
		get => _isPresetsLoading;
		set
		{
			if (SetProperty(ref _isPresetsLoading, value))
				OnPropertyChanged(nameof(ShowNoPresetsHint));
		}
	}

	// The empty-state hint's visibility: genuinely no presets, and not merely mid-load.
	public bool ShowNoPresetsHint => Presets.Count == 0 && !_isPresetsLoading;

	// ── New Preset dialog state ───────────────────────────────────────────────
	private bool _isNewPresetOpen;
	public bool IsNewPresetOpen
	{
		get => _isNewPresetOpen;
		set => SetProperty(ref _isNewPresetOpen, value);
	}

	// ── Save Preset dialog state ─────────────────────────────────────────────
	// The "Save Preset Settings" popup lets the user pick which preset to save the
	// current device state into. Defaulted to SelectedPreset on open.
	private bool _isSavePresetOpen;
	public bool IsSavePresetOpen
	{
		get => _isSavePresetOpen;
		set => SetProperty(ref _isSavePresetOpen, value);
	}

	private PresetViewModel? _savePresetTarget;
	public PresetViewModel? SavePresetTarget
	{
		get => _savePresetTarget;
		set
		{
			if (SetProperty(ref _savePresetTarget, value))
				ConfirmSavePresetCommand.NotifyCanExecuteChanged();
		}
	}

	// Called by the radio-button rows inside the Save Preset dialog. A row click sets
	// the target so Confirm knows where to write the snapshot.
	[RelayCommand]
	private void SelectSavePresetTarget(PresetViewModel? preset)
	{
		if (preset is null) return;
		foreach (var p in Presets)
			p.IsSaveTarget = p == preset;
		SavePresetTarget = preset;
	}

	// ── Reset Preset confirmation dialog state ────────────────────────────────
	// Separate dialog surface (620×220 chamfered modal) reused for the row-menu Reset
	// action. Holds the target row so Confirm knows which preset to restore.
	private bool _isResetPresetOpen;
	public bool IsResetPresetOpen
	{
		get => _isResetPresetOpen;
		set => SetProperty(ref _isResetPresetOpen, value);
	}

	private PresetViewModel? _pendingResetPreset;

	// ── Delete Preset confirmation dialog state ───────────────────────────────
	// Deleting a preset destroys its saved settings with no undo, so it gets the same confirmation
	// surface the Reset action uses rather than acting straight off the row menu.
	private bool _isDeletePresetOpen;
	public bool IsDeletePresetOpen
	{
		get => _isDeletePresetOpen;
		set => SetProperty(ref _isDeletePresetOpen, value);
	}

	private PresetViewModel? _pendingDeletePreset;

	// The dialog is shared by three flows. Mode drives the localized title / subtitle /
	// action-button label and the branch taken in ConfirmNewPreset:
	//   Create    → append a new row from the current dialog values.
	//   Edit      → update the row identified by _editingPresetId.
	//   Duplicate → append a new row (name pre-filled as "Copy of …" of the source).
	private enum PresetDialogMode { Create, Edit, Duplicate }
	private PresetDialogMode _presetDialogMode = PresetDialogMode.Create;
	private int _editingPresetId;

	// Which preset Duplicate was invoked on — the source of the copy on Confirm. Set by
	// DuplicatePreset, cleared by SetPresetDialogMode whenever the mode isn't Duplicate (mirrors
	// _editingPresetId). NOT the same as _editingPresetId: Duplicate inserts a new row, it never
	// updates the source.
	private int _duplicatingSourcePresetId;

	public bool IsEditingPreset => _presetDialogMode == PresetDialogMode.Edit;
	public bool IsDuplicatingPreset => _presetDialogMode == PresetDialogMode.Duplicate;

	public string PresetDialogTitle => LocalizationManager.GetString(_presetDialogMode switch
	{
		PresetDialogMode.Edit => "EditPreset_Title",
		PresetDialogMode.Duplicate => "DuplicatePreset_Title",
		_ => "NewPreset_Title",
	});
	public string PresetDialogSubtitle => LocalizationManager.GetString(_presetDialogMode switch
	{
		PresetDialogMode.Edit => "EditPreset_Subtitle",
		PresetDialogMode.Duplicate => "DuplicatePreset_Subtitle",
		_ => "NewPreset_Subtitle",
	});
	public string PresetDialogActionLabel => LocalizationManager.GetString(_presetDialogMode switch
	{
		PresetDialogMode.Edit => "EditPreset_Save",
		PresetDialogMode.Duplicate => "DuplicatePreset_Confirm",
		_ => "NewPreset_Confirm",
	});

	private void SetPresetDialogMode(PresetDialogMode mode, int editingPresetId = 0)
	{
		_presetDialogMode = mode;
		_editingPresetId = mode == PresetDialogMode.Edit ? editingPresetId : 0;
		if (mode != PresetDialogMode.Duplicate)
			_duplicatingSourcePresetId = 0;
		OnPropertyChanged(nameof(IsEditingPreset));
		OnPropertyChanged(nameof(IsDuplicatingPreset));
		OnPropertyChanged(nameof(PresetDialogTitle));
		OnPropertyChanged(nameof(PresetDialogSubtitle));
		OnPropertyChanged(nameof(PresetDialogActionLabel));
	}

	private string _newPresetName = string.Empty;
	public string NewPresetName
	{
		get => _newPresetName;
		set
		{
			var trimmed = value ?? string.Empty;
			if (trimmed.Length > NewPresetNameMaxLength)
				trimmed = trimmed[..NewPresetNameMaxLength];
			if (SetProperty(ref _newPresetName, trimmed))
			{
				OnPropertyChanged(nameof(NewPresetNameCounter));
				ConfirmNewPresetCommand.NotifyCanExecuteChanged();
			}
		}
	}

	private bool _newPresetSetAsDefault;
	public bool NewPresetSetAsDefault
	{
		get => _newPresetSetAsDefault;
		set => SetProperty(ref _newPresetSetAsDefault, value);
	}

	// True while the dialog is in Edit mode targeting the currently-default preset.
	// In that state the "Set as default" toggle is locked ON — un-ticking the current
	// default is a no-op in ApplyEditToExistingPreset (see "at least one default"
	// invariant), so the UI reflects that by disabling the control instead of silently
	// ignoring the click.
	private bool _isSetAsDefaultLocked;
	public bool IsSetAsDefaultLocked
	{
		get => _isSetAsDefaultLocked;
		private set => SetProperty(ref _isSetAsDefaultLocked, value);
	}

	// Description is not user-editable; the field is carried through the dialog for
	// Edit/Duplicate so we can preserve the source row's value on Confirm.
	private string _newPresetDescription = string.Empty;

	// "N/max" string the counter label binds to. Kept pre-formatted so the XAML
	// stays free of value converters.
	public string NewPresetNameCounter => $"{_newPresetName.Length}/{NewPresetNameMaxLength}";

	// ── Commands ──────────────────────────────────────────────────────────────
	[RelayCommand]
	private void OpenNewPreset()
	{
		SetPresetDialogMode(PresetDialogMode.Create);
		NewPresetName = string.Empty;
		_newPresetDescription = string.Empty;
		NewPresetSetAsDefault = false;
		IsSetAsDefaultLocked = false;
		IsNewPresetOpen = true;
	}

	[RelayCommand]
	private void CancelNewPreset()
	{
		IsNewPresetOpen = false;
		SetPresetDialogMode(PresetDialogMode.Create);
	}

	[RelayCommand(CanExecute = nameof(CanConfirmNewPreset))]
	private async Task ConfirmNewPreset()
	{
		var name = _newPresetName.Trim();
		if (string.IsNullOrEmpty(name) || Device is null)
		{
			IsNewPresetOpen = false;
			SetPresetDialogMode(PresetDialogMode.Create);
			return;
		}

		if (_localStorage is not null)
		{
			var owner = _ownerProvider?.GetCurrent();
			if (owner is not null)
			{
				if (IsEditingPreset)
					ApplyEditToExistingPreset(name);
				else if (IsDuplicatingPreset)
					AppendDuplicatePreset(owner.Value.Kind, owner.Value.Id, name, _duplicatingSourcePresetId);
				else
					await AppendNewPresetAsync(owner.Value.Kind, owner.Value.Id, name);
			}
		}

		IsNewPresetOpen = false;
		SetPresetDialogMode(PresetDialogMode.Create);
	}

	// Persists the edited fields and reflects them on the existing PresetViewModel row so
	// the list updates in place without a reload.
	private void ApplyEditToExistingPreset(string name)
	{
		var target = Presets.FirstOrDefault(p => p.Id == _editingPresetId);
		if (target is null || _localStorage is null || Device is null) return;

		target.Name = name;
		target.Description = _newPresetDescription ?? string.Empty;

		_localStorage.UpdatePreset(target.Id, target.Name, target.Description);

		if (NewPresetSetAsDefault && !target.IsDefault)
		{
			// Promote in storage first so the DB invariant (exactly one default per
			// owner/device) is enforced, then mirror in memory.
			_localStorage.PromotePresetToDefault(target.Id);
			foreach (var existing in Presets)
				existing.IsDefault = false;
			target.IsDefault = true;
		}
		// Note: unchecking "Set as default" on the current default is a no-op — the
		// default preset can only change by promoting another row, not by demoting
		// itself. This preserves the "at least one default" guarantee for the device.
	}

	// Inserts a fresh preset row (create-new mode). Extracted from ConfirmNewPreset so the
	// two branches don't tangle. Also snapshots the current live device state into the new
	// row so switching away and back doesn't reload an empty snapshot and wipe device
	// state — a fresh preset carries "the settings you were on when you created it".
	private async Task AppendNewPresetAsync(string ownerKind, string ownerId, string name)
	{
		if (_localStorage is null || Device is null) return;

		var record = new PresetRecord
		{
			Name = name,
			Description = _newPresetDescription ?? string.Empty,
			IsDefault = NewPresetSetAsDefault,
		};
		// Captured before the awaits: leaving the page mid-create nulls Device (same
		// pattern as ConfirmSavePreset).
		var localStorage = _localStorage;
		var (vid, pid, key) = (Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
		await localStorage.RunOnStorageAsync(() => localStorage.InsertPreset(
			ownerKind, ownerId, vid, pid, record));

		// Capture whatever the user currently has on the device into the new preset's
		// snapshot tables. Without this, ApplyPresetToDevice on a later re-select of
		// this row would DELETE-then-INSERT-nothing and clear device_* state. The snapshot
		// is queued BEHIND the stage/rate/RGB writes PersistDisplayedDeviceState enqueues,
		// so it observes them (review round 1, B1).
		PersistDisplayedDeviceState();
		await localStorage.RunOnStorageAsync(() => localStorage.SavePresetSnapshot(
			record.Id, ownerKind, ownerId, vid, pid, key));

		// If the new row is set as default, demote every sibling in storage as well
		// so the "exactly one default per (owner, device)" invariant survives reloads.
		if (NewPresetSetAsDefault)
			await localStorage.RunOnStorageAsync(() => localStorage.PromotePresetToDefault(record.Id));

		var vm = new PresetViewModel
		{
			Id = record.Id,
			Name = record.Name,
			Description = record.Description,
			IsDefault = NewPresetSetAsDefault,
		};
		foreach (var existing in Presets)
			existing.IsSelected = false;
		vm.IsSelected = true;

		if (NewPresetSetAsDefault)
			foreach (var existing in Presets)
				existing.IsDefault = false;

		Presets.Add(vm);
		SelectedPreset = vm;

		// Cancel any pending debounced apply — same reasoning as ApplyPresetToDeviceAsync/
		// ConfirmSavePreset: harmless here since the snapshot above already captured current live
		// state, but keeps every "authoritative apply" entry point consistent.
		_deviceApplyDebounceCts?.Cancel();

		// Push the just-captured state to the hardware immediately, same as Save Preset / preset
		// switch. Without this, any staged-but-unsaved edits baked into the snapshot above sit in
		// SQLite only — the device keeps showing whatever was last actually written, and since the
		// new preset isn't dirty (SelectedPreset's setter clears it), there is no Save button left to
		// push them with until the user edits something else.
		await ApplyCurrentStateToDeviceAsync(pushActuation: true);
		await localStorage.RunOnStorageAsync(() => localStorage.SetAppliedPreset(
			ownerKind, ownerId, vid, pid, key, record.Id));
	}

	// Duplicates a preset by copying its OWN saved snapshot (CopyPresetSnapshot) into a new row —
	// never the live rows, which reflect whichever preset is actually ACTIVE right now and could be
	// a completely different one than the row the user clicked "Duplicate" on. The new row is
	// inserted unselected and nothing is pushed to hardware: a duplicate isn't the active preset, so
	// there's nothing to apply yet. It becomes active later like any other preset, via SelectPreset.
	private void AppendDuplicatePreset(string ownerKind, string ownerId, string name, int sourcePresetId)
	{
		if (_localStorage is null || Device is null) return;

		var record = new PresetRecord
		{
			Name = name,
			Description = _newPresetDescription ?? string.Empty,
			IsDefault = NewPresetSetAsDefault,
		};
		_localStorage.InsertPreset(
			ownerKind, ownerId,
			Device.SettingsVendorId, Device.SettingsProductId,
			record);

		_localStorage.CopyPresetSnapshot(sourcePresetId, record.Id);

		if (NewPresetSetAsDefault)
			_localStorage.PromotePresetToDefault(record.Id);

		var vm = new PresetViewModel
		{
			Id = record.Id,
			Name = record.Name,
			Description = record.Description,
			IsDefault = NewPresetSetAsDefault,
		};

		if (NewPresetSetAsDefault)
			foreach (var existing in Presets)
				existing.IsDefault = false;

		Presets.Add(vm);
	}

	private bool CanConfirmNewPreset() => !string.IsNullOrWhiteSpace(_newPresetName);

	// ── Row kebab-menu commands (stubs for now; wire real behaviour when apply-preset lands) ─
	[RelayCommand]
	private void EditPreset(PresetViewModel? preset)
	{
		if (preset is null) return;

		// Close the row's kebab menu (bound via IsKebabMenuOpen) before opening the dialog
		// so the popup doesn't linger under the dialog overlay.
		preset.IsKebabMenuOpen = false;

		// Reopen the same dialog populated with the preset's current values; the
		// PresetDialogTitle / ActionLabel bindings flip to "EDIT PRESET" / "SAVE".
		SetPresetDialogMode(PresetDialogMode.Edit, preset.Id);
		NewPresetName = preset.Name;
		_newPresetDescription = preset.Description;
		NewPresetSetAsDefault = preset.IsDefault;
		// Editing the current default: lock the toggle ON. Un-ticking is a no-op in
		// ApplyEditToExistingPreset (preserves the "exactly one default" invariant),
		// so the UI disables the toggle instead of pretending it accepted the click.
		IsSetAsDefaultLocked = preset.IsDefault;
		IsNewPresetOpen = true;
	}

	[RelayCommand]
	private void DuplicatePreset(PresetViewModel? preset)
	{
		if (preset is null) return;
		preset.IsKebabMenuOpen = false;

		// Open the shared dialog in Duplicate mode, seeded with the source row's values. Confirm
		// copies the SOURCE's own saved snapshot (see AppendDuplicatePreset) — the NewPresetName
		// setter truncates to NewPresetNameMaxLength if the "Copy of …" prefix pushes past the cap.
		SetPresetDialogMode(PresetDialogMode.Duplicate);
		_duplicatingSourcePresetId = preset.Id;
		var format = LocalizationManager.GetString("DuplicatePreset_NameFormat");
		NewPresetName = string.Format(format, preset.Name);
		_newPresetDescription = preset.Description;
		NewPresetSetAsDefault = false;
		IsSetAsDefaultLocked = false;
		IsNewPresetOpen = true;
	}

	[RelayCommand]
	private void RestorePreset(PresetViewModel? preset)
	{
		if (preset is null) return;
		preset.IsKebabMenuOpen = false;

		// Show the confirmation dialog; the actual "reset to defaults" happens on Confirm.
		_pendingResetPreset = preset;
		IsResetPresetOpen = true;
	}

	[RelayCommand]
	private void CancelResetPreset()
	{
		IsResetPresetOpen = false;
		_pendingResetPreset = null;
	}

	// Restores the device to its catalog defaults — the same reset the Settings section's
	// "Restore to default" performs — and then SAVES that state into the preset, so the row holds
	// the defaults rather than nothing.
	//
	// This used to wipe the preset's snapshot and leave the live device untouched, which meant the
	// menu item appeared to do nothing: the reset only surfaced later, when the row was re-selected
	// and an empty snapshot loaded.
	//
	// Reaching here requires the confirmation dialog (RestorePreset opens it, this is its Confirm) —
	// the reset discards the preset's saved settings, so it must not be one click away.
	[RelayCommand]
	private async Task ConfirmResetPreset()
	{
		var target = _pendingResetPreset;
		IsResetPresetOpen = false;
		_pendingResetPreset = null;
		if (target is null || Device is null || _localStorage is null) return;

		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		// Captured before the awaits: leaving the page mid-reset nulls Device (same
		// pattern as ConfirmSavePreset).
		var localStorage = _localStorage;
		var (vid, pid, key) = (Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
		var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);

		await ResetPresetAspectsToDefaultsAsync();

		// Same save path as ConfirmSavePreset, so a reset preset is stored exactly like a saved one
		// — including the stage / rate / RGB that PersistDisplayedDeviceState backfills. Without it
		// the snapshot would omit whatever the user never explicitly touched, and reverting to this
		// preset later would find nothing to restore for those aspects. The snapshot is
		// queued BEHIND the writes PersistDisplayedDeviceState enqueues, so it observes
		// them (review round 1, B1).
		PersistDisplayedDeviceState();
		await localStorage.RunOnStorageAsync(() => localStorage.SavePresetSnapshot(
			target.Id, ownerKind, ownerId, vid, pid, key));

		// The reset is already staged (staging is synchronous; only the device push is debounced),
		// so cancel any in-flight debounced apply rather than let it duplicate the write below.
		_deviceApplyDebounceCts?.Cancel();
		await ApplyCurrentStateToDeviceAsync(pushActuation: true);

		await localStorage.RunOnStorageAsync(() => localStorage.SetAppliedPreset(
			ownerKind, ownerId, vid, pid, key, target.Id));

		// The working state now IS this preset's snapshot, so nothing is unsaved. Resetting a row
		// that isn't the active one leaves the active one dirty, matching ConfirmSavePreset.
		if (target == SelectedPreset)
			SettlePresetClean();
	}

	// Restores every aspect a PRESET stores, for whatever device type this is. Each step is guarded
	// by the capability it needs, so a keyboard or a headset runs only the parts that apply to it
	// instead of the mouse-shaped reset the Settings section performs.
	//
	// The aspect list is not arbitrary — it is the set of tables SavePresetSnapshot copies:
	// device_rgb / device_rgb_effect, button_bindings, device_settings (sliders, audio levels, DPI
	// stage, report rate), key_bindings and key_macro_bindings.
	//
	// App-scoped preferences (scroll speed, double-click speed, sleep timer) are deliberately NOT
	// reset here even though the Settings section's "Restore to default" clears them: they are not
	// part of a preset, so resetting one preset must not change settings shared by every device.
	internal async Task ResetPresetAspectsToDefaultsAsync()
	{
		// Mouse sliders (debounce / lift-off). No-ops when the device declares no such settings.
		// Awaited: each of these persists into device_settings, and the caller snapshots those rows
		// the moment this returns.
		await ResetDeviceSettingsToDefaultAsync();

		// Remappable mouse buttons. Guarded internally on IsButtonRemapSupported.
		ResetButtonBindingsToDefault();

		// Audio levels — the headset's equivalent of the mouse sliders.
		await ResetAudioSettingsToDefaultAsync();

		// Lighting, for every device that has any.
		ResetRgbToDefault();

		// Key actuation on the magnetic boards. Its defaults come from the board itself
		// (GetKeyActuation's `defaults`), not from the catalog — the hardware is authoritative
		// about its own travel, so there is nothing for docs/device-defaults.md to declare.
		// Awaited for the same reason as the settings reset above: the caller snapshots the live
		// rows as soon as this returns.
		await ResetActuationToDefaultAsync();

		// DPI ladder, active stage and polling rate. Last because it is the part that awaits, and
		// the debounce coalesces everything above it into one coordinated device write.
		await RestoreDpiSectionDefaultsAsync();

		// NOT restored: keyboard key assignments (key_bindings / key_macro_bindings), which ARE part
		// of a preset snapshot. Every aspect above has a defined default to restore to; a key map
		// does not — there is no default-layout mechanism, and a key map is per-board. Deliberately
		// out of scope, to be closed later; see "Known gap: keyboard key assignments are not reset"
		// in docs/device-defaults.md.
	}

	// Puts the audio sliders back on their defaults and pushes them to the endpoint. Mirrors
	// ResetDeviceSettingsToDefaultAsync: the individual writes run suppressed so they do not each
	// schedule a debounced apply, then one explicit apply persists and pushes both.
	//
	// The apply is AWAITED. It writes the levels into device_settings, and the preset snapshot taken
	// straight after this copies those rows — fire-and-forget would let the snapshot store the levels
	// from before the reset, so the preset and the endpoint would disagree.
	private async Task ResetAudioSettingsToDefaultAsync()
	{
		if (_audioSettings.Count == 0) return;

		_suppressAudioSave = true;
		try
		{
			foreach (var setting in _audioSettings)
				setting.Value = Math.Clamp(setting.DefaultValue, setting.Min, setting.Max);
		}
		finally
		{
			_suppressAudioSave = false;
		}

		await ApplyAudioSettingsAsync();
	}

	// Restores the device's out-of-box lighting from its catalog config. ApplyRgbDefaults only
	// mutates the backing fields, so the bound properties are re-raised here and the new state is
	// persisted and staged the same way an ordinary effect change is.
	private void ResetRgbToDefault()
	{
		if (!IsRgbSupported || RgbEffects.Count == 0) return;

		ApplyRgbDefaults();

		OnPropertyChanged(nameof(IsRgbEnabled));
		OnPropertyChanged(nameof(ActiveRgbEffect));

		// Persist AND stage, the pair a user-driven effect change makes — the reset is a user edit,
		// so the preset must go dirty and the device must be written.
		SaveRgbStateForCurrentDevice();
	}

	// Row-menu Delete. Opens the confirmation dialog; ConfirmDeletePreset does the deleting.
	// Named DeletePreset so the kebab menu's existing DeletePresetCommand binding keeps working.
	[RelayCommand]
	private void DeletePreset(PresetViewModel? preset)
	{
		if (preset is null) return;
		preset.IsKebabMenuOpen = false;

		// The default preset is un-deletable. The kebab menu already hides the delete entry
		// (IsVisible bound to !IsDefault); this is the code-side guard for the case where the
		// command is invoked via keyboard / accessibility. Checked BEFORE the dialog opens so the
		// user is never asked to confirm something that cannot happen.
		if (preset.IsDefault) return;

		_pendingDeletePreset = preset;
		IsDeletePresetOpen = true;
	}

	[RelayCommand]
	private void CancelDeletePreset()
	{
		IsDeletePresetOpen = false;
		_pendingDeletePreset = null;
	}

	[RelayCommand]
	private async Task ConfirmDeletePreset()
	{
		var preset = _pendingDeletePreset;
		IsDeletePresetOpen = false;
		_pendingDeletePreset = null;
		if (preset is null || preset.IsDefault) return;

		_localStorage?.DeletePreset(preset.Id);
		Presets.Remove(preset);
		if (SelectedPreset == preset)
		{
			var next = Presets.FirstOrDefault();
			if (next is not null)
			{
				foreach (var p in Presets)
					p.IsSelected = false;
				next.IsSelected = true;
				SelectedPreset = next;
				// Delete is an explicit user action → force-apply the next preset's snapshot
				// so the live device state matches the new active row instead of the deleted
				// preset's leftover state. Bypass the dirty guard: the deleted preset can't
				// receive any more saves.
				await ApplyPresetToDeviceAsync(next);
			}
			else
			{
				SelectedPreset = null;
			}
		}
	}

	// ── Load ──────────────────────────────────────────────────────────────────
	// Populates Presets from SQLite for the current device. Called from SetDeviceAsync
	// (and from tests via InternalsVisibleTo — see ProoveGaming.App.csproj).
	// generation: SetDeviceAsync passes its chain generation so a stale run (same card reopened —
	// see LoadMacroLibraryAsync) drops its apply; direct callers (tests) omit it and skip the check.
	internal async Task LoadPresetsForDeviceAsync(int? generation = null)
	{
		if (!_presetsCountHooked)
		{
			Presets.CollectionChanged += (_, _) =>
			{
				OnPropertyChanged(nameof(PresetsCountLabel));
				OnPropertyChanged(nameof(ShowNoPresetsHint));
				// Keep the Save-Preset picker's filtered view in sync with any add /
				// delete on the underlying list. Renames don't trigger CollectionChanged;
				// row names are read straight off PresetViewModel and picked up by the
				// TextBlock binding.
				RefreshFilteredSavePresets();
			};
			_presetsCountHooked = true;
		}

		Presets.Clear();
		SelectedPreset = null;
		var device = Device;
		if (device is null || _localStorage is null)
		{
			IsPresetsLoading = false;
			return;
		}
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null)
		{
			IsPresetsLoading = false;
			return;
		}

		// For a signed-in user an empty preset scope may just be the sign-in pull still in
		// flight: seeding a default now would mint a fresh-uid row the pull cannot dedup, and
		// SyncCoordinator would push it — one server-side duplicate default per DB wipe
		// (docs/plans/fix-duplicate-default-preset). So wait for the pull to settle before
		// CreateDefaultPresetIfNotExists gets to judge emptiness. Bounded by contract:
		// PullTask never faults, completes even when the pull aborts on a network error, and
		// is Task.CompletedTask when no pull ever started. Guests never pull, never wait.
		if (owner.Value.Kind == LocalStorageService.OwnerKindUser && _settingsSync is { } settingsSync)
			await settingsSync.PullTask;

		// Everything the background block needs from VM / live-device state, read on the calling
		// thread; the block itself touches storage only.
		var (stageToSeed, rateToSeed) = CaptureCatalogDefaultSeeds();
		var defaultName = LocalizationManager.GetString("Presets_DefaultName");
		var (vid, pid, key) = (device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		var localStorage = _localStorage;
		var settings = _settings;

		var (records, appliedId) = await Task.Run(async () =>
		{
			// Storage-level self-heal: seeds a default row when the scope is empty; promotes
			// the first row when rows exist but none is marked default (legacy DBs). Both
			// branches run inside a single transaction and are idempotent.
			var defaultPreset = localStorage.CreateDefaultPresetIfNotExists(
				owner.Value.Kind, owner.Value.Id, vid, pid, defaultName);

			// A just-seeded default preset has an EMPTY snapshot, and EnsureDefaultPresetAppliedAsync
			// then loads that emptiness onto the device — clearing the live rows SetDeviceAsync just
			// resolved, RGB included. That is how a keyboard the App had lit ended up with no persisted
			// RGB for ReapplyPersistedRgbAsync to restore after a reconnect. Capturing the live state
			// into the brand-new row's snapshot gives the default preset something to actually
			// represent, so the apply that follows is a no-op rather than a wipe.
			//
			// Guarded on Seeded, never on "the snapshot looks empty": an existing preset may be
			// legitimately empty (after Reset), and overwriting an existing snapshot from live state is
			// exactly the destructive step EnsureDefaultPresetAppliedAsync warns about. RGB is
			// pre-persisted for this by SetDeviceAsync's awaited persist, just above; the DPI stage /
			// report rate rows are seeded here (values captured by CaptureCatalogDefaultSeeds) —
			// SettingsRepository routes them through the storage executor now, so they are AWAITED
			// (write-completion tasks) to have the rows committed before the SavePresetSnapshot
			// below copies device_settings (review round 1, B2).
			if (defaultPreset.Seeded)
			{
				if (settings is not null)
				{
					if (stageToSeed is { } stage)
						await settings.SaveActiveDpiStageAsync(vid, pid, key, stage);
					if (rateToSeed is { } rate)
						await settings.SavePollingRateAsync(vid, pid, key, rate);
				}
				localStorage.SavePresetSnapshot(defaultPreset.Id, owner.Value.Kind, owner.Value.Id, vid, pid, key);
			}

			var presetRecords = localStorage.GetPresets(owner.Value.Kind, owner.Value.Id, vid, pid);

			// device_applied_preset — the same tracking EnsureDefaultPresetAppliedAsync and every
			// Save/switch/create already maintain.
			var applied = localStorage.GetAppliedPreset(owner.Value.Kind, owner.Value.Id, vid, pid, key);
			return (presetRecords, applied);
		});

		// Another open may have taken over while the block was in flight; its own load owns
		// Presets now.
		if ((generation is { } gen && gen != _setDeviceGeneration) || !ReferenceEquals(Device, device)) return;

		foreach (var record in records)
		{
			Presets.Add(new PresetViewModel
			{
				Id = record.Id,
				Name = record.Name,
				Description = record.Description,
				IsDefault = record.IsDefault,
			});
		}

		// Default the dropdown / row selection to whichever preset was last actually applied to
		// this device, so reopening the app shows what the user was using, not just whichever
		// preset happens to be first by id. Falls back to the first row when nothing has ever
		// been applied (fresh device) or the applied preset was since deleted.
		var initial = (appliedId is { } id ? Presets.FirstOrDefault(p => p.Id == id) : null)
			?? Presets.FirstOrDefault();
		if (initial is not null)
		{
			initial.IsSelected = true;
			SelectedPreset = initial;
		}

		// After the populate, not before it: the hint gates on (empty AND not loading), so
		// clearing the flag first would flash the hint for the tick before the rows land. A
		// stale run returned above without touching the flag — the newer open owns it.
		IsPresetsLoading = false;
	}

	/// <summary>
	/// Materializes a preset the sign-in pull brought down: it lives only in its snapshot tables,
	/// because the pull wrote <c>device_applied_preset</c> while holding no unit, so nothing ever
	/// copied that snapshot into the live rows or pushed it to the board — the sections would show
	/// empty and Save Preset would light up over rows the user never touched
	/// (docs/plans/add-extended-keys-api-sync). This runs once, through the ordinary preset-switch
	/// path (snapshot → live rows under this unit's key → full push, extended keys included).
	/// </summary>
	/// <remarks>
	/// Deliberately called from <see cref="SetDeviceAsync"/>'s FOLLOW-UP chain rather than from
	/// <see cref="LoadPresetsForDeviceAsync"/>, where it first lived: the apply reads state the
	/// earlier steps of the open have not produced yet (review round 1, B1/B2). Until the
	/// extended-keys probe has run, <c>ApplyStoredExtendedKeysToDeviceAsync</c> returns at its
	/// <c>_extendedKeysSupported != true</c> guard and the pulled extended keys — the whole point of
	/// the feature — never reach the board; and until <c>LoadMacroLibraryAsync</c> has filled
	/// <c>Macros</c>, <c>ResyncKeyMacroBindingsAsync</c> uploads an EMPTY library and then deletes
	/// every restored <c>key_macro_bindings</c> row as "the macro is gone from the library". Both
	/// are silent, both are permanent (the mark is spent by then), so this must sit after the probe
	/// AND after the macro/layout loads.
	///
	/// restoreUnsetToDefaults: false — like the first-connect push, a stage/rate the preset never
	/// stored must stay unset rather than resetting a DPI set outside the app.
	/// </remarks>
	private async Task MaterializePulledPresetAsync(DeviceCardViewModel device, int generation)
	{
		if (_localStorage is null) return;
		if (generation != _setDeviceGeneration || !ReferenceEquals(Device, device)) return;

		// ApplyPresetToDeviceAsync returns immediately on a transport that cannot carry
		// configuration, so consuming the mark here would burn it without a push and leave the
		// pulled preset unmaterialized for good. Leave it for an open over a transport that can.
		if (!IsDeviceConfigurable) return;

		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		var localStorage = _localStorage;
		var adopted = await ConsumePendingApplyMarkAsync(
			localStorage, owner.Value, device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		if (adopted is not { } presetId) return;

		// The consume above yields (the storage executor completes its continuations on
		// TaskScheduler.Default), so re-run the open's fence before the apply: a device switch in
		// that window would otherwise push THIS board's preset onto the next one's hardware and
		// live rows (review round 1, S1). The mark is spent either way — the newer open owns the
		// page, and re-selecting the preset by hand still pushes it.
		if (generation != _setDeviceGeneration || !ReferenceEquals(Device, device)) return;

		var preset = Presets.FirstOrDefault(p => p.Id == presetId);
		if (preset is null) return;

		await ApplyPresetToDeviceAsync(preset, restoreUnsetToDefaults: false, target: device);
	}

	// Reads the sign-in pull's pending-apply mark for (owner, device type) and, when it names the
	// preset that is STILL the applied one for this unit, runs the materialize callback and clears
	// the mark — all inside ONE storage delegate, so two overlapping page opens (or a page open
	// racing a connect) cannot both act on it. Returns the preset the caller may now materialize,
	// or null when there is nothing to do.
	//
	// The mark is spent whatever the outcome. A board that refuses the write must not make every
	// later open retry it; and a mark whose preset is no longer the applied one is simply stale —
	// the user has since chosen something else, and reloading the pulled snapshot over the live rows
	// would silently discard that choice (review round 1, S3).
	private static Task<int?> ConsumePendingApplyMarkAsync(
		LocalStorageService storage, SettingsOwner owner, int vendorId, int productId, string deviceKey,
		Action<int>? materialize = null)
	{
		// Not restricted to signed-in owners. It was, on the grounds that guests never pull and so
		// never carry the mark — true of the sign-in pull, and false since the owner sweep became
		// the mark's second writer (docs/plans/fix-account-switch-device-state). Switching between a
		// guest and an account is a switch like any other: whichever of the two arrives finds the
		// other's configuration on the board, and a guest that could never consume its own mark
		// would both keep wearing the account's records and accumulate a mark nothing ever clears.
		var key = LocalStorageService.SyncMetaDeviceStatePendingApply(vendorId, productId);
		return storage.RunOnStorageAsync<int?>(() =>
		{
			if (storage.GetSyncMeta(owner.Kind, owner.Id, key) is not { } mark) return null;
			storage.RemoveSyncMeta(owner.Kind, owner.Id, key);

			if (!int.TryParse(mark, NumberStyles.Integer, CultureInfo.InvariantCulture, out var adoptedPresetId))
				return null;
			// GetAppliedPreset falls back to the unit-agnostic '' row the pull wrote, so this
			// matches whether or not this unit has since recorded a choice of its own.
			if (storage.GetAppliedPreset(owner.Kind, owner.Id, vendorId, productId, deviceKey) != adoptedPresetId)
				return null;

			materialize?.Invoke(adoptedPresetId);
			return adoptedPresetId;
		});
	}

	// ── Unsaved-Changes confirmation dialog state ─────────────────────────────
	// Same dialog surface used for two flows:
	//   LeavingView    — the shell tried to navigate away from DeviceDetailsView.
	//                    Discard → clear dirty + fire ExitConfirmedAfterUnsavedChanges
	//                    (shell subscribes to actually navigate).
	//   SwitchingPreset — the user clicked another preset while dirty. The pending
	//                    target is parked in _pendingPresetSwitch and applied on
	//                    Discard. Cancel (X) drops the pending switch.
	// Body text is a computed property so the same view can reflect both wordings.
	private enum UnsavedChangesReason { LeavingView, SwitchingPreset }
	private UnsavedChangesReason _unsavedChangesReason = UnsavedChangesReason.LeavingView;
	private PresetViewModel? _pendingPresetSwitch;

	private bool _isUnsavedChangesOpen;
	public bool IsUnsavedChangesOpen
	{
		get => _isUnsavedChangesOpen;
		set => SetProperty(ref _isUnsavedChangesOpen, value);
	}

	public string UnsavedChangesBody => LocalizationManager.GetString(_unsavedChangesReason switch
	{
		UnsavedChangesReason.SwitchingPreset => "UnsavedChanges_Body_Switching",
		_ => "UnsavedChanges_Body",
	});

	public event Action? ExitConfirmedAfterUnsavedChanges;

	// Called by SelectPreset when the user picks a different preset while dirty.
	// Parks the pending target and shows the switching-preset variant of the dialog.
	private void OpenUnsavedChangesForSwitch(PresetViewModel target)
	{
		// The pick that got here was a click INSIDE the dropdown popup, so light-dismiss never
		// fires — without this the list stays floating over the confirmation dialog. Same
		// courtesy SelectAndApplyPresetAsync extends on the actual switch.
		IsPresetsDropdownOpen = false;

		_pendingPresetSwitch = target;
		_unsavedChangesReason = UnsavedChangesReason.SwitchingPreset;
		OnPropertyChanged(nameof(UnsavedChangesBody));
		IsUnsavedChangesOpen = true;
	}

	// Called by the shell's OnDeviceDetailsBack when leaving the view while dirty.
	public void OpenUnsavedChangesForLeaving()
	{
		_pendingPresetSwitch = null;
		_unsavedChangesReason = UnsavedChangesReason.LeavingView;
		OnPropertyChanged(nameof(UnsavedChangesBody));
		IsUnsavedChangesOpen = true;
	}

	[RelayCommand]
	private void CancelUnsavedChanges()
	{
		_pendingPresetSwitch = null;
		IsUnsavedChangesOpen = false;
	}

	[RelayCommand]
	private async Task DiscardUnsavedChanges()
	{
		SettlePresetClean();
		IsUnsavedChangesOpen = false;
		if (_unsavedChangesReason == UnsavedChangesReason.SwitchingPreset && _pendingPresetSwitch is not null)
		{
			var target = _pendingPresetSwitch;
			_pendingPresetSwitch = null;
			await SelectAndApplyPresetAsync(target, resetUnsetActuationToDefaults: true);
			return;
		}

		// Leaving the view while dirty: the tinkering already landed in the live rows (and
		// possibly on the hardware itself — RGB/DPI/etc. apply live as the user changes them),
		// so clearing the dirty flag alone leaves it in place as the new de-facto state. Re-load
		// the active preset's own last-saved snapshot back over the live state and re-push it to
		// the device, the same revert SelectPreset already does when switching presets, so
		// "Discard" actually discards instead of silently keeping the unsaved change.
		if (SelectedPreset is not null)
			await ApplyPresetToDeviceAsync(SelectedPreset, resetUnsetActuationToDefaults: true);

		ExitConfirmedAfterUnsavedChanges?.Invoke();
	}

	// Route "Save" to the existing Save Preset dialog. When the user picks a
	// target and confirms in that flow, HasUnsavedPresetChanges is cleared; the shell can
	// then decide whether to exit. Kept minimal here: close this dialog, open
	// the Save one, and let the user complete the save explicitly.
	// For the switching-preset case, the pending target is dropped rather than chained:
	// ConfirmSavePreset already switches the active preset to whichever target the user
	// picks in the Save dialog, so a parked auto-switch on top of that would fight it.
	[RelayCommand]
	private void SaveUnsavedChanges()
	{
		_pendingPresetSwitch = null;
		IsUnsavedChangesOpen = false;
		// Through the seeding helper, NOT a bare IsSavePresetOpen = true: this dialog can
		// otherwise open with the target radio still on whatever a previously cancelled
		// Save-Preset session left selected — see OpenSavePresetDialog.
		OpenSavePresetDialog();
	}
}

// One row in the Presets list. Selection is visual only until apply-preset lands (future work
// wires it into the device). The kebab menu is a placeholder for edit / remove. IsDefault is
// the runtime marker driving the "Default" badge on the bottom-right of the row; only the
// active default preset carries it. Persistence lands with the apply-preset work.
public sealed partial class PresetViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
	public int Id { get; set; }

	private string _name = string.Empty;
	public string Name
	{
		get => _name;
		set => SetProperty(ref _name, value);
	}

	private string _description = string.Empty;
	public string Description
	{
		get => _description;
		set => SetProperty(ref _description, value);
	}

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	// Drives the radio state inside the "Save Preset Settings" dialog. Kept separate from
	// IsSelected (which represents the active preset in the section list / dropdown) so
	// picking a save target doesn't spuriously flip the active preset selection.
	private bool _isSaveTarget;
	public bool IsSaveTarget
	{
		get => _isSaveTarget;
		set => SetProperty(ref _isSaveTarget, value);
	}

	private bool _isDefault;
	public bool IsDefault
	{
		get => _isDefault;
		set => SetProperty(ref _isDefault, value);
	}

	// Kebab (three-dots) menu open state. Bound two-way to the Popup so:
	//   - clicking the button toggles it open,
	//   - clicking outside (light dismiss) closes it,
	//   - any menu command can close it by setting this to false.
	// Also drives the octagon hover-background's "stays visible while menu is open" state.
	private bool _isKebabMenuOpen;
	public bool IsKebabMenuOpen
	{
		get => _isKebabMenuOpen;
		set => SetProperty(ref _isKebabMenuOpen, value);
	}

	[RelayCommand]
	private void ToggleKebabMenu() => IsKebabMenuOpen = !IsKebabMenuOpen;
}
