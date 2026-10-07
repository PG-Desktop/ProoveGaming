using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

// The per-key assignment dialog ("Key Assignment Dialog", docs/plans/add-key-assignment-popup):
// clicking a key on the Key Assignments / Macros keyboard opens a modal with KEYS / SYSTEM /
// MACROS tabs that consolidates remapping and macro binding for that key. All state lives on
// this singleton because MainWindow's DialogOverlay wiring subscribes to it once (see
// RefreshDialogOverlay).
//
// The same dialog doubles as the Extended Keys output-key picker (docs/plans/add-extended-key-mod-tap):
// in PickExtendedKeyOutput purpose it opens on KEYS with no MACROS / SYSTEM tab and no RESET link,
// and a pick hands the key's matrix-record triple to a callback instead of staging a remap — no
// StageAssignmentTarget, no device write, no toast. The purpose is Remap unless a pick is in
// progress, and every close puts it back, so the remap path never sees the other mode.
public partial class DeviceDetailsViewModel
{
	// ── Backing fields ────────────────────────────────────────────────────────
	private bool _isKeyAssignmentDialogOpen;
	private KeyDialogTab _keyDialogTab = KeyDialogTab.Keys;
	private ObservableCollection<KeyAssignmentOptionViewModel> _remapTargetKeys = [];
	private KeyDialogPurpose _keyDialogPurpose = KeyDialogPurpose.Remap;
	// Pick purpose only: what to call with the chosen key, and the role the title names ("Hold").
	private Action<(byte TypeByte, byte Bitmask, byte ScanCode, string Label)>? _keyDialogPickCallback;
	private string _keyDialogPickRole = string.Empty;

	private enum KeyDialogTab { Keys, System, Macros }

	/// <summary>What a pick in the dialog does: remap the selected key (the default) or report a key to a caller.</summary>
	private enum KeyDialogPurpose { Remap, PickExtendedKeyOutput }

	// ── Properties ────────────────────────────────────────────────────────────
	/// <summary>
	/// True while the per-key assignment dialog is up. MainWindow drives the DialogOverlay from
	/// this the same way it does for the other DeviceDetails dialogs (see RefreshDialogOverlay).
	/// Closing ends a pick in progress: the callback is dropped here, whichever path closed the
	/// dialog, so a late result can never land in an editor that has since moved on.
	/// </summary>
	public bool IsKeyAssignmentDialogOpen
	{
		get => _isKeyAssignmentDialogOpen;
		private set
		{
			if (SetProperty(ref _isKeyAssignmentDialogOpen, value) && !value && IsKeyDialogPickPurpose)
				EndKeyDialogPick();
		}
	}

	public bool IsKeyDialogKeysTabActive => _keyDialogTab == KeyDialogTab.Keys;
	public bool IsKeyDialogSystemTabActive => _keyDialogTab == KeyDialogTab.System;
	public bool IsKeyDialogMacrosTabActive => _keyDialogTab == KeyDialogTab.Macros;

	public bool IsKeyDialogRemapPurpose => _keyDialogPurpose == KeyDialogPurpose.Remap;
	public bool IsKeyDialogPickPurpose => _keyDialogPurpose == KeyDialogPurpose.PickExtendedKeyOutput;

	/// <summary>The MACROS tab is a remap-purpose surface: an extended-key output is a key, never a macro.</summary>
	public bool IsKeyDialogMacrosTabVisible => IsKeyDialogRemapPurpose;

	/// <summary>
	/// The SYSTEM tab is gated by the family (<see cref="IsSystemCommandsSupported"/>) and by the
	/// purpose: an MT / TGL output is encoded as a HID keyboard usage or a modifier mask, and the
	/// firmware record has no code type for a consumer-page command, so the picker offers keys only.
	/// </summary>
	public bool IsKeyDialogSystemTabVisible => IsSystemCommandsSupported && IsKeyDialogRemapPurpose;

	public bool IsKeyDialogRemapKeysHintVisible => IsKeyDialogKeysTabActive && IsKeyDialogRemapPurpose;
	public bool IsKeyDialogPickKeysHintVisible => IsKeyDialogKeysTabActive && IsKeyDialogPickPurpose;

	/// <summary>"ESC button" in remap purpose; "Choose the Hold key" while picking an extended-key output.</summary>
	public string KeyDialogTitle => IsKeyDialogPickPurpose
		? string.Format(LocalizationManager.GetString("KeyDialog_PickTitle_Format"), _keyDialogPickRole)
		: SelectedPhysicalKeyWithSuffix;

