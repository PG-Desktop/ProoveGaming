# Plan: Per-device presets — CRUD, save/apply, unsaved-changes guard

> **2026-08-25 schema note:** the `presets` table gained `uid` (client-generated GUID,
> unique, assigned at insert and backfilled for existing rows) and `server_updated_at`
> (the server's timestamp echoed verbatim; NULL = never synced) for API v2.0 sync — see
> [../add-api-v2-sync/add-api-v2-sync-plan.md](../add-api-v2-sync/add-api-v2-sync-plan.md).
> Nothing else in this plan's contract changed.

> **2026-08-25 schema note (live sync):** the `presets` table further gained `is_dirty` and
> `local_rev` (mutators mark user-owned rows dirty and bump the rev; a push clears the flag
> rev-guarded), and deleting a synced user preset now writes a row into the new
> `sync_tombstones` table in the same transaction — see
> [../add-api-v2-live-sync/add-api-v2-live-sync-plan.md](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md).
> Preset CRUD behavior described here is otherwise unchanged.

## Goal

Let the user save a named snapshot of a device's current settings (RGB, button
bindings, macros, DPI, polling rate) as a **preset** and switch between presets
without losing work. Every device has its own preset list, one preset is always
marked as the **default**, and the shell prevents accidental data loss when the
user tries to leave DeviceDetailsView or pick a different preset while there are
unsaved changes.

## Scope

**In:**
- SQLite schema for preset rows + child snapshot tables (`preset_rgb`,
  `preset_rgb_effect`, `preset_button_bindings`), all scoped by owner + device.
- ViewModels for preset list, kebab-menu actions, and the top-panel dropdown.
- Four dialog UserControls: **New Preset**, **Reset Preset**, **Save Preset**,
  **Unsaved Changes**.
- Auto-create a "Default" preset when a device is opened with an empty preset
  list; enforce the invariant *exactly one `IsDefault=true` per (owner, device)*.
- Un-deletable default preset (UI disabled + command + storage-side safety net).
- Unsaved-changes confirmation dialog reused for two flows (leaving the view /
  switching preset) with a context-aware body.
- Extraction of all preset dialogs from `MainWindow.axaml` into standalone
  UserControls under `Views/DeviceDetails/Dialogs/`.
- xUnit test coverage for the ViewModel command surface and the storage layer.

**Out (future / explicit):**
- Cross-device preset copy / import-export.
- Preset preview / diff view before applying.
- Auto-save on device disconnect.
- Sync of presets to a cloud account (guest → user migration still applies to the
  local rows via existing `MigrateGuestToUserAsync`).

## Data model

### Schema (`LocalStorageService.EnsureSchema`)

```sql
CREATE TABLE IF NOT EXISTS presets (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_kind  TEXT NOT NULL,          -- 'guest' | 'user'
    owner_id    TEXT NOT NULL,          -- guest UUID or customer id
    vendor_id   INTEGER NOT NULL,
    product_id  INTEGER NOT NULL,       -- SettingsProductId
    device_key  TEXT NOT NULL DEFAULT '', -- Container ID / deviceKey
    name        TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    is_default  INTEGER NOT NULL DEFAULT 0,  -- added via AddColumnIfMissing
    created_at  TEXT NOT NULL
);

-- Snapshot tables copied over from device_* rows when the user saves.
CREATE TABLE IF NOT EXISTS preset_rgb            (...);
CREATE TABLE IF NOT EXISTS preset_rgb_effect     (...);
CREATE TABLE IF NOT EXISTS preset_button_bindings(...);
```

Uniqueness per device is enforced by the `(owner_kind, owner_id, vendor_id,
product_id, device_key)` filter in every query. `is_default` is a soft flag with
the "exactly one" invariant guaranteed by `PromotePresetToDefault(id)` (single
transaction: demote every sibling in the scope, promote the target).

### Model (`PresetRecord`)

```csharp
public sealed class PresetRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDefault { get; set; }
}
```

### ViewModel (`PresetViewModel`)

Observable object with `Name`, `Description`, `IsDefault`, `IsSelected` (drives
the section-list radio), `IsSaveTarget` (drives the Save-Preset dialog's radio,
kept separate so picking a save target does not flip the active preset), and
`IsKebabMenuOpen`.

