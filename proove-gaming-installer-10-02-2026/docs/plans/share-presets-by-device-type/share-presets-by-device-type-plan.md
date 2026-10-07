# Plan: Share presets by device type (VID/PID), not by physical unit

> **2026-08-25 schema note:** the `presets` table gained `uid` (client-generated GUID,
> unique, assigned at insert and backfilled for existing rows) and `server_updated_at`
> (the server's timestamp echoed verbatim; NULL = never synced) for API v2.0 sync — see
> [../add-api-v2-sync/add-api-v2-sync-plan.md](../add-api-v2-sync/add-api-v2-sync-plan.md).
> The device-type scoping this plan describes is unchanged.

## Goal

Presets currently belong to one **physical device instance** — `LocalStorageService`'s
`presets` table (and its snapshot children) is keyed on
`(owner_kind, owner_id, vendor_id, product_id, device_key)`, where `device_key` is the
Windows Container ID that uniquely identifies one physical unit (see `docs/CONTEXT.md`
`deviceKey`/`Container ID`). Two identical units of the same model — e.g. two BLAZE mice —
therefore get two completely independent preset lists today, which is confusing: "Default",
"FPS", "Work" on unit A mean nothing on unit B, and creating a preset on one unit never shows
up on the other.

This plan rescopes the preset **list and its saved content** (name, description, default flag,
and the RGB/button/DPI/key-binding snapshot each preset holds) to `(owner, vendor_id,
product_id)` — shared across every physical unit of that model the user owns. Which preset is
**currently active on a given plugged-in unit right now**, and the unit's live hardware state,
stay exactly as they are today: per-`device_key`. Two connected units of the same model will see
the same preset list and can each have a *different* one selected/active at the same time.

## Scope

**In:**
- Drop `device_key` from the `presets` table's effective scope: `GetPresets`, `InsertPreset`,
  `CreateDefaultPresetIfNotExists` stop taking/using a `deviceKey` parameter for the `presets`
  table itself.
- No migration for existing per-unit `presets` rows — deliberate, and confirmed by the user after
  the consequence below was understood. This is still dev-only data; deleting the local
  `proovegaming.db` is the remedy if old per-unit presets look wrong after upgrading.

  **Correction (was stated backwards in earlier revisions of this plan).** Dropping the
  `device_key` predicate makes the query **wider, not narrower**: `WHERE owner_kind=… AND
  owner_id=… AND vendor_id=… AND product_id=…` matches rows with *any* `device_key`, legacy
  Container IDs included. Legacy rows are therefore **not** orphaned or invisible — they **merge**
  into one list, each unit bringing its own `is_default` row. Nothing repairs that:
  `CreateDefaultPresetIfNotExists` stops at the first default it finds, and Delete refuses to
  remove a default at all, so a pre-rescope DB can show two permanently undeletable "Default" rows
  with no in-app way out. Accepted for dev; delete the DB.
- App-side call sites in `DeviceDetailsViewModel.Presets.cs` that currently pass
  `Device.SettingsDeviceKey` into the three methods above stop passing it.
- Test updates: the existing `InsertPreset_DifferentDeviceKeys_DoNotCollide` test (encodes the
  *old*, per-unit behavior) is replaced with a test asserting the *new*, shared behavior. New
  tests for the "two connected units, shared list, independent active selection" scenario at
  the ViewModel level.
- `docs/CONTEXT.md`: update the `Default preset` glossary entry (currently says "per (owner,
  device)") and `PresetRecord.cs`'s doc comment to say "per (owner, device type)".

**Out (unchanged, explicitly not touched):**
- `device_rgb`, `device_rgb_effect`, `button_bindings`, `device_settings`, `key_bindings` — live
  hardware-state tables. Stay scoped by `device_key` exactly as today; two connected units of the
  same model keep independent live RGB/button/DPI state.
- `device_applied_preset` — "which preset is currently applied to *this* unit" tracking. Stays
  scoped by `device_key`; two connected units of the same model can have different presets
  active at once even though they now share one list.
- `SavePresetSnapshot`, `LoadPresetSnapshotToDevice`, `CopyPresetSnapshot`, `ResetPresetSnapshot`,
  `UpdatePreset`, `PromotePresetToDefault`, `GetAppliedPreset`, `SetAppliedPreset` — verified
  below (see *Why these need no changes*) that none of these require a code change; their
  `deviceKey` parameters (where present) already correctly refer to the live tables that stay
  per-unit, not to the `presets` table's own scope.
- `MigrateMacrosToDevices` / `OwningDevicesForMacro`'s one-time legacy macro migration (still
  joins `presets` on `device_key`) — this is a one-time pass gated on pre-existing `macros` rows
  with `vendor_id = 0`; it has already run on any real install and is a no-op on a fresh or
  already-migrated DB. Left untouched; touching it would be scope creep for a dead code path.