	/// <summary>
	/// Gates the dialog's SYSTEM tab. Only the Smart Infinite family has a documented
	/// consumer-control record — <c>CON_TYPE 0x20</c> with the HID Consumer Page usage split
	/// little-endian across code1/code2 (vendor's own
	/// <c>docs/knowledge-base/rampage-keyboard-protocol/keyboard_key_define.h</c>:
	/// <c>CONSUME_MEDIA 0x0183 → CON_MEDIA_L 0x83, CON_MEDIA_H 0x01</c>), and the DeviceHost's
	/// table sanity check already accepts the type. Raster/T-Racer and T800 stay gated off until
	/// their firmware shows an equivalent.
	/// </summary>
	public bool IsSystemCommandsSupported => IsSmartInfiniteDevice;

	/// <summary>
	/// The SYSTEM tab's rows. The same instances also live in <see cref="AssignmentTargets"/>,
	/// which is what makes the whole machinery free: the whole-record selection match highlights
	/// a command-bound key, <c>SelectedKeyHasOverride</c> makes it resettable, and a pick commits
	/// through <see cref="CommitRemapPickAsync"/> like any other target.
	/// </summary>
	public ObservableCollection<KeyAssignmentOptionViewModel> SystemCommandTargets { get; } = [];

	// The Smart Infinite consumer record type — CON_TYPE in keyboard_key_define.h. (The header
	// is authoritative for this firmware line: it names 0x20 consumer and 0x30 system.)
	private const byte SmartInfiniteConsumerType = 0x20;

	// The command set, restricted to usages the vendor's own header ships defines for. "Back"
	// is AC Back (0x0224) — the browser/GUI back command of the HID Consumer Page.
	private static readonly (string Name, string LabelKey, ushort Usage)[] SystemCommands =
	[
		("VolumeUp", "Macros_Command_VolumeUp", 0x00E9),
		("VolumeDown", "Macros_Command_VolumeDown", 0x00EA),
		("VolumeMute", "Macros_Command_VolumeMute", 0x00E2),
		("PlayPause", "KeyDialog_Command_PlayPause", 0x00CD),
		("NextTrack", "KeyDialog_Command_NextTrack", 0x00B5),
		("PrevTrack", "KeyDialog_Command_PrevTrack", 0x00B6),
		("BrightnessUp", "Macros_Command_BrightnessUp", 0x006F),
		("BrightnessDown", "Macros_Command_BrightnessDown", 0x0070),
		("BrowserBack", "Macros_Command_BrowserBack", 0x0224),
	];

	/// <summary>
	/// Appends one assignment target per system command for the supported family. Called from
	/// <see cref="LoadKeyLayoutForDevice"/> after the key rows are built; the rows survive
	/// <c>RebuildMacroAssignmentTargets</c>, which only removes macro rows.
	/// </summary>
	private void RebuildSystemCommandTargets()
	{
		SystemCommandTargets.Clear();
		OnPropertyChanged(nameof(IsSystemCommandsSupported));
		if (!IsSystemCommandsSupported) return;

		foreach (var (name, labelKey, usage) in SystemCommands)
		{
			var row = new KeyAssignmentOptionViewModel
			{
				DevKeyName = $"Command:{name}",
				KeyLabel = LocalizationManager.GetString(labelKey),
				MatrixIndex = 0,
				CatalogMatrixIndex = 0,
				TypeByte = SmartInfiniteConsumerType,
				Bitmask = (byte)(usage & 0xFF),   // usage low byte (CON_*_L)
				ScanCode = (byte)(usage >> 8),    // usage high byte (CON_*_H)
				SelectCommand = PickRemapTargetCommand,
			};
			AssignmentTargets.Add(row);
			SystemCommandTargets.Add(row);
		}
	}

	/// <summary>
	/// The dialog's KEYS tab picker: the same catalog and canvas geometry as
	/// <see cref="PhysicalKeys"/>, but clicking a key means "remap the selected key to this one"
	/// rather than "select this key". A separate collection so the dialog's highlight (the key
	/// the selection resolves to) and the page's highlight (the selected key) cannot fight over
	/// one IsSelected flag.
	/// </summary>
	public ObservableCollection<KeyAssignmentOptionViewModel> RemapTargetKeys
	{
		get => _remapTargetKeys;
		private set => SetProperty(ref _remapTargetKeys, value);
	}