## Storage layer (`LocalStorageService`)

- `GetPresets(owner, device)` — returns all rows for the (owner, device) scope,
  ordered by `id`. Loads `is_default`.
- `InsertPreset(owner, device, record)` — persists `is_default` alongside the
  name/description.
- `PromotePresetToDefault(id)` — the single point that maintains the "exactly one
  default per (owner, device)" invariant. Uses one transaction: reads the
  target's scope, `UPDATE ... SET is_default = 0` on every sibling, `UPDATE ...
  SET is_default = 1` on the target.
- `DeletePreset(id)` — refuses to delete rows where `is_default = 1` (safety net
  behind the UI guard).
- `LoadPresetSnapshotToDevice` / `SavePresetSnapshotFromDevice` — copy the RGB /
  effect / button-binding rows for the active (owner, device) between the
  `device_*` and `preset_*` tables so switching a preset actually applies to the
  hardware.
- `ResetPresetSnapshot(id)` — drops a preset's snapshot rows so the next apply
  yields the device's default state.

## ViewModel behaviour (`DeviceDetailsViewModel.Presets.cs`)

### Load

`LoadPresetsForDevice` is called from `SetDevice`. Three cases:

1. **No rows exist** for this (owner, device) → insert a
   `Presets_DefaultName` ("Default") row with `IsDefault=true` and add it to the
   observable list. This is the "when a device is connected, a default preset is
   created" requirement.
2. **Rows exist but none is default** (legacy DBs, or the column existed before
   any row was inserted) → call `PromotePresetToDefault(records[0].Id)` and mirror
   in memory. Guarantees the "at least one default" invariant.
3. **Normal case** → hydrate `PresetViewModel` from records (name / description
   / IsDefault).

### CRUD + dialog commands

- **New / Duplicate / Edit** — share the New Preset dialog with a
  `PresetDialogMode` enum switching title / subtitle / confirm-button label.
  `ApplyEditToExistingPreset` persists `name` / `description` via
  `UpdatePreset`, and if the user ticks "Set as default" and the row isn't
  already default, calls `PromotePresetToDefault(id)`. Un-checking "Set as default"
  on the current default is a **no-op** (can't demote without picking another →
  preserves the "at least one default" invariant).
- **Reset** — opens the Reset Preset dialog; Confirm calls
  `ResetPresetSnapshot`.
- **Delete** — hidden-by-default via `IsEnabled` binding on the kebab entry,
  and the command itself short-circuits on `preset.IsDefault`.
- **Save Preset** — opens the Save Preset dialog. The user picks a target row
  and confirms; `ApplyPresetToDevice` is what actually copies the current
  device state into the target's snapshot and clears `HasUnsavedPresetChanges`.

### Unsaved-Changes dialog (dual-purpose)

Single dialog reused for two flows via an internal `UnsavedChangesReason` enum
(`LeavingView`, `SwitchingPreset`) and a computed `UnsavedChangesBody` string
that resolves to a different resource key per reason.

- `OpenUnsavedChangesForLeaving()` — public; called by
  `MainWindow.OnDeviceDetailsBack` when `HasUnsavedPresetChanges` is true. Discard fires
  `ExitConfirmedAfterUnsavedChanges` which the shell subscribes to for the
  actual navigation.
- `OpenUnsavedChangesForSwitch(target)` — private; called by `SelectPreset` when
  the user picks a different preset while dirty. The pending target is parked in
  `_pendingPresetSwitch`; Discard clears dirty and re-invokes `SelectPreset` on
  the parked target. Also closes the header dropdown before opening the dialog
  (2026-08-17): the pick that gets here is a click inside the dropdown popup, so
  light-dismiss never fires and the list otherwise stayed floating over the
  confirmation dialog.
- **Save** — closes the Unsaved-Changes dialog and opens the Save Preset
  dialog. The user completes (or cancels) the save explicitly; auto-navigation
  after save was intentionally left out to avoid fragile chains.
- **Cancel (X)** — drops the parked target (if any) and closes the dialog.

## UI

### Structural refactor

All four preset dialogs were extracted from inline `<Panel>` blocks in
`MainWindow.axaml` into standalone UserControls under
`Views/DeviceDetails/Dialogs/`:

- `NewPresetDialog.axaml`
- `ResetPresetDialog.axaml`
- `SavePresetDialog.axaml`
- `UnsavedChangesDialog.axaml`

Each is a 620-wide chamfered modal with a purple `DropShadowEffect` on the
inner `<Panel>` (not on the UserControl itself — the UserControl's content
presenter clips the 100 px shadow bleed) and `ClipToBounds="False"` on the
UserControl to keep the shadow visible past the fixed bounds.

`MainWindow.axaml` now hosts them as element references inside the
`DialogOverlay` grid, with the DataContext bound to
`{ElementName=DeviceDetailsView, Path=DataContext}`.
`MainWindow.axaml.cs::RefreshDialogOverlay` toggles their visibility.

### Preset section (`PresetsSectionView.axaml`)

- Header row: `Total: N` label (Segoe UI) + `GAMES` category label
  (`AppPixelFont`) aligned to the far edges.
- Item rows: `RadioButton.presetItemRow`, 88 px tall, `#0DFFFFFF` background,
  `#6357FF` 2px border when `:checked`. Layout: radio + name / description /
  category badge / kebab.