- Cross-device preset copy/import-export, preset preview/diff, sync to cloud — same "out of
  scope" list as the original `docs/plans/presets/presets-plan.md`.

## Why these need no changes (read before implementing)

Verified by reading every `presets`-table-touching method in `LocalStorageService.cs`:

- `preset_rgb`, `preset_rgb_effect`, `preset_button_bindings`, `preset_settings`,
  `preset_key_bindings` have **no `device_key` column at all** — they're keyed purely by
  `preset_id` (FK to `presets.id`). Once `presets` itself is rescoped, these children need zero
  changes; they already inherit scope entirely through `preset_id`.
- `PromotePresetToDefault` (`LocalStorageService.cs:1454`) **self-derives** the scope to demote
  siblings in by reading `owner_kind/owner_id/vendor_id/product_id/device_key` off the target
  row itself (`SELECT ... FROM presets WHERE id = $id`). Once every `presets` row uniformly
  carries `device_key = ''`, this continues to work correctly with no code change — it demotes
  every other preset in the same `(owner, vendor_id, product_id)` group, dropping `device_key`
  from consideration for free.
- `SavePresetSnapshot` / `LoadPresetSnapshotToDevice` (`:1660`, `:1538`) take a `deviceKey`
  parameter, but it is used **exclusively** to read/write the live `device_rgb` /
  `device_rgb_effect` / `button_bindings` / `device_settings` / `key_bindings` rows — never to
  filter `presets` (they operate on a `presetId` already resolved by the caller). These tables
  stay per-unit by design (Out of scope above), so these methods are correct as-is.
- `CopyPresetSnapshot`, `ResetPresetSnapshot`, `UpdatePreset`, `DeletePreset` all operate purely
  by `presetId` (never take a `deviceKey`) — already correct.
- `GetAppliedPreset` / `SetAppliedPreset` operate on `device_applied_preset`, which is Out of
  scope (stays per-unit) — correct as-is.
- The guest→user re-key migration (`MigrateGuestToUserAsync`, `:2240`) already re-keys `presets`
  by `id` alone ("id is globally unique, no natural-key collision to resolve") — no `device_key`
  involved, correct as-is. `device_applied_preset`'s re-key block (`:2204`) correctly keeps
  `device_key` in its natural-key collision check — unaffected, correct as-is.

This means the **entire behavioral change is 3 methods** in `LocalStorageService.cs`
(`GetPresets`, `InsertPreset`, `CreateDefaultPresetIfNotExists`) plus their call sites in
`DeviceDetailsViewModel.Presets.cs`, plus the one-time migration. Nothing else in the file needs
to move.

## Options considered

