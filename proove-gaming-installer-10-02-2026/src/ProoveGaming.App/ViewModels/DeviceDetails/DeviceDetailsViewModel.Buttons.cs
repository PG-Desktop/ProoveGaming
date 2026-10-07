using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Button remapping ───────────────────────────────────────────────────────
	// Current action per remappable physical button. The host is the source of truth
	// (the device matrix can't be read back); this mirrors it.
	private readonly Dictionary<MouseButton, MouseButtonAction> _buttonAssignments = new();
	// For buttons bound to a macro (action == Macro), the specific library macro. The host
	// store only records the action, so this in-session map is what lets us re-send the
	// definition and highlight the right catalog option.
	private readonly Dictionary<MouseButton, MacroViewModel> _buttonMacros = new();

	public bool IsButtonRemapSupported => Device?.SupportsButtonRemap == true;

	// Raised when a hotspot's current assignment is reflected, so the view can scroll
	// the highlighted option into view (e.g. a binding in the far-down System group).
	public event Action? ScrollToAssignedRequested;

	// ── Buttons search filter ──────────────────────────────────────────────────
	private string _buttonSearchText = string.Empty;
	public string ButtonSearchText
	{
		get => _buttonSearchText;
		set
		{
			if (SetProperty(ref _buttonSearchText, value))
				ApplyButtonSearch();
		}
	}

	// Filters the action catalog live as the user types. Options whose label doesn't
	// contain the query are hidden; groups with no matches are hidden, and groups with
	// matches are expanded so results are visible. Clearing the query restores everything.
	private void ApplyButtonSearch()
	{
		var query = _buttonSearchText?.Trim() ?? string.Empty;
		var searching = query.Length > 0;

		foreach (var group in ButtonActionGroups)
		{
			var anyVisible = false;
			foreach (var option in group.Options)
			{
				var match = !searching || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
				option.IsVisible = match;
				anyVisible |= match;
			}

			group.IsVisible = !searching || anyVisible;
			if (searching)
				group.IsExpanded = anyVisible;
		}
	}

	// Maps a hotspot id to its physical button; only these four are remappable
	// (left click and the DPI button are fixed).
	private static MouseButton HotspotIdToButton(string? id) => id switch
	{
		"right-click" => MouseButton.ButtonRight,
		"scroll-click" => MouseButton.ButtonWheel,
		"side-forward" => MouseButton.ButtonForward,
		"side-back" => MouseButton.ButtonBack,
		_ => MouseButton.ButtonUnspecified,
	};

	private static bool IsAssignableButton(MouseButton button) =>
		button is MouseButton.ButtonRight or MouseButton.ButtonWheel
			or MouseButton.ButtonForward or MouseButton.ButtonBack;

	private static Dictionary<MouseButton, MouseButtonAction> DefaultButtonAssignments() => new()
	{
		[MouseButton.ButtonRight] = MouseButtonAction.RightClick,
		[MouseButton.ButtonWheel] = MouseButtonAction.WheelClick,
		[MouseButton.ButtonForward] = MouseButtonAction.Forward,
		[MouseButton.ButtonBack] = MouseButtonAction.Back,
	};

	// Restores the remappable buttons to their factory functions — writes the device, persists
	// the defaults to the store, and re-reflects the UI. No-op for devices without remapping.
	internal void ResetButtonBindingsToDefault()
	{
		// Read-only transport: no binding write reaches the device or the database.
		if (!IsDeviceConfigurable) return;
		if (Device == null || !IsButtonRemapSupported) return;

		var owner = _ownerProvider?.GetCurrent();
		_buttonAssignments.Clear();
		_buttonMacros.Clear();
		foreach (var (button, action) in DefaultButtonAssignments())
		{
			_buttonAssignments[button] = action;
			if (_localStorage != null && owner is not null)
				_localStorage.SetButtonBinding(owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)button, (int)action, null);
		}

		if (SelectedHotspot != null)
			ReflectHotspotAssignment(SelectedHotspot);

		StageChangeAndApply();
	}

	// Loads the current device's saved macro library (SQLite) into Macros; adding populates the
	// catalog group. A macro belongs to the device it was recorded on, so the collection is
	// rebuilt on every device open rather than once per session. Any in-progress recording is
	// abandoned first: carrying it across would save it against whichever device happens to be
	// open at the end. The SQLite read runs off the calling thread; the populate is guarded so a
	// device switched mid-read doesn't receive the abandoned device's rows. The generation check
	// covers what ReferenceEquals cannot: reopening the SAME card (double-click) leaves Device
	// identical, yet the stale chain must not append its rows after the newer chain's clear.
	private async Task LoadMacroLibraryAsync(int generation)
	{
		StopRecording();
		SetEditingMacroId(0);
		IsCreateMacroOpen = false;
		ResetRecorder();

		// The rows the inspection radio lived on are about to be discarded; the key/hotspot
		// highlights must not outlive them.
		ClearMacroInspection();
		Macros.Clear();

		var device = Device;
		if (_localStorage == null || device is null) return;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		var records = await Task.Run(() => _localStorage.GetMacros(
			owner.Value.Kind, owner.Value.Id,
			device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey));

		if (generation != _setDeviceGeneration || !ReferenceEquals(Device, device)) return;

		foreach (var record in records)
			Macros.Add(WireMacro(MacroViewModel.FromRecord(record)));
	}

	// Attaches the kebab-menu + assign commands so each macro row can edit/remove itself and be
	// bound to the selected hotspot (Macros page).
	private MacroViewModel WireMacro(MacroViewModel macro)
	{
		macro.EditCommand = EditMacroCommand;
		macro.RemoveCommand = RequestDeleteMacroCommand;
		macro.AssignCommand = AssignMacroToSelectedButtonCommand;
		macro.InspectCommand = InspectMacroCommand;
		return macro;
	}

	private static MouseButtonAction DefaultActionFor(MouseButton button) =>
		DefaultButtonAssignments().TryGetValue(button, out var a) ? a : MouseButtonAction.Unspecified;

	// SetDeviceAsync's variant of LoadButtonBindings: the SQLite read runs off the calling
	// thread, and the apply is skipped when the chain went stale while the read was in flight —
	// the new open's own load owns the dictionaries then. Generation, not just ReferenceEquals:
	// see LoadMacroLibraryAsync.
	private async Task LoadButtonBindingsAsync(DeviceCardViewModel device, int generation)
	{
		// Owner resolved on the calling thread; the background block touches storage only.
		var owner = _ownerProvider?.GetCurrent();
		var rows = await Task.Run(() => FetchButtonBindingRows(device, owner));
		if (generation != _setDeviceGeneration || !ReferenceEquals(Device, device)) return;
		ApplyButtonBindingRows(device, rows);
	}

	// The preset apply's variant: runs inline on the calling thread, unconditionally — it targets
	// ONE device (the apply's captured card) regardless of what is on screen when it finishes, and
	// the device push that follows reads these dictionaries, so the rebuild must never be skipped.
	// Synchronous completion is also what the test suite's command-driven flows rely on.
	private void LoadButtonBindings(DeviceCardViewModel device) =>
		ApplyButtonBindingRows(device, FetchButtonBindingRows(device, _ownerProvider?.GetCurrent()));

	private List<(int Button, int Action, int? MacroId)> FetchButtonBindingRows(DeviceCardViewModel device, SettingsOwner? owner)
	{
		return device.SupportsButtonRemap && _localStorage != null && owner is not null
			? _localStorage.GetButtonBindings(
				owner.Value.Kind, owner.Value.Id,
				device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey)
			: [];
	}

	// Loads the device's stored assignments (the App store is the source of truth) and reflects
	// the currently-selected hotspot. Defaults fill any button not stored.
	private void ApplyButtonBindingRows(DeviceCardViewModel device, List<(int Button, int Action, int? MacroId)> rows)
	{
		var assignments = DefaultButtonAssignments();
		_buttonAssignments.Clear();
		_buttonMacros.Clear();

		foreach (var (buttonInt, actionInt, macroId) in rows)
		{
			var button = (MouseButton)buttonInt;
			var action = (MouseButtonAction)actionInt;
			if (!IsAssignableButton(button) || action == MouseButtonAction.Unspecified)
				continue;

			// A Macro binding is only meaningful when its macro is in THIS unit's library.
			// Preset snapshots are shared per device type, but the macro library is still
			// scoped per physical unit (GetMacros filters on device_key, unioning in only the
			// unit-agnostic '' scope the sign-in pull writes), so a preset saved on
			// one BLAZE can carry a macro_id that does not exist for another unit of the same
			// model. Keeping action = Macro with nothing resolved put (Macro, null) into
			// CurrentBindingSet; the device composer then skips that slot entirely
			// (X43KeyMatrix.EncodeAction has no Macro case), so the button silently kept its
			// factory function while the UI still showed it bound to a macro that isn't in the
			// list. Falling back to the button's default — which is what the hardware ends up
			// doing anyway — keeps UI, device and the dirty comparison in agreement.
			if (action == MouseButtonAction.Macro)
			{
				var macro = macroId.HasValue ? Macros.FirstOrDefault(m => m.Id == macroId.Value) : null;
				if (macro is null)
					continue;
				_buttonMacros[button] = macro;
			}

			assignments[button] = action;
		}

		foreach (var (button, action) in assignments)
			_buttonAssignments[button] = action;

		if (SelectedHotspot != null)
			ReflectHotspotAssignment(SelectedHotspot);

		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
	}

	// Highlights the single catalog option matching a hotspot's current assignment
	// (or clears all when the hotspot is fixed, e.g. left click).
	internal void ReflectHotspotAssignment(DevicePartHotspotViewModel? hotspot)
	{
		var button = HotspotIdToButton(hotspot?.Id);
		var action = MouseButtonAction.Unspecified;
		MacroViewModel? macro = null;
		if (IsAssignableButton(button) && _buttonAssignments.TryGetValue(button, out var a))
		{
			action = a;
			_buttonMacros.TryGetValue(button, out macro);
		}
		HighlightAssignedOption(action, macro);
	}

	// Highlights the catalog option matching an assignment. For a macro the action alone is
	// ambiguous (every macro shares Action == Macro), so the specific macro disambiguates.
	private void HighlightAssignedOption(MouseButtonAction action, MacroViewModel? macro = null)
	{
		var anyMatch = false;
		foreach (var group in ButtonActionGroups)
		{
			var groupHasMatch = false;
			foreach (var option in group.Options)
			{
				var isMatch = action != MouseButtonAction.Unspecified
					&& option.Action == action
					&& (action != MouseButtonAction.Macro || ReferenceEquals(option.Macro, macro));
				option.IsSelected = isMatch;
				groupHasMatch |= isMatch;
			}

			// Open ONLY the group that holds the current assignment (collapse the rest), so the
			// panel opens focused on where the button is actually bound.
			group.IsExpanded = groupHasMatch;

			anyMatch |= groupHasMatch;
		}

		// Ask the view to scroll the highlighted option into view.
		if (anyMatch)
			ScrollToAssignedRequested?.Invoke();
	}

	private IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> CurrentBindingSet() =>
		_buttonAssignments
			.Select(kv => (kv.Key, kv.Value,
				kv.Value == MouseButtonAction.Macro && _buttonMacros.TryGetValue(kv.Key, out var m)
					? ToProtoMacro(m)
					: null))
			.ToList();

	// Builds the gRPC macro payload sent inline with a Macro binding.
	private static MacroDefinition ToProtoMacro(MacroViewModel macro)
	{
		var def = new MacroDefinition
		{
			Name = macro.Name,
			RepeatCount = (uint)macro.RepeatCount,
			Mode = macro.Mode,
		};
		foreach (var e in macro.Events)
			def.Events.Add(new MacroEvent
			{
				KeyCode = e.KeyCode,
				IsPress = e.IsPress,
				DelayMs = (uint)e.DelayMs,
			});
		return def;
	}

	// Same conversion from the stored library record, for the connect-time reapply which has no
	// MacroViewModel to hand (it runs for a device that is not the one open).
	private static MacroDefinition ToProtoMacro(MacroRecord macro)
	{
		var def = new MacroDefinition
		{
			Name = macro.Name,
			RepeatCount = (uint)macro.RepeatCount,
			Mode = macro.Mode,
		};
		foreach (var e in macro.Events)
			def.Events.Add(new MacroEvent
			{
				KeyCode = e.KeyCode,
				IsPress = e.IsPress,
				DelayMs = (uint)e.DelayMs,
			});
		return def;
	}

	// ── Buttons / Macros backing fields ───────────────────────────────────────
	// Cached reference so we can keep its Options in sync with Macros without scanning ButtonActionGroups every time.
	private readonly ButtonActionGroupViewModel _macrosesGroup;
	private const string MacrosesOptionsGroupName = "DeviceDetailsButtonsGroupMacrosesOptions";
	private bool   _isCreateMacroOpen;
	private string _newMacroName              = string.Empty;
	private string _newMacroRepetitions       = string.Empty;
	private bool   _newMacroAutoRecordDelays  = true;
	private bool   _newMacroRepeatUntilReleased;

	// Steps captured by the recorder for the macro currently being created (see the recorder partial).
	private readonly List<MacroEventRecord> _recordedEvents = new();

	// Non-zero while the Create Macro dialog is editing an existing macro (its library id).
	private int _editingMacroId;

	public bool IsEditingMacro => _editingMacroId > 0;
	public string MacroDialogTitle => LocalizationManager.GetString(
		IsEditingMacro ? "Macros_Dialog_EditTitle" : "Macros_Dialog_CreateTitle");
	public string MacroDialogActionLabel => LocalizationManager.GetString(
		IsEditingMacro ? "Macros_Dialog_Save" : "Macros_Dialog_Create");

	private void SetEditingMacroId(int id)
	{
		_editingMacroId = id;
		OnPropertyChanged(nameof(IsEditingMacro));
		OnPropertyChanged(nameof(MacroDialogTitle));
		OnPropertyChanged(nameof(MacroDialogActionLabel));
	}

	// The macro awaiting delete confirmation; non-null shows the confirm dialog.
	private MacroViewModel? _pendingDeleteMacro;
	public MacroViewModel? PendingDeleteMacro
	{
		get => _pendingDeleteMacro;
		private set
		{
			if (SetProperty(ref _pendingDeleteMacro, value))
				OnPropertyChanged(nameof(MacroDeleteMessage));
		}
	}

	public string MacroDeleteMessage => string.Format(
		LocalizationManager.GetString("Macros_Delete_Body"), _pendingDeleteMacro?.Name ?? string.Empty);

	// ── Macros/Commands tab state ──────────────────────────────────────────────
	// VM-driven (not RadioButton GroupName) so the default reliably sticks — grouped
	// RadioButtons drop their XAML IsChecked as siblings register with the group coordinator.
	private bool _isMacrosTabActive = true;
	public bool IsMacrosTabActive
	{
		get => _isMacrosTabActive;
		set
		{
			if (SetProperty(ref _isMacrosTabActive, value))
				OnPropertyChanged(nameof(IsCommandsTabActive));
		}
	}

	public bool IsCommandsTabActive => !_isMacrosTabActive;

	// ── Macros search filter ───────────────────────────────────────────────────
	private string _macroSearchText = string.Empty;
	public string MacroSearchText
	{
		get => _macroSearchText;
		set
		{
			if (SetProperty(ref _macroSearchText, value))
				ApplyMacroSearch();
		}
	}

	// Filters the macro list live by name as the user types; clearing restores all.
	private void ApplyMacroSearch()
	{
		var query = _macroSearchText?.Trim() ?? string.Empty;
		var searching = query.Length > 0;

		foreach (var macro in Macros)
			macro.IsVisible = !searching || macro.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
	}

	[RelayCommand]
	private void SelectMacrosTab() => IsMacrosTabActive = true;

	[RelayCommand]
	private void SelectCommandsTab() => IsMacrosTabActive = false;

	// ── Properties ───────────────────────────────────────────────────────────
	public bool IsCreateMacroOpen
	{
		get => _isCreateMacroOpen;
		set => SetProperty(ref _isCreateMacroOpen, value);
	}

	public string NewMacroName
	{
		get => _newMacroName;
		set => SetProperty(ref _newMacroName, value);
	}

	public string NewMacroRepetitions
	{
		get => _newMacroRepetitions;
		// The view sanitizes the field on TextChanged; this is a cheap safety net so any
		// programmatic/pasted value stored here is digits-only as well.
		set => SetProperty(ref _newMacroRepetitions, DigitsOnly(value));
	}

	// The repetitions field accepts digits only; strip anything that isn't a digit.
	private static string DigitsOnly(string? value)
	{
		if (string.IsNullOrEmpty(value))
			return string.Empty;

		Span<char> digits = stackalloc char[value.Length];
		var count = 0;
		foreach (var c in value)
		{
			if (char.IsAsciiDigit(c))
				digits[count++] = c;
		}

		return count == value.Length ? value : new string(digits[..count]);
	}

	public bool NewMacroAutoRecordDelays
	{
		get => _newMacroAutoRecordDelays;
		set => SetProperty(ref _newMacroAutoRecordDelays, value);
	}

	public bool NewMacroRepeatUntilReleased
	{
		get => _newMacroRepeatUntilReleased;
		set => SetProperty(ref _newMacroRepeatUntilReleased, value);
	}

	// ── Commands ──────────────────────────────────────────────────────────────
	[RelayCommand]
	private void OpenCreateMacro()
	{
		SetEditingMacroId(0);
		NewMacroName              = string.Empty;
		NewMacroRepetitions       = string.Empty;
		NewMacroAutoRecordDelays  = true;
		NewMacroRepeatUntilReleased = false;
		ResetRecorder();
		IsCreateMacroOpen         = true;
	}

	// Reopens the Create Macro dialog populated with an existing macro's data + recorded steps;
	// saving updates that library entry instead of adding a new one.
	[RelayCommand]
	private void EditMacro(MacroViewModel macro)
	{
		if (macro is null) return;

		SetEditingMacroId(macro.Id);
		NewMacroName                = macro.Name;
		NewMacroRepetitions         = macro.Repetitions > 0 ? macro.Repetitions.ToString() : string.Empty;
		NewMacroAutoRecordDelays    = macro.AutoRecordDelays;
		NewMacroRepeatUntilReleased = macro.RepeatUntilReleased;

		ResetRecorder();
		var editClickCount = 0;
		var editPressByCode = new Dictionary<byte, int>();
		foreach (var e in macro.Events)
		{
			_recordedEvents.Add(e);

			int iteration;
			if (e.IsPress)
			{
				iteration = ++editClickCount;
				editPressByCode[e.KeyCode] = iteration;
			}
			else if (!editPressByCode.Remove(e.KeyCode, out iteration))
			{
				iteration = editClickCount;
			}

			var (isKeyboard, label) = DescribeMacroCode(e.KeyCode);
			RecordedSteps.Add(new RecordedStepViewModel
			{
				IsKeyboard = isKeyboard,
				Label = label,
				DelayMs = e.DelayMs,
				IsPress = e.IsPress,
				Iteration = iteration,
				RemoveCommand = RemoveRecordedStepCommand,
			});
		}
		OnPropertyChanged(nameof(HasRecordedSteps));

		IsCreateMacroOpen = true;
	}

	// ── Remove macro (kebab → Remove → confirm) ─────────────────────────────────
	[RelayCommand]
	private void RequestDeleteMacro(MacroViewModel macro)
	{
		if (macro != null)
			PendingDeleteMacro = macro;
	}

	[RelayCommand]
	private void CancelDeleteMacro() => PendingDeleteMacro = null;

	[RelayCommand]
	private void ConfirmDeleteMacro()
	{
		var macro = PendingDeleteMacro;
		PendingDeleteMacro = null;
		if (macro is null) return;

		// A deleted macro cannot stay inspected — its highlights would outlive its bindings.
		if (ReferenceEquals(_inspectedMacro, macro))
			ClearMacroInspection();

		// Remove from the library and drop any button rows (any device) still pointing at it.
		_localStorage?.DeleteMacro(macro.Id);
		_localStorage?.DeleteButtonBindingsForMacro(macro.Id);
		// Same for keyboard key rows. Deleting a macro also shifts every later macro's device
		// slot, so the remaining bindings are re-sent below rather than left pointing at the
		// slot that macro used to occupy.
		_localStorage?.DeleteKeyMacroBindingsForMacro(macro.Id);
		InvalidateMacroBindingCache();
		Macros.Remove(macro); // rebuilds the catalog's Macros group via CollectionChanged

		// Keyboard: re-upload the (now shorter) library and re-write the slots still bound, so
		// no key replays the wrong macro.
		_ = ResyncKeyMacroBindingsAsync();

		// Revert any current-device button bound to this macro back to its default and re-write.
		var affected = _buttonMacros.Where(kv => ReferenceEquals(kv.Value, macro)).Select(kv => kv.Key).ToList();
		if (affected.Count == 0) return;

		var owner = _ownerProvider?.GetCurrent();
		foreach (var button in affected)
		{
			_buttonMacros.Remove(button);
			_buttonAssignments[button] = DefaultActionFor(button);
			if (Device != null && _localStorage != null && owner is not null)
				_localStorage.SetButtonBinding(owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)button, (int)DefaultActionFor(button), null);
		}

		if (SelectedHotspot != null)
			ReflectHotspotAssignment(SelectedHotspot);

		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
		StageChangeAndApply();
	}

	[RelayCommand]
	private void CancelCreateMacro()
	{
		StopRecording();
		IsCreateMacroOpen = false;
	}

	[RelayCommand]
	private void SaveCreateMacro()
	{
		var name = NewMacroName?.Trim();
		if (string.IsNullOrEmpty(name)) return;

		int.TryParse(NewMacroRepetitions, out var repetitions);

		var macro = WireMacro(new MacroViewModel
		{
			Id                  = _editingMacroId,
			Name                = name,
			Repetitions         = repetitions,
			AutoRecordDelays    = NewMacroAutoRecordDelays,
			RepeatUntilReleased = NewMacroRepeatUntilReleased,
			Events              = [.. _recordedEvents],
		});

		// Persist first so the row id is assigned (insert) or reused (update) before the catalog references it.
		// The macro is stamped with the device that is open, which is the device it belongs to.
		var owner = _ownerProvider?.GetCurrent();
		if (_localStorage != null && owner is not null && Device is not null)
			macro.Id = _localStorage.SaveMacro(
				owner.Value.Kind, owner.Value.Id,
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey,
				macro.ToRecord());

		if (_editingMacroId > 0)
			ReplaceEditedMacro(macro);
		else
			Macros.Add(macro);

		SetEditingMacroId(0);
		StopRecording();
		IsCreateMacroOpen = false;
	}

	// Swaps the edited macro's row for the updated instance and re-links any button bound to it
	// (so the change reflects and the device gets the new definition).
	private void ReplaceEditedMacro(MacroViewModel macro)
	{
		var old = Macros.FirstOrDefault(m => m.Id == macro.Id);
		if (old is null)
		{
			Macros.Add(macro);
			return;
		}

		Macros[Macros.IndexOf(old)] = macro;

		// The edit swapped the instance the inspection radio may be holding — carry it over, or
		// the highlights would answer for a macro that is no longer in the list (the refresh at
		// the bottom re-derives them against the new instance).
		if (ReferenceEquals(_inspectedMacro, old))
		{
			_inspectedMacro = macro;
			macro.IsInspected = true;
		}

		var rebound = _buttonMacros.Where(kv => ReferenceEquals(kv.Value, old)).Select(kv => kv.Key).ToList();
		foreach (var button in rebound)
			_buttonMacros[button] = macro;

		if (rebound.Count > 0)
			StageChangeAndApply();

		// A keyboard needs the same push, by a different route: its macro rides in an indexed
		// library rather than inline on a binding, so the edited events only reach the device when
		// the whole library is re-uploaded. Without this the App shows the new recording while the
		// key keeps playing the old one, until some unrelated action happens to trigger a resync.
		// Self-gating on macro support and idempotent, so it is safe to call unconditionally.
		_ = ResyncKeyMacroBindingsAsync();

		if (SelectedHotspot != null)
			ReflectHotspotAssignment(SelectedHotspot);

		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
	}

	[RelayCommand]
	private void CreateMacro() { }

	[RelayCommand]
	private void AssignButtonAction(string actionName) { }

	[RelayCommand]
	private void SelectButtonAction(ButtonActionOptionViewModel option)
	{
		// Read-only transport: no binding write reaches the device or the database.
		if (!IsDeviceConfigurable) return;
		if (option is null) return;

		// Non-binding options just select within their group.
		if (option.Action == MouseButtonAction.Unspecified)
		{
			ButtonActionGroups.FirstOrDefault(g => g.Options.Contains(option))?.SelectOption(option);
			return;
		}

		// Assigning requires a remappable hotspot on a device that supports remapping.
		var button = HotspotIdToButton(SelectedHotspot?.Id);
		if (!IsAssignableButton(button) || Device == null || _usbHidDeviceService == null || !IsButtonRemapSupported)
		{
			// Restore the correct highlight so a stray click (no button selected) doesn't stick.
			ReflectHotspotAssignment(SelectedHotspot);
			return;
		}

		_buttonAssignments[button] = option.Action;
		int? macroId = null;
		if (option.Action == MouseButtonAction.Macro && option.Macro != null)
		{
			_buttonMacros[button] = option.Macro;
			macroId = option.Macro.Id;
		}
		else
		{
			_buttonMacros.Remove(button);
		}

		HighlightAssignedOption(option.Action, option.Macro);

		// Stage into the App store (the source of truth); StageChangeAndApply below marks the
		// preset dirty and (debounced) pushes it to the device.
		var owner = _ownerProvider?.GetCurrent();
		if (_localStorage != null && owner is not null)
			_localStorage.SetButtonBinding(owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)button, (int)option.Action, macroId);

		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
		StageChangeAndApply();
	}

	// ── Macro → button assignment (Macros page) ─────────────────────────────────
	// Binds the clicked macro to the currently-selected hotspot's button. Same store + device path
	// as assigning a Macro action on the Buttons page. A no-op (that re-syncs the list so a stray
	// radio doesn't stick) when no assignable button is selected.
	[RelayCommand]
	private void AssignMacroToSelectedButton(MacroViewModel macro)
	{
		// Read-only transport: no binding write reaches the device or the database. The radio has
		// already toggled itself (TwoWay), so the list must be re-synced like every other early
		// exit below — otherwise the row stays checked (now with a dead ✕ on it) for a binding
		// that was never made.
		if (!IsDeviceConfigurable)
		{
			RefreshMacroListSelection();
			return;
		}
		// Keyboards bind a macro to the selected key instead of a mouse button. Routed through
		// the key-assignment path so the library upload, the matrix write and the
		// key_macro_bindings row all happen in one place.
		if (IsKeyboardDevice)
		{
			_ = AssignMacroToSelectedKeyAsync(macro);
			return;
		}

		var button = HotspotIdToButton(SelectedHotspot?.Id);
		if (macro is null || !IsAssignableButton(button) || Device == null
			|| _usbHidDeviceService == null || !IsButtonRemapSupported)
		{
			RefreshMacroListSelection();
			return;
		}

		_buttonAssignments[button] = MouseButtonAction.Macro;
		_buttonMacros[button] = macro;

		var owner = _ownerProvider?.GetCurrent();
		if (_localStorage != null && owner is not null)
			_localStorage.SetButtonBinding(owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)button, (int)MouseButtonAction.Macro, macro.Id);

		ReflectHotspotAssignment(SelectedHotspot);
		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
		StageChangeAndApply();
	}

	// Whether the header's Reset button is enabled for the selected hotspot. The rule differs by
	// section: the Buttons page resets any non-default assignment; the Macros page only works on
	// macro bindings, so Reset enables there only when a macro is currently assigned.
	public bool CanResetSelectedButton
	{
		get
		{
			var button = HotspotIdToButton(SelectedHotspot?.Id);
			if (!IsAssignableButton(button)) return false;
			var current = _buttonAssignments.TryGetValue(button, out var a) ? a : DefaultActionFor(button);
			return IsMacrosSection
				? current == MouseButtonAction.Macro
				: current != DefaultActionFor(button);
		}
	}

	// The Reset button in the selected-button header (Buttons + Macros pages): reverts just the
	// selected button to its factory default.
	[RelayCommand]
	private void ResetSelectedButton() => ResetButtonToDefault(HotspotIdToButton(SelectedHotspot?.Id));

	// The ✕ on a Macros-page hotspot: reverts that specific button to its factory default.
	[RelayCommand]
	private void UnassignHotspot(DevicePartHotspotViewModel hotspot)
		=> ResetButtonToDefault(HotspotIdToButton(hotspot?.Id));

	// Reverts a single button to its factory function: clears any macro, persists the default,
	// re-writes the device, and re-syncs the catalog highlight, hotspot badges and macro list.
	private void ResetButtonToDefault(MouseButton button)
	{
		// Read-only transport: no binding write reaches the device or the database.
		if (!IsDeviceConfigurable) return;
		if (!IsAssignableButton(button) || Device == null || !IsButtonRemapSupported) return;

		var action = DefaultActionFor(button);
		_buttonAssignments[button] = action;
		_buttonMacros.Remove(button);

		var owner = _ownerProvider?.GetCurrent();
		if (_localStorage != null && owner is not null)
			_localStorage.SetButtonBinding(owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)button, (int)action, null);

		ReflectHotspotAssignment(SelectedHotspot);
		RefreshHotspotMacroBadges();
		RefreshMacroListSelection();
		StageChangeAndApply();
	}

	// Macros page: reflect which buttons are bound to a macro on their hotspots (macro name on a
	// second line + ✕). Only shows while the Macros section is active — the Buttons page keeps
	// every hotspot single-line.
	private void RefreshHotspotMacroBadges()
	{
		foreach (var hotspot in PartHotspots)
		{
			var button = HotspotIdToButton(hotspot.Id);
			var hasMacro = IsAssignableButton(button)
				&& _buttonAssignments.TryGetValue(button, out var action)
				&& action == MouseButtonAction.Macro
				&& _buttonMacros.TryGetValue(button, out _);

			hotspot.AssignedMacroName = hasMacro ? _buttonMacros[button].Name : null;
			hotspot.ShowAssignedMacro = hasMacro && IsMacrosSection;
		}
	}

	// Macros page: check the macro row bound to the selected hotspot's button (or none), so the
	// list radio mirrors the current binding.
	private void RefreshMacroListSelection()
	{
		MacroViewModel? assigned;

		// A keyboard has no hotspot — its binding hangs off the selected physical key — so this
		// resolved `assigned` to null for every keyboard and cleared every radio. The Macros page
		// therefore never showed which macro a key was bound to, on any keyboard.
		if (IsKeyboardDevice)
		{
			assigned = BoundMacroForSelectedKey();
		}
		else
		{
			var button = HotspotIdToButton(SelectedHotspot?.Id);
			assigned = null;
			if (IsAssignableButton(button))
				_buttonMacros.TryGetValue(button, out assigned);
		}

		foreach (var macro in Macros)
			macro.IsSelected = ReferenceEquals(macro, assigned);

		// The inspection highlight is derived from the same bindings this method mirrors, and
		// this method already runs after every path that mutates them — so hooking the refresh
		// here is what keeps a highlighted key honest through assigns and resets without the
		// user re-clicking the row.
		RefreshMacroBindingHighlights();

		// Keep the header's Reset enablement in step with the current binding. Both names are
		// raised: the Buttons page still binds CanResetSelectedButton, while the shared Macros
		// header binds the CanResetSelectedMacroTarget dispatcher. Raising only the former left
		// the header stale after every assign and reset.
		OnPropertyChanged(nameof(CanResetSelectedButton));
		NotifySelectedMacroTargetChanged();
	}

	// ── Macro inspection (Macros page) ──────────────────────────────────────────
	// The list's radio answers "which keys/buttons play this macro?": checking a row highlights
	// every binding of that macro on the device image. Pure inspection — assignment still lives
	// in the Key Assignment Dialog (keyboards) and the Buttons section (mice).
	private MacroViewModel? _inspectedMacro;

	[RelayCommand]
	private void InspectMacro(MacroViewModel? macro)
	{
		if (macro is null) return;

		_inspectedMacro = macro;
		foreach (var m in Macros)
			m.IsInspected = ReferenceEquals(m, macro);

		// Drop the leftover key selection, or it eclipses the answer: right after assigning,
		// the bound key IS still the selected key (the dialog leaves it that way), and the
		// stronger .selected fill outranks .macroBound — so checking the radio looked like
		// nothing happened on exactly the key the user just bound. Inspection is a new focus;
		// with the selection gone the board lights precisely the macro's keys.
		if (IsKeyboardDevice && SelectedPhysicalKey is not null)
		{
			// The undo toast acts on the SELECTED key; with the selection gone its UNDO would
			// silently no-op, so it must not keep offering it (same rule as OpenKeyAssignmentDialog).
			HideRemapToast();
			foreach (var key in PhysicalKeys)
				key.IsSelected = false;
			SelectedPhysicalKey = null;
			// Covers the macro-row IsSelected mirror, the shared header and the highlight
			// refresh in one call — the same bundle every selection change goes through.
			RefreshMacroListSelection();
			return;
		}

		RefreshMacroBindingHighlights();
	}

	// Drops the inspection radio and every highlight it produced. Called when the macro list
	// reloads (device switch), the inspected macro is deleted, and when the user leaves the
	// Macros section — a highlight surviving any of those would point at bindings that are no
	// longer on screen.
	private void ClearMacroInspection()
	{
		if (_inspectedMacro is null) return;

		_inspectedMacro = null;
		foreach (var m in Macros)
			m.IsInspected = false;
		RefreshMacroBindingHighlights();
	}

	// Maps the inspected macro onto the per-key / per-hotspot highlight flags. With nothing
	// inspected this is a cheap all-false sweep (the null check short-circuits before any
	// binding lookup, and SetProperty no-ops on already-false flags).
	private void RefreshMacroBindingHighlights()
	{
		var inspected = _inspectedMacro;

		if (IsKeyboardDevice)
		{
			foreach (var key in PhysicalKeys)
				key.IsMacroBindingHighlighted =
					inspected is not null && BoundMacroForKey(key)?.Id == inspected.Id;
			return;
		}

		foreach (var hotspot in PartHotspots)
		{
			var button = HotspotIdToButton(hotspot.Id);
			hotspot.IsMacroBindingHighlighted =
				inspected is not null &&
				IsAssignableButton(button) &&
				_buttonMacros.TryGetValue(button, out var macro) &&
				ReferenceEquals(macro, inspected);
		}
	}

	// ── Helpers ───────────────────────────────────────────────────────────────
	private void SyncMacrosesGroupOptions()
	{
		_macrosesGroup.Options.Clear();
		foreach (var macro in Macros)
		{
			_macrosesGroup.Options.Add(new ButtonActionOptionViewModel
			{
				Label         = macro.Name,
				GroupName     = MacrosesOptionsGroupName,
				Action        = MouseButtonAction.Macro,
				Macro         = macro,
				IsSelected    = false,
				SelectCommand = SelectButtonActionCommand
			});
		}

		// A newly-added macro can be the one bound to the shown hotspot; re-reflect so its
		// fresh option highlights (options are recreated on every sync).
		if (SelectedHotspot != null)
			ReflectHotspotAssignment(SelectedHotspot);

		RefreshMacroListSelection();
	}

	// Builds a group whose options each carry a bindable MouseButtonAction.
	private ButtonActionGroupViewModel CreateActionGroup(
		string titleKey, bool isExpanded, (string labelKey, MouseButtonAction action)[] items)
	{
		var groupName = $"{titleKey.Replace(" ", string.Empty)}Options";
		var group = new ButtonActionGroupViewModel
		{
			Title = LocalizationManager.GetString(titleKey),
			TitleKey = titleKey,
			IsExpanded = isExpanded,
		};

		foreach (var (labelKey, action) in items)
		{
			group.Options.Add(new ButtonActionOptionViewModel
			{
				Label = LocalizationManager.GetString(labelKey),
				LabelKey = labelKey,
				GroupName = groupName,
				Action = action,
				IsSelected = false,
				SelectCommand = SelectButtonActionCommand,
			});
		}

		return group;
	}

	private static ButtonActionGroupViewModel CreateGroup(
		string titleKey, bool isExpanded, string[] optionKeys,
		IRelayCommand<ButtonActionOptionViewModel> selectCommand)
	{
		var groupName = $"{titleKey.Replace(" ", string.Empty)}Options";
		var group = new ButtonActionGroupViewModel
		{
			Title      = LocalizationManager.GetString(titleKey),
			TitleKey   = titleKey,
			IsExpanded = isExpanded
		};

		foreach (var key in optionKeys)
		{
			var isResourceKey = key.StartsWith("DeviceDetails_", StringComparison.Ordinal);
			group.Options.Add(new ButtonActionOptionViewModel
			{
				Label         = isResourceKey ? LocalizationManager.GetString(key) : key,
				LabelKey      = isResourceKey ? key : null,
				GroupName     = groupName,
				IsSelected    = false,
				SelectCommand = selectCommand
			});
		}

		if (group.Options.Count > 0)
			group.Options[0].IsSelected = true;

		return group;
	}
}