- Kebab menu items: Edit / Duplicate / Reset / Delete. Delete is dimmed to
  `Opacity=0.4` with `Cursor=Arrow` and no red hover fill when
  `IsEnabled="{Binding !IsDefault}"`.

### Save Preset dialog (`SavePresetDialog.axaml`)

- 620 × 580 chamfered modal.
- Search box (`TextBox` with its `PART_BorderElement` forced transparent on
  default / `:pointerover` / `:focus` states — Avalonia's default TextBox theme
  otherwise paints a light hover fill inside our own bordered `<Border>`).
- `Total: N` counter bound to `PresetsCountLabel`.
- Scrollable list of preset rows: name (white, SemiBold when `:checked`) +
  Description subtitle + category badge (monitor icon + "Desktop").
- Row selection styles pin `Background` to `#0DFFFFFF` in all states (default,
  `:pointerover`, `:checked`) — background is static, only the border changes.

### Unsaved-Changes dialog (`UnsavedChangesDialog.axaml`)

- 620 × 220 chamfered modal.
- Title `UnsavedChanges_Title` — "Save changes to the current preset?"
- Body bound to `UnsavedChangesBody` (computed):
  - `UnsavedChanges_Body` — "…Do you want to save the changes before leaving?"
  - `UnsavedChanges_Body_Switching` — "…Do you want to save the changes before
    switching presets?"
- Buttons: "Don't Save" (secondary, 139 × 40) + "Save" (primary, 84 × 40).
- Close X in the top-right chamfer maps to `CancelUnsavedChangesCommand`.

## Shell wiring (`MainWindow.axaml.cs`)

- `OnDeviceDetailsBack` checks `HasUnsavedPresetChanges` first: dirty → call
  `_deviceDetailsViewModel.OpenUnsavedChangesForLeaving()`; clean → call
  `PerformExitDeviceDetails()` (the actual navigation).
- Subscribes to `ExitConfirmedAfterUnsavedChanges` at ctor time (unsubscribes in
  teardown) → `PerformExitDeviceDetails`. This is how Discard actually
  navigates home.
- `OnDeviceDetailsViewModelPropertyChanged` and `RefreshDialogOverlay` include
  `IsUnsavedChangesOpen` alongside the existing four dialog flags.

## Localization

`Strings.en.axaml` / `Strings.uk.axaml`:

- `Presets_DefaultName` — "Default" / "За замовчуванням"
- `Presets_Header_TotalFormat`, `Presets_Header_ItemsLabel`, `Presets_EmptyHint`
- `NewPreset_*` / `EditPreset_*` / `DuplicatePreset_*` (title / subtitle /
  confirm labels for the shared dialog)
- `ResetPreset_*` (title / body / confirm)
- `SavePreset_*` (title / subtitle / search placeholder / confirm)
- `UnsavedChanges_Title`, `UnsavedChanges_Body`,
  `UnsavedChanges_Body_Switching`, `UnsavedChanges_Discard`,
  `UnsavedChanges_Save`

