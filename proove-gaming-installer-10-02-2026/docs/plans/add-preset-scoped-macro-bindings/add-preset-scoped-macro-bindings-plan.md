# Preset-scoped key→macro bindings

## Goal

A key→macro binding is the only piece of keyboard configuration that a preset does not own.
`key_macro_bindings` is scoped to (owner, device) and has no `preset_*` counterpart, so the
preset snapshot cannot capture it and the preset restore cannot put it back. The observable
consequence: bind a macro to a key, leave the device page without saving, choose **Discard** —
every other edit reverts and the macro binding stays. Worse, the revert ends by pushing the full
current state to the hardware, so the binding the user just discarded is actively re-written to
the keyboard. This change gives macro bindings the same preset lifecycle every other setting
already has: snapshot on save, restore on switch/discard/delete, and participate in the dirty
check that decides whether **SAVE PRESET** lights up.

## Scope

**In**

- New `preset_key_macro_bindings` snapshot table, mirroring `preset_key_bindings`.
- Wiring it into all six places `preset_key_bindings` already appears: save, restore, reset,
  delete, duplicate, and the live-vs-snapshot comparison.
- A one-time migration that seeds existing presets from the current live bindings — without it
  this change **destroys** every existing macro binding on the first preset switch.
- Marking the preset dirty when a macro binding changes on the immediate-write path
  (Cyrex/Aress), which today it does not.

**Out**

- **The macro library itself** (`macros` / `macro_events`). Recording a macro is creating a saved
  artifact, not configuring a preset — the same way it works for a mouse. Discarding a preset
  must not delete recordings. Only the *binding* becomes preset-scoped.
- Mouse button→macro bindings. Already covered: `button_bindings` carries `macro_id` and is
  already snapshotted into `preset_button_bindings`.
- The on-device macro library upload itself (the packed buffer and chunking). Unchanged.
  **Correction, found during implementation:** the claim that the resync "follows the restore for
  free" was wrong — see [The half the plan missed](#the-half-the-plan-missed).
- Any change to which write path macro bindings take (staged vs immediate). That is
  [../add-tracer-macros/add-tracer-macros-plan.md](../add-tracer-macros/add-tracer-macros-plan.md)'s
  territory and is orthogonal to snapshotting.

## What the code does today

`LocalStorageService` owns five snapshot tables — `preset_rgb`, `preset_rgb_effect`,
`preset_button_bindings`, `preset_settings`, `preset_key_bindings` — and each appears in exactly
six places:

| Method | Role |
|---|---|
| `SavePresetSnapshot` | wipe the preset's rows, re-insert from the live tables |
| `LoadPresetSnapshotToDevice` | wipe the live rows for the scope, copy back from the snapshot |
| `PresetMatchesLiveState` | symmetric `EXCEPT` pair per table — the authoritative dirty check |
| `ResetPresetSnapshot` | clear the preset's rows |
| `DeletePreset` | clear the preset's rows |
| `CopyPresetSnapshot` | duplicate one preset's rows into another |

`key_macro_bindings` appears in **none** of them. So:

- `SavePresetSnapshot` never captures it → nothing to restore from.
- `LoadPresetSnapshotToDevice` never wipes or restores it → it survives every switch and discard.
- `PresetMatchesLiveState` never compares it → changing a binding does not, by itself, mark the
  preset dirty.

That last point matters more than it looks. On the immediate-write path
(`ApplyAssignmentTargetAsync`, used by Cyrex/Aress) the only notification raised is
`NotifySelectedMacroTargetChanged()` — **no** `StageChangeAndApply()` and no
`NotifyPresetChanged()`. So on those boards binding a macro does not mark the preset dirty at
all, and leaving the page shows no unsaved-changes prompt. Adding the table to
`PresetMatchesLiveState` fixes this for free wherever `StageChangeAndApply` runs, but the
immediate path still has to be told to recompute.

## Options considered

### 1. How existing presets get their first snapshot

This is the decision that carries data-loss risk, so it is first. `LoadPresetSnapshotToDevice`
**wipes the live rows and copies back from the snapshot**. Every preset that exists today has
zero `preset_key_macro_bindings` rows. So the moment this change ships, the first preset switch
or discard wipes `key_macro_bindings` and restores nothing.

| Option | Trade-off |
|---|---|
| **A. One-time migration seeding every existing preset from its device's current live bindings** | **Chosen.** No user loses a binding. Exactly the precedent `preset_settings` set — `SeedPresetSettingsFromLiveDeviceSettings()` exists for this same reason, gated on `TableExists("preset_settings")` being false before `EnsureSchema` creates it. Downside: presets that share a device all start with identical bindings until each is next saved, which is the same compromise `preset_settings` accepted. |
| B. Ship with empty snapshots | Rejected — silently destroys existing bindings on the first switch. The `preset_key_bindings` precedent is *not* a licence for this: its own comment says seeding was unnecessary because `key_bindings` was new too and had no prior data. `key_macro_bindings` has real user data today. |
| C. Skip the wipe when the snapshot is empty | Rejected. It makes "a preset with no macro bindings" impossible to express — once any preset has a binding, no preset could ever restore to none, and unbinding-by-switching would silently stop working. Trades a migration for a permanent behavioural hole. |

### 2. Table shape

| Option | Trade-off |
|---|---|
| **A. Mirror `preset_key_bindings`: `(preset_id, matrix_index, macro_id)`, PK `(preset_id, matrix_index)`, FK to `presets` ON DELETE CASCADE** | **Chosen.** Identical in shape to the live table minus the owner/device columns, exactly as every other `preset_*` table relates to its live counterpart. The six call sites become copy-paste of the `preset_key_bindings` blocks. |
| B. Fold macro bindings into `preset_key_bindings` with a discriminator | Rejected. The two are keyed differently — `key_bindings` by HID scancode, `key_macro_bindings` by matrix slot — so they cannot share a primary key without inventing a synthetic one, and every existing query would need a filter. |