	// ── Commands ──────────────────────────────────────────────────────────────
	[RelayCommand]
	private void CloseKeyAssignmentDialog() => IsKeyAssignmentDialogOpen = false;

	[RelayCommand]
	private void SelectKeyDialogKeysTab() => SetKeyDialogTab(KeyDialogTab.Keys);

	[RelayCommand]
	private void SelectKeyDialogSystemTab() => SetKeyDialogTab(KeyDialogTab.System);

	[RelayCommand]
	private void SelectKeyDialogMacrosTab() => SetKeyDialogTab(KeyDialogTab.Macros);

	/// <summary>
	/// The dialog's RESET link: reverts the selected key to its factory record through the same
	/// path the shared header's Reset link takes, then closes the dialog. Gated like a pick —
	/// a reset issued while a previous write is still in flight would race it on the same slot
	/// (see <see cref="CommitRemapPickAsync"/>).
	/// </summary>
	[RelayCommand]
	private async Task ResetKeyFromDialog()
	{
		if (_assignmentWriteInFlight) return;
		await ResetSelectedKeyAsync();
		IsKeyAssignmentDialogOpen = false;
	}

	/// <summary>
	/// Routes a picker key to the matching <see cref="AssignmentTargets"/> row, so the dialog's
	/// visual picker stages or applies through exactly the path the old inline target list used —
	/// per-family staging vs. immediate write included.
	/// </summary>
	[RelayCommand]
	private Task PickRemapTarget(KeyAssignmentOptionViewModel? pickerKey)
	{
		if (pickerKey is null) return Task.CompletedTask;

		var target = AssignmentTargets.FirstOrDefault(
			t => !t.IsMacroTarget && t.DevKeyName == pickerKey.DevKeyName);
		if (target is null) return Task.CompletedTask;

		if (IsKeyDialogPickPurpose)
		{
			DeliverKeyDialogPick(target);
			return Task.CompletedTask;
		}

		return CommitRemapPickAsync(target);
	}

	// ── Pick purpose (Extended Keys output picker) ────────────────────────────
	/// <summary>
	/// Opens the dialog as a key picker: KEYS tab, title "Choose the {role} key", the picker key
	/// matching <paramref name="current"/> highlighted. A pick calls <paramref name="onPicked"/>
	/// with the key's matrix-record triple and label, then closes; the ✕ closes without calling.
	/// Nothing here or in the delivery touches the assignment maps or the device.
	/// </summary>
	internal void OpenKeyDialogForPick(
		string role,
		(byte TypeByte, byte Bitmask, byte ScanCode)? current,
		Action<(byte TypeByte, byte Bitmask, byte ScanCode, string Label)> onPicked)
	{
		if (!HasInteractiveKeyboardOverlay) return;

		HideRemapToast();
		_keyDialogPurpose = KeyDialogPurpose.PickExtendedKeyOutput;
		_keyDialogPickCallback = onPicked;
		_keyDialogPickRole = role;
		foreach (var key in RemapTargetKeys)
			key.IsSelected = current is { } c && key.TypeByte == c.TypeByte && key.Bitmask == c.Bitmask && key.ScanCode == c.ScanCode;
		SetKeyDialogTab(KeyDialogTab.Keys);
		RaiseKeyDialogPurposeChanged();
		IsKeyAssignmentDialogOpen = true;
	}

	// The pick lands: the callback runs with the dialog already closed (closing drops the callback,
	// so it is taken first), and the picker highlight goes back to the remap semantics.
	private void DeliverKeyDialogPick(KeyAssignmentOptionViewModel target)
	{
		var callback = _keyDialogPickCallback;
		IsKeyAssignmentDialogOpen = false;
		callback?.Invoke((target.TypeByte, target.Bitmask, target.ScanCode, target.KeyLabel));
	}

	private void EndKeyDialogPick()
	{
		_keyDialogPickCallback = null;
		_keyDialogPickRole = string.Empty;
		_keyDialogPurpose = KeyDialogPurpose.Remap;
		SyncRemapTargetPickerSelection();
		RaiseKeyDialogPurposeChanged();
	}

