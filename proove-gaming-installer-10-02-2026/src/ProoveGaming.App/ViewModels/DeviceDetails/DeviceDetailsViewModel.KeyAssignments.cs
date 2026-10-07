using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Services;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── Constants ─────────────────────────────────────────────────────────────
	// Type byte for a plain HID keyboard record (see RasterKeyboardKeyMappingService).
	private const byte KbTypeByte = 0x10;

	// RY5088 (T800) stores a plain keyboard key as type 0x00, not 0x10 — read from the device's own
	// factory table, see docs/plans/add-t800-key-assignments/notes.md. Resolved per device by
	// KeyboardTypeByte rather than used directly.
	private const byte T800KbTypeByte = 0x00;

	// ── Backing fields ────────────────────────────────────────────────────────
	// Populated for the current device (empty for non-remappable devices).
	private ObservableCollection<KeyAssignmentOptionViewModel> _physicalKeys = [];
	private ObservableCollection<KeyAssignmentOptionViewModel> _assignmentTargets = [];
	private KeyAssignmentOptionViewModel? _selectedPhysicalKey;

	// HID scancode → real matrix slot on this specific keyboard's firmware, populated by
	// runtime probe. Falls back to catalog's MatrixIndex if a scancode isn't in the map.
	private IReadOnlyDictionary<byte, byte>? _matrixMap;

	// The device's CURRENT override state as read from the probe: slot → override code2.
	// Raster / T-Racer only (Cyrex/Aress use _assignments below, which is unstaged/live). Used as
	// the diff baseline when pushing staged overrides to the hardware (see
	// ApplyKeyOverridesToDeviceAsync) — the UI truth for those two families is the staged state
	// below, not this probe.
	private Dictionary<byte, byte>? _userOverrides;

	// App-truth staged key overrides (from SQLite key_bindings) for Raster / T-Racer, keyed by the
	// physical key's HID scancode. The UI reflects these; the device is written from them on Save
	// Preset / preset switch (ApplyKeyOverridesToDeviceAsync), not on selection. Cyrex/Aress and
	// macro bindings are a separate, unstaged, immediate-write path (see SelectAssignmentTarget) —
	// they predate the preset system and are not scoped by it.
	private Dictionary<byte, (byte TypeByte, byte Bitmask, byte TargetCode)>? _stagedKeyOverrides;

	// Cyrex equivalent: slot -> the full {type, code1, code2} record currently in the firmware.
	// This family has no factory-vs-override split (its base matrix IS the live state), and modifier
	// keys carry their identity in code1 with a 0x00 scancode, so a code2-only map cannot represent
	// the device state. Same policy though: the device is the truth source, SQLite is not consulted
	// for what the firmware HOLDS — but see _slotKeyOverrides for what the active preset WANTS.
	private Dictionary<byte, (byte Type, byte Code1, byte Code2)>? _assignments;

	// The immediate-write family's (Smart Infinite / T800) persisted key overrides, mirroring this
	// device's key_bindings rows. That table is scancode-keyed for Raster/T-Racer, but this
	// family's modifiers all share scancode 0x00, so its rows are keyed by MATRIX SLOT instead —
	// stored in the same phys_scancode column; the keyings never mix because a device is always
	// one family. Persisted at all so Save Preset snapshots the remap and Discard / preset switch
	// has something to put back — the immediate device write itself leaves no trace the preset
	// system can see, which is exactly how an unsaved remap used to survive a Discard.
	private Dictionary<byte, (byte Type, byte Code1, byte Code2)>? _slotKeyOverrides;

	// Slots this session wrote a non-default key record to, same job as _appliedMacroSlots below:
	// a preset apply reloads the page state (nulling _assignments) without re-reading the matrix,
	// so if the re-read fails the revert push would otherwise not know which slots still hold an
	// abandoned remap that must be written back to default.
	//
	// Deliberately NOT cleared by LoadKeyLayoutForDevice, for _appliedMacroSlots' reason: this
	// tracks what the DEVICE holds. Cleared on device change.
	private HashSet<byte>? _appliedSlotOverrides;

	// Matrix slots this session last wrote a macro record to. Needed because
	// ResyncKeyMacroBindingsAsync is otherwise additive — it writes the bindings that exist and has
	// no way to know a slot USED to hold one. Removing a binding (Reset, or a preset revert that
	// deletes the row straight out of SQLite) would then leave the macro playing on the keyboard
	// with nothing in the App claiming it. ApplyKeyOverridesToDeviceAsync cannot cover this either:
	// its diff baseline is _userOverrides, which LoadKeyLayoutForDevice nulls just before a preset
	// apply pushes.
	//
	// Deliberately NOT cleared by LoadKeyLayoutForDevice: this tracks what the DEVICE holds, and
	// that reload happens on every preset apply for the same keyboard. Cleared on device change.
	private HashSet<byte>? _appliedMacroSlots;

	// matrix slot -> macro id for the current device, mirroring key_macro_bindings. Null means
	// "not loaded"; every mutation sets it back to null rather than trying to patch it.
	//
	// Exists because BoundMacroForSelectedKey feeds SelectedKeyHasOverride and
	// RefreshMacroListSelection, both of which run on every key selection — and each call was an
	// indexed SQLite read taken under LocalStorageService._lock, on the UI thread, several times
	// per click.
	private Dictionary<byte, int>? _macroBindingsBySlot;

	private bool _keyAssignmentReadFailed;
	private bool _macroUploadFailed;

	// Bumped every time a device page loads. The matrix read is started fire-and-forget and takes about
	// a second over gRPC and real HID, so a user switching devices inside that window has two reads in
	// flight; the slower one must not land its result — or its failure flag — into the state that now
	// belongs to a different keyboard. Cyrex and Aress differ in only three slots, and the highlight
	// matches on (type, mask, usage) rather than slot, so a late landing shows up as exactly those
	// navigation keys being wrong: quiet, plausible, and hard to trace back.
	private int _keyLayoutGeneration;

	/// <summary>
	/// False once <see cref="LoadKeyLayoutForDevice"/> has run again since <paramref name="generation"/>
	/// was captured. Any async method that lands a <b>device read</b> into the shared key-assignment
	/// state must capture <see cref="_keyLayoutGeneration"/> before the await and check it through this —
	/// the read takes about a second, and a re-load has already reset the state it would write into.
	/// </summary>
	/// <remarks>
	/// Deliberately <b>not</b> the guard for writes. A re-load is not the same event as "the user is now
	/// looking at a different keyboard": it also fires for a card refresh or a preset apply on the same
	/// device. Discarding a write's bookkeeping on that leaves the firmware written and SQLite not — see
	/// the capture block in <see cref="ApplyAssignmentTargetAsync"/>, which compares device identity.
	/// </remarks>
	private bool IsCurrentKeyLayout(int generation) => generation == _keyLayoutGeneration;

	// ── Properties ────────────────────────────────────────────────────────────
	/// <summary>
	/// True when the live key matrix could not be read from the device. The overlay then falls back to
	/// each key's factory assignment, which is a guess — if the user had remapped anything, what they
	/// see is wrong. Showing that silently is worse than showing an error, so the view surfaces this.
	/// </summary>
	public bool KeyAssignmentReadFailed
	{
		get => _keyAssignmentReadFailed;
		private set => SetProperty(ref _keyAssignmentReadFailed, value);
	}

	/// <summary>
	/// True when the macro library could not be written to (or verified on) the keyboard. The bind
	/// is deliberately skipped when the upload fails — a key must never point into a library the
	/// board does not hold — so without this flag a refused upload reads as "clicking a macro does
	/// nothing". The Macros page surfaces it the same way <see cref="KeyAssignmentReadFailed"/> is
	/// surfaced on Key Assignments.
	/// </summary>
	public bool MacroUploadFailed
	{
		get => _macroUploadFailed;
		private set => SetProperty(ref _macroUploadFailed, value);
	}

	/// <summary>
	/// All clickable overlays on the on-screen keyboard image (device-dependent). Empty for
	/// non-remappable devices.
	/// </summary>
	public ObservableCollection<KeyAssignmentOptionViewModel> PhysicalKeys
	{
		get => _physicalKeys;
		private set => SetProperty(ref _physicalKeys, value);
	}

	/// <summary>
	/// All possible replacement targets shown as radio rows on the left panel.
	/// </summary>
	public ObservableCollection<KeyAssignmentOptionViewModel> AssignmentTargets
	{
		get => _assignmentTargets;
		private set => SetProperty(ref _assignmentTargets, value);
	}

	public KeyAssignmentOptionViewModel? SelectedPhysicalKey
	{
		get => _selectedPhysicalKey;
		private set
		{
			if (SetProperty(ref _selectedPhysicalKey, value))
			{
				OnPropertyChanged(nameof(SelectedPhysicalKeyLabel));
				OnPropertyChanged(nameof(SelectedPhysicalKeyWithSuffix));
				OnPropertyChanged(nameof(KeyDialogTitle));
				NotifySelectedMacroTargetChanged();
			}
		}
	}

	public string? SelectedPhysicalKeyLabel => SelectedPhysicalKey?.KeyLabel;
	public string SelectedPhysicalKeyWithSuffix => SelectedPhysicalKey is null
		? string.Empty
		: string.Format(
			LocalizationManager.GetString("KeyDialog_Title_Format"), SelectedPhysicalKey.KeyLabel);

	/// <summary>
	/// True when the connected device is one we know how to render as an interactive
	/// on-screen keyboard for key remapping. Currently Raster and T-Racer share the same
	/// vendor family (VID 14000) and the same interactive overlay rendering path.
	/// </summary>
	public bool HasInteractiveKeyboardOverlay =>
		Device is not null && (
			RasterKeyCatalog.IsRaster(Device.VendorId, Device.ProductId) ||
			RasterKeyCatalog.IsRaster(Device.Name) ||
			TRacerKeyCatalog.IsTRacer(Device.VendorId, Device.ProductId) ||
			TRacerKeyCatalog.IsTRacer(Device.Name) ||
			IsSmartInfiniteDevice ||
			IsT800Device);

	/// <summary>
	/// Split chassis/keycap artwork for the connected keyboard, or null if it only has the
	/// flat single-layer image. Drives the live backlight preview.
	/// </summary>
	public Models.RgbKeyboardArt? KeyboardArt =>
		Models.RgbKeyboardArtCatalog.For(Device?.Name);

	public bool HasRgbKeyboardArt => KeyboardArt is not null;

	/// <summary>
	/// The Macros section renders the same clickable keyboard as Key Assignments, so a macro
	/// can be bound to a key from there. Every other keyboard section shows the static image,
	/// which is hit-test-invisible and therefore offers nothing to click.
	/// </summary>
	public bool ShowKeyboardOverlayInMacros => IsMacrosSection && HasInteractiveKeyboardOverlay;

	/// <summary>
	/// The Actuation section's Per-key mode needs the same clickable keyboard to pick which keys
	/// the sliders edit; All-keys mode keeps the static image.
	/// </summary>
	public bool ShowKeyboardOverlayInActuation =>
		IsActuationSection && ActuationPerKeyMode && HasInteractiveKeyboardOverlay;

	/// <summary>
	/// The Extended Keys section always shows the clickable keyboard: the editor picks its keys on
	/// it, and the configured records highlight on it even with no editor open.
	/// </summary>
	public bool ShowKeyboardOverlayInExtendedKeys => IsExtendedKeysSection && HasInteractiveKeyboardOverlay;

	/// <summary>
	/// The layered board's backlight preview is for the lighting pages; Actuation and Extended Keys
	/// are about switch behaviour and render the same unlit board Key Assignments and Macros show.
	/// </summary>
	public bool ShowKeyboardBacklight => !IsActuationSection && !IsExtendedKeysSection;

	/// <summary>
	/// What the Macros page's selected-target header shows. The Buttons page is mouse-only, but
	/// Macros serves both device types, so this resolves to whichever selection the current
	/// device actually has — a mouse hotspot or a keyboard key.
	/// </summary>
	public string? SelectedMacroTargetLabel =>
		IsKeyboardDevice ? SelectedPhysicalKeyLabel : SelectedHotspotLabel;

	public bool HasSelectedMacroTarget =>
		IsKeyboardDevice ? SelectedPhysicalKey is not null : SelectedHotspot is not null;

	/// <summary>Reset link visibility for the shared header — a key is always resettable.</summary>
	public bool IsSelectedMacroTargetResettable =>
		IsKeyboardDevice ? SelectedPhysicalKey is not null : IsSelectedButtonResettable;

	/// <summary>
	/// Reset link enablement. For a key this is "the firmware currently holds an override for
	/// this slot" — the same condition the target list uses to show a non-default selection.
	/// </summary>
	public bool CanResetSelectedMacroTarget =>
		IsKeyboardDevice ? SelectedKeyHasOverride : CanResetSelectedButton;

	/// <summary>
	/// Raises all four shared-header properties together. They are computed from the current
	/// selection AND from what that target is currently bound to, so anything that changes either
	/// has to announce all four — the two selection setters, the macro-list refresh, and the
	/// assignment write. Kept as one method because the four-line block was previously copied into
	/// each caller, and the copy that was never added is exactly how the header's Reset button
	/// stopped updating after an assignment: the header binds
	/// <see cref="CanResetSelectedMacroTarget"/>, while the macro-list refresh was still raising
	/// only the older <c>CanResetSelectedButton</c> that the header no longer listens to.
	/// </summary>
	private void NotifySelectedMacroTargetChanged()
	{
		OnPropertyChanged(nameof(SelectedMacroTargetLabel));
		OnPropertyChanged(nameof(HasSelectedMacroTarget));
		OnPropertyChanged(nameof(IsSelectedMacroTargetResettable));
		OnPropertyChanged(nameof(CanResetSelectedMacroTarget));
	}

	// Checked against both truth sources: the staged preset override (Raster/T-Racer — what will
	// be pushed on next apply) and the live device probe (Cyrex/Aress, and the pre-staging
	// fallback) — either one holding a non-default value means the key is reset-able.
	private bool SelectedKeyHasOverride
	{
		get
		{
			if (SelectedPhysicalKey is null) return false;

			// An assignment write is in flight: what the stores say right now is mid-transition,
			// and a reset issued against it races the write (round 3 M1). The flag's two
			// NotifySelectedMacroTargetChanged() calls re-raise this property at both ends.
			if (_assignmentWriteInFlight) return false;

			// Staged preset overrides outrank both device maps: they are what the next apply will
			// push, so a key the user has already restaged is reset-able even if the firmware still
			// holds the factory record. Applies to every family.
			if (_stagedKeyOverrides is not null && _stagedKeyOverrides.ContainsKey(SelectedPhysicalKey.ScanCode))
				return true;

			// A macro binding is an override too, but it lives in key_macro_bindings and never
			// appears in _stagedKeyOverrides — so without this the Reset link stayed greyed out for
			// a key that plays a macro, and the macro could be assigned or replaced but never
			// removed. Now that macro bindings are staged, the device maps below cannot stand in
			// for it either: a freshly assigned macro is not on the keyboard yet. This is the same
			// gap the Smart Infinite branch below already closes for Cyrex and Aress.
			if (BoundMacroForSelectedKey() is not null)
				return true;

			var slot = ResolveMatrixSlot(SelectedPhysicalKey);

			// This family has no factory-vs-override map — its live table IS the state — so "has an
			// override" is "the record differs from what the key ships as". Compare the whole record
			// for the same reason SyncAssignmentTargetSelection does: every modifier key has scancode
			// 0x00 and is told apart only by its bitmask. Without this branch the shared header's
			// Reset link stayed permanently greyed out on Cyrex and Aress, so a macro bound to a key
			// could be replaced but never unassigned.
			if (HasNoKeyOverrideLayer)
			{
				// The persisted override row counts even before the device read lands — it is
				// what the next push will (re-)assert.
				if (_slotKeyOverrides is not null && _slotKeyOverrides.ContainsKey(slot))
					return true;
				if (_assignments is null || !_assignments.TryGetValue(slot, out var record)) return false;
				return record.Type != SelectedPhysicalKey.TypeByte ||
				       record.Code1 != SelectedPhysicalKey.Bitmask ||
				       record.Code2 != SelectedPhysicalKey.ScanCode;
			}

			if (_userOverrides is null) return false;
			return _userOverrides.TryGetValue(slot, out var code) && code != SelectedPhysicalKey.ScanCode;
		}
	}

	/// <summary>
	/// Binds a library macro to the currently selected physical key. Never a write path of
	/// its own: with the Key Assignment Dialog open the pick lands in
	/// <see cref="CommitRemapPickAsync"/> like a KEYS pick, otherwise it goes through
	/// <see cref="SelectAssignmentTarget"/> — either way it stages or writes identically to
	/// picking the same macro in the target list (the two used to diverge here, with this
	/// one always writing immediately).
	/// </summary>
	private async Task AssignMacroToSelectedKeyAsync(MacroViewModel macro)
	{
		if (macro is null || SelectedPhysicalKey is null || !IsMacrosSupported)
		{
			RefreshMacroListSelection();
			return;
		}

		// Macro targets are rebuilt whenever the library changes, so this resolves against the
		// current slot ordering rather than a captured index.
		var target = AssignmentTargets.FirstOrDefault(t => t.MacroId == macro.Id);
		if (target is null)
		{
			RefreshMacroListSelection();
			return;
		}

		// From the dialog's MACROS tab a pick commits like a KEYS/SYSTEM pick (PG-381: the
		// dialog staying open with no feedback read as "nothing happened"): the commit closes
		// the dialog optimistically before the ~0.5 s apply, re-opens it if the write is
		// refused, and raises the undo toast only after success. The dialog is the only place
		// a keyboard macro is picked now (the Macros page list is display-only), so the
		// in-place branch below is a guard for a direct call, not a UI path.
		if (IsKeyAssignmentDialogOpen)
			await CommitRemapPickAsync(target);
		else
			await SelectAssignmentTarget(target);
		RefreshMacroListSelection();
	}

	/// <summary>
	/// Single reset entry point for the shared header, dispatching to whichever selection the
	/// current device has.
	/// </summary>
	[RelayCommand]
	private Task ResetSelectedMacroTarget()
	{
		if (IsKeyboardDevice)
			return ResetSelectedKeyAsync();

		ResetSelectedButton();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Reverts the selected key to the keycode it ships with. Cyrex/Aress take the same immediate
	/// device write as picking any other target (device is the truth source there); Raster/T-Racer
	/// go through the staged preset path so a reset doesn't get silently re-applied by the next
	/// Save Preset / preset switch reading a stale staged override.
	/// </summary>
	private async Task ResetSelectedKeyAsync()
	{
		if (SelectedPhysicalKey is null) return;

		var self = AssignmentTargets.FirstOrDefault(
			t => !t.IsMacroTarget && t.DevKeyName == SelectedPhysicalKey.DevKeyName);
		if (self is null)
		{
			// Catalog/target-list divergence: the selected key has no own-keycode row to reset to.
			// This exit is reachable straight from the list's unassign ✕ now, so it must leave a
			// trace — "pressed ✕, nothing happened" is undiagnosable otherwise.
			_logger?.LogWarning(
				"[KEY] Reset skipped: no self target for key '{DevKeyName}' (scancode 0x{ScanCode:X2})",
				SelectedPhysicalKey.DevKeyName, SelectedPhysicalKey.ScanCode);
			return;
		}

		if (HasNoKeyOverrideLayer)
			await ApplyAssignmentTargetAsync(self);
		else
			StageAssignmentTarget(self);

		SyncAssignmentTargetSelection();

		// The staged path refreshes the macro radio list inside StageAssignmentTarget; the
		// immediate path above does not, which left the just-unbound macro's row checked — and its
		// unassign (✕) visible — until the next selection change.
		RefreshMacroListSelection();
	}

	// The two static-image branches, suppressed while the interactive overlay is up so the
	// board is not drawn twice (the overlay renders its own art layer).
	public bool ShowLayeredKeyboardImage => HasRgbKeyboardArt && !ShowKeyboardOverlayInMacros && !ShowKeyboardOverlayInActuation && !ShowKeyboardOverlayInExtendedKeys;
	public bool ShowFlatKeyboardImage => !HasRgbKeyboardArt && !ShowKeyboardOverlayInMacros && !ShowKeyboardOverlayInActuation && !ShowKeyboardOverlayInExtendedKeys;

	private bool IsTRacerDevice =>
		Device is not null && (
			TRacerKeyCatalog.IsTRacer(Device.VendorId, Device.ProductId) ||
			TRacerKeyCatalog.IsTRacer(Device.Name));

	private bool IsRasterDevice =>
		Device is not null && (
			RasterKeyCatalog.IsRaster(Device.VendorId, Device.ProductId) ||
			RasterKeyCatalog.IsRaster(Device.Name));

	/// <summary>
	/// The artwork the Key Assignments / Macros overlay draws its keycap boxes on — the board's
	/// own artwork, with bounds measured from it (see <see cref="ArtworkKeyOverlayLayout"/>).
	/// </summary>
	/// <remarks>
	/// Kept separate from <see cref="KeyboardArt"/> even though every board now resolves both to
	/// the same file: the overlay contract is "the artwork the measured bounds were taken from",
	/// and history shows the two can diverge (Aress rendered Cyrex's artwork here until its own
	/// artwork got a measured table).
	/// </remarks>
	public Models.RgbKeyboardArt? KeyOverlayArt => KeyboardArt;

	/// <summary>
	/// Extra darkening scrim for the interactive overlay's artwork, 0 = none. The Rampage
	/// artwork's keycaps are saturated violet-blue even unlit (its physical caps are), so the
	/// #6357FF selection box blended straight into them on Key Assignments / Macros (PG-494);
	/// a partial black scrim keeps the board recognisable while letting the selection read.
	/// Per-board like every other presentation value, so it comes from the device catalog
	/// (DeviceHost appsettings — 0.35 for Rampage); boards whose unlit art is already dark
	/// (Cyrex, Aress, Raster, T-Racer, T800) declare nothing and keep 0.
	/// </summary>
	public double KeyOverlayArtDim => Device?.KeyOverlayArtDim ?? 0.0;

	/// <summary>
	/// Measured per-keycap bounds for the artwork the overlay renders, or null when there are none.
	/// </summary>
	/// <remarks>
	/// The invariant is "we have bounds measured from the artwork we are about to draw", not "this
	/// board has artwork". Several boards ship split artwork for the RGB preview without a measured
	/// table — resolving on artwork alone would lay one board's keycap positions over a differently
	/// shaped one.
	///
	/// Aress now has its own measured table (<see cref="AressArtworkLayout"/>) so the overlay draws
	/// the Aress artwork, not Cyrex's. The artwork's navigation column still differs from the
	/// physical board — see the remarks on that class for the one legend that does not match its
	/// slot (HOME carries Insert).
	/// </remarks>
	private ArtworkKeyLayout? ArtworkKeyOverlayLayout
	{
		get
		{
			if (KeyOverlayArt is null) return null;
			if (IsRasterDevice)
				return new ArtworkKeyLayout(
					RasterArtworkLayout.ViewBoxWidth, RasterArtworkLayout.ViewBoxHeight, RasterArtworkLayout.Layout);
			// Keyed by name rather than by matrix slot: T-Racer's catalog names are unique and its
			// table was measured from its own artwork, so the slot join Aress needs does not apply.
			if (IsTRacerDevice)
				return new ArtworkKeyLayout(
					TRacerArtworkLayout.ViewBoxWidth, TRacerArtworkLayout.ViewBoxHeight, TRacerArtworkLayout.Layout);
			// Same shape as T-Racer: Rampage's table was measured from its own artwork and its
			// catalog names are unique, so no slot join is needed.
			if (IsRampageDevice)
				return new ArtworkKeyLayout(
					RampageArtworkLayout.ViewBoxWidth, RampageArtworkLayout.ViewBoxHeight, RampageArtworkLayout.Layout);
			if (IsAressDevice)
				return new ArtworkKeyLayout(
					AressArtworkLayout.ViewBoxWidth, AressArtworkLayout.ViewBoxHeight,
					AressArtworkLayout.LayoutFor(AressKeyCatalog.Keys));
			if (IsCyrexDevice)
				return new ArtworkKeyLayout(
					CyrexArtworkLayout.ViewBoxWidth, CyrexArtworkLayout.ViewBoxHeight,
					CyrexArtworkLayout.LayoutFor(CyrexKeyCatalog.Keys));
			// Joined by slot, like Cyrex's: the T800 matrix is column-major, so its slot numbers do
			// not run in the visual order the catalog is written in.
			if (IsT800Device)
				return new ArtworkKeyLayout(
					T800ArtworkLayout.ViewBoxWidth, T800ArtworkLayout.ViewBoxHeight,
					T800ArtworkLayout.LayoutFor(T800KeyCatalog.Keys));
			return null;
		}
	}

	/// <summary>
	/// True when the key overlays can be drawn on the rendered board rather than on a bare canvas.
	/// </summary>
	public bool UseArtworkKeyOverlays => ArtworkKeyOverlayLayout is not null;

	/// <summary>Artwork viewBox the overlay stage is sized to; 0 when there is no artwork layout.</summary>
	public double ArtworkViewBoxWidth => ArtworkKeyOverlayLayout?.ViewBoxWidth ?? 0;

	public double ArtworkViewBoxHeight => ArtworkKeyOverlayLayout?.ViewBoxHeight ?? 0;

	/// <summary>Key name → measured bounds for the current board, empty when there is no layout.</summary>
	public IReadOnlyDictionary<string, RasterKeyBounds> ArtworkKeyBounds =>
		ArtworkKeyOverlayLayout?.Bounds ?? EmptyKeyBounds;

	private static readonly IReadOnlyDictionary<string, RasterKeyBounds> EmptyKeyBounds =
		new Dictionary<string, RasterKeyBounds>();

	// One board's measured artwork geometry: the viewBox the bounds are expressed in, and the
	// bounds themselves already joined against that board's catalog.
	private sealed record ArtworkKeyLayout(
		double ViewBoxWidth,
		double ViewBoxHeight,
		IReadOnlyDictionary<string, RasterKeyBounds> Bounds);

	/// <summary>
	/// Cyrex is a different vendor family from Raster / T-Racer: the Smart Infinite protocol, a
	/// 65% layout on a 75-slot matrix, and slots that come from the catalog rather than a runtime
	/// probe. The name fallback matters because a device reported without VID/PID still has to
	/// render.
	/// </summary>
	private bool IsCyrexDevice =>
		Device is not null && (
			CyrexKeyCatalog.IsCyrex(Device.VendorId, Device.ProductId) ||
			CyrexKeyCatalog.IsCyrex(Device.Name));

	private bool IsAressDevice =>
		Device is not null && (
			AressKeyCatalog.IsAress(Device.VendorId, Device.ProductId) ||
			AressKeyCatalog.IsAress(Device.Name));

	/// <summary>
	/// The two 65% Smart Infinite boards. They share the protocol, the read path, the 75-slot matrix
	/// and the physical geometry — only three navigation keys are labelled differently, so each keeps
	/// its own catalog while everything else is common.
	/// </summary>
	private bool IsSmartInfinite65Device => IsCyrexDevice || IsAressDevice;

	/// <summary>
	/// Rampage is the third Smart Infinite board — a TKL on a 128-slot matrix rather than a 65% on
	/// 75 slots. The name fallback matters here too: a Bluetooth-connected Rampage arrives with
	/// VID = PID = 0 because <c>BluetoothDeviceResponse</c> carries no identifiers.
	/// </summary>
	private bool IsRampageDevice =>
		Device is not null && (
			RampageKeyCatalog.IsRampage(Device.VendorId, Device.ProductId) ||
			RampageKeyCatalog.IsRampage(Device.Name));

	/// <summary>
	/// Every Smart Infinite board. They share the family behaviour — slot-keyed device-truth read,
	/// whole-record matching, exact catalog slots, immediate writes — while the 65%-only predicate
	/// above additionally selects the shared Cyrex geometry and artwork, which Rampage does not use.
	/// </summary>
	private bool IsSmartInfiniteDevice => IsSmartInfinite65Device || IsRampageDevice;

	/// <summary>
	/// T800 — a magnetic-axis 65% board on the Rongyuan RY5088 MCU, a third protocol family
	/// alongside Smart Infinite and Raster.
	/// </summary>
	private bool IsT800Device =>
		Device is not null && (
			T800KeyCatalog.IsT800(Device.VendorId, Device.ProductId) ||
			T800KeyCatalog.IsT800(Device.Name));

	/// <summary>
	/// Boards whose firmware has <b>no factory-vs-override split</b>: the live key matrix is the
	/// state, so "is this key overridden" means "does its record differ from the catalog default",
	/// and slot numbers come from the catalog rather than a runtime scancode probe.
	/// </summary>
	/// <remarks>
	/// Named for the property rather than the family because it is true of two unrelated protocol
	/// families — Smart Infinite (all three boards) and RY5088 — and every branch that tests it does
	/// so for this reason and no other. Widening <see cref="IsSmartInfiniteDevice"/> instead would
	/// make that property lie about which boards it identifies, which matters because a handful of
	/// genuinely Smart-Infinite-only behaviours (the macro repeat encoding) still test it.
	/// </remarks>
	private bool HasNoKeyOverrideLayer => IsSmartInfiniteDevice || IsT800Device;

	/// <summary>
	/// The record type byte this board uses for a plain keyboard key.
	/// </summary>
	/// <remarks>
	/// Not a constant: Raster and the Smart Infinite boards use <c>0x10</c>, but RY5088 stores a
	/// plain key as type <c>0x00</c> (a modifier is an ordinary HID usage <c>0xE0</c>-<c>0xE7</c>
	/// rather than a bitmask). Sending <c>0x10</c> to a T800 writes a record its firmware does not
	/// recognise, and comparing against it makes every key read as overridden.
	/// </remarks>
	private byte KeyboardTypeByte => IsT800Device ? T800KbTypeByte : KbTypeByte;

	/// <summary>Canvas size for the on-screen keyboard, which differs per model.</summary>
	/// <remarks>
	/// T800 reports its artwork viewBox: it has no idealised-grid layout, so the bare-canvas
	/// fallback and the artwork overlay are the same coordinate space for it.
	/// </remarks>
	public double OverlayCanvasWidth => IsT800Device
		? T800ArtworkLayout.ViewBoxWidth
		: IsRampageDevice
			? RampageVisualLayout.CanvasWidth
			: IsSmartInfinite65Device
				? SmartInfinite65Geometry.CanvasWidth
				: IsTRacerDevice ? TRacerVisualLayout.CanvasWidth : RasterVisualLayout.CanvasWidth;

	public double OverlayCanvasHeight => IsT800Device
		? T800ArtworkLayout.ViewBoxHeight
		: IsRampageDevice
			? RampageVisualLayout.CanvasHeight
			: IsSmartInfinite65Device
				? SmartInfinite65Geometry.CanvasHeight
				: IsTRacerDevice ? TRacerVisualLayout.CanvasHeight : RasterVisualLayout.CanvasHeight;

	// ── Device load ───────────────────────────────────────────────────────────
	/// <summary>
	/// Rebuilds <see cref="PhysicalKeys"/>, <see cref="AssignmentTargets"/> and
	/// <see cref="RemapTargetKeys"/> for the current device and restores the last-saved
	/// selection per key from local storage.
	/// </summary>
	public void LoadKeyLayoutForDevice()
	{
		ResetKeyLayoutState();
		PopulateKeyLayout();
	}

	// Set by SetDeviceAsync's prologue (which runs only the cheap reset below) and cleared by
	// PopulateKeyLayout. The catalog build is ~150 ms of VM construction a mouse never pays, so
	// the open defers it into the loader chain — past the visibility flip, before the tail's
	// matrix probe (which writes into the layout, and whose map the reset would wipe if the
	// build ran after it). Safe to defer: every consumer is gated on the keys existing — nothing
	// can be clicked before these collections fill — and the lazy overlay rebuild self-heals via
	// CollectionChanged when they do.
	private bool _keyLayoutPopulatePending;

	// The cheap half: drops every piece of per-device layout state (selection, dialogs, maps,
	// generation) so nothing of the previous board leaks into the new one's frame.
	internal void ResetKeyLayoutState()
	{
		// Covers a device change AND a preset restore: that writes key_macro_bindings in SQL,
		// outside this view model, and then calls straight into here.
		InvalidateMacroBindingCache();

		// The actuation selection holds references into PhysicalKeys — they die with the reload.
		ClearActuationSelection();
		RaiseActuationSelectionChanged();
		// So do the Extended Keys editor's picks.
		CancelExtendedKeyEditor();

		PhysicalKeys.Clear();
		AssignmentTargets.Clear();
		RemapTargetKeys.Clear();
		// Cleared here too (not only in the rebuild below) because the no-overlay early return
		// skips the rebuild — a mouse must not keep the previous keyboard's command rows, and
		// the SYSTEM tab gate has to re-announce for the new device either way.
		SystemCommandTargets.Clear();
		OnPropertyChanged(nameof(IsSystemCommandsSupported));
		SelectedPhysicalKey = null;
		// Both call sites (device change, preset switch) just cleared the selection the dialog
		// was opened for, so a dialog left up would show an empty title over dead pickers — and
		// a surviving toast would offer to undo a key that no longer exists.
		IsKeyAssignmentDialogOpen = false;
		HideRemapToast();
		_matrixMap = null;
		_userOverrides = null;
		_stagedKeyOverrides = null;
		_slotKeyOverrides = null;
		_assignments = null;
		KeyAssignmentReadFailed = false;
		MacroUploadFailed = false;
		_keyLayoutGeneration++;
		// The generation bump above already dooms a running self-heal loop on its next check;
		// cancelling retires it now, before it spends another wait and device read on a layout
		// nobody is looking at.
		CancelSlotReadSelfHeal();
	}

	// The expensive half: catalog dispatch and the per-key VM builds. One small indexed SQLite
	// read inside (LoadStagedKeyOverrides) runs on the calling thread — same accepted residue as
	// the rebuild kicks, and it only ever runs post-flip on the open path.
	private void PopulateKeyLayout()
	{
		_keyLayoutPopulatePending = false;

		if (!HasInteractiveKeyboardOverlay)
			return;

		// Dispatch catalog + visual layout by device identity. Raster and T-Racer share the
		// vendor family but differ by one key (T-Racer adds PrintScreen), so each ships its
		// own catalog / layout. MatrixIndex on each key is the catalog default; the runtime
		// probe (ProbeAndUpdateMatrixMapAsync) overwrites it with the firmware's real slot.
		IReadOnlyList<PhysicalKey> catalog;
		IReadOnlyDictionary<string, RasterKeyBounds> layout;
		if (IsT800Device)
		{
			catalog = T800KeyCatalog.Keys;
			// No idealised-grid layout for this board: its bounds are measured from the artwork, and
			// the artwork overlay is the only way it renders. Same table, joined by slot.
			layout = T800ArtworkLayout.LayoutFor(T800KeyCatalog.Keys);
		}
		else if (IsRampageDevice)
		{
			catalog = RampageKeyCatalog.Keys;
			layout = RampageVisualLayout.Layout;
		}
		else if (IsAressDevice)
		{
			catalog = AressKeyCatalog.Keys;
			layout = AressVisualLayout.Layout;
		}
		else if (IsCyrexDevice)
		{
			catalog = CyrexKeyCatalog.Keys;
			layout = CyrexVisualLayout.Layout;
		}
		else if (IsTRacerDevice)
		{
			catalog = TRacerKeyCatalog.Keys;
			layout = TRacerVisualLayout.Layout;
		}
		else
		{
			catalog = RasterKeyCatalog.Keys;
			layout = RasterVisualLayout.Layout;
		}

		foreach (var key in catalog)
		{
			var bounds = layout.TryGetValue(key.DevKeyName, out var b) ? b : null;

			PhysicalKeys.Add(new KeyAssignmentOptionViewModel
			{
				DevKeyName = key.DevKeyName,
				KeyLabel = key.KeyLabel,
				Category = key.Category,
				MatrixIndex = key.MatrixIndex,
				CatalogMatrixIndex = key.MatrixIndex,
				TypeByte = KeyboardTypeByte,
				Bitmask = key.ModifierMask,
				ScanCode = key.HidUsageCode,
				CanvasX = bounds?.X ?? 0,
				CanvasY = bounds?.Y ?? 0,
				PixelWidth = bounds?.Width ?? 0,
				PixelHeight = bounds?.Height ?? 0,
				SelectCommand = SelectPhysicalKeyCommand,
			});
		}

		// Target list = the same catalog. Users can remap any physical key to any HID key.
		foreach (var key in catalog)
		{
			AssignmentTargets.Add(new KeyAssignmentOptionViewModel
			{
				DevKeyName = key.DevKeyName,
				KeyLabel = key.KeyLabel,
				Category = key.Category,
				MatrixIndex = key.MatrixIndex,
				CatalogMatrixIndex = key.MatrixIndex,
				TypeByte = KeyboardTypeByte,
				Bitmask = key.ModifierMask,
				ScanCode = key.HidUsageCode,
				SelectCommand = SelectAssignmentTargetCommand,
			});
		}

		// The per-key dialog's KEYS tab: the same keys and geometry again, but wired to the
		// remap-pick command. See RemapTargetKeys for why this is not PhysicalKeys itself.
		foreach (var key in catalog)
		{
			var bounds = layout.TryGetValue(key.DevKeyName, out var b) ? b : null;

			RemapTargetKeys.Add(new KeyAssignmentOptionViewModel
			{
				DevKeyName = key.DevKeyName,
				KeyLabel = key.KeyLabel,
				Category = key.Category,
				MatrixIndex = key.MatrixIndex,
				CatalogMatrixIndex = key.MatrixIndex,
				TypeByte = KeyboardTypeByte,
				Bitmask = key.ModifierMask,
				ScanCode = key.HidUsageCode,
				CanvasX = bounds?.X ?? 0,
				CanvasY = bounds?.Y ?? 0,
				PixelWidth = bounds?.Width ?? 0,
				PixelHeight = bounds?.Height ?? 0,
				SelectCommand = PickRemapTargetCommand,
			});
		}

		RebuildSystemCommandTargets();
		RebuildMacroAssignmentTargets();
		LoadStagedKeyOverrides();

		// The keys above are NEW view models with every flag at its default — but a preset
		// switch reaches here without reloading the macro library, so an inspection radio
		// checked before the switch survives while the highlight flags it produced just got
		// discarded with the old keys (PR #104 M1: checked radio, dark board). Re-running the
		// standard selection bundle re-derives the highlights (and the IsSelected mirror and
		// the header) against the freshly rebuilt keys; on a device open Macros is still empty
		// and nothing is inspected, so the call is a cheap no-op there.
		RefreshMacroListSelection();

		// Same for the Extended Keys rows and their overlay highlights, which name and mark keys by
		// slot against the freshly rebuilt PhysicalKeys.
		RefreshExtendedKeysAfterLayoutRebuild();

		// No notifications for the overlay sizes: nothing binds them. The overlay view reads them
		// (and the artwork bounds) imperatively while rebuilding, which the PhysicalKeys reset
		// above already triggers.

		// Nothing is selected until the user picks a key. Auto-selecting the first one made
		// Escape look chosen on load and put the header into a state the user never asked for.
		// main selected PhysicalKeys[0] here; that is deliberately not carried through the merge,
		// so it now applies to every interactive board rather than just Raster.
	}

	// Loads this device's app-truth key overrides from SQLite. One table, two keyings: Raster /
	// T-Racer rows are scancode-keyed staged overrides (_stagedKeyOverrides); the immediate-write
	// family's rows are slot-keyed (_slotKeyOverrides — see its declaration for why). Each family
	// populates only its own map, so the scancode-keyed readers can never misread a slot number
	// as a scancode.
	private void LoadStagedKeyOverrides()
	{
		_stagedKeyOverrides = new Dictionary<byte, (byte, byte, byte)>();
		_slotKeyOverrides = new Dictionary<byte, (byte, byte, byte)>();
		if (_localStorage == null || Device == null)
			return;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null)
			return;

		foreach (var (keyByte, typeByte, bitmask, targetCode) in _localStorage.GetKeyBindings(
			owner.Value.Kind, owner.Value.Id, Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey))
		{
			if (HasNoKeyOverrideLayer)
				_slotKeyOverrides[keyByte] = (typeByte, bitmask, targetCode);
			else
				_stagedKeyOverrides[keyByte] = (typeByte, bitmask, targetCode);
		}
	}

	// The App doesn't reference Infrastructure, so these mirror the per-family constants — same
	// convention DeviceDetailsViewModel.MacroRecorder.cs uses for the X43/Taipan ceilings.
	private const int MacroLibrarySlots = 16;

	// Raster / T-Racer family — keep in sync with RasterMacroEncoder.BindingTypePlay/-WhileHeld.
	// Hardware-verified there: 0x60 plays with the mode in code2, 0x61 is its while-held variant.
	// The repeat constant is deliberately unreferenced: MacroBindingRecord routes repeat counts to
	// the Smart Infinite branch only, because Raster's 0x61 is unverified on hardware and a wrong
	// type on a working board could stop the macro playing at all. Reserved until someone can put
	// a Raster on the bench (see the MacroBindingRecord remarks).
	private const byte RasterMacroBindingTypePlay = 0x60;
	private const byte RasterMacroBindingTypeRepeat = 0x61;

	// Smart Infinite family (Cyrex / Aress / Rampage) — keep in sync with
	// SmartInfiniteProtocol.TypeMacroStopMode/-PlayCount. This firmware's OWN KeyType enum
	// (extracted from the vendor's Keyborder app) gives 0x50/0x51 to macros and 0x60 to FUNCTION —
	// which is why the previous shared-0x60 binding was stored, verified, and played nothing.
	private const byte SmartInfiniteMacroBindingTypeStopMode = 0x50;
	private const byte SmartInfiniteMacroBindingTypePlayCount = 0x51;

	// T800 (RY5088) — keep in sync with Ry5088Protocol.TypeMacro/-PlayType*. This family's record
	// is [0x09, playType, slot]: code1 is the PLAY TYPE and code2 the library slot, the mirror
	// image of the other two families. The repeat count does not travel in the binding at all —
	// it sits in the macro blob's header, carried up via MacroLibraryEntry.RepeatCount. Vendor
	// play type 1 ("always" — loop until any key press) has no UI control, same as the two extra
	// Raster stop modes.
	private const byte T800MacroBindingType = 0x09;
	private const byte T800MacroPlayTypeRepeat = 0x00;
	private const byte T800MacroPlayTypeWhileHeld = 0x02;

	/// <summary>
	/// The full <c>(type, code1, code2)</c> record for binding <paramref name="macro"/> — living
	/// in library slot <paramref name="slot"/> — to a key. Returned together because the bytes
	/// are not independent: the type decides which operand is the slot and what the other means.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Raster and Smart Infinite use the <b>same shape with different type bytes</b> — code1 is
	/// the slot; the play type (Raster <c>0x60</c> / Smart Infinite <c>0x50</c>) spends
	/// <c>code2</c> on the stop mode (<c>0x00</c> play through, <c>0x01</c> play while held), and
	/// the counted type (Raster <c>0x61</c> / Smart Infinite <c>0x51</c>) puts the repeat count
	/// there instead. T800 swaps the operands: <c>[0x09, playType, slot]</c>, with the repeat
	/// count in the uploaded blob rather than the record.
	/// </para>
	/// <para>
	/// <b>The counted type is used on Smart Infinite only:</b> Raster's <c>0x60</c> behaviour is
	/// confirmed on hardware and its <c>0x61</c> is not, so on Raster a repeat count is silently
	/// ignored today — wrong but harmless. Worth revisiting once someone can put a Raster on the
	/// bench.
	/// </para>
	/// </remarks>
	private (byte Type, byte Code1, byte Code2) MacroBindingRecord(MacroViewModel macro, int slot)
	{
		if (IsT800Device)
		{
			var playType = macro.RepeatUntilReleased ? T800MacroPlayTypeWhileHeld : T800MacroPlayTypeRepeat;
			return (T800MacroBindingType, playType, (byte)slot);
		}

		var type = IsSmartInfiniteDevice ? SmartInfiniteMacroBindingTypeStopMode : RasterMacroBindingTypePlay;

		if (macro.RepeatUntilReleased)
			return (type, (byte)slot, MacroPlayModeWhileHeld);

		var repeats = Math.Clamp(macro.RepeatCount, 1, 255);
		if (repeats > 1 && IsSmartInfiniteDevice)
			return (SmartInfiniteMacroBindingTypePlayCount, (byte)slot, (byte)repeats);

		return (type, (byte)slot, MacroPlayModeOnce);
	}

	/// <summary>
	/// How the firmware plays the bound macro, carried in code2 <b>on the play type</b> (Raster
	/// <c>0x60</c> / Smart Infinite <c>0x50</c>). Confirmed on Raster hardware: writing 0x01
	/// advances the macro only while the key is held, so a tap of a six-key macro types two letters
	/// and a longer hold types three; 0x00 is the play-through mode. The vendor app validates two
	/// more stop modes — 0x02 (press-and-release starts, press stops) and 0x03 (press toggles) —
	/// which the UI has no control for. A repeat count does not live here; it needs the counted
	/// type, see <see cref="MacroBindingRecord"/>.
	/// </summary>
	private const byte MacroPlayModeOnce = 0x00;
	private const byte MacroPlayModeWhileHeld = 0x01;

	/// <summary>
	/// Appends one assignment target per library macro, so binding a macro to a key uses the
	/// same click-a-target surface as remapping it to another key — the vendor UI treats a
	/// macro as just another kind of keycode. Only for keyboards that declare macro support.
	/// </summary>
	private void RebuildMacroAssignmentTargets()
	{
		// Drop any macro rows from a previous build; the key rows are rebuilt by the caller.
		for (int i = AssignmentTargets.Count - 1; i >= 0; i--)
			if (AssignmentTargets[i].IsMacroTarget)
				AssignmentTargets.RemoveAt(i);

		if (!IsMacrosSupported || !HasInteractiveKeyboardOverlay)
			return;

		var category = LocalizationManager.GetString("KeyAssignments_Category_Macros");

		// The device slot is the macro's position in the library, which is also what gets
		// uploaded as MacroLibraryEntry.Index — the two must stay in step, so both derive
		// from the same ordering here.
		var slot = 0;
		foreach (var macro in Macros)
		{
			if (slot >= MacroLibrarySlots) break;

			var record = MacroBindingRecord(macro, slot);
			AssignmentTargets.Add(new KeyAssignmentOptionViewModel
			{
				DevKeyName = $"Macro:{macro.Id}",
				KeyLabel = macro.Name,
				Category = category,
				MacroId = macro.Id,
				MatrixIndex = 0,
				TypeByte = record.Type,
				Bitmask = record.Code1,    // slot on Raster/Smart Infinite, play type on T800
				ScanCode = record.Code2,   // play mode / repeat count — or the slot on T800
				SelectCommand = SelectAssignmentTargetCommand,
			});
			slot++;
		}
	}

	/// <summary>
	/// Re-uploads the macro library and re-writes every key slot recorded in
	/// <c>key_macro_bindings</c>. Needed because a macro's device slot is its position in the
	/// library: adding, deleting or reordering macros shifts those positions, so slots written
	/// earlier would replay the wrong macro. Also run on device connect, since the firmware
	/// keeps its own copy across reboots while the library may have changed in between.
	/// </summary>
	public async Task ResyncKeyMacroBindingsAsync()
	{
		// Read-only transport: no key-assignment write reaches the device or the database.
		if (!IsDeviceConfigurable) return;
		if (Device is null || _usbHidDeviceService is null || _localStorage is null)
			return;
		if (!IsMacrosSupported || !HasInteractiveKeyboardOverlay)
			return;

		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		// Captured before any await: this runs fire-and-forget from SetDevice, so the user can
		// switch boards while it is still going and the loop would otherwise write one keyboard's
		// bindings to another. Same hazard and same guard as ApplyAssignmentTargetAsync.
		var device = Device;
		var deviceIdentity = (device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		bool StillSameDevice() =>
			Device is { } current &&
			(current.SettingsVendorId, current.SettingsProductId, current.SettingsDeviceKey) == deviceIdentity;

		// Read the bindings ONCE, before the upload, so the early-out below can see them — the loop
		// further down reuses this list rather than querying again.
		var bindings = _localStorage.GetKeyMacroBindings(
			owner.Value.Kind, owner.Value.Id, device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);

		// Nothing bound and nothing left over from earlier in this session means there is no work:
		// no library worth uploading and no slot to clear. Without this the method is reached by
		// every staged edit — StageChangeAndApply has 15 call sites (RGB, DPI, report rate, mouse
		// settings), all debouncing into ApplyCurrentStateToDeviceAsync — so changing a colour on a
		// keyboard with no macros at all fired a full 1096-byte chunked HID upload 250 ms later,
		// ~20 write/read pairs holding _applyGate. The upload below is unconditional by design (it
		// must clear a stale on-device library), which is exactly why the gate has to be here.
		if (bindings.Count == 0 && (_appliedMacroSlots is null || _appliedMacroSlots.Count == 0))
			return;

		var uploaded = await _usbHidDeviceService.SetMacrosAsync(
			device.VendorId, device.ProductId, device.DeviceKey ?? string.Empty,
			BuildMacroLibraryPayload());
		// The flag belongs to the board this upload targeted; after a switch it describes nobody.
		if (StillSameDevice())
			MacroUploadFailed = !uploaded;
		if (!uploaded || !StillSameDevice()) return;

		// macro id → current device slot + its own binding bytes, from the same ordering the
		// upload used. The macro is carried along so the re-write reproduces the exact record
		// the original binding wrote, rather than defaulting to play-once.
		var byMacroId = new Dictionary<int, (int Slot, MacroViewModel Macro)>();
		var slot = 0;
		foreach (var macro in Macros)
		{
			if (slot >= MacroLibrarySlots) break;
			byMacroId[macro.Id] = (slot++, macro);
		}

		var rewrote = false;
		var nowApplied = new HashSet<byte>();
		// Slots inside an extended-key record hold a pointer, not a key record; writing a macro there
		// would orphan the record. Taken from the live rows (see the Extended Keys partial), with the
		// board's own mirror asked alongside so the skip survives a probe that never landed.
		var extendedSlots = ExtendedKeySlotsInUse();

		foreach (var (matrixIndex, macroId) in bindings)
		{
			if (extendedSlots.Contains(matrixIndex) || BoardMirrorHoldsExtendedKeyPointer((byte)matrixIndex)) continue;

			if (!byMacroId.TryGetValue(macroId, out var entry))
			{
				// The macro is gone from the library — drop the row so the key stops claiming it.
				_localStorage.DeleteKeyMacroBinding(
					owner.Value.Kind, owner.Value.Id, device.SettingsVendorId, device.SettingsProductId,
					device.SettingsDeviceKey, matrixIndex);
				InvalidateMacroBindingCache();
				continue;
			}

			// Must build the record the same way RebuildMacroAssignmentTargets does, or a resync
			// rewrites the key with a different type/count than the assignment originally used.
			var record = MacroBindingRecord(entry.Macro, entry.Slot);
			var ok = await _usbHidDeviceService.SetKeyMappingAsync(
				device.VendorId, device.ProductId, (byte)matrixIndex,
				record.Type, record.Code1, record.Code2);

			if (!StillSameDevice()) return;

			// The firmware now holds this record, but _assignments still holds what the read
			// BEFORE the resync returned — RefreshFromDeviceAsync probes first and resyncs second.
			// Smart Infinite matches the selection on the whole {type, code1, code2} triple, so a
			// macro that moved slot in the library, or whose repeat count changed (which flips the
			// type 0x50 -> 0x51), would match no target at all and the key would read as unbound
			// until the next device open. Keep the mirror in step with what was actually written.
			//
			// Raster cannot hit this: its branch of SyncAssignmentTargetSelection excludes macro
			// rows outright. This path only became reachable for Smart Infinite when the early
			// return in ApplyAssignmentTargetAsync was removed and key_macro_bindings started
			// holding rows for these boards.
			if (ok)
				nowApplied.Add((byte)matrixIndex);

			if (ok && _assignments is not null)
			{
				// The record's own bytes, not (slot, Code2): on T800 Code1 is the play type and the
				// slot lives in Code2, so hard-coding the slot here desynced the mirror from the
				// write above and the bound macro row lost its selection after a resync.
				_assignments[(byte)matrixIndex] = (record.Type, record.Code1, record.Code2);
				rewrote = true;
			}
		}

		// Clear the slots that held a macro before this resync and no longer do. Without this the
		// firmware keeps replaying a macro the App has already forgotten — the symptom being a
		// Reset, or a Discard, that changes the UI and leaves the key untouched.
		//
		// The protected slots are excluded as well, because the binding loop above skipped them and
		// they therefore never reached nowApplied — so without this they read here as "the macro
		// left" and the clear writes the key's own record over the {0x8x, offset} pointer, orphaning
		// the extended-key record while the section still lists it and paints the key. Excluded
		// rather than pre-seeded into nowApplied: seeding would tell the NEXT resync that a macro is
		// applied there, which is the opposite of true. Narrow but real (review round 3, S6):
		// _appliedMacroSlots is replaced only by a resync that runs to its end, so in the ordinary
		// two-pass push the stale write precedes the extended-keys push that reinstalls the pointer;
		// the window opens when pass 1 returns early on the MacroUploadFailed path above, leaving the
		// old slot set in place for pass 2 to clear AFTER the record is already on the board.
		foreach (var stale in (_appliedMacroSlots ?? [])
			.Where(slot => !nowApplied.Contains(slot) && !extendedSlots.Contains(slot)
				&& !BoardMirrorHoldsExtendedKeyPointer(slot)))
		{
			var key = PhysicalKeys.FirstOrDefault(k => ResolveMatrixSlot(k) == stale);
			if (key is null) continue;

			// Same lookup the key push uses, so a key that also carries a persisted remap is
			// restored to that remap rather than stomped back to its factory code. Per family:
			// slot-keyed rows for the immediate-write boards (whose default keeps the key's own
			// bitmask — its modifiers are identified by it), scancode-keyed staged rows otherwise.
			var (typeByte, bitmask, code) = HasNoKeyOverrideLayer
				? _slotKeyOverrides is not null && _slotKeyOverrides.TryGetValue(stale, out var so)
					? so
					: (key.TypeByte, key.Bitmask, key.ScanCode)
				: _stagedKeyOverrides is not null && _stagedKeyOverrides.TryGetValue(key.ScanCode, out var ov)
					? ov
					: (KeyboardTypeByte, (byte)0, key.ScanCode);

			await _usbHidDeviceService.SetKeyMappingAsync(
				device.VendorId, device.ProductId, stale, typeByte, bitmask, code);

			if (!StillSameDevice()) return;
			rewrote = true;
		}

		_appliedMacroSlots = nowApplied;

		if (rewrote) SyncAssignmentTargetSelection();
	}

	/// <summary>
	/// The library in the exact order <see cref="RebuildMacroAssignmentTargets"/> assigned
	/// slots, ready for upload. The device blob holds events only — names and repeat counts
	/// stay in the App library.
	/// </summary>
	private IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> BuildMacroLibraryPayload()
	{
		var entries = new List<(int, MacroRepeatMode, IReadOnlyList<MacroEvent>, int)>();
		var slot = 0;
		foreach (var macro in Macros)
		{
			if (slot >= MacroLibrarySlots) break;
			var events = macro.Events
				.Select(e => new MacroEvent
				{
					KeyCode = (uint)e.KeyCode,
					IsPress = e.IsPress,
					DelayMs = (uint)e.DelayMs,
				})
				.ToList();
			// The repeat count rides with the entry for T800's sake — that family stores it in
			// the uploaded blob; the other keyboards take it from the binding record instead.
			entries.Add((slot, MacroRepeatMode.MacroPlayCount, events, Math.Clamp(macro.RepeatCount, 1, 65535)));
			slot++;
		}
		return entries;
	}

	/// <summary>
	/// Probes the keyboard's default matrix to build a <c>scancode → real slot</c> map for
	/// the current device. Overrides <see cref="KeyAssignmentOptionViewModel.MatrixIndex"/>
	/// on each physical key with the real slot as discovered by firmware. Should be awaited
	/// before any user-initiated writes to guarantee correct slot targeting.
	/// </summary>
	public async Task ProbeAndUpdateMatrixMapAsync()
	{
		if (!HasInteractiveKeyboardOverlay || Device is null || _usbHidDeviceService is null)
			return;

		// The Smart Infinite family and T800 take the slot-keyed read instead: neither has a
		// factory-vs-override split for the scancode probe to compare, and their slots are already
		// exact from the catalog.
		if (HasNoKeyOverrideLayer)
		{
			await ReadSlotKeyedAssignmentsAsync();
			return;
		}

		var probe = await _usbHidDeviceService.ProbeKeyMatrixAsync(Device.VendorId, Device.ProductId);
		if (probe is null)
			return;

		_matrixMap = probe.Value.ScancodeToSlot;
		_userOverrides = new Dictionary<byte, byte>(probe.Value.UserOverrides);

		// Overwrite MatrixIndex on each physical key using the probed slot when we have one.
		// Snapshot: since the transport-switch hook this can run on the gRPC thread while the UI
		// thread rebuilds PhysicalKeys (LoadKeyLayoutForDevice) — enumerating the live
		// ObservableCollection would then throw mid-iteration (PR #124 m2).
		foreach (var pk in PhysicalKeys.ToList())
		{
			if (_matrixMap.TryGetValue(pk.ScanCode, out var realSlot))
				pk.MatrixIndex = realSlot;
		}

		// Re-sync the target-list highlight to reflect the app-truth staged overrides (or, for
		// Smart Infinite above, the live device state).
		SyncAssignmentTargetSelection();
	}

	/// <summary>
	/// Called from <see cref="SetDeviceAsync"/>'s sequentially awaited follow-up chain (failures
	/// swallowed there, not observed by the caller).
	/// Probes the default matrix (scancode → slot) and the device's current override state, used as
	/// the diff baseline when pushing staged overrides. The UI highlight reflects the app-truth
	/// staged overrides (SQLite key_bindings) for Raster/T-Racer, which are written to the device on
	/// Save Preset / preset switch — so a manual FN+Space reset is overridden by the active preset
	/// on the next apply. Cyrex/Aress remain fully device-truth (see ReadSlotKeyedAssignmentsAsync).
	/// </summary>
	public async Task RefreshFromDeviceAsync()
	{
		await ProbeAndUpdateMatrixMapAsync();

		// The device keeps its macro buffer across reboots, but the App library may have been
		// edited since — so slots could point at the wrong macro. Re-push both on connect.
		// Ordered after the probe because re-writing a slot needs the real matrix map.
		await ResyncKeyMacroBindingsAsync();
	}

	/// <summary>
	/// Reads the live key matrix of a Smart Infinite board. A failed read leaves <see cref="_assignments"/>
	/// null, so the overlay falls back to each key's factory assignment rather than inventing state — but
	/// that fallback is only a guess at what the firmware holds. It is wrong for anyone who has remapped a
	/// key, so the failure is surfaced instead of being swallowed. Surfaced via the self-heal loop rather
	/// than flagged here: a wireless board that dozed off through the page-open read (QA 2026-08-19,
	/// Rampage) is back within seconds, and the fallback banner should neither flash for that blip nor
	/// outlive the link's return.
	/// </summary>
	private async Task ReadSlotKeyedAssignmentsAsync()
	{
		var generation = _keyLayoutGeneration;
		if (await TryReadSlotKeyedOnceAsync(generation) is not false)
			return;

		// A read-only transport (Rampage over BT: VID/PID = 0) can never satisfy a retry — the
		// banner is the durable, deliberate state there, so it shows at once, exactly as before.
		if (!IsDeviceConfigurable)
		{
			KeyAssignmentReadFailed = true;
			return;
		}

		StartSlotReadSelfHeal(generation);
	}

	// ── Matrix-read self-heal ─────────────────────────────────────────────────
	// The quick phase runs BEFORE the fallback banner shows, so a sleep/wake blip never surfaces
	// at all; the slow phase runs behind the banner and clears it the moment the link is back.
	private static readonly TimeSpan[] SlotReadQuickRetryDelays =
		[TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
	private static readonly TimeSpan SlotReadSlowRetryDelay = TimeSpan.FromSeconds(5);
	private const int SlotReadSlowRetryCount = 24; // ~2 minutes behind the banner, then give up

	// Generation the loop is currently healing, or -1. Guards against a duplicate loop for the
	// same layout (the open path and a preset push can both fail in one window); a stale loop
	// stops itself on its next generation check, and the field resets so a later same-generation
	// failure can start over.
	private int _slotReadSelfHealGeneration = -1;

	// Retires the running loop the moment its page is torn down (exit to Home, device switch,
	// preset reload) or a successor loop starts, instead of letting it idle out one more wait on
	// its own generation/Device checks (PR #112 m3/m4 — a superseded loop must not spend even one
	// extra tick's device read).
	private CancellationTokenSource? _slotReadSelfHealCts;

	// Test seams (InternalsVisibleTo ProoveGaming.App.Tests): the loop's waits, swappable so tests
	// don't sleep through real minutes, and the loop's task, so a test can await it to completion
	// instead of racing a fire-and-forget.
	internal Func<TimeSpan, Task> SlotReadRetryDelay = Task.Delay;
	internal Task? SlotReadSelfHealTask { get; private set; }

	private void StartSlotReadSelfHeal(int generation)
	{
		if (Interlocked.Exchange(ref _slotReadSelfHealGeneration, generation) == generation)
			return;
		CancelSlotReadSelfHeal();
		var cts = new CancellationTokenSource();
		_slotReadSelfHealCts = cts;
		SlotReadSelfHealTask = RunSlotReadSelfHealAsync(generation, cts.Token);
	}

	// Also called from ClearDeviceForExit and LoadKeyLayoutForDevice — every place the page state
	// the loop serves stops existing.
	private void CancelSlotReadSelfHeal() => _slotReadSelfHealCts?.Cancel();

	// Fire-and-forget from StartSlotReadSelfHeal, deliberately not Task.Run: awaits resume on the
	// caller's context, so state lands the same way the original read's continuation did.
	private async Task RunSlotReadSelfHealAsync(int generation, CancellationToken token)
	{
		try
		{
			foreach (var delay in SlotReadQuickRetryDelays)
			{
				await SlotReadRetryDelay(delay);
				if (token.IsCancellationRequested)
					return;
				if (await TryReadSlotKeyedOnceAsync(generation) is not false)
					return; // healed with no banner, or the page moved on
			}

			// The link is really down. Announce the factory-default fallback, then keep listening
			// for the keyboard to come back — a reconnect clears the banner without a page reopen.
			if (token.IsCancellationRequested || !IsCurrentKeyLayout(generation))
				return;
			KeyAssignmentReadFailed = true;

			for (int i = 0; i < SlotReadSlowRetryCount; i++)
			{
				await SlotReadRetryDelay(SlotReadSlowRetryDelay);
				if (token.IsCancellationRequested)
					return;
				if (await TryReadSlotKeyedOnceAsync(generation) is not false)
					return; // TryRead cleared the banner on success
			}
			// Budget exhausted: the banner stays, and its "reconnect and reopen" advice applies.
		}
		catch (Exception ex)
		{
			// Same containment as SetDeviceAsync's Quietly: the loop is fire-and-forget, so an
			// exception here has no observer and must not surface as an unobserved-task fault.
			System.Diagnostics.Debug.WriteLine($"Slot-read self-heal failed: {ex}");
		}
		finally
		{
			Interlocked.CompareExchange(ref _slotReadSelfHealGeneration, -1, generation);
		}
	}

	/// <summary>
	/// One matrix-read attempt. <c>true</c>: landed, banner cleared. <c>false</c>: the read failed and
	/// the layout is still current — worth retrying. <c>null</c>: stale generation or no device; stop.
	/// </summary>
	private async Task<bool?> TryReadSlotKeyedOnceAsync(int generation)
	{
		if (Device is null || _usbHidDeviceService is null || !IsCurrentKeyLayout(generation))
			return null;

		var records = await _usbHidDeviceService.ReadKeyMatrixAsync(Device.VendorId, Device.ProductId);

		// A newer device page loaded while this read was in flight — its state is the current one, and
		// this result now describes a keyboard the user has moved on from. Drop it silently: the read
		// that belongs to the visible device reports its own outcome.
		if (!IsCurrentKeyLayout(generation))
			return null;

		if (records is null)
			return false;

		KeyAssignmentReadFailed = false;
		_assignments = new Dictionary<byte, (byte, byte, byte)>(records);
		SyncAssignmentTargetSelection();
		return true;
	}

	/// <summary>
	/// The library macro currently bound to the selected physical key, or null. Lets the Macros
	/// page's radio list mirror a keyboard binding the way it already does for a mouse button.
	/// </summary>
	/// <remarks>
	/// Read from <c>key_macro_bindings</c> rather than an in-memory map because that table is the
	/// only truth source for a key→macro binding: the firmware records a slot and a library index
	/// and cannot say which macro that index was. Both slot candidates are checked for the reason
	/// spelled out in <see cref="StageAssignmentTarget"/> — the probed slot and the catalog index
	/// disagree on T-Racer, so a row written under one must still be found under the other.
	/// </remarks>
	internal MacroViewModel? BoundMacroForSelectedKey() =>
		SelectedPhysicalKey is null ? null : BoundMacroForKey(SelectedPhysicalKey);

	/// <summary>Same lookup for an arbitrary key — the hover tooltip needs it per key.</summary>
	private MacroViewModel? BoundMacroForKey(KeyAssignmentOptionViewModel key)
	{
		if (Device is null || _localStorage is null)
			return null;

		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return null;

		var bindings = MacroBindingsBySlot();
		if (bindings.Count == 0) return null;

		// Both candidates, for the reason spelled out on DeleteMacroBindingForKey.
		var slot = ResolveMatrixSlot(key);
		if (!bindings.TryGetValue(slot, out var macroId) &&
			!bindings.TryGetValue(key.CatalogMatrixIndex, out macroId))
			return null;

		return Macros.FirstOrDefault(m => m.Id == macroId);
	}

	/// <summary>
	/// The name line's remap suffix for a key-overlay hover tooltip: what <paramref name="key"/>
	/// currently resolves to — the remap target's label or the bound macro's name — or null when
	/// the key sits on its own (factory) record, which keeps the tip as the key name alone.
	/// </summary>
	/// <remarks>
	/// Follows the dialog highlight's precedence per family (see
	/// <see cref="SyncAssignmentTargetSelection"/>): the live device record first for the
	/// immediate-write family — so the tip and the highlighted row can never name two
	/// different targets for one key — with the persisted override row only as the fallback
	/// for the window before the device read lands; the staged override map for the staging
	/// family. Generalised from the selected key to any hovered key.
	/// </remarks>
	internal string? GetKeyRemapTooltip(KeyAssignmentOptionViewModel key)
	{
		// A macro binding wins: on the immediate family the device record for a macro-bound slot
		// is the play record, whose matching target row is the macro's — so this is also what
		// keeps the record match below from ever needing to resolve one.
		if (BoundMacroForKey(key) is { } macro)
			return macro.Name;

		if (Device is null) return null;

		if (HasNoKeyOverrideLayer)
		{
			var slot = ResolveMatrixSlot(key);
			(byte Type, byte Code1, byte Code2)? record =
				_assignments is not null && _assignments.TryGetValue(slot, out var live)
					? live
					: _slotKeyOverrides is not null && _slotKeyOverrides.TryGetValue(slot, out var persisted)
						? persisted
						: null;
			if (record is null) return null;

			// The key's own record — no remap to name.
			if (record.Value.Type == key.TypeByte &&
			    record.Value.Code1 == key.Bitmask &&
			    record.Value.Code2 == key.ScanCode)
				return null;

			return AssignmentTargets.FirstOrDefault(t =>
				!t.IsMacroTarget &&
				t.TypeByte == record.Value.Type &&
				t.Bitmask == record.Value.Code1 &&
				t.ScanCode == record.Value.Code2)?.KeyLabel;
		}

		if (_stagedKeyOverrides is not null && _stagedKeyOverrides.TryGetValue(key.ScanCode, out var staged))
			return AssignmentTargets.FirstOrDefault(t =>
				!t.IsMacroTarget &&
				t.TypeByte == staged.TypeByte &&
				t.Bitmask == staged.Bitmask &&
				t.ScanCode == staged.TargetCode)?.KeyLabel;
		return null;
	}

	/// <summary>Current device's key→macro bindings, read once and cached until something writes.</summary>
	private Dictionary<byte, int> MacroBindingsBySlot()
	{
		if (_macroBindingsBySlot is not null) return _macroBindingsBySlot;

		var map = new Dictionary<byte, int>();
		var owner = _ownerProvider?.GetCurrent();
		if (Device is not null && _localStorage is not null && owner is not null)
		{
			foreach (var (matrixIndex, macroId) in _localStorage.GetKeyMacroBindings(
				owner.Value.Kind, owner.Value.Id,
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey))
			{
				map[(byte)matrixIndex] = macroId;
			}
		}

		return _macroBindingsBySlot = map;
	}

	/// <summary>
	/// Drops the cached bindings. Called from every path that writes <c>key_macro_bindings</c> —
	/// including <see cref="LoadKeyLayoutForDevice"/>, which covers both a device change and a
	/// preset restore (that writes the table in SQL, outside this view model, and then reloads).
	/// </summary>
	private void InvalidateMacroBindingCache() => _macroBindingsBySlot = null;

	/// <summary>
	/// Removes this key's <c>key_macro_bindings</c> row under both slot candidates — the resolved
	/// slot and the key's untouched catalog slot.
	/// </summary>
	/// <remarks>
	/// Two candidates because the matrix probe is fire-and-forget: a row written before it lands is
	/// keyed by the catalog slot, everything afterwards uses the probed one, and for T-Racer those
	/// differ (E: catalog 45, probed 35). Deleting only the resolved slot leaves the other row
	/// behind, and the resync writes every row it finds — so the stale one lands a macro on a
	/// different physical key.
	/// </remarks>
	private void DeleteMacroBindingForKey(
		SettingsOwner owner, KeyAssignmentOptionViewModel key, byte slot)
	{
		if (Device is null || _localStorage is null) return;

		_localStorage.DeleteKeyMacroBinding(owner.Kind, owner.Id,
			Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, slot);

		if (key.CatalogMatrixIndex != slot)
			_localStorage.DeleteKeyMacroBinding(owner.Kind, owner.Id,
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey,
				key.CatalogMatrixIndex);

		InvalidateMacroBindingCache();
	}

	private byte ResolveMatrixSlot(KeyAssignmentOptionViewModel physicalKey)
	{
		// These slots come from the device's own factory table and are exact — never second-guess
		// them with a scancode probe map.
		if (HasNoKeyOverrideLayer)
			return physicalKey.MatrixIndex;

		if (_matrixMap is not null && _matrixMap.TryGetValue(physicalKey.ScanCode, out var slot))
			return slot;
		return physicalKey.MatrixIndex;
	}

	// ── Commands ──────────────────────────────────────────────────────────────
	[RelayCommand]
	private void SelectPhysicalKey(KeyAssignmentOptionViewModel? option)
	{
		if (option is null) return;

		// The Actuation page's Per-key mode single-selects: a click moves the selection to that key
		// (re-clicking it deselects). The radio consumers below must not run — their selection
		// state belongs to Key Assignments/Macros.
		if (IsActuationSection && ActuationPerKeyMode)
		{
			SelectActuationKey(option);
			return;
		}

		// The Extended Keys page picks the record's keys on the same overlay; its editor owns the
		// IsSelected flags there and refuses or accepts the click itself.
		if (IsExtendedKeysSection)
		{
			SelectExtendedKey(option);
			return;
		}

		// Same refusal one step earlier, while the open's probe has not said what the board holds:
		// the guard below would answer "not protected" for every key, and a remap accepted on that
		// answer writes a key record over a record pointer the App has never read — orphaning it
		// exactly as a click on a known record would (review round 3, S8). This is the only gate
		// the probe window needs: the dialog and the Macros page's macro→key binding both act on
		// SelectedPhysicalKey, which the device open nulls (ResetKeyLayoutState) and only this
		// command fills, so refusing here shuts both of them too.
		if ((IsButtonsSection || IsMacrosSection) && IsExtendedKeyProtectionUnknown)
		{
			ShowExtendedKeyProbePendingMessage();
			return;
		}

		// A key inside an extended-key record — ours, or one the host preserves but does not manage —
		// is not remappable or macro-bindable here: the dialog stays shut and the page says why
		// (edit it on Extended Keys).
		if ((IsButtonsSection || IsMacrosSection) && IsExtendedKeyProtectedSlot(ResolveMatrixSlot(option)))
		{
			ShowExtendedKeyBlockedMessage(option);
			return;
		}

		SelectPhysicalKeyCore(option);

		// The assignment surface is the per-key dialog: selecting a key on the Key Assignments or
		// Macros keyboard opens it. Actuation returned above; a mouse never builds PhysicalKeys,
		// but the overlay gate is kept anyway so a direct call cannot open a dialog with no picker.
		// A read-only transport never opens it either — every tab's action would silently no-op
		// there (the same entry-point guard the staged-edit flows use).
		if (HasInteractiveKeyboardOverlay && IsDeviceConfigurable && (IsButtonsSection || IsMacrosSection))
			OpenKeyAssignmentDialog();
	}

	/// <summary>
	/// Clicking empty space on a keyboard panel clears the key selection — the "no key"
	/// counterpart of <see cref="SelectPhysicalKey"/>, and it branches the same way: Actuation's
	/// Per-key mode owns its own single-select, every other section drops the shared selection.
	/// The view invokes this only for clicks no key button consumed.
	/// </summary>
	[RelayCommand]
	private void DeselectPhysicalKey()
	{
		if (IsActuationSection && ActuationPerKeyMode)
		{
			// The flush discipline every selection/mode change carries (SelectActuationKey, the
			// mode toggle): an edit still in its debounce window belongs to the key it was made
			// on — clearing the selection first would drop it (review round 4 B1).
			FlushPendingActuationApply();
			ClearActuationSelection();
			RaiseActuationSelectionChanged();
			return;
		}

		// Empty space on the Extended Keys page is not a "drop the picks" gesture — the editor has
		// its own Cancel and per-chip clear.
		if (IsExtendedKeysSection)
			return;

		ClearPhysicalKeySelection();
	}

	/// <summary>
	/// The shared-radio clear alone — used by the empty-space deselect above and by section
	/// switches (PG-494: a section is entered with no key selected, so a key picked on Key
	/// Assignments must not arrive on Macros still highlighted).
	/// </summary>
	internal void ClearPhysicalKeySelection()
	{
		if (SelectedPhysicalKey is null) return;

		// Same rule as a selection move: the toast's UNDO acts on the selected key, so it must
		// not outlive the selection it talks about.
		HideRemapToast();

		foreach (var key in PhysicalKeys)
			key.IsSelected = false;
		foreach (var target in AssignmentTargets)
			target.IsSelected = false;
		SelectedPhysicalKey = null;
		SyncRemapTargetPickerSelection();

		// The Macros radio list mirrors the selected key's binding — with no key selected it
		// clears, same as right after the section loads.
		RefreshMacroListSelection();
	}

	// The selection state change alone, without the dialog: shared by the key click above and the
	// hover tip's unassign ✕, which must act on its key without popping the dialog over it.
	private void SelectPhysicalKeyCore(KeyAssignmentOptionViewModel option)
	{
		// A new selection ends the previous toast's undo window — its UNDO acts on the SELECTED
		// key, so a surviving toast starts lying the moment the selection moves. Same rule
		// OpenKeyAssignmentDialog applies; stating it here covers the paths that select without
		// opening the dialog (the tip's ✕), where a stale toast would sit through the new
		// unassign's device write offering an UNDO that silently no-ops (review round 1 S1).
		HideRemapToast();

		foreach (var key in PhysicalKeys)
			key.IsSelected = ReferenceEquals(key, option);

		SelectedPhysicalKey = option;
		SyncAssignmentTargetSelection();

		// The Macros page's radio list reflects the binding of whatever is selected, and for a
		// keyboard that is this key — without this the list stayed on the previous key's macro.
		RefreshMacroListSelection();
	}

	/// <summary>
	/// Whether the hover tip should offer its unassign ✕ for this key — true only when the key
	/// plays a library macro (a plain key remap has nothing to unassign from the tip).
	/// </summary>
	internal bool KeyHasBoundMacro(KeyAssignmentOptionViewModel key) =>
		BoundMacroForKey(key) is not null;

	/// <summary>
	/// The hover tip's unassign ✕: reverts <paramref name="key"/> to its own keycode, dropping the
	/// macro binding. Selects the key first (without opening the dialog) so the whole existing
	/// reset path — family dispatch, staged-vs-immediate write, list/header refresh — runs
	/// unchanged against it, including the in-flight assignment gate the other unassign
	/// affordances carry (PR #81 review M1): a ✕ landing inside the ~0.5 s assignment write
	/// window would race that write, so it is a no-op until the binding is actually recorded.
	/// </summary>
	[RelayCommand]
	private async Task UnassignMacroFromKey(KeyAssignmentOptionViewModel? key)
	{
		if (key is null || Device is null) return;
		var macro = BoundMacroForKey(key);
		if (macro is null) return;

		// Mirror SelectPhysicalKey's Actuation branch: Per-key mode owns the IsSelected flags
		// through its own single-select (_actuationSelectedKey), and the sweep below would
		// desync it — sliders editing a key that no longer looks selected. The view already
		// withholds the ✕ there (ShowKeyTip); this closes the direct-invocation path.
		if (IsActuationSection && ActuationPerKeyMode) return;

		// Mirror SelectPhysicalKey's extended-key branches too, and for the same reason: this
		// command's reset writes the key's OWN record into its slot, so on a slot that is inside an
		// extended-key record that write lands on the record's {0x8x, offset} pointer and orphans
		// it — the section still lists the record and paints the key, but the board no longer plays
		// it. The App's UI cannot create a slot that carries both a key_macro_bindings row and a
		// record, but the API intake validates the two sections independently, so a pulled preset
		// can; in that state the ✕ is the ONLY affordance the key offers, because the click path is
		// already refused here (review round 3, S7). Unlike SelectPhysicalKey these are not scoped
		// to Buttons/Macros: the tip offers the ✕ only there, so any other caller is a direct
		// invocation, and the write it would make is unsafe whatever section asked for it.
		//
		// The probe window first — while it is open IsExtendedKeyProtectedSlot answers "not
		// protected" for every key (review round 3, S8), so the verdict below cannot be believed.
		// The two messages are the truthful ones for each case and neither is new: "still reading
		// the keyboard" for a verdict that has not arrived, and the record's own mode · partner for
		// one that has.
		if (IsExtendedKeyProtectionUnknown)
		{
			ShowExtendedKeyProbePendingMessage();
			return;
		}

		if (IsExtendedKeyProtectedSlot(ResolveMatrixSlot(key)))
		{
			ShowExtendedKeyBlockedMessage(key);
			return;
		}

		// The in-flight gate, checked BEFORE the selection moves (PR #104 m2): inside the
		// assignment-write window this command is documented as a no-op, and a no-op must not
		// move the selection, hide another key's toast or rewrite the list mirror on its way
		// out. With a macro bound (checked above) and no write in flight, this is exactly what
		// CanResetSelectedMacroTarget evaluates to once the selection lands on this key — so
		// the gate is the same one the other unassign affordances carry (PR #81 review M1),
		// just asked one step earlier.
		if (_assignmentWriteInFlight) return;

		SelectPhysicalKeyCore(key);
		await ResetSelectedKeyAsync();

		// Announce only what actually happened, and only where it can still act: the reset has
		// silent early-outs (missing self target, refused device write), and if the user moved
		// the selection during the ~0.5 s write, SelectPhysicalKeyCore already ended this undo
		// window — raising the toast then would offer an UNDO whose selection guard silently
		// no-ops (PR #104 m1). The binding map is re-read here, so this is the same truth the
		// tip and the radio highlight render.
		if (ReferenceEquals(SelectedPhysicalKey, key) && BoundMacroForKey(key) is null)
			ShowMacroUnassignToast(key, macro);
	}

	[RelayCommand]
	private Task SelectAssignmentTarget(KeyAssignmentOptionViewModel? target) =>
		SelectAssignmentTargetCore(target);

	// The command keeps its plain Task shape for the bindings; the bool is for
	// CommitRemapPickAsync, whose "successfully remapped" toast must not outrun the truth —
	// false means nothing was staged or written (read-only transport, refused device write).
	private async Task<bool> SelectAssignmentTargetCore(KeyAssignmentOptionViewModel? target)
	{
		if (target is null || SelectedPhysicalKey is null || Device is null) return false;

		foreach (var t in AssignmentTargets)
			t.IsSelected = ReferenceEquals(t, target);

		// Smart Infinite (Cyrex/Aress) writes straight to the device — that family is device-truth
		// throughout, so its macro bindings follow its key remaps onto the immediate path.
		//
		// Raster/T-Racer stage everything, macro bindings included. Staging is what keeps the two
		// stores in step (a slot is either a macro or a key record, never both) and is what makes
		// Reset able to clear a binding; the device still sees the change promptly, because
		// StageChangeAndApply debounces a full push a moment later. Macro bindings used to bypass
		// this on their own immediate-write path, which is how a reset key could silently go back
		// to playing its macro.
		var applied = HasNoKeyOverrideLayer
			? await ApplyAssignmentTargetAsync(target)
			: StageAssignmentTarget(target);

		// The optimistic highlight above moved before the outcome was known. The failed-write
		// path re-syncs on its own, but the guard early-outs (read-only transport, missing
		// owner) do not — put the highlight back where the truth is.
		if (!applied)
			SyncAssignmentTargetSelection();
		return applied;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────
	/// <summary>
	/// After the physical key changes, highlight the target row that matches the current truth
	/// source for this device family: the live device probe for Cyrex/Aress, or the app-truth
	/// staged override for Raster/T-Racer.
	/// </summary>
	private void SyncAssignmentTargetSelection()
	{
		if (SelectedPhysicalKey is null || Device is null) return;

		var slot = ResolveMatrixSlot(SelectedPhysicalKey);

		if (HasNoKeyOverrideLayer)
		{
			// Match on the whole record rather than the scancode: every modifier key has scancode
			// 0x00 and is distinguished only by its bitmask, so comparing scancodes alone would
			// highlight whichever modifier happens to come first in the list.
			var current = _assignments is not null && _assignments.TryGetValue(slot, out var record)
				? record
				: (SelectedPhysicalKey.TypeByte, SelectedPhysicalKey.Bitmask, SelectedPhysicalKey.ScanCode);

			foreach (var t in AssignmentTargets)
				t.IsSelected = t.TypeByte == current.Item1 &&
				               t.Bitmask == current.Item2 &&
				               t.ScanCode == current.Item3;
			SyncRemapTargetPickerSelection();
			return;
		}

		// Reflect the app-truth staged override (keyed by scancode), not the device probe — the UI
		// shows what the active preset will push, not necessarily what the firmware holds right now.
		var targetHid = _stagedKeyOverrides is not null && _stagedKeyOverrides.TryGetValue(SelectedPhysicalKey.ScanCode, out var ov)
			? ov.TargetCode
			: SelectedPhysicalKey.ScanCode;

		// Macro rows carry ScanCode 0 and so would all light up for a key whose override is 0.
		// They are excluded: macro bindings live in a separate, unstaged table (key_macro_bindings)
		// and are not represented by the staged key_bindings override map.
		foreach (var t in AssignmentTargets)
			t.IsSelected = !t.IsMacroTarget && t.ScanCode == targetHid;
		SyncRemapTargetPickerSelection();
	}

	// Stages a key remap into the active preset (SQLite key_bindings + in-memory);
	// StageChangeAndApply below marks the preset dirty and (debounced) pushes it to the keyboard
	// via ApplyKeyOverridesToDeviceAsync. Selecting the physical key's own scancode clears the
	// override (reset to default). Raster/T-Racer only — see SelectAssignmentTarget. Returns
	// whether anything was actually staged, for the dialog's commit gate.
	private bool StageAssignmentTarget(KeyAssignmentOptionViewModel target)
	{
		// Read-only transport: no key-assignment write reaches the device or the database.
		if (!IsDeviceConfigurable) return false;
		if (SelectedPhysicalKey is null || Device is null || _localStorage is null)
			return false;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return false;

		var scancode = SelectedPhysicalKey.ScanCode;
		var slot = ResolveMatrixSlot(SelectedPhysicalKey);
		_stagedKeyOverrides ??= new Dictionary<byte, (byte, byte, byte)>();

		// A slot is either a macro or a key record, never both — so each branch clears the other's
		// store. Without this the two tables both claim the slot and whichever push runs last wins:
		// a reset key silently went back to playing its macro, and a remap onto a macro-bound key
		// never took effect, because ResyncKeyMacroBindingsAsync runs after the key push and
		// faithfully re-wrote the stale row.
		if (target.IsMacroTarget)
		{
			// Staged like a key remap: recorded now, written to the keyboard by the resync inside
			// the Save Preset / preset-switch push. key_macro_bindings is keyed by matrix slot, not
			// by scancode, so it cannot share the key_bindings key.
			_stagedKeyOverrides.Remove(scancode);
			_localStorage.DeleteKeyBinding(owner.Value.Kind, owner.Value.Id,
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, scancode);

			// Clear any row this key already owns under EITHER slot candidate before writing the
			// new one. Binding a macro before the probe lands keys the row by the catalog slot;
			// re-binding the same key afterwards writes a second row under the probed slot and the
			// first is orphaned. The resync iterates every row, so that orphan would put a macro on
			// whichever physical key owns the catalog slot — a key the user never touched and
			// cannot reset, because its own Reset deletes by its own slot.
			DeleteMacroBindingForKey(owner.Value, SelectedPhysicalKey, slot);

			_localStorage.SetKeyMacroBinding(owner.Value.Kind, owner.Value.Id,
				Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey,
				slot, target.MacroId!.Value);
			InvalidateMacroBindingCache();
		}
		else
		{
			// Delete by BOTH slot candidates, not just the resolved one. ResolveMatrixSlot returns
			// the probed slot once _matrixMap has landed and the catalog MatrixIndex before that —
			// and for T-Racer those disagree (E probes to 35, catalog says 45, because the catalog
			// is seeded from Raster). The probe is fire-and-forget on page open, so a binding
			// assigned after it lands and reset before it lands would delete the wrong row: the
			// real one survived and the next Save Preset re-applied the macro. That is the
			// "reset doesn't always stick" symptom.
			DeleteMacroBindingForKey(owner.Value, SelectedPhysicalKey, slot);

			if (target.ScanCode == scancode)
			{
				_stagedKeyOverrides.Remove(scancode);
				_localStorage.DeleteKeyBinding(owner.Value.Kind, owner.Value.Id,
					Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, scancode);
			}
			else
			{
				_stagedKeyOverrides[scancode] = (target.TypeByte, target.Bitmask, target.ScanCode);
				_localStorage.SetKeyBinding(owner.Value.Kind, owner.Value.Id,
					Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey,
					scancode, target.TypeByte, target.Bitmask, target.ScanCode);
			}
		}

		SyncAssignmentTargetSelection();

		// Covers NotifySelectedMacroTargetChanged() as well as the Macros radio list, which now
		// reflects a keyboard binding too.
		RefreshMacroListSelection();

		// Marks the preset dirty synchronously and debounces the device push — the same path every
		// other staged edit takes, so a macro binding now lands on the keyboard as promptly as a
		// key remap instead of waiting for Save Preset.
		StageChangeAndApply();
		return true;
	}

	// Pushes the staged key overrides to the keyboard, diffing against the device's current override
	// set (the probe baseline) so only changed slots are written. Slots overridden on the device but
	// no longer staged are reset to their default (identity) mapping. Used by the full-state device
	// push (Save Preset / preset switch / connect), not per edit. Raster/T-Racer only — Cyrex/Aress
	// physical keys never have a _stagedKeyOverrides entry (they stay on the immediate-write path in
	// SelectAssignmentTarget), so this is a harmless no-op for those devices.
	internal async Task ApplyKeyOverridesToDeviceAsync()
	{
		if (Device is null || _usbHidDeviceService is null || !HasInteractiveKeyboardOverlay)
			return;

		// The immediate-write family stores full per-slot records rather than scancode-keyed
		// code2 overrides — its push is the slot-keyed twin below.
		if (HasNoKeyOverrideLayer)
		{
			await ApplySlotKeyOverridesToDeviceAsync();
			return;
		}

		if (_matrixMap is null)
			await ProbeAndUpdateMatrixMapAsync();

		foreach (var pk in PhysicalKeys)
		{
			var slot = ResolveMatrixSlot(pk);
			byte typeByte, bitmask, desired;
			if (_stagedKeyOverrides is not null && _stagedKeyOverrides.TryGetValue(pk.ScanCode, out var ov))
				(typeByte, bitmask, desired) = (ov.TypeByte, ov.Bitmask, ov.TargetCode);
			else
				(typeByte, bitmask, desired) = (KeyboardTypeByte, (byte)0, pk.ScanCode);

			var current = _userOverrides is not null && _userOverrides.TryGetValue(slot, out var c) ? c : pk.ScanCode;
			if (desired == current)
				continue;

			var ok = await _usbHidDeviceService.SetKeyMappingAsync(
				Device.VendorId, Device.ProductId, slot, typeByte, bitmask, desired);
			if (!ok)
				continue;

			_userOverrides ??= new Dictionary<byte, byte>();
			if (desired == pk.ScanCode)
				_userOverrides.Remove(slot);
			else
				_userOverrides[slot] = desired;
		}
	}

	// The slot-keyed push for the immediate-write family (Smart Infinite / T800): desired is the
	// persisted override row (or the key's own record), current is the device-truth mirror. Runs
	// from every preset push (Save / switch / Discard / connect, via ApplyKeyOverridesToDeviceAsync)
	// — which is what makes an unsaved remap on this family revertible at all: the pick already
	// wrote the device, so a revert has to write the previous record back.
	private async Task ApplySlotKeyOverridesToDeviceAsync()
	{
		if (Device is null || _usbHidDeviceService is null) return;

		// The diff baseline. A preset apply reloads the page state (nulling the mirror) without
		// re-reading the matrix, so re-read here — a blind full-matrix write is not an option on
		// this family, where every slot write is a read-modify-write of the whole table.
		if (_assignments is null)
			await ReadSlotKeyedAssignmentsAsync();

		var device = Device;
		if (device is null) return;

		var wrote = false;
		// Slots inside an extended-key record hold a {0x8x, off_lo, off_hi} pointer that differs from
		// every key record, so without this skip the push would "restore" the key and orphan the
		// record on every Save / switch / connect. From the live rows, never from the device mirror.
		var extendedSlots = ExtendedKeySlotsInUse();
		foreach (var pk in PhysicalKeys)
		{
			var slot = ResolveMatrixSlot(pk);
			// Two skips, two different questions, and the board's answer is the one that survives a
			// probe that never landed — see BoardMirrorHoldsExtendedKeyPointer.
			if (extendedSlots.Contains(slot) || BoardMirrorHoldsExtendedKeyPointer(slot)) continue;
			var selfRecord = (pk.TypeByte, pk.Bitmask, pk.ScanCode);
			var desired = _slotKeyOverrides is not null && _slotKeyOverrides.TryGetValue(slot, out var ov)
				? ov
				: selfRecord;

			// Macro-bound slots are deliberately overwritten with the key record here and
			// re-asserted by ResyncKeyMacroBindingsAsync right after — the same ordering the
			// Raster push documents in ApplyCurrentStateToDeviceCoreAsync.
			if (_assignments is not null && _assignments.TryGetValue(slot, out var current))
			{
				if (current == desired) continue;
			}
			else if (desired == selfRecord &&
				(_appliedSlotOverrides is null || !_appliedSlotOverrides.Contains(slot)))
			{
				// No baseline (the re-read failed): only write slots something claims — a
				// persisted override, or a session write that must go back to default.
				continue;
			}

			var ok = await _usbHidDeviceService.SetKeyMappingAsync(
				device.VendorId, device.ProductId, slot, desired.Item1, desired.Item2, desired.Item3);
			if (!ok) continue;

			_assignments ??= [];
			_assignments[slot] = desired;
			if (desired == selfRecord)
				_appliedSlotOverrides?.Remove(slot);
			else
				(_appliedSlotOverrides ??= []).Add(slot);
			wrote = true;
		}

		if (wrote) SyncAssignmentTargetSelection();
	}

	// True while ApplyAssignmentTargetAsync is between its first write and its bookkeeping.
	// CanResetSelectedMacroTarget reads it, so the unassign ✕ and the header Reset are disabled
	// for the whole window — not just when the key had no previous binding. Gating on "is a
	// binding recorded" alone left the re-bind case open (PR #81 review, round 3 M1): while
	// macro B's write is in flight the OLD binding A is still in SQLite, the gate read true, and
	// a ✕ click raced the write — the reset landed first and the resuming assignment silently
	// re-bound B under a UI that said "unassigned".
	private bool _assignmentWriteInFlight;

	private async Task<bool> ApplyAssignmentTargetAsync(KeyAssignmentOptionViewModel target)
	{
		// Read-only transport: no key-assignment write reaches the device or the database.
		if (!IsDeviceConfigurable) return false;
		if (SelectedPhysicalKey is null || Device is null || _usbHidDeviceService is null)
			return false;

		// Set before the first await: everything below runs on the UI thread, so the ✕ click
		// that could race this write can only be dispatched at an await point — by which time
		// the gate is already closed.
		_assignmentWriteInFlight = true;
		NotifySelectedMacroTargetChanged();
		try
		{
			return await ApplyAssignmentTargetCoreAsync(target);
		}
		finally
		{
			_assignmentWriteInFlight = false;
			NotifySelectedMacroTargetChanged();
		}
	}

	// True only when the key write reached the addressed device AND its bookkeeping was
	// recorded — a mid-write device switch drops the bookkeeping and reports false, which the
	// dialog's commit treats as "do not toast" (its own selection guard fires first anyway).
	private async Task<bool> ApplyAssignmentTargetCoreAsync(KeyAssignmentOptionViewModel target)
	{
		if (SelectedPhysicalKey is null || Device is null || _usbHidDeviceService is null)
			return false;

		// Captured before ANY await below: what this has to survive is the user switching to a
		// DIFFERENT keyboard mid-write, and the only faithful test of that is the device's own
		// identity. The _keyLayoutGeneration counter cannot be used here, because it also bumps
		// when LoadKeyLayoutForDevice re-runs for the SAME device — a card refresh or a preset
		// apply is enough. When that happened, this method returned after the firmware had
		// already been written, so the device held the macro binding and SQLite did not: the
		// Macros page then showed the key as unassigned and the user could not bind it at all.
		// Cyrex and Aress hit it far more than Raster because their key write is a full 225-byte
		// read-modify-write of the matrix (~420 ms measured) against Raster's single slot.
		var device = Device;
		var deviceIdentity = (device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);
		var selectedKey = SelectedPhysicalKey;

		// Also captured: LoadKeyLayoutForDevice clears SelectedPhysicalKey, so the post-await code
		// below would dereference null on exactly the re-load this method now tolerates.
		var slot = ResolveMatrixSlot(selectedKey);

		bool StillSameDevice() =>
			Device is { } current &&
			(current.SettingsVendorId, current.SettingsProductId, current.SettingsDeviceKey) == deviceIdentity;

		// A macro binding only stores an index, so the library has to be on the device before
		// the slot can point into it. Re-uploading the whole library each time is deliberate:
		// the device blob carries no ids, so a partial write could leave slots aimed at the
		// wrong macro after the user edits the library.
		if (target.IsMacroTarget)
		{
			var uploaded = await _usbHidDeviceService.SetMacrosAsync(
				Device.VendorId, Device.ProductId, Device.DeviceKey ?? string.Empty,
				BuildMacroLibraryPayload());
			if (!uploaded)
			{
				// The bind below is skipped on purpose — say so, or the click looks ignored.
				if (StillSameDevice())
					MacroUploadFailed = true;
				return false;
			}

			// The library went to the keyboard that was connected when this started. If the user
			// switched boards during that exchange, the slot write below would target the new one
			// against a library it never received.
			if (!StillSameDevice())
				return false;
			MacroUploadFailed = false;
		}

		var ok = await _usbHidDeviceService.SetKeyMappingAsync(
			device.VendorId,
			device.ProductId,
			slot,
			target.TypeByte,
			target.Bitmask,
			target.ScanCode);

		// The user switched keyboards while this write was in flight. The checks below would be
		// evaluated against the new one, so recording the result now would claim a remap on a
		// keyboard that was never sent it. The write itself already reached the device it was
		// addressed to; only the bookkeeping is dropped.
		if (!StillSameDevice())
			return false;

		// A failed write must not move the highlight — otherwise the UI claims a remap the firmware
		// rejected. Put the selection back where the device actually is.
		if (!ok)
		{
			SyncAssignmentTargetSelection();
			return false;
		}

		// This family keeps the full record per slot rather than a code2-only override map, because
		// its modifier keys are identified by code1 with a 0x00 scancode.
		if (HasNoKeyOverrideLayer)
		{
			_assignments ??= [];
			_assignments[slot] = (target.TypeByte, target.Bitmask, target.ScanCode);

			// Refreshes the target-list highlight against the record just written. Deliberately not
			// followed by a return: the macro-binding write below has to run for this family too,
			// and NotifySelectedMacroTargetChanged() already fires at the end of the method.
			SyncAssignmentTargetSelection();
		}

		// Macro bindings are the one thing firmware can't tell us back: a read of the matrix
		// returns a slot type and index, with no way to say "library macro #3". So unlike key
		// remaps — where the device is the truth source — these are recorded in SQLite. This runs
		// for every family: returning early here left Smart Infinite macro bindings unpersisted, so
		// nothing re-uploaded them and the key silently stopped playing after a reconnect.
		var owner = _ownerProvider?.GetCurrent();
		if (_localStorage != null && owner is not null)
		{
			if (target.IsMacroTarget)
				_localStorage.SetKeyMacroBinding(
					owner.Value.Kind, owner.Value.Id, device.SettingsVendorId, device.SettingsProductId,
					device.SettingsDeviceKey, slot, target.MacroId!.Value);
			else
				_localStorage.DeleteKeyMacroBinding(
					owner.Value.Kind, owner.Value.Id, device.SettingsVendorId, device.SettingsProductId,
					device.SettingsDeviceKey, slot);

			InvalidateMacroBindingCache();

			// The slot's key-record override, persisted slot-keyed (see _slotKeyOverrides) so
			// Save Preset snapshots it and a Discard / preset switch has the previous record to
			// write back — the immediate device write above is otherwise invisible to the preset
			// system, and an unsaved remap silently survived a Discard. A macro target or the
			// key's own record clears the row: the slot then belongs to key_macro_bindings or to
			// the factory default.
			var isOwnRecord = !target.IsMacroTarget &&
				target.TypeByte == selectedKey.TypeByte &&
				target.Bitmask == selectedKey.Bitmask &&
				target.ScanCode == selectedKey.ScanCode;
			if (target.IsMacroTarget || isOwnRecord)
			{
				_localStorage.DeleteKeyBinding(owner.Value.Kind, owner.Value.Id,
					device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey, slot);
				_slotKeyOverrides?.Remove(slot);
				_appliedSlotOverrides?.Remove(slot);
			}
			else
			{
				_localStorage.SetKeyBinding(owner.Value.Kind, owner.Value.Id,
					device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey,
					slot, target.TypeByte, target.Bitmask, target.ScanCode);
				(_slotKeyOverrides ??= new Dictionary<byte, (byte, byte, byte)>())[slot] =
					(target.TypeByte, target.Bitmask, target.ScanCode);
				(_appliedSlotOverrides ??= []).Add(slot);
			}
		}

		// Reflect the write in our in-memory override map so subsequent SyncAssignmentTargetSelection
		// calls (e.g. when the user clicks another physical key and comes back) see the new state
		// without a re-probe. Nothing is persisted to SQLite for a plain remap here — this path is
		// macro targets only for Raster/T-Racer (see SelectAssignmentTarget); Smart Infinite is
		// handled above.
		if (_userOverrides is not null)
		{
			if (target.ScanCode == selectedKey.ScanCode)
				_userOverrides.Remove(slot);
			else
				_userOverrides[slot] = target.ScanCode;
		}

		// SelectedKeyHasOverride is computed from _userOverrides, which just changed, so the
		// header's Reset link has to be told. Without this the link stays greyed out after
		// binding a key and stays active after resetting one.
		SyncAssignmentTargetSelection();
		NotifySelectedMacroTargetChanged();

		// Now that key_macro_bindings is preset-scoped, a binding written here is a preset edit and
		// has to mark the preset dirty — otherwise leaving the page offers no unsaved-changes
		// prompt at all, so there is nothing to Discard and the binding silently becomes the new
		// saved state. This path already wrote the device, so the debounced push it schedules is a
		// redundant re-assert rather than a new write.
		StageChangeAndApply();
		return true;
	}

}