### 3. Referential integrity with the macro library

A snapshot row points at a `macro_id`. The macro can be deleted from the library while an old
preset still references it.

| Option | Trade-off |
|---|---|
| **A. Let the row go stale; `ResyncKeyMacroBindingsAsync` already drops bindings whose macro is gone** | **Chosen.** That method already handles exactly this for the live table — it deletes the row when the id is missing from the library. Restoring a stale row is therefore self-correcting on the next resync. No new cleanup path, no FK to `macros`. |
| B. `FOREIGN KEY (macro_id) REFERENCES macros(id) ON DELETE CASCADE` | Rejected for now. Correct in principle, but `preset_button_bindings.macro_id` has no such constraint either, and adding one only here makes the two macro-binding paths behave differently on macro deletion. Worth doing to both, in its own change. |

## Recommended approach

Option 1A + 2A + 3A. Concretely, in `LocalStorageService`:

1. **Schema** — add `preset_key_macro_bindings` next to `preset_key_bindings` in `EnsureSchema`.
2. **Migration** — capture `var presetKeyMacroBindingsIsNew = !TableExists("preset_key_macro_bindings");`
   *before* `EnsureSchema` runs (alongside the existing `presetSettingsIsNew` at `:71`), and after
   it call a `SeedPresetKeyMacroBindingsFromLiveBindings()` modelled on
   `SeedPresetSettingsFromLiveDeviceSettings()`.
3. **The six call sites** — add the `preset_key_macro_bindings` block beside each existing
   `preset_key_bindings` block, including the `DELETE FROM key_macro_bindings … / INSERT …
   SELECT … FROM preset_key_macro_bindings` pair in `LoadPresetSnapshotToDevice` and the symmetric
   `EXCEPT` pair in `PresetMatchesLiveState`.

And in the App:

4. **`ApplyAssignmentTargetAsync`** — call `StageChangeAndApply()` after a macro binding is
   persisted, so the immediate path marks the preset dirty like every other edit. Without this,
   Cyrex/Aress users still get no unsaved-changes prompt after binding a macro.

The App needs nothing else: `ResyncKeyMacroBindingsAsync` reads the live table, and
`ApplyCurrentStateToDeviceAsync` already calls it, so a restored snapshot reaches the keyboard on
the same push that applies the preset.

## The half the plan missed

Found when the storage change turned out not to fix the reported bug. The plan assumed restoring
the live table was enough, because `ResyncKeyMacroBindingsAsync` re-writes slots from it. It is
not: **that method is additive.** It iterates the `key_macro_bindings` rows that exist and writes
each one. It has no notion of a slot that *used* to hold a macro, so removing a binding writes
nothing at all.

`ApplyKeyOverridesToDeviceAsync` cannot cover the gap either. It resets un-staged slots to their
factory code, but only where its diff baseline `_userOverrides` says the device differs — and
`LoadKeyLayoutForDevice` sets `_userOverrides = null`, which `ApplyPresetToDeviceCoreAsync` calls
immediately before the push. With a null baseline every key looks unchanged and nothing is written.

So on Discard the row was deleted from SQLite and the keyboard kept playing the macro — exactly
the symptom reported, with the storage layer fully correct and every storage test green.

**Fix:** `_appliedMacroSlots` records the slots this session last wrote a macro record to.
`ResyncKeyMacroBindingsAsync` diffs it against the slots it just wrote and restores each stale one
— to its staged remap if it has one, otherwise to the key's own scancode, using the same
expression `ApplyKeyOverridesToDeviceAsync` uses. It is not cleared by `LoadKeyLayoutForDevice`
(that reload runs on every preset apply for the same keyboard) and is cleared in `SetDevice`,
because it describes one specific keyboard's firmware.

Pinned by `Resync_ClearsASlotWhoseBindingWasRemoved`, which removes a binding straight out of
storage — the way a preset revert does, bypassing the view model — and asserts the slot is written
back. Verified to fail without the clearing pass.

## File-level change list