	private void RaiseKeyDialogPurposeChanged()
	{
		OnPropertyChanged(nameof(IsKeyDialogRemapPurpose));
		OnPropertyChanged(nameof(IsKeyDialogPickPurpose));
		OnPropertyChanged(nameof(IsKeyDialogMacrosTabVisible));
		OnPropertyChanged(nameof(IsKeyDialogSystemTabVisible));
		OnPropertyChanged(nameof(IsKeyDialogRemapKeysHintVisible));
		OnPropertyChanged(nameof(IsKeyDialogPickKeysHintVisible));
		OnPropertyChanged(nameof(KeyDialogTitle));
	}

	/// <summary>
	/// The one place a dialog pick lands — KEYS (click or typed), SYSTEM, and a MACROS-tab
	/// macro click (via <see cref="AssignMacroToSelectedKeyAsync"/>): closes the dialog
	/// optimistically, applies the target, and raises the undo toast. The toast still waits
	/// for the apply — on the immediate-write families "successfully remapped" must not
	/// precede the device write — and a refused apply puts the dialog back up, restoring the
	/// open dialog as the refusal signal (PR #101 M3).
	/// </summary>
	private async Task CommitRemapPickAsync(KeyAssignmentOptionViewModel target)
	{
		// A pick while the previous one is still writing must not start a second
		// read-modify-write on the same slot (PR #94 M2): the two HID exchanges finish in
		// arbitrary order, so the device could end up holding pick 1 while the bookkeeping
		// records pick 2 — and the next preset push would then skip the slot as "already
		// right". The flag is set before the first await and everything here runs on the UI
		// thread, so this early-out is race-free.
		if (_assignmentWriteInFlight) return;

		var key = SelectedPhysicalKey;
		if (key is null) return;

		// What the pick is about to displace, captured BEFORE the apply mutates the
		// override maps — this is what UNDO restores (PR #94 M1: undo means "put back what
		// was there", not "reset to factory"; resetting silently destroyed a previously
		// saved remap).
		var displaced = CaptureUndoTarget(key);

		// Close BEFORE the apply: on the immediate-write family the write takes ~0.5 s and a
		// dialog sitting frozen for that long reads as a hang (PG-381). Captured first so the
		// refusal path below can put back exactly what this pick took away — and so a dialog
		// the user RE-opens during the write is left alone when the pick succeeds.
		var wasOpen = IsKeyAssignmentDialogOpen;
		IsKeyAssignmentDialogOpen = false;

		var applied = await SelectAssignmentTargetCore(target);

		// The user switched keyboards while the write was in flight: the reload already closed
		// the dialog, and a toast offering to undo a key of the previous board would lie.
		if (!ReferenceEquals(SelectedPhysicalKey, key)) return;

		// Nothing was staged or written (read-only transport, refused device write): the
		// highlight has already been put back, so bring the dialog back and say nothing — a
		// "successfully remapped" toast here would be a lie (review r2-S1), and on the Key
		// Assignments section the reopened dialog is the ONLY visible refusal signal (the
		// macro radio list lives on the Macros section and the upload banner only covers
		// library-upload failures — PR #101 M3).
		if (!applied)
		{
			if (wasOpen) IsKeyAssignmentDialogOpen = true;
			return;
		}

		// A key/command pick over a macro-bound slot displaces the macro (a slot is either a
		// macro or a key record, never both — the apply already deleted the binding row), so the
		// Macros page radio list must drop the now-unbound row too. Without this the macro still
		// showed as assigned after "remap Z to Z", reading as "the macro never went away".
		RefreshMacroListSelection();
		ShowRemapToast(key, target, displaced);
	}