## Tests (`tests/ProoveGaming.App.Tests/`)

- `DeviceDetailsPresetsViewModelTests.cs` — command surface: SelectPreset flips
  IsSelected and applies the snapshot; ConfirmNewPreset in each mode; edit /
  duplicate / delete guards; unsaved-changes flow (leaving vs switching);
  discard re-invokes SelectPreset on the parked target.
- `LocalStorageServicePresetsTests.cs` — schema migration adds `is_default`;
  `GetPresets` scoping by (owner, device); `InsertPreset` persists IsDefault;
  `PromotePresetToDefault` enforces the single-default invariant; `DeletePreset`
  refuses on `is_default = 1`.

Both files run against a temporary SQLite DB per test.

## Files touched (branch delta vs `main`)

- `src/ProoveGaming.App/Models/MacroRecord.cs` — `PresetRecord.IsDefault`.
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — schema + migration,
  `is_default` on Get / Insert, `PromotePresetToDefault`, guarded `DeletePreset`,
  snapshot copy helpers.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.*.cs` —
  new `Presets.cs` partial; `SetDevice` calls `LoadPresetsForDevice`;
  HasUnsavedPresetChanges flags set from Rgb / Buttons partials.
- `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/*` — four new UserControls +
  code-behinds.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/PresetsSectionView.axaml` —
  preset list, kebab menu, disabled Delete on default.
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — preset
  dropdown popup under the header.
- `src/ProoveGaming.App/Views/MainWindow.axaml{,.cs}` — dialogs hosted in the
  `DialogOverlay` grid; back-flow gated on `HasUnsavedPresetChanges`.
- `src/ProoveGaming.App/Views/MainWindow.Controls.cs` — `FindControl` accessors
  for the four preset dialog panels.
- `src/ProoveGaming.App/Resources/Strings.{en,uk}.axaml` — all preset /
  unsaved-changes strings.
- `tests/ProoveGaming.App.Tests/*PresetsTests.cs` — coverage described above.

## Review findings

### Round 1 — Ship: no

#### Code
- [x] `DeviceDetailsViewModel.DeletePreset` sets `SelectedPreset = next` directly instead of calling `SelectPreset(next)`, so `ApplyPresetToDevice` never runs on the row that now takes over — the live device state remains the deleted preset's live values instead of the next preset's snapshot. — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:523` — Should-fix
- [x] `DeviceDetailsViewModel.AppendNewPreset` inserts a preset with an empty snapshot and sets `SelectedPreset = vm` directly (no `SavePresetSnapshot`, no `ApplyPresetToDevice`). If the user later switches away and back to this row, `ApplyPresetToDevice` will run against the empty snapshot and wipe device state — silent data loss. Fix: capture the current device state into the fresh preset via `SavePresetSnapshot` immediately on Create. — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:427` — Should-fix
- [ ] All four dialog UserControls (`NewPresetDialog`, `ResetPresetDialog`, `SavePresetDialog`, `UnsavedChangesDialog`) set `x:CompileBindings="False"` while binding heavily to VM properties/commands (`UnsavedChangesBody`, `DiscardUnsavedChangesCommand`, `PresetDialogActionWidth`, …). Any rename or typo goes silent at build time and fails at runtime. Set `x:DataType="viewmodels:DeviceDetailsViewModel"` + `x:CompileBindings="True"`. — `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/*.axaml` — Should-fix — **deferred**: enabling compile bindings on all four dialogs is a self-contained refactor across nested `DataTemplate`s (needs per-template `x:DataType` + explicit `((viewmodels:DeviceDetailsViewModel)DataContext)` casts on the `$parent[ItemsControl]` cross-references in the Save dialog). Worth its own follow-up commit.
- [x] `NewPresetDialog` doesn't disable "Set as default" toggle when editing the current default. Un-ticking is silently no-op in `ApplyEditToExistingPreset` (documented), but the UI still lets the user click it and think it took effect. Bind `IsEnabled` on the toggle to `!(IsEditingPreset && target.IsDefault)` or hide it in that case. — `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/NewPresetDialog.axaml:133` — Should-fix — fixed via new `IsSetAsDefaultLocked` VM state (`Presets.cs`) + `IsEnabled="{Binding !IsSetAsDefaultLocked}"` on the toggle.
- [ ] `LocalStorageService.DeletePreset` silently returns when `is_default = 1`; `PromotePresetToDefault` silently rolls back when the id is missing. No throw, no return code, no log — debugging a "why did nothing happen" trace requires reading the source. Consider throwing `InvalidOperationException` for the default-guard case (it indicates a caller bug) and letting the missing-id case throw naturally. — `src/ProoveGaming.App/Services/LocalStorageService.cs:786`, `:820` — Should-fix — **deferred**: swapping silent no-op for throw changes the storage contract; the callers currently rely on the no-op semantic (VM already short-circuits, delete kebab is `IsEnabled=false` on default). Worth its own design pass rather than a silent tightening.
- [x] `PresetRecord` lives inside `Models/MacroRecord.cs`. Filename doesn't match content and grep-for-file becomes misleading. Move to `Models/PresetRecord.cs`. — `src/ProoveGaming.App/Models/MacroRecord.cs:26` — Nit
- [x] Mojibake in `MainWindow.axaml` comment: `Recorded steps ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢…` (double-encoded em-dash from a prior "UI fix" commit on this branch). Replace with `—`. — `src/ProoveGaming.App/Views/MainWindow.axaml:783` — Nit

#### Tests
- [x] Three tests **fail** on the branch (`dotnet test ./tests/ProoveGaming.App.Tests --filter Presets`, 29 passed / 3 failed):
  - `SelectPreset_ClearsHasUnsavedPresetChanges` — replaced by `SelectPreset_CleanSwitch_ClearsHasUnsavedPresetChanges` + `SelectPreset_DirtySwitch_OpensUnsavedChangesDialog_AndDoesNotSwitch` + `SelectPreset_DirtySwitch_ThenDiscard_SwitchesToParkedTarget_AndClearsDirty` + `SelectPreset_DirtySwitch_ThenCancel_DropsParkedTarget_AndKeepsDirty`.
  - `SavePreset_Execute_ClearsHasUnsavedPresetChanges` → renamed `SavePresetThenConfirm_ClearsHasUnsavedPresetChanges` and now calls `ConfirmSavePresetCommand` after `SavePresetCommand`. Companion `SavePreset_Execute_OpensDialog_DoesNotClearDirtyYet` asserts the intermediate open-dialog state.
  - `SavePreset_Execute_SnapshotsCurrentBindingsIntoDb` → renamed `SavePresetThenConfirm_SnapshotsCurrentBindingsIntoDb` and confirms before the round-trip assertion.
- [x] Storage-side coverage added: `InsertPreset_PersistsIsDefault_AndGetPresetsReadsItBack`, `DeletePreset_OnDefaultRow_IsNoOp`, `PromotePresetToDefault_PromotesTarget_AndDemotesSiblings`, `PromotePresetToDefault_LeavesOtherDevicesUntouched`.
- [x] `LoadPresetsForDevice` self-heal coverage added: `LoadPresetsForDevice_EmptyList_AutoCreatesDefaultRow`, `LoadPresetsForDevice_LegacyRowsWithoutDefault_PromoteFirstRow`. Method visibility relaxed to `internal` (covered by `InternalsVisibleTo`).
- [x] Unsaved-Changes dual-flow coverage added: `SelectPreset_DirtySwitch_ThenDiscard_SwitchesToParkedTarget_AndClearsDirty`, `SelectPreset_DirtySwitch_ThenCancel_DropsParkedTarget_AndKeepsDirty`, `OpenUnsavedChangesForLeaving_DiscardFires_ExitConfirmedEvent`, `OpenUnsavedChangesForLeaving_Cancel_DoesNotFireExit_AndKeepsDirty`. Plus the new `DeletePreset_OfActivePreset_SwitchesToNextRow_AndAppliesItsSnapshot` and `EditPreset_OnDefaultRow_LocksSetAsDefaultToggle` cover the two code fixes above.

Result: `dotnet test ./tests/ProoveGaming.App.Tests` reports **88/88 passing (was 88 with 3 failing preset tests before Round 1 fixes)**. Two Round-1 items are explicitly deferred (see notes above); everything else is closed.