### Edited — App

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | `preset_key_macro_bindings` schema; `presetKeyMacroBindingsIsNew` probe + `SeedPresetKeyMacroBindingsFromLiveBindings()`; the table added to `SavePresetSnapshot`, `LoadPresetSnapshotToDevice`, `PresetMatchesLiveState`, `ResetPresetSnapshot`, `DeletePreset`, `CopyPresetSnapshot`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `ApplyAssignmentTargetAsync` calls `StageChangeAndApply()` after persisting a macro binding, so the immediate path marks the preset dirty. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `_appliedMacroSlots` + the clearing pass in `ResyncKeyMacroBindingsAsync` — see [The half the plan missed](#the-half-the-plan-missed). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | `SetDevice` clears `_appliedMacroSlots`; it describes one keyboard's firmware. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | `Resync_ClearsASlotWhoseBindingWasRemoved`. |

### Edited — docs

| File | Intent |
|---|---|
| `docs/CONTEXT.md` | New term **preset_key_macro_bindings**; extend **key_macro_bindings** and **Preset** to say macro bindings are preset-scoped. Mandatory per CLAUDE.md. |

### Edited — tests

| File | Intent |
|---|---|
| `tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs` | Snapshot / restore / duplicate / delete / dirty-check coverage for the new table, mirroring the existing `preset_key_bindings` cases. |
| `tests/ProoveGaming.App.Tests/LocalStorageServiceKeyMacroBindingTests.cs` | The migration-seeding case — the one that guards against wiping existing bindings. |

No schema-version bump exists in this codebase; migrations are `CREATE TABLE IF NOT EXISTS` plus
a `TableExists` probe, and this follows that. No new NuGet, no `.slnx` change, no gRPC or
installer impact.

## Acceptance criteria

- [ ] Given a key bound to a macro and the preset saved, when the preset is switched away and
      back, then the key still plays that macro.
- [ ] Given a saved preset with no macro bindings, when a macro is bound to a key and the user
      leaves the page and chooses **Discard**, then the binding is gone from the app **and** the
      physical key no longer plays the macro.
- [ ] Given a saved preset **with** a macro binding, when the binding is changed to a different
      macro and the user chooses **Discard**, then the key returns to playing the originally
      saved macro — not none, and not the discarded one.
- [ ] Given a macro binding is added or removed, then **SAVE PRESET** becomes enabled; when the
      change is reverted by hand to what the preset already holds, then it becomes disabled again.
- [ ] Given two presets where only one has a macro on a key, when switching between them, then
      the key gains and loses the macro accordingly.
- [ ] Given a preset is duplicated, then the copy carries the same macro bindings as its source,
      and editing the copy's bindings does not affect the source.
- [ ] Given a preset is deleted, then its `preset_key_macro_bindings` rows are gone (FK cascade).
- [ ] **Migration:** given a database created before this change that has macro bindings and one
      or more presets, when the app starts and the user switches presets, then **no binding is
      lost** — every existing preset was seeded from the live bindings at startup.
- [ ] Given a preset whose snapshot references a macro that has since been deleted, when that
      preset is applied, then the key is left unbound and the stale row is dropped, with no error.
- [ ] Given a **mouse**, then nothing about its behaviour changes — `button_bindings` and
      `preset_button_bindings` are untouched.
- [ ] `dotnet build ./src/ProveGaming.slnx` — 0 errors; `dotnet test ./src/ProveGaming.slnx` — green.

## Test strategy

This is a **new feature** (`add-*`) built on a table that already has coverage, so the existing
`preset_key_bindings` tests are the template — the new cases should read as siblings of them, not
as a new style. All coverage lands in **`tests/ProoveGaming.App.Tests`**;
`ProoveGaming.Infrastructure.Tests` has no stake (no protocol change).

**Must add — `LocalStorageServicePresetsTests`**

- **Snapshot round trip:** live binding → `SavePresetSnapshot` → mutate live → `LoadPresetSnapshotToDevice`
  → live matches the snapshot.
- **Restore clears:** preset saved with **no** bindings, live has one → restore leaves live empty.
  This is the one that proves Discard actually discards, and the one option 1C would have broken.
- **Restore replaces:** preset saved with macro A on a slot, live changed to macro B → restore
  puts A back.
- **Dirty check:** `PresetMatchesLiveState` is true after save, false once a binding is
  added / removed / repointed, and true again when reverted by hand.
- **Duplicate:** `CopyPresetSnapshot` copies the rows and the two presets are independent.
- **Delete:** deleting a preset removes its rows.
- **Scope isolation:** a binding under a different (owner, device) is untouched by save and
  restore — the live tables are scoped, the snapshot tables are not, and this pair is where that
  gets confused.

**Must add — `LocalStorageServiceKeyMacroBindingTests`**

- **Migration seeding, the highest-value test here.** Build a database that has `key_macro_bindings`
  rows and presets but no `preset_key_macro_bindings` table, construct `LocalStorageService`, and
  assert every preset came out seeded with the live bindings — then that a restore is a no-op
  rather than a wipe. Without this test the failure mode is silent, permanent, and only visible on
  a real user's database.

**Deliberately not automated**

- The device write itself. `ResyncKeyMacroBindingsAsync` opens real HID through gRPC; there is no
  device in CI. Covered by QA steps 4–7.
- Preset *timing* (debounced auto-apply vs Save Preset). Owned by the preset system and already
  covered by `DeviceDetailsPresetApplyTests`; this change does not alter when a push happens, only
  what the push finds in the live table.

## QA (manual test steps)

Requires a T-Racer or Raster keyboard connected by USB cable, and Notepad.
**Before you start:** note which keys currently play macros, so you can confirm nothing was lost.

1. Start the device service, then the app, and open the keyboard. Go to **Macros**.
   **Expected:** any macros you had before this update are still listed, and any keys that played
   a macro still do. Nothing was lost by the update itself.
2. Record a macro (type `qwerty`, save it). Click a key, e.g. `Q`, and assign that macro.
   **Expected:** the assignment is accepted and the macro's row shows as selected.
3. Click `SAVE PRESET` and confirm `SAVE`. Open Notepad and tap physical `Q`.
   **Expected:** `qwerty` is typed.
4. Now assign a *different* macro to `Q` (or clear it with **RESET**) — but **do not save**.
   Press the back arrow to leave the device page.
   **Expected:** a dialog appears warning about unsaved changes.
5. Choose **Discard**, then open the keyboard again and tap physical `Q` in Notepad.
   **Expected:** `Q` plays the macro you saved in step 3 — not the one you just picked, and not
   nothing. This is the whole point of the change.
6. Create a second preset (`+ NEW PRESET`). On it, leave `Q` unbound and save.
   Switch back and forth between the two presets, tapping `Q` after each switch.
   **Expected:** `Q` plays the macro on the first preset and types a plain `q` on the second,
   every time.
7. Duplicate the first preset from the presets list, switch to the copy, tap `Q`.
   **Expected:** the copy plays the same macro. Now change the copy's binding and save it, switch
   back to the original, tap `Q`.
   **Expected:** the original is unchanged.
8. Delete a macro from the Macros list that a *non-active* preset still uses, then switch to that
   preset and tap the key it was bound to.
   **Expected:** the key types its normal character. No error dialog, no stuck key.
9. Open a mouse (BLAZE or TAIPAN) if available and bind a macro to a button, save, switch presets.
   **Expected:** unchanged from before this update — mouse behaviour must not be affected.

## Review focus

- `null-safety` — `deviceKey` is nullable at several boundaries and every existing preset method
  starts with `deviceKey ??= string.Empty;`; the new blocks must do the same or they will silently
  scope to a different row set.
- `resource disposal` — each snapshot block creates its own `SqliteCommand` inside a `using` and
  shares one transaction; the new blocks must follow that exactly and not leak a command or open
  a second transaction.
- `thread-safety` — `LocalStorageService` serialises on `_lock`; the seeding method runs from the
  constructor's `EnsureSchema` path, which is **not** inside that lock. Check the new seeding call
  matches how `SeedPresetSettingsFromLiveDeviceSettings` handles this, and does not deadlock by
  taking `_lock` re-entrantly.

## Performance & quality

- **No hot path.** All of this is user-action SQLite work: preset save, switch, discard, delete.
  Nothing here runs per frame, per poll, or on the RGB timer.
- **Migration cost.** The seeding runs once, on the first start after upgrade, as a single
  `INSERT … SELECT` per preset scope rather than a row-by-row loop — matching
  `SeedPresetSettingsFromLiveDeviceSettings`. Databases here hold a handful of presets and tens of
  bindings; this is milliseconds and it is off the render path.
- **`PresetMatchesLiveState` grows by one `EXCEPT` pair.** It runs synchronously on every staged
  edit (`StageChangeAndApply` → `TryRecomputeHasUnsavedPresetChanges`) to decide the SAVE PRESET
  button state, so it is on a UI-responsiveness path even though it is not a hot loop. Both sides
  are indexed by primary key and the row counts are tiny; no new index is needed, but this is the
  one query in the change worth keeping an eye on.
- **Allocations / shared state** — none added. No new caches, no new singleton state.

## Risks

- **The migration is the whole risk.** Get the seeding wrong and every existing user's macro
  bindings vanish on their first preset switch, silently and irreversibly. It is guarded by one
  test and one QA step (step 1), and both are load-bearing.
- **Presets sharing a device all seed identically.** After migration, every preset for a device
  claims that device's current bindings until it is next saved — so switching to an old preset
  restores bindings it never had. This is the same compromise `preset_settings` made and is
  strictly better than losing data, but it will look odd to anyone who had different bindings in
  mind for different presets. Worth saying in the release note.
- **Two macro-binding paths still diverge.** Mouse bindings ride inside `button_bindings`;
  keyboard bindings get their own table. They are snapshotted by different code and can drift —
  which is exactly how this bug arose in the first place.
- **`ApplyAssignmentTargetAsync` gaining `StageChangeAndApply()` schedules a debounced device
  push** it did not schedule before. That push is idempotent and the method has already written
  the device by then, so the expected effect is a redundant re-push, not a behaviour change — but
  it is a real change to Cyrex/Aress timing and deserves a look on hardware.

## Open questions

None blocking — `/coder` can execute this as written. One to confirm alongside:

1. **Should the seeding be per-preset or only for the default preset?** The plan seeds *every*
   existing preset from the live bindings, matching `preset_settings`. The alternative — seed only
   the currently-applied preset and leave others empty — is more truthful to what each preset
   actually contained, but means switching to any other preset clears the user's bindings, which
   reads as data loss even though it is technically correct. Recommend seeding all, as planned.

## Review findings

Maintained by `/reviewer` as a round-by-round journal. `/coder` and `/test-writer` read this
section and mark items `[x]` as they are closed.

### Round 1 — Ship: yes

**Surface.** This change is entirely **uncommitted**: 7 modified files plus this untracked plan
folder. `git status --porcelain` reconciles exactly with `git diff`; nothing staged.

**Out of scope this round.** The branch's commits (`9d3ccbb T-Racer SVG` and the `43fbe78` merge
bringing `d146223` / `ed388db` from main) carry the T-Racer artwork and macro work, already
reviewed under
[../add-tracer-artwork/add-tracer-artwork-plan.md](../add-tracer-artwork/add-tracer-artwork-plan.md)
and [../add-tracer-macros/add-tracer-macros-plan.md](../add-tracer-macros/add-tracer-macros-plan.md).
Not re-litigated here. The two areas do meet in `DeviceDetailsViewModel.KeyAssignments.cs`, so
that file was read whole rather than by hunk.

