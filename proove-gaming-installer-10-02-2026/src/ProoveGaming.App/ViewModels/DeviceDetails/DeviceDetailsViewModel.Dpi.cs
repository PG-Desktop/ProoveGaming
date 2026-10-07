using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Custom DPI points ─────────────────────────────────────────────────────
	// The DPI ladder is user-defined: each point holds a value the user picks inside the device's
	// own range, and points can be added and removed within the slot count the firmware supports
	// (1-8 on both mice). The customization block below the list always edits the SELECTED point,
	// so the radio group doubles as "which point is active" and "which point am I editing".
	//
	// The bounds come from the catalog (DeviceCardViewModel.Dpi*), never from constants here: the
	// quantum is a firmware property — TAIPAN stores CPI in 50-DPI units — and BLAZE's real sensor
	// granularity is still unverified, so it has to stay a configuration value.
	// See docs/plans/add-custom-dpi-values/.

	private string _customDpiText = string.Empty;
	// Coalesces the storage write while the slider is being dragged — see PersistDpiLadderAndApply.
	private CancellationTokenSource? _dpiLadderPersistCts;

	// What the point grid renders: the points, then the "add" cell while the device has a free slot.
	// One collection, because the add button sits IN the point flow — only an ItemsControl's own
	// items can land in its panel. Same shape the home grid uses for its AddDeviceCardViewModel tile.
	//
	// DpiPresets stays the typed list everything else works against; this mirrors it, and is rebuilt
	// only where the composition changes (BuildDpiPresets), never on selection or value edits.
	public ObservableCollection<object> DpiPointCells { get; } = [];

	private readonly AddDpiPointCellViewModel _addDpiPointCell = new();

	public DpiPresetViewModel? SelectedDpiPoint => DpiPresets.FirstOrDefault(p => p.IsSelected);

	// "3rd point" — 1-based, because the user counts points from one. The ordinal comes from a
	// per-point resource string, not from suffix logic here: "st/nd/rd/th" is English grammar, and
	// Ukrainian needs endings that agree with the noun's gender. The keys cover 1-8, which is every
	// slot either firmware has; anything past that falls back to the plain "Point {0}" format.
	public string DpiPointTitle
	{
		get
		{
			var slot = SelectedDpiPoint?.SlotNumber ?? 1;
			return slot is >= 1 and <= MaxNamedDpiPoints
				? LocalizationManager.GetString($"DeviceDetails_Dpi_Point_{slot}")
				: string.Format(CultureInfo.CurrentCulture,
					LocalizationManager.GetString("DeviceDetails_Dpi_PointFormat"), slot);
		}
	}

	// How many points have their own ordinal string in the resource files.
	private const int MaxNamedDpiPoints = 8;

	// "Step: 50 dpi" — the device's quantum, not a hardcoded 50, so a device that turns out to
	// resolve 100 shows the truth after a catalog edit alone.
	public string DpiStepLabel =>
		string.Format(CultureInfo.CurrentCulture,
			LocalizationManager.GetString("DeviceDetails_Dpi_StepFormat"),
			Device?.DpiStep ?? DeviceCardViewModel.FallbackDpiStep);

	// The editable value of the selected point, as shown in the input box.
	public string CustomDpiText
	{
		get => _customDpiText;
		private set => SetProperty(ref _customDpiText, value);
	}

	// Where the selected point's value sits in the device's range, 0..100 for PercentSliderControl.
	public double CustomDpiPct
	{
		get
		{
			var (min, max, _) = DpiBounds();
			if (SelectedDpiPoint is not { } point || max <= min) return 0;
			return (point.DpiValue - min) * 100.0 / (max - min);
		}
	}

	// The customization block is only meaningful while a point is selected on a configurable mouse.
	public bool HasDpiPoints => DpiPresets.Count > 0 && Device?.SupportsDpiStageSwitch == true;

	// Hidden (not just disabled) at the device's slot ceiling, matching how the row's own delete
	// button disappears at the floor.
	public bool CanAddDpiPoint =>
		HasDpiPoints && DpiPresets.Count < (Device?.DpiSlotMax ?? DeviceCardViewModel.FallbackDpiSlotMax);

	// Mirrors DpiPresets into the rendered cells and appends the add cell when there is room.
	// Called only from BuildDpiPresets: the composition changes there and nowhere else, and
	// rebuilding on every selection change would regenerate the whole grid on each DPI-button press.
	private void RebuildDpiPointCells()
	{
		DpiPointCells.Clear();
		foreach (var point in DpiPresets)
			DpiPointCells.Add(point);
		if (CanAddDpiPoint)
			DpiPointCells.Add(_addDpiPointCell);
	}

	// Pushes every derived label/flag of the section. Called by BuildDpiPresets and by
	// HighlightActiveDpiStage, i.e. after anything that changes the list or the selection.
	private void RefreshDpiPointState()
	{
		CustomDpiText = SelectedDpiPoint?.DpiValue.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
		OnPropertyChanged(nameof(SelectedDpiPoint));
		OnPropertyChanged(nameof(DpiPointTitle));
		OnPropertyChanged(nameof(DpiStepLabel));
		OnPropertyChanged(nameof(CustomDpiPct));
		OnPropertyChanged(nameof(HasDpiPoints));
		OnPropertyChanged(nameof(CanAddDpiPoint));
	}

	private (int Min, int Max, int Step) DpiBounds() => (
		Device?.DpiMin ?? DeviceCardViewModel.FallbackDpiMin,
		Device?.DpiMax ?? DeviceCardViewModel.FallbackDpiMax,
		Device?.DpiStep ?? DeviceCardViewModel.FallbackDpiStep);

	// The one place a DPI value is made legal: snapped to the device's step, then clamped into its
	// range. The input box, the slider and the add command all go through it so they cannot disagree
	// about what a valid value is. Integer math — this runs per pointer-move while dragging.
	//
	// The grid is anchored at ZERO, not at min: the step is the firmware's own quantum (TAIPAN stores
	// CPI in 50-DPI units), so a multiple of the step is what the device can actually hold. Anchoring
	// at min would produce values congruent to min instead — correct only while min is itself a
	// multiple of the step, and silently rounded on the wire the first time a catalog says otherwise.
	// Clamping last can still return min or max off-grid; staying inside the device's range wins over
	// staying on it.
	internal static int QuantizeDpi(int value, int min, int max, int step)
	{
		if (step <= 0) return Math.Clamp(value, min, max);

		var snapped = (int)(Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step);
		return Math.Clamp(snapped, min, max);
	}

	// The point the value box is editing, captured when it takes focus. The commit below targets THIS
	// row rather than whatever is selected at commit time: clicking another point's radio both moves
	// the selection and takes focus off the box, and the two are not ordered against each other — so
	// resolving the target at commit time could write the typed value onto the row the user just
	// clicked instead of the one they typed into.
	private DpiPresetViewModel? _editingDpiPoint;

	// Held for the whole focus session, not just one commit: the box can commit more than once
	// while it keeps focus (an outside click applies the value without moving the caret), and every
	// one of those commits belongs to the row the user started typing into.
	internal void BeginCustomDpiEdit() => _editingDpiPoint = SelectedDpiPoint;

	internal void EndCustomDpiEdit() => _editingDpiPoint = null;

	// Strips everything that is not a digit. The view applies this to the box's content on every
	// change, so the field can only ever hold a number no matter how the text arrived — typed,
	// pasted, dropped, or entered through an IME, which each take a different route through
	// Avalonia and cannot all be caught by refusing keystrokes alone.
	internal static string KeepDigitsOnly(string? text)
	{
		if (string.IsNullOrEmpty(text)) return string.Empty;
		if (text.All(char.IsAsciiDigit)) return text;

		return string.Concat(text.Where(char.IsAsciiDigit));
	}

	// Commits what the user typed. Called on Enter and on lost focus — never per keystroke, which
	// would write to the device while they are still typing.
	//
	// A number OUT OF RANGE is pulled into range rather than rejected: typing 0 (or anything under
	// the device's floor) means "as low as this mouse goes", so it lands on DpiMin, and the same in
	// reverse at the ceiling. Only text that is not a number at all — an empty box, most obviously —
	// reverts to the point's current value, because there is no intent there to honour.
	internal void CommitCustomDpiText(string? text)
	{
		var point = _editingDpiPoint ?? SelectedDpiPoint;
		// The row can be gone by now — a preset switch or reconnect rebuilds the list while the box
		// still holds focus.
		if (point is null || !DpiPresets.Contains(point)) return;

		var trimmed = text?.Trim() ?? string.Empty;
		var (min, max, step) = DpiBounds();

		if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var typed))
		{
			// All digits but unparseable means it overflowed int — more digits than any DPI has, so
			// it is a number, just an absurdly large one. Treat it as the ceiling rather than
			// discarding an edit the user clearly meant as "maximum".
			if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit))
			{
				SetDpiPointValue(point, QuantizeDpi(max, min, max, step));
				return;
			}

			RefreshDpiPointState();
			return;
		}

		SetDpiPointValue(point, QuantizeDpi(typed, min, max, step));
	}

	// Slider drag: fraction is 0..1 of the track, raised on every pointer move. This is a PREVIEW —
	// it moves the value in the UI and persists it through the debounced path, but deliberately does
	// NOT push to the device. The device apply is its own 250 ms debounce, and a real drag is full of
	// pauses longer than that (you slow down approaching the target), so applying here wrote every
	// value the thumb passed over while the button was still held. CommitCustomDpiDrag does the
	// device write, once, when the drag ends.
	internal void SetCustomDpiFraction(double fraction)
	{
		if (SelectedDpiPoint is not { } point) return;

		var (min, max, step) = DpiBounds();
		var value = min + (int)Math.Round(Math.Clamp(fraction, 0, 1) * (max - min));
		SetDpiPointValue(point, QuantizeDpi(value, min, max, step), debouncePersist: true, applyToDevice: false);
	}

	// The drag ended (pointer released, or capture lost). Everything the drag previewed is already in
	// the view model; this is what finally offers it to the hardware.
	internal void CommitCustomDpiDrag()
	{
		if (Device is null) return;
		StageChangeAndApply(DeviceApplyAspects.DpiStages | DeviceApplyAspects.Settings);
	}

	// Appends a point at the device's default DPI and selects it, so the customization block is
	// immediately pointed at what was just added.
	[RelayCommand]
	private void AddDpiPoint()
	{
		if (!CanAddDpiPoint || Device is not { } device) return;

		var (min, max, step) = DpiBounds();
		var seed = QuantizeDpi(device.Defaults.Dpi ?? min, min, max, step);

		var values = DpiPresets.Select(p => p.DpiValue).ToList();
		values.Add(seed);

		BuildDpiPresets(device, values, selectedIndex: values.Count - 1);
		_dpiStageKnown = true;
		PersistDpiLadderAndApply();
	}

	// Removes a point and renumbers the survivors. The device's stage table is positional, so the
	// active stage has to be re-clamped into the shorter table — otherwise the next apply would send
	// a stage index past its end.
	//
	// Removal is CONFIRMED, never immediate: the row's bin icon only arms the dialog, and nothing
	// touches the ladder until ConfirmDeleteDpiPoint runs. The pending row is what the host watches
	// to show the dialog, mirroring PendingDeleteMacro.
	[RelayCommand]
	private void RemoveDpiPoint(DpiPresetViewModel? point)
	{
		if (point is null || Device is null) return;
		if (DpiPresets.Count <= Device.DpiSlotMin) return;
		if (!DpiPresets.Contains(point)) return;

		PendingDeleteDpiPoint = point;
	}

	// The row the confirmation dialog is asking about; null when no dialog is open. Observable
	// because the window's dialog coordinator drives the overlay off its PropertyChanged.
	public DpiPresetViewModel? PendingDeleteDpiPoint
	{
		get => _pendingDeleteDpiPoint;
		private set => SetProperty(ref _pendingDeleteDpiPoint, value);
	}

	private DpiPresetViewModel? _pendingDeleteDpiPoint;

	[RelayCommand]
	private void CancelDeleteDpiPoint() => PendingDeleteDpiPoint = null;

	[RelayCommand]
	private void ConfirmDeleteDpiPoint()
	{
		var point = PendingDeleteDpiPoint;
		PendingDeleteDpiPoint = null;

		if (point is null || Device is not { } device) return;

		// Re-checked rather than trusted from the arming click: the ladder can have changed while
		// the dialog was open (a preset switch, a device reconnect rebuilding the rows).
		if (DpiPresets.Count <= device.DpiSlotMin) return;

		var index = DpiPresets.IndexOf(point);
		if (index < 0) return;

		var active = SelectedDpiPoint?.StageIndex ?? 0;
		var values = DpiPresets.Select(p => p.DpiValue).ToList();
		values.RemoveAt(index);

		// Removing the active point, or one below it, shifts what is active: land on the point that
		// took its place, and on the new last point when the tail was removed.
		var newActive = Math.Clamp(active > index ? active - 1 : active, 0, values.Count - 1);

		BuildDpiPresets(device, values, newActive);
		PersistDpiLadderAndApply();
	}

	// Writes one point's value, then persists and schedules the (debounced) device write.
	private void SetDpiPointValue(DpiPresetViewModel point, int value, bool debouncePersist = false, bool applyToDevice = true)
	{
		if (point.DpiValue == value) return;

		point.DpiValue = value;

		// Only when the edited row is still the selected one: a commit can land on the row the user
		// typed into after the selection has already moved elsewhere (see _editingDpiPoint), and the
		// box and slider belong to whatever is selected now.
		if (ReferenceEquals(point, SelectedDpiPoint))
		{
			OnPropertyChanged(nameof(CustomDpiPct));
			CustomDpiText = value.ToString(CultureInfo.CurrentCulture);
		}

		PersistDpiLadderAndApply(debouncePersist, applyToDevice);
	}

	// Persists the whole ladder (and the active stage), then marks the preset dirty.
	// StageChangeAndApply's own debounce coalesces a burst of edits into ONE all-blocks device write,
	// which is the only write shape BLAZE tolerates — a per-point write storm is what bricked its
	// buttons (docs/mouse/blaze/engineering-notes.md).
	//
	// `debounce` additionally coalesces the STORAGE write, for the slider's per-pointer-move stream.
	// Discrete edits (typing a value, adding, removing) persist immediately, so navigating away
	// right after one cannot lose it. Same 300 ms shape as the mouse-settings debounce above.
	//
	// `applyToDevice` is false for the slider's per-move preview: the value is staged and persisted,
	// but the hardware write waits for the drag to end (CommitCustomDpiDrag). The preset still goes
	// dirty, because that reflects the edit, not the write.
	private void PersistDpiLadderAndApply(bool debounce = false, bool applyToDevice = true)
	{
		if (Device is not { } device) return;

		_dpiLadderPersistCts?.Cancel();

		if (!debounce)
		{
			PersistDpiLadder();
		}
		else
		{
			var cts = new CancellationTokenSource();
			_dpiLadderPersistCts = cts;
			_ = DebouncePersistDpiLadderAsync(device, cts.Token);
		}

		if (applyToDevice)
			StageChangeAndApply(DeviceApplyAspects.DpiStages | DeviceApplyAspects.Settings);
		else
			StagePresetDirtyOnly();
	}

	// The device is captured before the delay and re-checked after it. Nothing cancels this pending
	// write when the shown device changes, so without the guard a drag on one mouse could land 300 ms
	// later while another is open — writing the first one's active stage under the second one's
	// settings key, an index past the end of its shorter table that the next open would then assert
	// to the hardware unclamped. Same guard LoadPersistedDeviceStateAsync uses after each of its
	// awaits.
	private async Task DebouncePersistDpiLadderAsync(DeviceCardViewModel device, CancellationToken token)
	{
		try { await Task.Delay(300, token); }
		catch (TaskCanceledException) { return; }
		if (!ReferenceEquals(Device, device)) return;
		PersistDpiLadder();

		// Re-stage the dirty flag now that the rows exist. HasUnsavedPresetChanges is computed by
		// comparing the PERSISTED settings against the preset's snapshot, and the caller staged it
		// 300 ms ago — when this edit had written nothing yet, so there was nothing to differ on and
		// Save Preset stayed dark for a drag that really did change the ladder. The device write is
		// not re-scheduled here: StageChangeAndApply already owns that, and its own debounce has
		// been coalescing the drag the whole time.
		StagePresetDirtyOnly();
	}

	// Both the ladder and the active stage are read HERE, from the live list, rather than passed in
	// by the caller: every caller's idea of the active stage is just "whatever BuildDpiPresets
	// selected", and a value captured before the delay can disagree with the ladder it is stored
	// beside — a preset switch mid-debounce shortens the list without changing the device.
	private void PersistDpiLadder()
	{
		if (_settings is null || Device is not { } device) return;

		var snapshot = new DpiPresetsSettings(
			DpiPresets.Select(p => new DpiPresetSetting(p.StageIndex, p.DpiValue, p.R, p.G, p.B)).ToList());
		_ = _settings.SaveDpiPresetsAsync(
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey, snapshot);

		if (_dpiStageKnown && SelectedDpiPoint?.StageIndex is { } stage)
			_ = _settings.SaveActiveDpiStageAsync(
				device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey, stage);
	}
}

// The "add point" cell of the DPI point grid. A marker type with no state: it exists so the add
// affordance can be an ITEM of the point flow, selected by DataType like the home grid's
// AddDeviceCardViewModel tile. The command lives on the details VM, because adding a point mutates
// that VM's ladder.
public sealed class AddDpiPointCellViewModel;