	/// <summary>
	/// Resolves the target row that is currently in effect for <paramref name="key"/> — the
	/// state a pick displaces and its UNDO must restore. Null means the key sits on its own
	/// (factory) record, in which case undo falls back to the plain reset.
	/// </summary>
	/// <remarks>
	/// Immediate-write family: the persisted override row wins (it is what the preset system
	/// asserts), else the device mirror — which for a macro-bound slot holds the macro record,
	/// so the match deliberately includes macro rows and undo re-binds the macro through the
	/// same full path (library upload included). Staging family: the staged override map; its
	/// macro bindings live in a separate slot-keyed table that key picks already clear, so the
	/// match is key rows only — the pre-existing reset semantics for those.
	/// </remarks>
	private KeyAssignmentOptionViewModel? CaptureUndoTarget(KeyAssignmentOptionViewModel key)
	{
		var slot = ResolveMatrixSlot(key);

		if (HasNoKeyOverrideLayer)
		{
			(byte Type, byte Code1, byte Code2)? record =
				_slotKeyOverrides is not null && _slotKeyOverrides.TryGetValue(slot, out var persisted)
					? persisted
					: _assignments is not null && _assignments.TryGetValue(slot, out var live)
						? live
						: null;
			if (record is null) return null;

			// The key's own record — nothing to restore beyond the reset fallback.
			if (record.Value.Type == key.TypeByte &&
			    record.Value.Code1 == key.Bitmask &&
			    record.Value.Code2 == key.ScanCode)
				return null;

			return AssignmentTargets.FirstOrDefault(t =>
				t.TypeByte == record.Value.Type &&
				t.Bitmask == record.Value.Code1 &&
				t.ScanCode == record.Value.Code2);
		}

		if (_stagedKeyOverrides is not null && _stagedKeyOverrides.TryGetValue(key.ScanCode, out var staged))
			return AssignmentTargets.FirstOrDefault(t =>
				!t.IsMacroTarget &&
				t.TypeByte == staged.TypeByte &&
				t.Bitmask == staged.Bitmask &&
				t.ScanCode == staged.TargetCode);
		return null;
	}

	/// <summary>
	/// Routes a key pressed on the PHYSICAL keyboard to a remap target while the dialog's KEYS
	/// tab is up — the type-to-remap alternative to clicking the on-screen picker. Returns true
	/// when the press was consumed, so the window's key handler can swallow the event. The KEYS
	/// tab gate doubles as the guard that keeps typing into the MACROS tab's search box from
	/// remapping anything.
	/// </summary>
	/// <remarks>
	/// Matching is per family: every catalog identifies a plain key by its HID usage, but the
	/// Smart Infinite boards identify modifiers by bitmask with usage 0x00 (see
	/// <see cref="KeyAssignmentOptionViewModel"/>), so a modifier usage falls back to the
	/// equivalent bitmask row when no usage row exists. Escape is deliberately a pick, not a
	/// dismiss — in a press-to-remap surface every key must stay bindable; the ✕ closes.
	/// </remarks>
	internal bool TryPickRemapTargetFromKeyboard(byte hidUsageCode)
	{
		if (!IsKeyAssignmentDialogOpen || !IsKeyDialogKeysTabActive)
			return false;

		// The pick purpose has no selected key and no write to race: the typed key is the output.
		if (IsKeyDialogPickPurpose)
		{
			var picked = FindTypedTarget(hidUsageCode);
			if (picked is null) return false;
			DeliverKeyDialogPick(picked);
			return true;
		}

		if (SelectedPhysicalKey is null) return false;

		// Mid-write presses are ignored, not queued (PR #94 M2) — see CommitRemapPickAsync.
		// False, not true: a press this method will not act on must not be swallowed either.
		if (_assignmentWriteInFlight) return false;

		var target = FindTypedTarget(hidUsageCode);
		if (target is null) return false;

		// The remap lives in the keyboard's firmware, so an overridden key EMITS its target: with
		// "1" remapped to F1, pressing physical "1" arrives here as F1 — indistinguishable from the
		// real F1 key. Re-picking the code the selected key already emits is a no-op either way, so
		// when the press matches the selected key's current record, read it as the key itself and
		// restore its own code — otherwise a remapped key could never be typed back to itself
		// (QA 2026-08-20: "press 1 to put 1 back" kept re-assigning F1).
		if (target.DevKeyName != SelectedPhysicalKey.DevKeyName && SelectedKeyCurrentlyEmits(target))
		{
			var self = AssignmentTargets.FirstOrDefault(
				t => !t.IsMacroTarget && t.DevKeyName == SelectedPhysicalKey.DevKeyName);
			if (self is not null) target = self;
		}

		// Same fire-and-forget the picker click takes through its command; the shared commit
		// closes the dialog and raises the undo toast.
		_ = CommitRemapPickAsync(target);
		return true;
	}