Build clean; 391 App + 357 Infrastructure tests pass. **User confirmed the reported bug is fixed
on hardware.**

#### Plan conformance

Full conformance, and the plan was corrected mid-flight rather than quietly worked around.

- **Implemented as planned:** the `preset_key_macro_bindings` table; the `TableExists` probe and
  `SeedPresetKeyMacroBindingsFromLiveBindings()`; the table wired into all six snapshot methods
  **plus** the guest-cleanup cascade in `ClearData` (not in the plan's list, but correct — omitting
  it would have leaked snapshot rows past a guest wipe); `StageChangeAndApply()` on the immediate
  path; the glossary entry; all planned tests.
- **Deviates (necessary, and disclosed):** `LoadPresetSnapshotToDevice` also needed a
  `DELETE FROM key_macro_bindings` on the **live** side (`:1612`). The plan's change list only
  described the copy. Without the wipe the restore would append to existing rows rather than
  replace them, so a removed binding would survive — the exact bug being fixed.
- **Plan was wrong, and is now corrected:** the Scope claimed the resync "follows the restore for
  free". It does not — see *The half the plan missed*. The correction is recorded in the plan body
  rather than left as a silent code difference, which is the right handling.
- **Accepted limitation (per plan):** presets sharing a device all seed identically after
  migration. Residual risk stands — switching to an older preset restores bindings it never had.
  Matches the `preset_settings` precedent and beats data loss, but it should reach the release note.
- **Acceptance criteria:** the storage-level ones are covered by tests; the device-observable ones
  were confirmed by the user on hardware after the clearing pass landed.

#### Code

