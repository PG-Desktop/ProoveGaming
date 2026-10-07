using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Actuation backing fields ──────────────────────────────────────────────
	// Values live in hundredths of a millimetre (the wire unit); the display properties convert.
	// State is read from the board on device open and written (debounced) on change. What the user
	// edits is ALSO mirrored into device_actuation so a preset can carry it — the board is still
	// what the UI reads on open, but it is no longer the only place these values survive.
	private KeyActuationInfo? _actuationInfo;
	private int _actuationPressCentiMm;
	private int _actuationReleaseCentiMm;
	private bool _actuationRapidTrigger;
	private int _actuationQuickPressCentiMm;
	private int _actuationQuickReleaseCentiMm;
	// Distinguishes "probe still running / transport failed" (null) from a definite verdict: the
	// nav entry only hides on a hard "no" FROM THE DEVICE. A dead RPC keeps this null so the
	// section stays put — other sections don't vanish when the DeviceHost hiccups, and neither
	// may this one.
	private bool? _actuationSupported;
	private bool _actuationWriteFailed;
	private bool _suppressActuationApply;
	private CancellationTokenSource? _actuationDebounceCts;
	// Per-target latest-wins apply: while a write is in flight, follow-ups queue FIFO, but a new
	// request replaces a queued one only when it aims at the SAME target (same mode + slots) — so
	// six release-and-drag edits of one key still collapse into one catch-up flash commit (T800
	// wear), while edits to different keys all reach the device instead of the last one silently
	// evicting the others. Both fields are UI-thread-only.
	//
	// Every queued entry is a full snapshot (mode + slots + values), not a flag: it is built when
	// the edit leaves its debounce window, so a mode/selection/section change that lands while a
	// write is in flight can no longer retarget the edit — the review's B1, where switching to
	// All keys mid-write turned a single-key edit into a whole-board write.
	private bool _actuationApplyInFlight;
	private readonly List<ActuationWriteRequest> _actuationQueuedWrites = [];

	// Bumped by every preset apply that pushes stored actuation. A slider write already awaiting the
	// device when that happens belongs to the preset the user has left: it must not update the cache,
	// must not persist itself into the incoming preset's live rows, and must not mark it dirty. The
	// same generation-fence shape _keyLayoutGeneration uses for late device reads.
	private int _actuationApplyGeneration;

	// What the board is believed to hold right now: set from the device read on open and from every
	// write it accepted. A preset push that would write exactly this is skipped — on T800 an
	// actuation write is ~43 HID transfers, ~1 s of settle and a FLASH COMMIT, so re-sending values
	// the board already has costs real wear for no effect. Null means "unknown", which always pushes.
	// Ordered by slot so a plain SequenceEqual is a valid comparison.
	private List<ActuationRecord>? _actuationOnDevice;

	/// <summary>One committed edit: where it goes (mode + slots) and what it writes.</summary>
	private sealed record ActuationWriteRequest(bool PerKey, int[]? Slots, KeyActuationKeyValues Values);

	// ── Per-key mode state (all UI-thread-only) ───────────────────────────────
	// slot → current values from GetKeyActuation's per-key map. Null/empty means the DeviceHost
	// predates per-key actuation and the mode toggle never renders. Kept current locally after
	// each successful write, so mode and selection switches need no device round-trip.
	private Dictionary<int, KeyActuationKeyValues>? _perKeyActuation;
	private bool _actuationPerKeyMode;
	// Single-select: the user edits either All keys or exactly this one key.
	private KeyAssignmentOptionViewModel? _actuationSelectedKey;
	// True while an edit sits in the debounce window. A selection or mode change flushes it to the
	// selection it was made against, instead of silently retargeting (or dropping) the edit.
	private bool _actuationEditPending;

	private const int ActuationApplyDebounceMs = 400;

	// Test seam (the file's *ForTests convention): whether an edit is still sitting in the debounce
	// window. A test that needs the edit to have been CONSUMED — before releasing a gated write, say
	// — must wait on this rather than on a fixed sleep. The debounce is a real 400 ms timer, and a
	// loaded CI runner can miss any wall-clock margin picked for a quiet machine, letting a late
	// edit escape as an extra write (Release CI, 2026-08-27 —
	// RepeatedEditsOfTheSameKey_StillCoalesceIntoOneQueuedWrite: expected 2 writes, got 3).
	internal bool HasPendingActuationEditForTests => _actuationEditPending;

	// ── Actuation properties ──────────────────────────────────────────────────
	public bool IsActuationSupported => _actuationSupported == true;
	public bool IsActuationProbePending => _actuationSupported == null;

	/// <summary>
	/// True when the last actuation write did not reach (or verify on) the keyboard — the sliders
	/// have been re-read from the device, so without this the failure reads as "the slider snapped
	/// back by itself". Cleared by the next successful write or a device switch.
	/// </summary>
	public bool ActuationWriteFailed
	{
		get => _actuationWriteFailed;
		private set => SetProperty(ref _actuationWriteFailed, value);
	}

	public double ActuationPressMm => _actuationPressCentiMm / 100.0;
	public double ActuationReleaseMm => _actuationReleaseCentiMm / 100.0;
	public double ActuationQuickPressMm => _actuationQuickPressCentiMm / 100.0;
	public double ActuationQuickReleaseMm => _actuationQuickReleaseCentiMm / 100.0;

	// Invariant, not current culture: a plain interpolation renders "2,00 mm" on any comma-decimal
	// locale (uk-UA, de-DE, …), which mixes a localised separator with a hard-coded English "mm" and
	// made the display assertions fail on those machines. The unit is not localised, so the number
	// should not be either — localise both together, or neither. Same reason
	// DevicePartHotspotViewModel formats its path data invariantly.
	public string ActuationPressDisplay => FormatMm(ActuationPressMm);
	public string ActuationReleaseDisplay => FormatMm(ActuationReleaseMm);
	public string ActuationQuickPressDisplay => FormatMm(ActuationQuickPressMm);
	public string ActuationQuickReleaseDisplay => FormatMm(ActuationQuickReleaseMm);

	private static string FormatMm(double millimetres) =>
		string.Create(CultureInfo.InvariantCulture, $"{millimetres:0.00} mm");

	/// <summary>
	/// The value line of a key-overlay hover tooltip: this key's press actuation in
	/// millimetres as a bare invariant number ("2.08" — the design shows no unit), or null
	/// when the device does not support actuation, which collapses the tooltip to the key
	/// name alone. A per-key value wins; a key without one falls back to the uniform value.
	/// </summary>
	internal string? GetKeyActuationTooltip(KeyAssignmentOptionViewModel key)
	{
		if (!IsActuationSupported) return null;

		int centiMm = _perKeyActuation is not null
			&& _perKeyActuation.TryGetValue(key.MatrixIndex, out var values)
			? values.PressCentiMm
			: _actuationPressCentiMm;
		return string.Create(CultureInfo.InvariantCulture, $"{centiMm / 100.0:0.00}");
	}

	public double ActuationPressPct => Pct(_actuationPressCentiMm, _actuationInfo?.PressMinCentiMm, _actuationInfo?.PressMaxCentiMm);
	public double ActuationReleasePct => Pct(_actuationReleaseCentiMm, _actuationInfo?.PressMinCentiMm, _actuationInfo?.PressMaxCentiMm);
	public double ActuationQuickPressPct => Pct(_actuationQuickPressCentiMm, _actuationInfo?.RtMinCentiMm, _actuationInfo?.RtMaxCentiMm);
	public double ActuationQuickReleasePct => Pct(_actuationQuickReleaseCentiMm, _actuationInfo?.RtMinCentiMm, _actuationInfo?.RtMaxCentiMm);

	public bool ActuationRapidTrigger
	{
		get => _actuationRapidTrigger;
		set
		{
			if (SetProperty(ref _actuationRapidTrigger, value))
			{
				OnPropertyChanged(nameof(AreRtControlsEnabled));
				ScheduleActuationApply();
			}
		}
	}

	public bool AreRtControlsEnabled => ActuationRapidTrigger;

	// ── Per-key mode properties ───────────────────────────────────────────────
	/// <summary>
	/// True when this board+host combination can edit actuation per key: the DeviceHost sent a
	/// per-key map (an older host sends none — that is "hide the mode", never "no keys") and the
	/// board has a clickable overlay to select keys on.
	/// </summary>
	public bool IsActuationPerKeyAvailable =>
		IsActuationSupported && _perKeyActuation is { Count: > 0 } && HasInteractiveKeyboardOverlay;

	public bool ActuationPerKeyMode
	{
		get => _actuationPerKeyMode;
		set
		{
			if (_actuationPerKeyMode == value) return;
			// An edit still in its debounce window belongs to the mode it was made in.
			FlushPendingActuationApply();
			_actuationPerKeyMode = value;
			ClearActuationSelection();
			// The overlay highlight rides the same IsSelected flag the Key-Assignments/Macros
			// radio uses, so the two selections must not bleed into each other: entering Per-key
			// mode clears a radio-selected key's ghost highlight, leaving restores it.
			if (value)
			{
				foreach (var key in PhysicalKeys)
					key.IsSelected = false;
			}
			else
			{
				if (SelectedPhysicalKey is not null)
					SelectedPhysicalKey.IsSelected = true;
				ShowActuationMajorityValues();
			}
			OnPropertyChanged(nameof(ActuationPerKeyMode));
			RaiseActuationSelectionChanged();
			RaiseActuationOverlayChanged();
		}
	}

	public bool HasActuationSelection => _actuationSelectedKey is not null;

	/// <summary>Sliders and RT are inert in Per-key mode until a key is selected.</summary>
	public bool AreActuationControlsEnabled => !_actuationPerKeyMode || HasActuationSelection;

	/// <summary>
	/// "Selected: W" for the selected key. Empty with no selection — the view shows the
	/// select-a-key hint instead.
	/// </summary>
	public string ActuationSelectedKeysLabel =>
		_actuationSelectedKey is { } key
			? string.Format(
				LocalizationManager.GetString("DeviceDetails_Actuation_Selected_Format"),
				key.KeyLabel)
			: string.Empty;

	// ── Per-key selection (called from SelectPhysicalKey's actuation branch) ──
	internal void SelectActuationKey(KeyAssignmentOptionViewModel option)
	{
		// The pending edit was made against the previous selection — commit it there first.
		FlushPendingActuationApply();

		if (ReferenceEquals(_actuationSelectedKey, option))
		{
			// Clicking the selected key steps back to the neutral hint state.
			_actuationSelectedKey = null;
			option.IsSelected = false;
		}
		else
		{
			if (_actuationSelectedKey is not null)
				_actuationSelectedKey.IsSelected = false;
			_actuationSelectedKey = option;
			option.IsSelected = true;
		}

		ShowActuationValuesForSelectedKey();
		RaiseActuationSelectionChanged();
	}

	private void ClearActuationSelection()
	{
		if (_actuationSelectedKey is null) return;
		_actuationSelectedKey.IsSelected = false;
		_actuationSelectedKey = null;
	}

	/// <summary>Loads the selected key's values into the sliders.</summary>
	private void ShowActuationValuesForSelectedKey()
	{
		if (_actuationSelectedKey is null || _perKeyActuation is null)
			return;
		if (!_perKeyActuation.TryGetValue(_actuationSelectedKey.MatrixIndex, out var values))
			return;

		_suppressActuationApply = true;
		try
		{
			_actuationPressCentiMm = values.PressCentiMm;
			_actuationReleaseCentiMm = values.ReleaseCentiMm;
			_actuationRapidTrigger = values.RapidTrigger;
			_actuationQuickPressCentiMm = values.QuickPressCentiMm;
			_actuationQuickReleaseCentiMm = values.QuickReleaseCentiMm;
		}
		finally
		{
			_suppressActuationApply = false;
		}
		RaiseActuationStateChanged();
	}

	/// <summary>
	/// Recomputes the whole-board majority from the per-key cache — per-key edits may have moved
	/// it since the device read, and All-keys mode must reopen on honest values.
	/// </summary>
	private void ShowActuationMajorityValues()
	{
		if (_perKeyActuation is not { Count: > 0 })
			return;

		var press = new Dictionary<int, int>();
		var release = new Dictionary<int, int>();
		var quickPress = new Dictionary<int, int>();
		var quickRelease = new Dictionary<int, int>();
		int rtVotes = 0;
		foreach (var values in _perKeyActuation.Values)
		{
			Vote(press, values.PressCentiMm);
			Vote(release, values.ReleaseCentiMm);
			Vote(quickPress, values.QuickPressCentiMm);
			Vote(quickRelease, values.QuickReleaseCentiMm);
			if (values.RapidTrigger) rtVotes++;
		}

		_suppressActuationApply = true;
		try
		{
			_actuationPressCentiMm = Winner(press);
			_actuationReleaseCentiMm = Winner(release);
			_actuationRapidTrigger = rtVotes * 2 > _perKeyActuation.Count;
			_actuationQuickPressCentiMm = Winner(quickPress);
			_actuationQuickReleaseCentiMm = Winner(quickRelease);
		}
		finally
		{
			_suppressActuationApply = false;
		}
		RaiseActuationStateChanged();

		static void Vote(Dictionary<int, int> votes, int value)
		{
			votes.TryGetValue(value, out int count);
			votes[value] = count + 1;
		}

		static int Winner(Dictionary<int, int> votes)
		{
			int best = 0, bestCount = -1;
			foreach (var (value, count) in votes)
				if (count > bestCount) { best = value; bestCount = count; }
			return best;
		}
	}

	/// <summary>
	/// Commits an edit still sitting in its debounce window against the selection/mode it was made
	/// in, before that context changes under it. The request is snapshotted here, synchronously —
	/// the caller mutates the selection/mode right after this returns.
	/// </summary>
	private void FlushPendingActuationApply()
	{
		if (!_actuationEditPending) return;
		_actuationEditPending = false;
		_actuationDebounceCts?.Cancel();
		if (BuildActuationWriteRequest() is { } request)
			_ = ApplyActuationToDeviceAsync(request);
	}

	/// <summary>
	/// The current edit as a write request: target (mode + selected slots) and values. Null when
	/// Per-key mode has nothing selected — an edit with no target. Called only while the context
	/// still matches the edit (debounce fire or flush), which the flush discipline guarantees:
	/// every selection/mode/section change flushes before mutating.
	/// </summary>
	private ActuationWriteRequest? BuildActuationWriteRequest()
	{
		int[]? slots = null;
		if (_actuationPerKeyMode)
		{
			if (_actuationSelectedKey is null)
				return null;
			slots = [_actuationSelectedKey.MatrixIndex];
		}

		return new ActuationWriteRequest(_actuationPerKeyMode, slots,
			new KeyActuationKeyValues(
				_actuationPressCentiMm, _actuationReleaseCentiMm, _actuationRapidTrigger,
				_actuationQuickPressCentiMm, _actuationQuickReleaseCentiMm));
	}

	private void RaiseActuationSelectionChanged()
	{
		OnPropertyChanged(nameof(HasActuationSelection));
		OnPropertyChanged(nameof(AreActuationControlsEnabled));
		OnPropertyChanged(nameof(ActuationSelectedKeysLabel));
	}

	// Raised from the KeyAssignments partial too — the overlay and the static art swap places
	// when the mode flips.
	private void RaiseActuationOverlayChanged()
	{
		OnPropertyChanged(nameof(ShowKeyboardOverlayInActuation));
		OnPropertyChanged(nameof(ShowLayeredKeyboardImage));
		OnPropertyChanged(nameof(ShowFlatKeyboardImage));
	}

	// ── Slider entry points (called from the section view's slider controls) ──
	// "Fraction" is the pointer position as 0..1 of the track; the percent properties above are
	// 0..100 for the width converters. Named apart on purpose — feeding a percent in here would
	// silently clamp to the maximum.
	public void SetActuationPressFromFraction(double fraction) =>
		SetActuationValue(ref _actuationPressCentiMm, FromFraction(fraction, _actuationInfo?.PressMinCentiMm, _actuationInfo?.PressMaxCentiMm),
			nameof(ActuationPressMm), nameof(ActuationPressDisplay), nameof(ActuationPressPct));

	public void SetActuationReleaseFromFraction(double fraction) =>
		SetActuationValue(ref _actuationReleaseCentiMm, FromFraction(fraction, _actuationInfo?.PressMinCentiMm, _actuationInfo?.PressMaxCentiMm),
			nameof(ActuationReleaseMm), nameof(ActuationReleaseDisplay), nameof(ActuationReleasePct));

	public void SetActuationQuickPressFromFraction(double fraction) =>
		SetActuationValue(ref _actuationQuickPressCentiMm, FromFraction(fraction, _actuationInfo?.RtMinCentiMm, _actuationInfo?.RtMaxCentiMm),
			nameof(ActuationQuickPressMm), nameof(ActuationQuickPressDisplay), nameof(ActuationQuickPressPct));

	public void SetActuationQuickReleaseFromFraction(double fraction) =>
		SetActuationValue(ref _actuationQuickReleaseCentiMm, FromFraction(fraction, _actuationInfo?.RtMinCentiMm, _actuationInfo?.RtMaxCentiMm),
			nameof(ActuationQuickReleaseMm), nameof(ActuationQuickReleaseDisplay), nameof(ActuationQuickReleasePct));

	private void SetActuationValue(ref int field, int centiMm, params string[] changedProperties)
	{
		if (field == centiMm) return;
		field = centiMm;
		foreach (var name in changedProperties)
			OnPropertyChanged(name);
		ScheduleActuationApply();
	}

	private static double Pct(int value, int? min, int? max) =>
		min is int lo && max is int hi && hi > lo
			? Math.Clamp((value - lo) / (double)(hi - lo) * 100.0, 0.0, 100.0)
			: 0.0;

	// Snaps a slider position to the device's value granularity so the thumb lands on values the
	// firmware can actually store (Cyrex: 0.01 mm; a coarser step snaps accordingly).
	private int FromFraction(double fraction, int? min, int? max)
	{
		if (min is not int lo || max is not int hi || hi <= lo) return 0;
		int stepCentiMm = Math.Max(1, (_actuationInfo?.StepMicrometres ?? 10) / 10);
		double raw = lo + Math.Clamp(fraction, 0.0, 1.0) * (hi - lo);
		int snapped = lo + (int)Math.Round((raw - lo) / stepCentiMm) * stepCentiMm;
		return Math.Clamp(snapped, lo, hi);
	}

	// ── Restore defaults ──────────────────────────────────────────────────────
	// The factory values Device Info reports, applied through the same debounced write path as a
	// manual edit — the actuation counterpart of the Settings section's "Default Settings" button.
	[RelayCommand]
	private void RestoreActuationDefaults()
	{
		if (_actuationInfo is not { Supported: true } info) return;

		_actuationPressCentiMm = info.DefaultPressCentiMm;
		_actuationReleaseCentiMm = info.DefaultReleaseCentiMm;
		_actuationRapidTrigger = info.DefaultRapidTrigger;
		_actuationQuickPressCentiMm = info.DefaultQuickPressCentiMm;
		_actuationQuickReleaseCentiMm = info.DefaultQuickReleaseCentiMm;
		RaiseActuationStateChanged();
		ScheduleActuationApply();
	}

	/// <summary>
	/// Preset Reset's actuation aspect: back to the board's own reported defaults, applied to every
	/// key. No-op on a board without actuation, so it is safe to call for any device.
	/// </summary>
	/// <remarks>
	/// Drops out of Per-key mode first: a reset means "the whole board goes back to stock", and
	/// leaving a per-key selection in place would write the defaults to that one key.
	/// <para>
	/// Awaited and debounce-free, unlike the slider path. Preset Reset snapshots the live rows the
	/// moment this returns, and <see cref="PersistActuationState"/> only writes them once the board
	/// has accepted the values — going through the 400 ms debounce would let the snapshot run first
	/// and leave the reset out of the preset entirely.
	/// </para>
	/// </remarks>
	internal async Task ResetActuationToDefaultAsync()
	{
		if (_actuationSupported != true || _actuationInfo is not { Supported: true } info) return;

		if (_actuationPerKeyMode)
			ActuationPerKeyMode = false;

		_suppressActuationApply = true;
		try
		{
			_actuationPressCentiMm = info.DefaultPressCentiMm;
			_actuationReleaseCentiMm = info.DefaultReleaseCentiMm;
			_actuationRapidTrigger = info.DefaultRapidTrigger;
			_actuationQuickPressCentiMm = info.DefaultQuickPressCentiMm;
			_actuationQuickReleaseCentiMm = info.DefaultQuickReleaseCentiMm;
		}
		finally
		{
			_suppressActuationApply = false;
		}

		RaiseActuationStateChanged();

		_actuationEditPending = false;
		_actuationDebounceCts?.Cancel();
		if (BuildActuationWriteRequest() is { } request)
			await ApplyActuationToDeviceAsync(request);
	}

	// ── Load (device open) ────────────────────────────────────────────────────
	// Awaited (failure-swallowed) in SetDeviceAsync's follow-up chain; internal so tests can await
	// it directly against a Device set via the plain property, without the rest of the chain.
	// Stale-result guard: the
	// device may change while the read is in flight, so the result is dropped unless it still
	// belongs to the current device.
	internal async Task LoadActuationFromDeviceAsync() => await LoadActuationFromDeviceAsync(clearWriteFailure: true);

	private async Task LoadActuationFromDeviceAsync(bool clearWriteFailure)
	{
		var device = Device;
		_actuationDebounceCts?.Cancel();
		_actuationEditPending = false;
		_actuationInfo = null;
		_actuationSupported = null;
		// A device switch starts clean — per-key mode and selection included; the reload after a
		// failed write keeps the banner up AND the mode/selection, because the re-read is what
		// makes the sliders snap back and the user is still looking at the same keys.
		if (clearWriteFailure)
		{
			ActuationWriteFailed = false;
			// Writes queued behind an in-flight one belong to the previous device — draining them
			// into the one being opened would write another board's slots and values.
			_actuationQueuedWrites.Clear();
			_perKeyActuation = null;
			// Belief about the other board says nothing about this one.
			_actuationOnDevice = null;
			ClearActuationSelection();
			if (_actuationPerKeyMode)
			{
				_actuationPerKeyMode = false;
				OnPropertyChanged(nameof(ActuationPerKeyMode));
				RaiseActuationOverlayChanged();
			}
			RaiseActuationSelectionChanged();
		}
		RaiseActuationStateChanged();

		if (device is null || !device.SupportsActuation || _usbHidDeviceService is null)
			return;

		var info = await _usbHidDeviceService.GetKeyActuationAsync(device.VendorId, device.ProductId);
		if (!ReferenceEquals(Device, device))
			return;

		if (info is null)
		{
			// The RPC itself failed — a transport fact, not a device verdict. The section stays
			// (probe-pending state) instead of vanishing from under the user; other sections
			// behave the same way when the DeviceHost hiccups. A reopen retries naturally.
			return;
		}

		if (!info.Supported)
		{
			// A hard "no" from the probe: hide the section rather than offer sliders that would
			// write to a board the safety gate refused.
			_actuationSupported = false;
			if (ActiveSection == NavSection.Actuation)
				ActiveSection = NavSection.Presets;
			RaiseActuationStateChanged();
			return;
		}

		_suppressActuationApply = true;
		try
		{
			_actuationInfo = info;
			_actuationPressCentiMm = info.PressCentiMm;
			_actuationReleaseCentiMm = info.ReleaseCentiMm;
			_actuationRapidTrigger = info.RapidTrigger;
			_actuationQuickPressCentiMm = info.QuickPressCentiMm;
			_actuationQuickReleaseCentiMm = info.QuickReleaseCentiMm;
			_actuationSupported = true;
			_perKeyActuation = info.PerKeyValues.Count > 0
				? new Dictionary<int, KeyActuationKeyValues>(info.PerKeyValues)
				: null;
		}
		finally
		{
			_suppressActuationApply = false;
		}

		// The read IS the board's state, so it is also the most accurate belief about what the board
		// holds — which lets a preset whose values already match skip its push entirely.
		_actuationOnDevice = BuildActuationRows();

		// The failure-reload path keeps the mode; if the fresh read no longer carries a per-key
		// map, the mode has lost its data source and must drop back to All keys.
		if (_perKeyActuation is null && _actuationPerKeyMode)
			ActuationPerKeyMode = false;

		RaiseActuationStateChanged();

		// On a failure-reload in Per-key mode the sliders should snap back to the still-selected
		// key's device truth, not to the whole-board majority the block above loaded.
		if (_actuationPerKeyMode && _actuationSelectedKey is not null)
			ShowActuationValuesForSelectedKey();
	}

	private void RaiseActuationStateChanged()
	{
		OnPropertyChanged(nameof(IsActuationSupported));
		OnPropertyChanged(nameof(IsActuationProbePending));
		OnPropertyChanged(nameof(IsActuationPerKeyAvailable));
		OnPropertyChanged(nameof(IsActuationNavVisible));
		OnPropertyChanged(nameof(KeyboardNavButtonsCount));
		OnPropertyChanged(nameof(KeyboardActionsMenuHeight));
		OnPropertyChanged(nameof(ActuationPressMm));
		OnPropertyChanged(nameof(ActuationReleaseMm));
		OnPropertyChanged(nameof(ActuationQuickPressMm));
		OnPropertyChanged(nameof(ActuationQuickReleaseMm));
		OnPropertyChanged(nameof(ActuationPressDisplay));
		OnPropertyChanged(nameof(ActuationReleaseDisplay));
		OnPropertyChanged(nameof(ActuationQuickPressDisplay));
		OnPropertyChanged(nameof(ActuationQuickReleaseDisplay));
		OnPropertyChanged(nameof(ActuationPressPct));
		OnPropertyChanged(nameof(ActuationReleasePct));
		OnPropertyChanged(nameof(ActuationQuickPressPct));
		OnPropertyChanged(nameof(ActuationQuickReleasePct));
		OnPropertyChanged(nameof(ActuationRapidTrigger));
		OnPropertyChanged(nameof(AreRtControlsEnabled));
	}

	// ── Debounced apply ───────────────────────────────────────────────────────
	// Slider drags fire per pixel; only the value the user settles on reaches the device. Same
	// CTS-restart shape as the preset live-apply debounce.
	private void ScheduleActuationApply()
	{
		if (_suppressActuationApply || _actuationSupported != true) return;
		// In Per-key mode an edit without a selection has no target — the controls are disabled
		// then, but the guard keeps a programmatic edit from writing to nothing.
		if (_actuationPerKeyMode && _actuationSelectedKey is null) return;

		_actuationEditPending = true;
		_actuationDebounceCts?.Cancel();
		var cts = new CancellationTokenSource();
		_actuationDebounceCts = cts;
		_ = DebounceActuationApplyAsync(cts.Token);
	}

	private async Task DebounceActuationApplyAsync(CancellationToken token)
	{
		try { await Task.Delay(ActuationApplyDebounceMs, token); }
		catch (TaskCanceledException) { return; }
		if (token.IsCancellationRequested) return;

		_actuationEditPending = false;
		if (BuildActuationWriteRequest() is { } request)
			await ApplyActuationToDeviceAsync(request);
	}

	private async Task ApplyActuationToDeviceAsync(ActuationWriteRequest request)
	{
		// While a write is in flight, follow-ups queue FIFO and coalesce per target: replacing the
		// tail is only safe when the new request hits the same keys — blanket latest-wins dropped
		// a queued edit of ANOTHER key without a trace (no banner, no log, and the per-key cache
		// only moves on success, so the UI honestly showed the value that never got written).
		// Each entry carries the snapshot it was queued with — never the mode/selection/values as
		// they happen to look when the in-flight write ends.
		// UI-thread-only state — every continuation below resumes on the dispatcher context.
		if (_actuationApplyInFlight)
		{
			if (_actuationQueuedWrites.Count > 0 && SameActuationTarget(_actuationQueuedWrites[^1], request))
				_actuationQueuedWrites[^1] = request;
			else
				_actuationQueuedWrites.Add(request);
			return;
		}

		_actuationApplyInFlight = true;
		var generation = _actuationApplyGeneration;
		try
		{
			for (ActuationWriteRequest? current = request; current is not null; current = DequeueActuationWrite())
			{
				var device = Device;
				if (device is null || _usbHidDeviceService is null || _actuationSupported != true)
					return;

				var written = current.Values;
				var ok = current.PerKey
					? await _usbHidDeviceService.SetPerKeyActuationAsync(
						device.VendorId, device.ProductId, current.Slots!,
						written.PressCentiMm, written.ReleaseCentiMm, written.RapidTrigger,
						written.QuickPressCentiMm, written.QuickReleaseCentiMm)
					: await _usbHidDeviceService.SetKeyActuationAsync(
						device.VendorId, device.ProductId,
						written.PressCentiMm, written.ReleaseCentiMm, written.RapidTrigger,
						written.QuickPressCentiMm, written.QuickReleaseCentiMm);

				if (!ReferenceEquals(Device, device))
					return;

				// A preset apply landed while this write was in flight — see _actuationApplyGeneration.
				// The board may briefly hold this value; the stores must not, or it follows the user
				// onto the preset they switched to.
				if (generation != _actuationApplyGeneration)
					return;

				if (ok)
				{
					// Keep the local per-key cache the truth the next selection click reads from:
					// a per-key write moved just its slots, a global write made the board uniform.
					if (_perKeyActuation is not null)
					{
						if (current.PerKey)
						{
							foreach (int slot in current.Slots!)
								_perKeyActuation[slot] = written;
						}
						else
						{
							// Overwriting existing keys' values mid-enumeration is legal on
							// .NET Core 3.0+ dictionaries; only add/remove invalidates.
							foreach (int slot in _perKeyActuation.Keys)
								_perKeyActuation[slot] = written;
						}
					}

					// Persist only what the board accepted, so SQLite can never claim a value the
					// hardware refused.
					PersistActuationState();
					ActuationWriteFailed = false;
				}
				else
				{
					// The write failed (or its verify did) — say so and re-read, so the sliders
					// show what the keyboard actually holds instead of a value it rejected. The
					// banner explains the snap-back; without it this looks like a haunted slider.
					ActuationWriteFailed = true;
					await LoadActuationFromDeviceAsync(clearWriteFailure: false);
					return;
				}
			}
		}
		finally
		{
			_actuationApplyInFlight = false;
		}
	}

	// ── Preset persistence ────────────────────────────────────────────────────
	// Only a real edit reaches here, and only after the board accepted it. Deliberately NOT called
	// on the device read: seeding live rows from the board on open would make them differ from a
	// preset saved before actuation was preset-scoped, and PresetMatchesLiveState compares rows —
	// so Save Preset would light up on a page the user never touched. A preset therefore carries
	// actuation only once the user has actually tuned it, and a preset with no rows has no opinion.
	private void PersistActuationState()
	{
		var device = Device;
		var owner = _ownerProvider?.GetCurrent();
		if (device is null || _localStorage is null || owner is null || _actuationSupported != true)
			return;

		var rows = BuildActuationRows();
		_localStorage.ReplaceDeviceActuation(
			owner.Value.Kind, owner.Value.Id,
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey,
			rows);
		_actuationOnDevice = rows;

		// StagePresetDirtyOnly, not NotifyPresetChanged: the rows above are already persisted, so
		// the recompute compares them against the preset's snapshot — which is what turns the Save
		// Preset button back OFF when an edit returns to the saved value (2.6 → 3.0 → 2.6), the
		// same both-directions behaviour every other section gets. No device write is scheduled:
		// the actuation write path has its own debounce and queue.
		StagePresetDirtyOnly();
	}

	// The per-key map when the board reports one, otherwise a single all-keys row — the same split
	// the write path itself makes between SetPerKeyActuationAsync and SetKeyActuationAsync.
	private List<ActuationRecord> BuildActuationRows()
	{
		if (_perKeyActuation is { Count: > 0 })
		{
			var rows = new List<ActuationRecord>(_perKeyActuation.Count);
			foreach (var (slot, v) in _perKeyActuation)
			{
				rows.Add(new ActuationRecord(
					slot, v.PressCentiMm, v.ReleaseCentiMm, v.RapidTrigger,
					v.QuickPressCentiMm, v.QuickReleaseCentiMm));
			}

			// Slot order, matching GetDeviceActuation's ORDER BY, so the two can be compared directly.
			rows.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
			return rows;
		}

		return [new ActuationRecord(
			ActuationRecord.AllKeysSlot, _actuationPressCentiMm, _actuationReleaseCentiMm,
			_actuationRapidTrigger, _actuationQuickPressCentiMm, _actuationQuickReleaseCentiMm)];
	}

	/// <summary>
	/// Pushes the stored actuation to the board as part of a preset apply (switch, save, or the
	/// default-preset push on first connect).
	/// </summary>
	/// <remarks>
	/// When the preset holds no actuation rows, every path but one writes nothing: an empty set
	/// means "no opinion", not "zero", which keeps a preset saved before actuation was
	/// preset-scoped from resetting a board the user has tuned. The exception is the
	/// unsaved-changes Discard (<paramref name="restoreUnsetToDefaults"/> true): abandoned
	/// actuation edits must not stay on the board, so it returns the board to the state it
	/// reported at page open (see <see cref="PushProbedActuationWithoutPersistAsync"/>) — without
	/// persisting rows, so the live set stays empty and keeps matching the preset's empty
	/// snapshot.
	/// </remarks>
	internal async Task ApplyStoredActuationToDeviceAsync(bool restoreUnsetToDefaults = false)
	{
		// A preset apply supersedes any actuation edit the user was in the middle of: that edit
		// belongs to the preset they have just left. Dropped BEFORE the guards below, and even when
		// this preset turns out to hold no actuation rows — otherwise the stale 400 ms debounce
		// fires after the switch, writes the outgoing preset's value to the board, and
		// PersistActuationState stores it into the INCOMING preset's live rows, marking it dirty
		// with a change the user never made.
		_actuationEditPending = false;
		_actuationDebounceCts?.Cancel();
		// Edits that already left the debounce but are queued behind an in-flight write are the third
		// state, and the one PR #91's M1 caught: they survive both the cancel above and the fence
		// below, then drain on the NEXT edit — writing the outgoing preset's value to the board and
		// into the incoming preset's live rows, long after the switch. Everything queued at this
		// instant belongs to the preset being left, so it goes. Cleared here rather than in the
		// fence: a write queued AFTER the switch belongs to the new preset and must still reach it.
		_actuationQueuedWrites.Clear();
		// Writes already awaiting the device cannot be cancelled, so they are fenced off instead:
		// ApplyActuationToDeviceAsync drops anything that started in an earlier generation.
		_actuationApplyGeneration++;

		var device = Device;
		var owner = _ownerProvider?.GetCurrent();
		if (device is null || !device.SupportsActuation || _usbHidDeviceService is null
			|| _localStorage is null || owner is null)
			return;

		var rows = _localStorage.GetDeviceActuation(
			owner.Value.Kind, owner.Value.Id,
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		if (rows.Count == 0)
		{
			if (restoreUnsetToDefaults)
				await PushProbedActuationWithoutPersistAsync();
			return;
		}

		// The board already holds exactly this (Save Preset on an unchanged board, or the second
		// push of a Preset Reset that just wrote the defaults itself). Skipping is not an
		// optimisation: on T800 every push is a flash commit, so writing values already in place is
		// pure wear. The UI is still re-synced below.
		if (_actuationOnDevice is not null && rows.SequenceEqual(_actuationOnDevice))
		{
			ReflectStoredActuation(rows);
			return;
		}

		var allKeys = rows.FirstOrDefault(r => r.Slot == ActuationRecord.AllKeysSlot);
		bool ok;
		if (allKeys is not null)
		{
			ok = await _usbHidDeviceService.SetKeyActuationAsync(
				device.VendorId, device.ProductId,
				allKeys.PressCentiMm, allKeys.ReleaseCentiMm, allKeys.RapidTrigger,
				allKeys.QuickPressCentiMm, allKeys.QuickReleaseCentiMm);
		}
		else
		{
			// Slots sharing a value go in one call, so a uniformly-tuned board costs a single write
			// rather than one per key.
			ok = true;
			foreach (var group in rows.GroupBy(r => (r.PressCentiMm, r.ReleaseCentiMm, r.RapidTrigger, r.QuickPressCentiMm, r.QuickReleaseCentiMm)))
			{
				if (!ReferenceEquals(Device, device))
					return;

				ok = await _usbHidDeviceService.SetPerKeyActuationAsync(
					device.VendorId, device.ProductId, group.Select(r => r.Slot).ToArray(),
					group.Key.PressCentiMm, group.Key.ReleaseCentiMm, group.Key.RapidTrigger,
					group.Key.QuickPressCentiMm, group.Key.QuickReleaseCentiMm);
				if (!ok)
					break;
			}
		}

		if (!ReferenceEquals(Device, device))
			return;

		if (!ok)
		{
			// The board refused the preset's values (or the verify failed). Treated exactly like a
			// refused slider edit: raise the banner and re-read, so the section shows what the
			// keyboard actually holds. Reflecting the preset here instead would leave the UI
			// asserting values the hardware rejected, with nothing on screen to say so.
			//
			// The re-read is then written back to the live rows (PR #91 m3). Leaving them holding the
			// refused values would make PresetMatchesLiveState call the preset clean while the board
			// holds something else — and a per-key push that failed part-way leaves the board split
			// between the preset's slots and its own. Persisting the truth marks the preset dirty,
			// which is the honest outcome: the preset genuinely is not applied.
			ActuationWriteFailed = true;
			await LoadActuationFromDeviceAsync(clearWriteFailure: false);
			if (ReferenceEquals(Device, device))
				PersistActuationState();
			return;
		}

		ActuationWriteFailed = false;
		ReflectStoredActuation(rows);
	}

	// The Discard-onto-an-unset-preset case: nothing was ever saved for this preset, so the
	// baseline to return to is the state the board itself reported when the page opened — the
	// last state the user implicitly "had" before this editing session; for a never-tuned board
	// that is its factory defaults. The probe (_actuationInfo) is immutable after the load (the
	// per-key cache is a copy, see LoadActuationFromDeviceAsync), so it still holds that state.
	// Unlike ResetActuationToDefaultAsync nothing is persisted: the live rows stay empty,
	// matching the preset's empty snapshot, exactly as they were at page open.
	private async Task PushProbedActuationWithoutPersistAsync()
	{
		var device = Device;
		if (device is null || _usbHidDeviceService is null
			|| _actuationSupported != true || _actuationInfo is not { Supported: true } info)
			return;

		// The at-open state, in BuildActuationRows' shape: the probe's per-key map when the board
		// reported one, otherwise one uniform all-keys row.
		List<ActuationRecord> baseline;
		if (info.PerKeyValues.Count > 0)
		{
			baseline = new List<ActuationRecord>(info.PerKeyValues.Count);
			foreach (var (slot, v) in info.PerKeyValues)
			{
				baseline.Add(new ActuationRecord(
					slot, v.PressCentiMm, v.ReleaseCentiMm, v.RapidTrigger,
					v.QuickPressCentiMm, v.QuickReleaseCentiMm));
			}

			baseline.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
		}
		else
		{
			baseline =
			[
				new ActuationRecord(
					ActuationRecord.AllKeysSlot,
					info.PressCentiMm, info.ReleaseCentiMm, info.RapidTrigger,
					info.QuickPressCentiMm, info.QuickReleaseCentiMm),
			];
		}

		// Same wear guard as the stored-rows path: on T800 every push is a flash commit.
		if (_actuationOnDevice is not null && baseline.SequenceEqual(_actuationOnDevice))
		{
			ReflectStoredActuation(baseline);
			return;
		}

		bool ok;
		if (baseline is [{ Slot: ActuationRecord.AllKeysSlot } allKeys])
		{
			ok = await _usbHidDeviceService.SetKeyActuationAsync(
				device.VendorId, device.ProductId,
				allKeys.PressCentiMm, allKeys.ReleaseCentiMm, allKeys.RapidTrigger,
				allKeys.QuickPressCentiMm, allKeys.QuickReleaseCentiMm);
		}
		else
		{
			// Same shared-value grouping as the stored-rows path, for the same wear reason.
			ok = true;
			foreach (var group in baseline.GroupBy(r => (r.PressCentiMm, r.ReleaseCentiMm, r.RapidTrigger, r.QuickPressCentiMm, r.QuickReleaseCentiMm)))
			{
				if (!ReferenceEquals(Device, device))
					return;

				ok = await _usbHidDeviceService.SetPerKeyActuationAsync(
					device.VendorId, device.ProductId, group.Select(r => r.Slot).ToArray(),
					group.Key.PressCentiMm, group.Key.ReleaseCentiMm, group.Key.RapidTrigger,
					group.Key.QuickPressCentiMm, group.Key.QuickReleaseCentiMm);
				if (!ok)
					break;
			}
		}

		if (!ReferenceEquals(Device, device))
			return;

		if (!ok)
		{
			// Same treatment as a refused stored-rows push: banner plus re-read, so the sliders
			// show what the keyboard actually holds. Nothing is persisted either way.
			ActuationWriteFailed = true;
			await LoadActuationFromDeviceAsync(clearWriteFailure: false);
			return;
		}

		ActuationWriteFailed = false;
		_actuationOnDevice = baseline;
		ReflectStoredActuation(baseline);
	}

	// Moves the sliders and the per-key cache onto what was just pushed, so the section shows the
	// preset's values without a second device round-trip.
	private void ReflectStoredActuation(IReadOnlyList<ActuationRecord> rows)
	{
		_suppressActuationApply = true;
		try
		{
			if (_perKeyActuation is not null)
			{
				foreach (var row in rows.Where(r => r.Slot >= 0))
				{
					_perKeyActuation[row.Slot] = new KeyActuationKeyValues(
						row.PressCentiMm, row.ReleaseCentiMm, row.RapidTrigger,
						row.QuickPressCentiMm, row.QuickReleaseCentiMm);
				}
			}

			// Only the all-keys row speaks for the whole board. For a per-key set the sliders are
			// filled in below — from the refreshed cache, not from an arbitrary row.
			if (rows.FirstOrDefault(r => r.Slot == ActuationRecord.AllKeysSlot) is { } allKeys)
			{
				_actuationPressCentiMm = allKeys.PressCentiMm;
				_actuationReleaseCentiMm = allKeys.ReleaseCentiMm;
				_actuationRapidTrigger = allKeys.RapidTrigger;
				_actuationQuickPressCentiMm = allKeys.QuickPressCentiMm;
				_actuationQuickReleaseCentiMm = allKeys.QuickReleaseCentiMm;
			}
		}
		finally
		{
			_suppressActuationApply = false;
		}

		// Per-key rows with no per-key map (a preset saved against a newer DeviceHost, restored on an
		// older one) leave the sliders on the device read rather than on one arbitrary key's values.
		if (_actuationPerKeyMode && _actuationSelectedKey is not null)
			ShowActuationValuesForSelectedKey();
		else if (_perKeyActuation is { Count: > 0 })
			ShowActuationMajorityValues();

		RaiseActuationStateChanged();
	}

	private ActuationWriteRequest? DequeueActuationWrite()
	{
		if (_actuationQueuedWrites.Count == 0) return null;
		var next = _actuationQueuedWrites[0];
		_actuationQueuedWrites.RemoveAt(0);
		return next;
	}

	/// <summary>Same destination: both global, or per-key over the same slots.</summary>
	private static bool SameActuationTarget(ActuationWriteRequest a, ActuationWriteRequest b) =>
		a.PerKey == b.PerKey && (!a.PerKey || a.Slots.AsSpan().SequenceEqual(b.Slots));
}