	// The target row a typed HID usage names, with the modifier fallback described above.
	private KeyAssignmentOptionViewModel? FindTypedTarget(byte hidUsageCode)
	{
		var target = AssignmentTargets.FirstOrDefault(
			t => !t.IsMacroTarget && t.ScanCode == hidUsageCode);
		if (target is null && hidUsageCode is >= HidModifierUsageFirst and <= HidModifierUsageLast)
		{
			var mask = (byte)(1 << (hidUsageCode - HidModifierUsageFirst));
			target = AssignmentTargets.FirstOrDefault(
				t => !t.IsMacroTarget && t.ScanCode == 0 && t.Bitmask == mask);
		}
		return target;
	}

	/// <summary>
	/// True when <paramref name="target"/> is the record the selected key's firmware slot currently
	/// holds — i.e. pressing the selected physical key produces exactly this target's code. Reads
	/// the device-truth maps, not the staged ones: the question is what the hardware emits today,
	/// and a staged-but-unapplied override does not change that.
	/// </summary>
	private bool SelectedKeyCurrentlyEmits(KeyAssignmentOptionViewModel target)
	{
		var key = SelectedPhysicalKey;
		if (key is null) return false;

		var slot = ResolveMatrixSlot(key);

		if (HasNoKeyOverrideLayer)
		{
			// The live device mirror is what the firmware emits; the persisted override row stands
			// in when the matrix read failed (it is what the last successful write put there).
			(byte Type, byte Code1, byte Code2)? record =
				_assignments is not null && _assignments.TryGetValue(slot, out var live)
					? live
					: _slotKeyOverrides is not null && _slotKeyOverrides.TryGetValue(slot, out var persisted)
						? persisted
						: null;
			return record is not null &&
			       record.Value.Type == target.TypeByte &&
			       record.Value.Code1 == target.Bitmask &&
			       record.Value.Code2 == target.ScanCode;
		}

		// Raster / T-Racer: the firmware's own override probe (slot → code2) is the emitted state.
		return _userOverrides is not null &&
		       _userOverrides.TryGetValue(slot, out var code) &&
		       code == target.ScanCode;
	}

	// HID keyboard usage range for modifiers (LCtrl..RWin); the bitmask bit is usage - first.
	private const byte HidModifierUsageFirst = 0xE0;
	private const byte HidModifierUsageLast = 0xE7;

	// ── Remap toast (undo notification) ───────────────────────────────────────
	// Raised when a pick closes the dialog: "<key> key successfully remapped to <target>" with
	// an UNDO link. Auto-hides after a few seconds via the same CTS + Task.Delay shape the
	// preset debounce uses (DebounceApplyAsync) — cancel-and-replace, so a second pick restarts
	// the window instead of racing the first one's hide.
	private bool _isRemapToastVisible;
	private string _remapToastText = string.Empty;
	private KeyAssignmentOptionViewModel? _remapToastKey;
	// The target the announced pick displaced — what UNDO puts back. Null = the key was on
	// its own record, so undo falls back to the reset.
	private KeyAssignmentOptionViewModel? _remapToastUndoTarget;
	private CancellationTokenSource? _remapToastCts;

	private const int RemapToastAutoHideMs = 5000;

	public bool IsRemapToastVisible
	{
		get => _isRemapToastVisible;
		private set => SetProperty(ref _isRemapToastVisible, value);
	}

	public string RemapToastText
	{
		get => _remapToastText;
		private set => SetProperty(ref _remapToastText, value);
	}

	[RelayCommand]
	private void DismissRemapToast() => HideRemapToast();

	/// <summary>
	/// The toast's UNDO: restores what the announced pick displaced — the previously bound
	/// target when there was one, the key's own record otherwise. Guarded on the selection
	/// still being the toast's key: selecting another key hides the toast (see
	/// <see cref="OpenKeyAssignmentDialog"/>), so this is defence against a stale click racing
	/// that hide, not a reachable everyday path.
	/// </summary>
	[RelayCommand]
	private async Task UndoRemap()
	{
		var key = _remapToastKey;
		var displaced = _remapToastUndoTarget;
		HideRemapToast();

		if (key is null || !ReferenceEquals(SelectedPhysicalKey, key)) return;

		if (displaced is not null)
		{
			await SelectAssignmentTargetCore(displaced);
			// A restored macro binding has to reflect in the Macros radio list too — the
			// immediate apply path does not refresh it itself (AssignMacroToSelectedKeyAsync
			// does this after the same call).
			RefreshMacroListSelection();
		}
		else
		{
			await ResetSelectedKeyAsync();
		}
	}