- [ ] **S1. Five files gained a UTF-8 BOM they did not have** — `src/ProoveGaming.App/Services/LocalStorageService.cs:1` and four others — Should-fix
  **Problem / why it matters:** the edits were applied with a script writing `utf-8-sig`, so
  `LocalStorageService.cs`, `DeviceDetailsViewModel.KeyAssignments.cs`,
  `DeviceDetailsKeyboardMacroBindingTests.cs`, `LocalStorageServiceKeyMacroBindingTests.cs` and
  `LocalStorageServicePresetsTests.cs` now begin with `EF BB BF`. Verified against `HEAD`: none of
  the five had one before; `CONTEXT.md` and `DeviceDetailsViewModel.cs` did, so they are unaffected.
  It shows in the diff as a spurious change to the first line (`-using System;` / `+﻿using System;`),
  which is pure noise in a review, and it makes the encoding inconsistent within the same folder.
  Harmless to the compiler, so not a blocker.
  **Fix:** rewrite the five files without the BOM (`utf-8`, not `utf-8-sig`) and confirm the first
  diff line disappears. Worth doing before this is committed, since a BOM flip is far harder to
  spot once it is in history.

- [ ] **S2. The `preset_settings` seeder lost its doc comment to the new method** — `src/ProoveGaming.App/Services/LocalStorageService.cs:359-386` — Should-fix
  **Problem / why it matters:** `SeedPresetKeyMacroBindingsFromLiveBindings` was inserted between
  the existing *"One-time backfill for the preset_settings migration above…"* comment and the
  method it documents. The file now reads as one method carrying two unrelated comment blocks,
  and `SeedPresetSettingsFromLiveDeviceSettings` is left undocumented. This is cosmetic, but it is
  cosmetic **in the migration code**, which is the part of this change a future reader most needs
  to understand before touching.
  **Fix:** move the new method (with its own comment) below `SeedPresetSettingsFromLiveDeviceSettings`,
  restoring that comment to the method it belongs to.

