# Plan — Per-device macros

## Goal

Make the macro library **per-device** instead of shared across every device. Today the `macros`
table is scoped only by `(owner_kind, owner_id)`, and `LoadMacroLibrary()` runs once in the
`DeviceDetailsViewModel` constructor, so opening any device shows the same macros. We scope macros
by the device's settings identity — exactly how `button_bindings`, `device_rgb` and
`device_settings` are already scoped — and load them per device.

## Scope

**In:** macro storage schema + migration, `GetMacros`/`SaveMacro` device scoping, loading the
library per device in `SetDevice`, tests.

**Out:** macro recording/encoding, the device-write path, presets structure (unchanged; a device's
presets already reference that device's macros by id).

## Options considered

1. **Scope by settings identity `(VendorId, SettingsProductId, SettingsDeviceKey)`** — mirrors the
   existing per-device tables (button_bindings/rgb/settings). Merged-transport devices (TAIPAN)
   keep one macro set across transports; distinct units get distinct sets, consistent with bindings.
2. **Scope by live routing identity `(VendorId, ProductId, DeviceKey)`** — would split a merged
   device's macros per transport, diverging from how its bindings/settings behave. Rejected.

## Recommended approach

Option 1. Add `vendor_id`, `product_id`, `device_key` to `macros`, filter reads/writes on them, and
load the library in `SetDevice` (clearing first) before `LoadButtonBindings` so bindings still
resolve `macro_id` → `MacroViewModel` from the just-loaded set.

**Existing macros (migration):** the user chose **fresh start** — on the one-time schema migration
(old DB gaining the new columns), delete all pre-existing shared macros and the `macro_events`,
`button_bindings` and `preset_button_bindings` rows referencing them. New DBs are unaffected.

## File-level change list

- `src/ProoveGaming.App/Services/LocalStorageService.cs`
  - `macros` CREATE TABLE: add `vendor_id INTEGER NOT NULL DEFAULT 0`, `product_id INTEGER NOT NULL DEFAULT 0`, `device_key TEXT NOT NULL DEFAULT ''`.
  - `AddColumnIfMissing`: return `bool` (whether it added a column).
  - Migration block: add the three columns; when `vendor_id` was newly added (old DB), run the
    fresh-start clear (delete legacy macros + their events + referencing button/preset bindings).
  - `GetMacros`: add `(int vendorId, int productId, string deviceKey)` params + `WHERE` filter.
  - `SaveMacro`: add `(int vendorId, int productId, string deviceKey)` params; INSERT stores them.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs`
  - Remove `LoadMacroLibrary()` from the constructor; call it in `SetDevice` after `Device = device`
    and **before** `LoadButtonBindings(device)`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs`
  - `LoadMacroLibrary()`: `Macros.Clear()` first; pass `Device.VendorId/SettingsProductId/SettingsDeviceKey` to `GetMacros`; guard `Device == null`.
  - `SaveCreateMacro()`: pass the same device identity to `SaveMacro` (guard `Device != null`).
- `tests/ProoveGaming.App.Tests/LocalStorageServiceSettingsTests.cs`
  - Update `MigrateGuestToUserAsync_MovesMacros` for the new signatures (guest→user re-key preserves device scope).
  - Add a test: a macro saved for device A is returned by `GetMacros` for A, and **not** for device B.

## Acceptance criteria

- Given a macro created while device A is open, when device B is opened, then B's macro list does
  not contain it; reopening A shows it.
- Given a macro saved for `(vid, pidA, keyA)`, when `GetMacros` is called for `(vid, pidB, keyB)`,
  then the result does not include it.
- Given an old DB with shared macros, when the app starts on the new schema, then those macros and
  any button/preset bindings referencing them are removed (fresh start).
- Given guest macros for a device, when `MigrateGuestToUserAsync` runs, then they appear under the
  user owner for that same device and are gone for the guest.
- `dotnet build ./src/ProveGaming.slnx` succeeds; existing macro-assignment tests stay green.

## Test strategy

`tests/ProoveGaming.App.Tests` (this is an `add-*` feature; new coverage is required):
- **New:** macros are per-device (save A → visible for A, empty for B).
- **Updated:** `MigrateGuestToUserAsync_MovesMacros` with device-scoped signatures.
- Existing `DeviceDetailsMacroAssignmentTests` must stay green (they drive the VM through `SetDevice`).
- Not automated: the visual per-device switch in the Macros page (UI-thread/Avalonia) — covered by QA.

## QA (manual test steps)

1. Launch the app, connect two different mice (or two units).
2. Open mouse A → Macros → create a macro "AAA".
3. Go back, open mouse B → Macros. Expected: "AAA" is **not** listed; B starts with its own (empty) list.
4. On B, create "BBB". Go back to A → Macros. Expected: A shows only "AAA", not "BBB".
5. Assign a macro to a button on A, switch to B and back to A. Expected: the assignment and macro persist for A.

## Review focus

`null-safety` (Device null in load/save), `thread-safety` (SQLite single-writer via existing lock),
`resource disposal` (n/a — reuses existing readers), `DI lifetimes` (n/a).

## Performance & quality

No hot paths. `LoadMacroLibrary` runs on `SetDevice` (device open), a UI action, reading a small
table synchronously like the sibling loads (button bindings, presets) already do. `Macros.Clear()`
+ re-add fires `CollectionChanged` a few times (rebuilds the catalog Macros group) — same cost as
the existing initial load, acceptable.

## Risks

- **Binding resolution ordering:** `LoadButtonBindings` resolves `macro_id` against `Macros`; the
  library must load first in `SetDevice`. Enforced by ordering.
- **Fresh-start delete is destructive** but gated to the one-time column-add on old DBs; new DBs
  never hit it. Accepted per the user's choice.

## Open questions

None — migration behavior confirmed (fresh start).

## Review findings

_Filled in by /reviewer._