	private void ShowRemapToast(
		KeyAssignmentOptionViewModel key,
		KeyAssignmentOptionViewModel target,
		KeyAssignmentOptionViewModel? displaced) =>
		// A self-pick — the picker's own row, or the typed-echo redirect — restores the key
		// rather than remapping it, and "1 remapped to 1" reads as a bug, so it gets the reset
		// wording (PR #117 m2). The undo target is unchanged either way.
		ShowToastCore(key, displaced, key.DevKeyName == target.DevKeyName
			? string.Format(
				LocalizationManager.GetString("KeyDialog_ResetToast_Format"), key.KeyLabel)
			: string.Format(
				LocalizationManager.GetString("KeyDialog_RemapToast_Format"),
				key.KeyLabel, target.KeyLabel));

	/// <summary>
	/// Raised by the hover tip's unassign ✕: "<c>macro</c> unassigned from <c>key</c>", with
	/// UNDO re-binding the macro. Rides the remap toast's chrome and plumbing — the undo
	/// target is the macro's own assignment row, so UNDO takes the standard assignment path
	/// (library upload included) on every family.
	/// </summary>
	private void ShowMacroUnassignToast(KeyAssignmentOptionViewModel key, MacroViewModel macro) =>
		ShowToastCore(
			key,
			AssignmentTargets.FirstOrDefault(t => t.MacroId == macro.Id),
			string.Format(
				LocalizationManager.GetString("Macros_UnassignToast_Format"),
				macro.Name, key.KeyLabel));

	private void ShowToastCore(
		KeyAssignmentOptionViewModel key,
		KeyAssignmentOptionViewModel? undoTarget,
		string text)
	{
		_remapToastKey = key;
		_remapToastUndoTarget = undoTarget;
		RemapToastText = text;
		IsRemapToastVisible = true;

		_remapToastCts?.Cancel();
		var cts = new CancellationTokenSource();
		_remapToastCts = cts;
		_ = AutoHideRemapToastAsync(cts.Token);
	}

	private async Task AutoHideRemapToastAsync(CancellationToken token)
	{
		try { await Task.Delay(RemapToastAutoHideMs, token); }
		catch (TaskCanceledException) { return; }

		IsRemapToastVisible = false;
	}

	private void HideRemapToast()
	{
		_remapToastCts?.Cancel();
		_remapToastKey = null;
		_remapToastUndoTarget = null;
		IsRemapToastVisible = false;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────
	/// <summary>Opens the dialog for the just-selected key, always on its default KEYS tab.</summary>
	private void OpenKeyAssignmentDialog()
	{
		// A new selection ends the previous pick's undo window — UNDO resets the SELECTED key,
		// so a surviving toast would start lying the moment the selection moves.
		HideRemapToast();
		SetKeyDialogTab(KeyDialogTab.Keys);
		IsKeyAssignmentDialogOpen = true;
	}

	private void SetKeyDialogTab(KeyDialogTab tab)
	{
		if (_keyDialogTab == tab) return;
		_keyDialogTab = tab;
		OnPropertyChanged(nameof(IsKeyDialogKeysTabActive));
		OnPropertyChanged(nameof(IsKeyDialogSystemTabActive));
		OnPropertyChanged(nameof(IsKeyDialogMacrosTabActive));
		// The KEYS hints derive from the active tab too — without these the KEYS sentence
		// stays up on the SYSTEM / MACROS tab and the two hints render on top of each other.
		OnPropertyChanged(nameof(IsKeyDialogRemapKeysHintVisible));
		OnPropertyChanged(nameof(IsKeyDialogPickKeysHintVisible));
	}

	/// <summary>
	/// Mirrors the highlighted target row onto the picker keys, so the key the selection
	/// currently resolves to renders filled. Faithful to the family semantics the target list
	/// already has: on the immediate-apply boards a macro binding highlights a macro row, which
	/// has no picker key, so every picker key clears; on the staging boards the list falls back
	/// to the key's own row (macro rows are excluded there — see SyncAssignmentTargetSelection)
	/// and the picker follows it.
	/// </summary>
	private void SyncRemapTargetPickerSelection()
	{
		var selected = AssignmentTargets.FirstOrDefault(t => t.IsSelected && !t.IsMacroTarget);
		foreach (var key in RemapTargetKeys)
			key.IsSelected = selected is not null && key.DevKeyName == selected.DevKeyName;
	}
}