- [ ] **S3. An orphaned macro on the device can never be cleared, because the tracker is session-scoped** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:58` (`_appliedMacroSlots`) — Should-fix
  **Problem / why it matters:** the clearing pass can only clear a slot it remembers writing this
  session. `_appliedMacroSlots` starts `null` on every launch and on every device change, and is
  populated **only** by a successful resync write.
  **Concrete failure:** Reset a macro-bound key (row deleted from SQLite, `StageChangeAndApply`
  schedules a 250 ms debounced push) and close the app inside that window — or have the push fail.
  The device still holds the macro record; the row is gone. On the next launch the resync finds no
  row for that slot, so `nowApplied` is empty, `_appliedMacroSlots` is `null`, the stale set is
  empty, and nothing is ever written. The key plays a macro forever while the UI shows it unbound,
  and no user action inside the app can fix it — rebinding and unbinding that same key in one
  session is the only route back.
  Narrow window, but silent and unrecoverable-by-UI, which is why it is not a nit.
  **Fix:**
  - **Option A (correct):** make the connect path able to clear. `ProbeUserOverridesAsync` already
    reads every slot; have it surface the record **type** as well as `code2` so a `0x60`/`0x61`
    slot with no matching row can be reset on connect. That closes the gap regardless of how the
    orphan arose.
  - **Option B (cheap):** persist `_appliedMacroSlots` per device — it is the same shape as the
    binding table and could simply be derived from "rows that existed at last successful push".
  - **Option C (accept):** document it in Risks and leave it. Defensible given the window, but it
    should then be a stated limitation rather than an unexamined one.

#### Tests

- [ ] No coverage gap worth blocking on. Both new guards were **verified to fail without their
  fix** — the migration guard wipes bindings (`Assert.Equal` failure) and
  `Resync_ClearsASlotWhoseBindingWasRemoved` writes nothing (`Assert.Single() Failure: The
  collection was empty`). That negative verification is the difference between a guard and a
  decoration, and it is the single best thing about this change's test work.
- [ ] **S3's failure mode has no test**, because it needs a process restart mid-window. If Option A
  or B is taken, a test asserting "a slot the device holds with no matching row is cleared on
  connect" becomes possible and should be added — `tests/ProoveGaming.App.Tests`.
- [ ] Worth noting: `.github/workflows/dotnet-ci.yml` now runs the whole solution, so these do
  execute in CI. Not a gap; recorded because earlier plans in this repo were bitten by the opposite.

#### Nits

- The clearing pass `continue`s when no physical key resolves to a stale slot
  (`KeyAssignments.cs:741`), but `_appliedMacroSlots = nowApplied` then drops that slot from
  tracking anyway — so a slot skipped once is never retried. Reachable only if `ResolveMatrixSlot`
  changes between the write and the clear (the probed-vs-catalog ambiguity recorded as **S7** in
  the `add-tracer-macros` journal). Cheap hardening: keep unresolved stale slots in the set.
- `PresetMatchesLiveState` gains a sixth symmetric `EXCEPT` pair and runs synchronously on every
  staged edit to decide the SAVE PRESET button. Both sides are PK-indexed and the row counts are
  tiny, so this is almost certainly free — but it is the one query here on a UI-responsiveness
  path, as the plan itself flagged.
- `SeedPresetKeyMacroBindingsFromLiveBindings` has no `INSERT OR IGNORE`. Correct as written —
  the table was just created so it is empty — but it relies on that ordering holding, which is
  worth a word given the method is one `TableExists` away from running twice.

#### What's done well

- **The migration is the right call and is guarded by a test that was proven to fail without it.**
  Shipping this without seeding would have destroyed every existing user's macro bindings on their
  first preset switch — silently, permanently, and only on real upgraded databases. That is the
  highest-consequence part of the change and it got the most rigour.
- **`RestoringAPresetWithNoBindings_ClearsLiveBindings` pins a design decision, not just a code
  path.** An implementation that skipped the wipe on an empty snapshot passes every other test in
  the file and fails only this one — and would have made "a preset with no macro bindings"
  permanently inexpressible.
- **The clearing-pass test removes the row straight from storage**, bypassing the view model, which
  is exactly how a preset revert does it. A test that went through the VM would have passed against
  the broken code.
- **The plan was corrected rather than quietly patched.** The "resync follows for free" assumption
  was wrong; *The half the plan missed* says so plainly and explains why neither
  `ResyncKeyMacroBindingsAsync` nor `ApplyKeyOverridesToDeviceAsync` covered it. That reasoning is
  the most valuable thing in the document for whoever touches this next.
- The clearing pass restores a staged remap rather than blindly writing the factory code, reusing
  the same expression `ApplyKeyOverridesToDeviceAsync` uses — the obvious implementation would have
  stomped a remap on a key that also had a macro.

#### Verdict

Ship: yes — no blockers, the schema and migration are sound, the two highest-risk behaviours are
covered by tests proven to fail without their fixes, and the reported bug is confirmed fixed on
hardware. **S1 should be cleaned up before this is committed**, since a BOM flip across five files
is much harder to notice once it is in history.
First: 1) **S1** strip the BOMs. 2) **S2** restore the `preset_settings` comment to its method.
3) **S3** decide between closing the orphan gap and recording it as an accepted limitation.


### Round 2 — external review (PR #79, @boikoserhii84)

Findings raised on the PR and **verified against the code** before being recorded here. Both PR
reviewers approved; these are follow-ups, not merge blockers in their view. Numbering is theirs.

- [x] **M1 + M2 (one bug, two halves) — the both-slot delete is dead code, and macro binds leave
  orphan rows** — `KeyAssignments.cs:819`, `:1024-1035`, `:1047` — Blocker-grade correctness
  **Verified.** `ProbeAndUpdateMatrixMapAsync:819` does `pk.MatrixIndex = realSlot`, so after the
  probe `ResolveMatrixSlot(key)` and `key.MatrixIndex` return the *same* value — and before it,
  both return the catalog index. They can never differ, so the `if (SelectedPhysicalKey.MatrixIndex
  != slot)` guard I added never fires. My "reset not always working" fix worked because of the
  *single* delete plus the clearing pass, not the double-delete I attributed it to.
  The residual bug is real: a row written **before** the probe lands is keyed by the catalog index
  (T-Racer `E` → 45); after the probe, deletes and lookups use 35. The 45 row is orphaned forever
  and `ResyncKeyMacroBindingsAsync` faithfully writes it — putting a macro on a **different
  physical key** that the user cannot reset from the UI. The macro branch of
  `StageAssignmentTarget` has no cleanup at all (confirmed at `:1024-1035`).
  **Fix:** the guard cannot work while the probe mutates `MatrixIndex`. Either keep the catalog
  index in a separate never-mutated field so the two-candidate delete functions, or (root fix)
  stop mutating `MatrixIndex` and block binding writes until the probe completes. Same root cause
  as **S7** in [../add-tracer-macros/add-tracer-macros-plan.md](../add-tracer-macros/add-tracer-macros-plan.md).

- [x] **M4 — the full macro-library upload now runs on every staged edit, including RGB and DPI**
  — `Presets.cs:492` — Should-fix, arguably Blocker under CLAUDE.md's no-stutter rule
  **Verified.** `StageChangeAndApply()` has **15** call sites (RGB, Settings, Buttons, DPI, report
  rate), each debouncing 250 ms into `ApplyCurrentStateToDeviceAsync` → the resync I added.
  `ResyncKeyMacroBindingsAsync` has **no early-out** before its unconditional `SetMacrosAsync`, so
  every colour/brightness/DPI change on a macro-capable keyboard triggers a full 1096-byte chunked
  HID upload (~20 write/read pairs with read timeouts), plus one `SetKeyMapping` per binding — all
  serialised under `_applyGate`. On Cyrex/Aress each of those is a ~420 ms matrix read-modify-write.
  Before this branch the resync ran only on connect and on macro edit/delete.
  **Fix (cheap, closes the common case):** early-out at the top of `ResyncKeyMacroBindingsAsync`
  when `key_macro_bindings` is empty **and** `_appliedMacroSlots` is null/empty. Better: pass a
  flag so the debounced path skips the resync and only Save Preset / switch / connect run it.

- [x] **M3 — `BoundMacroForSelectedKey()` re-queries SQLite under `_lock.Wait()` on every call**
  — Should-fix. Called from `SelectedKeyHasOverride` and `RefreshMacroListSelection`, both of which
  run per selection change. Cache the current device's bindings in memory, refreshed on
  `SetDevice` / `LoadKeyLayoutForDevice` / after each write.

- [x] **M5 — the T-Racer card lost its raster cache** — `DoubleCard.axaml:133-144` — Should-fix
  `SvgImageView` has a process-wide `static ConcurrentDictionary<string, Bitmap?>` keyed
  `uri|width|height|tint`; `RgbKeyboardView` has none, so its two `<Svg>` controls re-parse
  ~3.2 MB of XML-with-embedded-PNG per card instance, on the UI thread, at full 1595×689 before the
  `Viewbox` shrinks it to 336×160. The reviewer is explicit that **no measurement was taken**.
  **Fix:** generate a small `Card/PG_T-Racer_card.svg` already unlit and return the card to
  `SvgImageView` — that also deletes `UseUnlitCardArt` / `UseProductShotCardArt` and the extra dim
  style. Measure first.

- [ ] **m2 — `PG_Keyboard_RGB-off_T-Racer_black.svg` (3.43 MB) is now rendered nowhere** but is
  still embedded via `<AvaloniaResource Include="Assets\**" />`. All three consumers are false for
  T-Racer. Move it under the plan folder (the generator is its only user) or document why
  `ImagePath` still names it.

- [ ] **m1 — the BOMs reached history.** My own Round 1 **S1** asked for this before commit and it
  was not done. Five files.
- [ ] **m3 — comments/glossary contradict the code in the same commit.** `Presets.cs:483` still
  says macro bindings are "per device rather than per preset"; `CONTEXT.md`'s **Staged edit** entry
  still lists macro bindings as an immediate-write exception. Only Smart Infinite is now.
- [ ] **m4 — `modelName` default `"Raster"` is dead and a trap.** Already S2 in the macros journal.
- [ ] **m5 — the Infrastructure changes still have no tests.** Already S1 in the macros journal.
- [ ] **m6 — picking a macro on Raster/T-Racer bounces the target-list highlight** back to the
  key's own row, because `SyncAssignmentTargetSelection` excludes macro rows for that family. Now
  visible as two adjacent screens disagreeing, since this branch made the Macros list correct.
- [ ] **m7 — an unresolved stale slot is dropped from tracking forever** — `KeyAssignments.cs:741,758`.
  Already a Round 1 nit; reachable exactly under M1/M2.
- [ ] **m8 — `KeysDoNotOverlap`'s comment overpromises.** Already a Round 1 nit, not fixed.

#### Round 2 — fixes applied (M4, M1/M2)

**M4 — closed.** `ResyncKeyMacroBindingsAsync` reads `key_macro_bindings` once up front and returns
before `SetMacrosAsync` when there are no bindings **and** `_appliedMacroSlots` is empty. The
upload stays unconditional beyond that gate on purpose — it must still be able to clear a stale
on-device library — which is exactly why the gate belongs at the top rather than around the
upload. The single read also removes a duplicate query, since the loop below now reuses the list.
Pinned by `Resync_UploadsNothing_WhenThereAreNoBindingsAndNothingApplied`, verified to fail
without the gate (`Assert.Empty() Failure: Collection was not empty`).

**M1 + M2 — closed together, since they are one bug.** The reviewer was right that the guard was
unreachable: the probe overwrites `MatrixIndex` in place, so it and `ResolveMatrixSlot()` can never
disagree. Added `KeyAssignmentOptionViewModel.CatalogMatrixIndex` — `init`-only, set from the
catalog and never mutated — so the two candidates genuinely differ once the probe lands. Both
branches of `StageAssignmentTarget` now go through one `DeleteMacroBindingForKey` helper that
deletes under both, which also gives the macro branch the cleanup it never had (M1).
`BoundMacroForSelectedKey` uses the same untouched index as its fallback candidate.
Pinned by `RebindingAfterTheProbeLands_LeavesNoOrphanRowUnderTheCatalogSlot`, which binds before
the probe, lands the probe, re-binds, and asserts one row. Verified to fail without the fix with
`Assert.Single() Failure: The collection contained 2 items` — the orphan, reproduced exactly as
described.

The `ProbeKeyMatrixAsync` fake in `DeviceDetailsKeyboardMacroBindingTests` gained a settable
`ProbedScancodeToSlot`; it previously always returned null, so no test could reach the post-probe
state where these bugs live.

**Not addressed this round:** M3 (SQLite read per selection), M5 (card raster cache — needs a
measurement first, per the reviewer's own caveat), and m1–m8.

400 App + 357 Infrastructure tests pass.

#### Round 2 — M3 closed, M5 measured and dismissed

**M3 — closed.** `_macroBindingsBySlot` caches the current device's bindings; `BoundMacroForSelectedKey`
reads it instead of querying. Invalidated at six points: the shared delete helper, the staged
write, both branches of the immediate path, the resync's stale-row drop, macro deletion in
`Buttons.cs`, and `LoadKeyLayoutForDevice` — that last one covering both a device change and a
preset restore, which writes `key_macro_bindings` in SQL outside the view model and then reloads.
The lookup also dropped a `Macros.FirstOrDefault` per row in favour of two dictionary probes.

Caching moves the risk from "too many reads" to "a stale read", so two tests pin the invalidation:
`BindingCache_IsInvalidatedByEveryMutation` and
`BindingCache_IsInvalidatedByAPresetRestoreWritingStorageDirectly`. Both verified to fail when
`InvalidateMacroBindingCache()` is made a no-op.

**M5 — measured; the premise does not hold, no code change.** The finding assumed the card was
"strictly worse" because `RgbKeyboardView` has no raster cache. Measured parse cost (Skia, warmed,
3 runs):

| | cost |
|---|---|
| `PG_T-Racer_base.svg` + `PG_T-Racer_keys.svg` (what the card now does) | **60–85 ms** |
| `PG_Keyboard_RGB-off_T-Racer_black.svg` (what it did before) | **160–231 ms** |

The new path is ~2.5× **faster** on first paint. The old one was only cheaper on *repeat*
instantiation, thanks to `SvgImageView`'s static raster cache — and repeat instantiation does not
happen here: `HomeView` is a single instance in `MainWindow.axaml` toggled by `IsVisible`, not
recreated on navigation, and its `ItemsControl` uses a plain `WrapPanel`, so containers are not
virtualized and cards are not recycled on scroll. The cost is paid once per session and replaced a
larger one.

Residual risk, recorded rather than fixed: if that `ItemsControl` ever gains a virtualizing panel,
or the device list starts being rebuilt rather than updated in place, the ~60 ms returns per
materialization. The reviewer's Option A (a small pre-composited card asset) remains the right fix
*if* that happens — it would beat both numbers — but it is not justified by the current measurement.

### Round 3 — Ship: yes (the PR-fix round)

**Surface.** Two new commits landed the earlier work: `a7cf862 macros fix` (preset scoping,
migration, tests) and `777bd3c fixed card icon`. The uncommitted delta is the PR #79 fix round
only — 4 files + this plan. `git status --porcelain` reconciles with `git diff`; nothing staged.

Build clean; 402 App + 357 Infrastructure pass.

#### Plan conformance

This round implements Round 2's external findings rather than the plan body, so conformance is
against those items.

- **M4 — closed and correct.** The gate is at the top of `ResyncKeyMacroBindingsAsync`
  (`KeyAssignments.cs:697`), not wrapped around the upload, which is the right placement: the
  upload has to stay unconditional past the gate so it can still overwrite a stale on-device
  library. Verified the gate cannot skip needed work — with `bindings.Count == 0` and
  `_appliedMacroSlots` non-empty it correctly falls through to the clearing pass, and the
  now-unassigned `_appliedMacroSlots` stays `null` rather than becoming empty, which every
  consumer (`?? []`, and the gate itself) treats identically.
- **M1/M2 — closed at the root.** `CatalogMatrixIndex` is `init`-only and populated from the same
  catalog value at both construction sites, so the two candidates genuinely differ once the probe
  mutates `MatrixIndex`. Both branches of `StageAssignmentTarget` now share `DeleteMacroBindingForKey`.
  Correctly **not** applied to `ApplyAssignmentTargetAsync`: that path is Smart Infinite only, where
  `ResolveMatrixSlot` returns `MatrixIndex` directly and no probe mutates it, so the two candidates
  are equal by construction.
- **M3 — closed.** Six invalidation points, and the one that matters most is
  `LoadKeyLayoutForDevice` — verified it runs *after* `LoadPresetSnapshotToDevice`
  (`Presets.cs:362` then `:371`), so the preset restore's out-of-band SQL write is covered.
- **M5 — measured, not changed.** The measurement is recorded and the conclusion (new path is
  ~2.5× faster on first paint; repeat instantiation does not occur because `HomeView` is a single
  `IsVisible`-toggled instance over a non-virtualizing `WrapPanel`) is verifiable from the code.
  Correct call.

#### Code

- [ ] **S1. The BOMs are now in history** — five files, `a7cf862` — Should-fix
  **Problem / why it matters:** Round 1 **S1** and PR **m1** both asked for this *before* commit;
  it did not happen, and `git show HEAD:<path>` confirms `EF BB BF` on
  `LocalStorageService.cs`, `DeviceDetailsViewModel.KeyAssignments.cs` and
  `LocalStorageServicePresetsTests.cs` (plus the two other test files). Every future diff of those
  files carries a phantom first-line change, and the encoding is now inconsistent within the same
  folders. Harmless to the compiler; purely a diff-hygiene cost that grows the longer it sits.
  **Fix:** one cleanup commit rewriting the five files as UTF-8 without signature. Do it as its
  own commit so it is obviously mechanical and does not muddy a review.

#### Tests

- [x] Every guard added this round was **verified to fail on the pre-fix code**, which is the
  standard this branch has held throughout: `RebindingAfterTheProbeLands…` →
  `Assert.Single() Failure: The collection contained 2 items` (the orphan, exactly as the PR
  described); `Resync_UploadsNothing…` → `Assert.Empty() Failure: Collection was not empty`; both
  `BindingCache_…` tests fail when `InvalidateMacroBindingCache()` is stubbed to a no-op.
- [x] The `ProbeKeyMatrixAsync` fake gained a settable `ProbedScancodeToSlot`. This is the most
  durable part of the round: it previously always returned `null`, so **no test could reach the
  post-probe state** where M1/M2 lived — which is precisely why the suite stayed green through
  both bugs. The same slot ambiguity is still open as S7 in the macros journal, and it is now
  testable.
- [ ] No coverage for the interaction between the M4 gate and a device-side orphan (S3 from
  Round 1). Not a regression — the clearing pass iterates `_appliedMacroSlots`, which is `null` on
  a fresh session either way — but the gate now makes it structurally impossible for a later
  session to notice such an orphan. Worth a test once S3 is decided.

#### Nits

- `MacroBindingsBySlot()` caches an **empty map even when `Device` / owner / `_localStorage` are
  null** (`KeyAssignments.cs:928-945`) — a negative cache that only clears on an explicit
  invalidation. I could not construct a live failure: every path that makes those non-null
  (`SetDevice` → `LoadKeyLayoutForDevice`) invalidates on the way through. Graded a nit for that
  reason. One-line hardening: return `map` without assigning when the preconditions fail.
- Two identical `Bindings(...)` test helpers now exist, in
  `DeviceDetailsKeyboardMacroBindingTests.cs:451` and `LocalStorageServicePresetsTests.cs:511`.
  Different fixtures, so not shareable as written; harmless, but the second one invites a third.
- `ResyncKeyMacroBindingsAsync` reads `key_macro_bindings` directly rather than through the new
  cache (`:692`). Deliberate and correct — the resync wants ground truth, not a possibly-stale
  view — but it means two readers of one table with different freshness rules, which is worth a
  one-line comment so the next reader does not "unify" them.

#### What's done well

- **The M1/M2 fix addresses the root cause rather than the symptom.** The obvious move was to make
  the dead `if` reachable somehow; instead the ambiguity itself was removed by keeping an
  unmutated catalog index, which is what makes the guard meaningful. The shared
  `DeleteMacroBindingForKey` then gave the macro branch cleanup it never had — closing M1 as a
  side effect of fixing M2 properly.
- **M5 was measured before being "fixed".** The finding was plausible and would have cost a
  3 MB asset plus the removal of two view-model flags and a style. The measurement showed the
  premise inverted — the replaced file was *larger* — so the right action was no action. Recording
  the residual condition (if the panel ever virtualizes) keeps that decision reviewable.
- **The M4 gate is placed where it can't hide a bug.** Putting it at the top rather than around
  `SetMacrosAsync` keeps "upload is unconditional" true for every path that gets past it, and the
  comment states why — which is the part that stops someone later moving it "closer to the cost".
- Reading the bindings once and reusing the list also removed a duplicate query the loop was
  making, and the cache lookup replaced an O(n) `Macros.FirstOrDefault` per row with two dictionary
  probes.

#### Verdict

Ship: yes — no blockers. Four external findings closed, each with a guard proven to fail on the
old code, and the one finding that turned out to be wrong was disproved by measurement rather than
argument. **S1 (the BOMs) is the only carried-over debt** and is now more annoying to fix than it
was two rounds ago, which is itself the argument for doing it next.
First: 1) **S1** — one mechanical commit stripping the five BOMs. 2) Decide **S3** (device-side
orphan) — close it or record it as accepted; the M4 gate has made it permanent otherwise.
3) The PR's remaining Minors, of which **m6** (target-list highlight bouncing off the macro row)
is the only one a user can see.