**A. Drop `device_key` from the 3 methods' SQL/parameters, leave the column in the schema
(chosen).** The `presets` table's `device_key` column stays declared (`TEXT NOT NULL DEFAULT
''`), but nothing meaningful is ever written to it again — inserts stop naming the column, so it
defaults to `''`. No `ALTER TABLE`, no table-rebuild migration. Matches this file's own
established convention: every existing schema change in `EnsureSchema` is strictly additive
(`AddColumnIfMissing`, never a `DROP COLUMN` or rebuild) — see the `is_rainbow` and `is_default`
migrations already there, and the explicit comment on the macros migration explaining why columns
are added, never dropped, because `EnsureSchema` runs on every construction.
*Trade-off:* the column becomes permanently vestigial (`''` forever) — mildly untidy, but zero
migration risk and consistent with the file's existing style.

**B. Actually remove the `device_key` column from `presets` via a full table rebuild** (`CREATE
TABLE presets_new ... ; INSERT INTO presets_new SELECT ... FROM presets; DROP TABLE presets;
ALTER TABLE presets_new RENAME TO presets;`). Cleaner end-state schema, but introduces exactly
the kind of migration risk (data during rebuild, FK re-declaration, `preset_rgb`/etc. child FKs
pointing at `presets(id)` need the rebuild to preserve `id` values exactly) this codebase has so
far deliberately avoided, for a column that costs nothing left empty. Rejected — the cost doesn't
buy anything current code or the next migration needs.

## Recommended approach

Option A. Concretely:

1. `GetPresets(ownerKind, ownerId, vendorId, productId)` — drop the `deviceKey` parameter and
   the `AND device_key = $dk` predicate.
2. `InsertPreset(ownerKind, ownerId, vendorId, productId, preset)` — drop the `deviceKey`
   parameter; drop `device_key` from the `INSERT` column list and `VALUES` (column keeps its
   schema default of `''`).
3. `CreateDefaultPresetIfNotExists(ownerKind, ownerId, vendorId, productId, defaultName)` — drop
   the `deviceKey` parameter; drop `device_key` from the scan `WHERE` and the seed `INSERT`.
4. No migration for pre-rescope `presets` rows (dev-only data — see Scope). Old rows keep whatever
   `device_key` they were written with and **merge into the shared list** (the predicate was
   removed, not narrowed), which can leave a pre-rescope DB with more than one `is_default` row.
   If a dev DB shows duplicate or undeletable presets after this change, delete the local
   `proovegaming.db` file.
5. `DeviceDetailsViewModel.Presets.cs`: drop `Device.SettingsDeviceKey` from the call sites of
   `CreateDefaultPresetIfNotExists`, `GetPresets` (both in `LoadPresetsForDevice`), and
   `InsertPreset` (in `AppendNewPresetAsync`). Every other preset-related call in this file
   (`SavePresetSnapshot`, `LoadPresetSnapshotToDevice`, `GetAppliedPreset`, `SetAppliedPreset`)
   keeps passing `Device.SettingsDeviceKey` unchanged — those calls are about live per-unit state
   and applied-preset tracking, both explicitly out of scope.

`Device.SettingsProductId` (not the raw transport `ProductId`) is already what
`DeviceDetailsViewModel.Presets.cs` passes today and must keep passing — it's already the
correct "device type" identity per `HomeViewModel.ApplyDeviceIdentity`: for a merge-transport
product (e.g. TAIPAN cable/dongle/BT) it's pinned to one shared catalog id regardless of which
transport is live, so a preset created while TAIPAN is on the dongle is visible when it's next
opened over the cable. No new usage needed here — just confirm no call site was accidentally
using `Device.ProductId` instead (none currently are, per the file read for this plan).

## File-level change list

- `src/ProoveGaming.App/Services/LocalStorageService.cs`
  - No `EnsureSchema()` migration — see Scope.
  - `GetPresets`: drop `deviceKey` parameter and predicate.
  - `InsertPreset`: drop `deviceKey` parameter and column.
  - `CreateDefaultPresetIfNotExists`: drop `deviceKey` parameter, predicate, and column.
  - Update the `// ── Presets (per owner, per physical device) ──` section comment (`:1260`) to
    say "per owner, per device type (VID/PID)".
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs`
  - `LoadPresetsForDevice`: drop `Device.SettingsDeviceKey` argument from the
    `CreateDefaultPresetIfNotExists` and `GetPresets` calls.
  - `AppendNewPresetAsync`: drop `Device.SettingsDeviceKey` argument from the `InsertPreset` call.
  - No other method in this file changes.
- `src/ProoveGaming.App/Models/PresetRecord.cs` — update the doc comment ("per owner, per
  physical device" → "per owner, per device type").
- `docs/CONTEXT.md` — update the `Default preset` entry: "for its `(owner, device)`" →
  "for its `(owner, device type)`"; note it is shared across physical units of the same
  VID/PID, distinct from [[deviceKey]]/[[Container ID]]-scoped live device state.
- `tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs`
  - Replace `InsertPreset_DifferentDeviceKeys_DoNotCollide` (asserts the *old* per-unit
    isolation — will not compile once `deviceKey` is dropped from the signatures) with
    `InsertPreset_SameVidPid_DifferentPhysicalUnits_ShareTheSameList` or similar, asserting two
    different logical units of the same `(vendor, product)` both see the one shared preset
    inserted for that scope.
  - Update every other call site in this file that currently passes a `DeviceKey`/`"unit-a"`-style
    argument to `GetPresets`/`InsertPreset`/`CreateDefaultPresetIfNotExists` to match the new
    signatures.
- `tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs` /
  `DeviceDetailsPresetApplyTests.cs` — add a test simulating two `DeviceCardViewModel`s with the
  same `VendorId`/`SettingsProductId` but different `DeviceKey` (mirrors two connected BLAZE
  units): confirm `LoadPresetsForDevice` on either produces the identical `Presets` list (by
  name/id), while each can independently have a different `SelectedPreset` /
  `HasUnsavedPresetChanges` without affecting the other.

## Acceptance criteria

- Given two `DeviceCardViewModel`s with the same `VendorId`/`SettingsProductId` and different
  `DeviceKey` (two physical units of one model) → open Device Details for each → both show the
  identical preset list (same names, same ids).
- Given a preset created while unit A is open → open Device Details for unit B of the same model
  → the new preset appears in unit B's list without needing a restart or reconnect.
- Given unit A has preset "FPS" selected/active and unit B has preset "Work" selected/active
  (same model, both connected) → switching the active preset on unit A does not change unit B's
  `SelectedPreset` or live RGB/button state, and vice versa.
- Given two different `(vendor_id, product_id)` device types (e.g. BLAZE and TAIPAN) → their
  preset lists remain fully independent — this plan does not merge presets across different
  models, only across physical units of the *same* model.
- `device_rgb`, `device_rgb_effect`, `button_bindings`, `device_settings`, `key_bindings`, and
  `device_applied_preset` continue to be scoped by `device_key` exactly as before this change —
  no regression in per-unit live-state isolation.

## Test strategy

This is a behavior change to an already-tested feature (`LocalStorageServicePresetsTests.cs`,
`DeviceDetailsPresetsViewModelTests.cs`, `DeviceDetailsPresetApplyTests.cs` all exist and pass
today). Per the refactor convention: confirm existing coverage, then extend/replace for the new
behavior — do not leave the old per-unit-isolation assertion in place, since it now asserts the
behavior this plan removes.

- **`tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs`** (storage layer):
  - Replace `InsertPreset_DifferentDeviceKeys_DoNotCollide` with a same-VID/PID-shared-list test
    (see File-level change list).
  - Existing `InsertPreset_DifferentOwners_DoNotCollide` and default/promote/delete tests should
    continue to pass with the `deviceKey` argument simply removed from call sites — confirms
    owner isolation is untouched.
- **`tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs`** (ViewModel layer):
  - New: two-unit-same-model scenario (see File-level change list / acceptance criteria) —
    shared list, independent `SelectedPreset`.
- **`tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`**: re-run as-is where
  possible; extend only if a call site's signature change (dropped `deviceKey` argument) requires
  it to compile.
- **Not covered by automated tests:** real two-BLAZE hardware behavior (covered by the QA section
  below instead) — the App-level tests use `DeviceCardViewModel` doubles, not real HID
  enumeration, consistent with how existing preset tests already work.

## QA (manual test steps)

1. Connect two identical BLAZE mice at the same time.
2. Open Device Details for the first mouse. Create a new preset named "Test A". Go back to Home.
3. Open Device Details for the second mouse. **Expected:** "Test A" appears in its preset list
   too, without restarting the app.
4. On the second mouse, select preset "Test A", then create a second preset named "Test B" and
   select it.
5. Go back to Home, then reopen Device Details for the first mouse. **Expected:** the first
   mouse's currently-selected preset is unaffected by what the second mouse just did (it did not
   jump to "Test B"), but "Test B" is visible in its dropdown list as an available preset.
6. Change an RGB colour or button binding on the first mouse and Save it into "Test A".
   **Expected:** on the second mouse, switching to "Test A" now shows the updated colour/binding
   — confirming the saved snapshot itself is shared, not just the preset's name.
7. Disconnect one mouse, leaving one connected. **Expected:** no crash, no error toast; the
   remaining mouse's preset list and behavior are unaffected.

## Review focus

- `null-safety` — `GetPresets`/`InsertPreset`/`CreateDefaultPresetIfNotExists` after the
  parameter removal; no behavior change to null-handling, just fewer parameters to pass through.
- `async/await` — none of the touched methods are async (matches the existing synchronous
  `LocalStorageService` preset API); no new async surface introduced.

## Performance & quality

No hot paths touched — preset CRUD is a cold-path, low-frequency operation (device open,
explicit user actions). No new allocations of note; this plan removes code rather than adding
any (no migration).

## Risks

- **Old per-unit `presets` rows merge in, and can leave duplicate undeletable defaults.** Rows
  written before this ships (carrying a real `device_key`) still satisfy the new
  `(owner, vendor_id, product_id)`-only predicate, so both units' presets appear in one list, each
  keeping its own `is_default = 1`. `CreateDefaultPresetIfNotExists` breaks at the first default
  it sees and Delete refuses to remove a default, so the extra "Default" rows cannot be removed
  from the UI. Accepted deliberately (dev-only data, confirmed with the user); remedy is deleting
  the local `proovegaming.db`. **This must be revisited before the rescope ships with real user
  data** — at that point it needs the one-time normalisation this plan chose not to build
  (`UPDATE presets SET device_key = ''` plus a keep-lowest-id dedupe of `is_default`).
- **Two connected units racing to auto-create the default preset.** If both units of a model are
  opened in Device Details in quick succession before either's `CreateDefaultPresetIfNotExists`
  has committed, there's a narrow window where both could independently decide "no default exists
  yet" and each insert one, leaving two `is_default = 1` rows until the next
  `PromotePresetToDefault` fixes it up. This race already exists today in the per-unit-scoped
  code (nothing new introduced by this plan) — worth noting, not worth fixing here unless the
  reviewer disagrees, since the schema doesn't have a uniqueness constraint enforcing "at most one
  default" and never has.
- **Vestigial `device_key` column.** Per the chosen Option A, `presets.device_key` remains in the
  schema forever holding `''`. Low risk, but flag in the PR so nobody re-adds a foreign-key-style
  assumption onto it later.

## Open questions

None — scope was confirmed directly with the user before this plan was written (shared list +
snapshot content only; live per-unit state and applied-preset tracking stay as-is), and the
migration was subsequently dropped entirely at the user's request (dev-only data — delete the
local DB file instead if needed).

## Implementation outcome

Implemented as planned. `GetPresets` / `InsertPreset` / `CreateDefaultPresetIfNotExists` dropped
their `deviceKey` parameter and predicate; `presets.device_key` is left in the schema as a
vestigial always-`''` column (Option A). Every other preset method was confirmed by reading to
need no change — the `preset_*` snapshot children key purely off `preset_id` and
`SavePresetSnapshot` / `LoadPresetSnapshotToDevice`'s `deviceKey` argument only ever addresses the
live per-device tables, which stay per-unit by design. **One exception found later in review:**
`PromotePresetToDefault` did need a change — see the Review findings journal (M2).

Two deviations from the file-level change list, both at the user's direction:

- **No migration.** The planned one-time wipe (and its two tests) was written, then removed —
  dev-only data, so deleting the local `proovegaming.db` is the fix if stale per-unit presets
  look wrong. Note the earlier wording here ("orphaned, not a crash") was **wrong** and is
  corrected in Scope/Risks above: legacy rows *merge* into the shared list rather than being
  ignored, and can leave duplicate undeletable defaults. The decision to ship without a migration
  stands, but on an accurate description of the consequence.
- Call sites in `LocalStorageServiceMacroScopeTests.cs` and `LocalStorageServiceSettingsTests.cs`
  also needed updating (the plan's list named only the two preset test files), and
  `AppendDuplicatePreset` also calls `InsertPreset` (the list named only `AppendNewPresetAsync`).

Tests: `InsertPreset_DifferentDeviceKeys_DoNotCollide` (which encoded the old per-unit isolation)
replaced with a shared-list assertion; new ViewModel-level tests cover two physical units of one
model sharing a list while keeping independent `SelectedPreset`. Suite green at the time of
delivery.

## Review findings

### Round 1 (external review) — M2

- [x] **M2. Legacy per-unit presets merge instead of being orphaned, and
  `PromotePresetToDefault` was still scoped by `device_key`** —
  `src/ProoveGaming.App/Services/LocalStorageService.cs` (`GetPresets`,
  `CreateDefaultPresetIfNotExists` scan, `PromotePresetToDefault` demote) — **confirmed; partly
  fixed, partly accepted**

  **Verified against the code, and it contradicted this plan.** `GetPresets` reads
  `WHERE owner_kind=… AND owner_id=… AND vendor_id=… AND product_id=…` with no `device_key`
  predicate — removing that predicate widens the query, so legacy rows carrying a real Container ID
  still match. This plan previously claimed such rows "never match … orphaned, not a crash". That
  was **wrong**, and Scope / Recommended approach / Risks / Implementation outcome above have all
  been corrected. The knock-on effects the report described are real: with each unit's own
  `is_default` row merged in, `CreateDefaultPresetIfNotExists` `break`s at the first default it
  finds and reports "nothing to do", while `DeletePreset` refuses to remove a default — leaving
  two permanently undeletable "Default" rows.

  **Fixed:** `PromotePresetToDefault`'s demote no longer filters on the target row's `device_key`;
  it now scopes by `(owner_kind, owner_id, vendor_id, product_id)`, matching `GetPresets`. The old
  filter worked on a fresh database only by coincidence (every row written since the rescope has
  `device_key = ''`); on a database still holding legacy rows it demoted only the new ones and
  silently left a second default behind. Regression test added:
  `PromotePresetToDefault_DemotesEveryPresetOfTheSameDeviceType` in
  `tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs`.

  **Not fixed — accepted, by explicit user decision.** The report's Option A (one-time
  normalisation: `UPDATE presets SET device_key = ''` plus a keep-lowest-id dedupe of
  `is_default`) was implemented and then reverted at the user's instruction: no DB migration is
  wanted, the merge is intentional, and this remains dev-only data where deleting
  `proovegaming.db` is an acceptable remedy. Residual risk is now stated accurately in Risks —
  a pre-rescope database can present duplicate, undeletable defaults, and **this must be revisited
  before the rescope ships with real user data.**

  Suite after the change: 366 passing, build clean.

- [x] **M3. `preset_button_bindings.macro_id` is a cross-scope reference: presets are shared per
  device type, macros are still per physical unit** —
  `src/ProoveGaming.App/Services/LocalStorageService.cs` (`GetMacros`),
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs`
  (`LoadButtonBindings`, `CurrentBindingSet`) — **confirmed; symptom fixed, root scope mismatch
  left open**

  **Verified end to end.** `GetMacros` filters on `device_key` and is called with
  `Device.SettingsDeviceKey`, so the macro library is per physical unit while the preset snapshot
  that references `macro_id` is now shared. In `LoadButtonBindings` the assignment was written
  before the macro was resolved, so an unresolvable id left `action = Macro` with no entry in
  `_buttonMacros`; `CurrentBindingSet` then emitted `(button, Macro, null)`. Traced into the device
  path: `X43ConfigComposer` takes the macro branch only when `b.Macro != null`, otherwise falls to
  `X43KeyMatrix.EncodeAction(Macro)`, which has no `Macro` case and returns `null`, so the composer
  `continue`s and leaves that slot at its factory baseline. Net effect on a BLAZE: the button
  silently performs its factory function while the UI still shows it bound to a macro that is not
  in the list, and `PresetMatchesLiveState` sees nothing wrong because the `macro_id` values match.
  Confirmed the report's scoping note: merge-transport models (TAIPAN) pin `SettingsDeviceKey` to
  `string.Empty`, so only devices with a real Container ID — BLAZE, the two-mice scenario this
  feature exists for — are affected.

  **Fixed (report's Option B).** `LoadButtonBindings` now resolves the macro first and skips the
  binding entirely when it cannot, leaving the button at its factory default. `assignments` is
  seeded from `DefaultButtonAssignments()`, so this is a clean degrade — and it makes the UI,
  the device and the dirty comparison agree on what the hardware was already doing anyway, instead
  of the three disagreeing silently. Regression test added:
  `SelectPreset_OnAnotherUnit_WithUnresolvableMacroBinding_DoesNotSendAMacroWithNoBody` in
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`, verified to fail against the
  pre-fix code (`Assert.DoesNotContain` matched a `(Macro, null)` entry) and pass with it.

  **Left open — needs a product decision, not a bug fix.** Option A (moving `macros` to the same
  `(owner, vendor_id, product_id)` scope as presets) is the consistent end state: a macro is part
  of a preset's content, and presets are now shared. It was not taken here because it changes user-
  visible semantics — a macro recorded on mouse #1 would appear in mouse #2's library — and because
  it needs the same legacy-row normalisation this branch deliberately declined for M2, plus a
  matching decision for `key_macro_bindings`. Until then, a macro binding does not survive moving a
  shared preset between two units of the same model; it degrades to the button's default rather
  than misbehaving. This limitation is not yet reflected in `docs/CONTEXT.md`, and QA step 6 of the
  live-apply plan (switching a preset on the second mouse) only passes because no macro is involved
  in that step.

  Suite after the change: 367 passing, build clean.

Plan ready. Invoke /coder to implement.
