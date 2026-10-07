# Preset coverage audit + actuation (press force) in presets

## Goal

Two things the user asked for, in one change because the second is only meaningful once the first
is written down. **(1) Audit** what a preset actually captures today, per keyboard and per section,
and prove by test that a preset round-trips (save → switch away → switch back → the device and the
UI are as saved) on every supported board. **(2) Close the one real gap the audit finds:** key
actuation — the press/release force and Rapid Trigger settings on the magnetic boards
(**T800**, **Cyrex**, **Aress**) — is today **completely absent from the preset system**, so a user
who tunes their actuation and switches presets keeps whatever the board's flash happens to hold.
After this change actuation is preset-scoped like every other section, without breaking the boards
that have no actuation at all.

## Audit findings (current state, verified against code)

Every SQLite table (`LocalStorageService.EnsureSchema`, lines 81–322) and every device-write call
site was enumerated. The preset model is consistent: each section keeps a **live row** table that
the UI edits, and a **`preset_*` snapshot** table that Save Preset copies it into.

| Section | Live rows | Preset snapshot | In presets today? |
|---|---|---|---|
| RGB colour | `device_rgb` | `preset_rgb` | **Yes** |
| RGB effect | `device_rgb_effect` | `preset_rgb_effect` | **Yes** |
| Mouse buttons / macro bindings | `button_bindings` | `preset_button_bindings` | **Yes** (mouse only) |
| Mouse settings, DPI table, active stage, report rate | `device_settings` | `preset_settings` | **Yes** (mouse only) |
| Keyboard key remap | `key_bindings` | `preset_key_bindings` | **Yes** |
| Keyboard macro bindings | `key_macro_bindings` | `preset_key_macro_bindings` | **Yes** |
| **Key actuation (force / Rapid Trigger)** | **none** | **none** | **No — the gap** |

**Actuation has no persistence of any kind.** `DeviceDetailsViewModel.Actuation.cs` never touches
`_localStorage` (grep: zero hits in that file); its state lives in `_actuationInfo` and
`_perKeyActuation` (`Dictionary<int, KeyActuationKeyValues>`, line 51), both populated from the
device by `LoadActuationFromDeviceAsync` (line 398) and written back by
`ApplyActuationToDeviceAsync` (line 536). `SetKeyActuationAsync` / `SetPerKeyActuationAsync` are
called from **that file only** — never from `DeviceDetailsViewModel.Presets.cs`.

This is not an oversight, it was a decision: both actuation plans put it out of scope explicitly —
"*Preset (SQLite) capture of actuation values — the keyboard's flash is the source of truth*"
([`add-keyboard-actuation`](../add-keyboard-actuation/add-keyboard-actuation-plan.md), line 30)
and "*flash stays the only truth, as decided in the parent plan*"
([`add-per-key-actuation`](../add-per-key-actuation/add-per-key-actuation-plan.md), line 35).
**This change reverses that decision**, which is why the migration/no-op rules below carry so much
weight — see *Risks*.

### Three further gaps the audit found — all keyboard-specific

The actuation gap is the largest, but it is not the only one. These came out of walking
`SavePresetSnapshot` / `LoadPresetSnapshotToDevice` / `ReapplyLastCustomPresetAsync` against every
section:

- **Gap A — Preset Reset does not restore key assignments or key→macro bindings.** Both *are*
  snapshotted and both *are* restored on a preset switch, but `ResetPresetAspectsToDefaultsAsync`
  (`Presets.cs:1268`) deliberately skips them; the exclusion is commented at `Presets.cs:1288-1292`
  and already documented as a *"Known gap"* in `docs/device-defaults.md:106-115`. So "Reset preset"
  on a keyboard leaves every remap and macro binding in place, which is not what the wording
  promises.
- **Gap B — keyboards are skipped entirely by the connect-time re-apply.**
  `ReapplyLastCustomPresetAsync` returns early for keyboards (`Presets.cs:572-573`); only keyboard
  *RGB* is re-pushed on connect, by the lighting-only fallback in `ReapplyOnConnectAsync`. Key
  remaps and macro bindings are therefore **not** re-asserted when a keyboard is re-plugged, so a
  board that was reset or used on another PC comes back without the active preset's remaps until
  the user switches presets by hand. This is the gap most likely to read as "presets don't work" on
  a keyboard.
- **Gap C — `BackfillPresetSnapshotFromLive` omits `preset_key_macro_bindings`.** Its table list
  covers five of the six snapshot tables; key→macro bindings are missing, so a snapshot repaired by
  that path can come back without them. **Corrected after review (Round 1, S2): this is a latent
  inconsistency, not an active bug.** The method's only caller,
  `DeviceDetailsViewModel.BackfillSelectedPresetSettings`, returns early on
  `!Device.SupportsDpiStageSwitch`, and that flag is `DeviceTypeLabel.Contains("mouse")` — so no
  keyboard ever reaches the backfill, and keyboards are the only devices with key bindings, macro
  bindings or actuation. The missing entry could therefore never have been observed. It is filled
  in for consistency and commented as unreachable; the gate is deliberately **not** widened, because
  the backfill's contract ("record what the preset was already effectively doing") holds for a
  catalog-resolved DPI stage but not for a key remap or actuation value the user edited and chose
  not to save — widening it would silently promote unsaved work into the snapshot.

Two more absences are **deliberate and correct**, recorded here so the audit is complete: the macro
**library** itself is not snapshotted (presets reference `macro_id` into the shared `macros` table —
DDL comment `LocalStorageService.cs:231-234`), and app-scoped preferences (scroll speed,
double-click speed, sleep timer) are explicitly not preset state (`Presets.cs:1265-1267`,
`docs/device-defaults.md:101-104`).

### Capability matrix (from `DeviceHost/appsettings.json` + `IsSectionAvailable`, line 1136)

| Keyboard | RGB | Macros | Key assign | Actuation | Sections shown |
|---|---|---|---|---|---|
| Raster | ✅ | ✅ | ✅ | ❌ | Presets, RGB, Key Assignments, Macros, Firmware |
| T-Racer | per-transport | ✅ | ✅ | ❌ | Presets, (RGB), Key Assignments, Macros, Firmware |
| Rampage | ✅ | ✅ | ✅ | ❌ | Presets, RGB, Key Assignments, Macros, Firmware |
| Cyrex | ✅ | ✅ | ✅ | ✅ | + Actuation |
| Aress | ✅ | ✅ | ✅ | ✅ | + Actuation |
| T800 | ✅ | ✅ | ✅ | ✅ | + Actuation |

`SupportsActuation: true` appears on exactly Cyrex, Aress and T800 — the three the user named. No
keyboard has DPI, mouse-settings or Audio sections (`IsDpiNavVisible`, `IsSettingsNavVisible`,
`IsAudioNavVisible` are all mouse/headset-gated), so "all available sections for each keyboard" is
the six rows above and nothing else.

### How this lands in `docs/device-defaults.md`

[`docs/device-defaults.md`](../../device-defaults.md) is the authority for what "default" means in
this app: factory-sane starting values declared per device under
`AllowedDevices:Devices[*]:Defaults` in `src/ProoveGaming.DeviceHost/appsettings.json`, travelling
over gRPC on `DeviceCatalogEntry` and surfacing as `DeviceCardViewModel.Defaults`. Adding actuation
to presets makes that document incomplete in three places, and it must be updated in the same
change:

- **"Preset reset — what it restores"** (line 93) states its aspect list is *"exactly the set a
  preset snapshot stores"*. Once actuation is in a snapshot, that table is wrong until actuation is
  added to it.
- **"Fields"** (line 37) and **"Current values"** (line 51) describe what a device may declare.
- **"Fallbacks"** (line 128) describes what the app uses when a device declares nothing.

**Decision: actuation declares no catalog `Defaults` fields; its defaults come from the board.**
The device already reports them — `GetKeyActuationResponse.defaults` → `_actuationInfo.Default*CentiMm`
— and `RestoreActuationDefaults` (`Actuation.cs:377-389`) already restores from exactly that. Adding
`ActuationPressMm`-style catalog fields would duplicate hardware truth in configuration and create a
second source of truth for a value the board is authoritative about, which is the precise mistake
this change is otherwise working to avoid. In the document's own vocabulary this is "no opinion":
the reset table gains a row reading *"the board's own reported defaults"*, with a short note saying
why there are no `Actuation*` fields.

This also resolves how actuation fits the document's *"When the fallback kicks in"* ordering
(line 149) — live value wins, then stored, then catalog default. For actuation the ordering is
**live (the board's read) → stored (`device_actuation` / the preset) → the board's reported
defaults**; there is no catalog tier at all.

**A documentation bug found while reading it.** Lines 74–75 assert *"T800 declares nothing at all.
It has no adjustable DPI and no lighting, so there is nothing for a default to describe."* That is
stale: T800 has `"SupportsRgb": true` and declares a full `Defaults` block
(`RgbEnabled/RgbEffect/RgbColorHex/RgbBrightness`), and the document's own "Current values" table
(line 60) lists it. The note contradicts the table two rows above it. Fixed in the same pass.

## Scope

**In**

- A live `device_actuation` table and a `preset_actuation` snapshot, following the established
  live-row/snapshot pattern exactly.
- Writing actuation to the live rows wherever it is currently written to the device.
- Snapshotting actuation on Save Preset, and pushing it on preset switch / apply, for the three
  boards that support it.
- Preset **reset-to-default** restoring actuation to the device's own defaults.
- Automated round-trip coverage of the whole preset contract, per keyboard family — the audit half
  of the request.

**Out**

- Any change to the actuation protocol, the gRPC contract, or the DeviceHost/Infrastructure
  services. This is App-side persistence only; `SetKeyActuationAsync` / `SetPerKeyActuationAsync`
  are called exactly as they are today.
- Bringing actuation to boards that do not support it (Raster, T-Racer, Rampage) — the flag stays
  the gate.
- Dead zones, calibration, advanced key modes (DKS/MT/TGL/Snap) — still untouched, still preserved
  byte-for-byte by the family services.
- Cross-device preset copy, import/export, cloud sync — out since the original preset plan.
- **Gap A** (Reset preset not restoring key assignments) and **Gap B** (keyboards skipped by the
  connect-time re-apply) — both are behaviour changes users could notice, so they are *reported* by
  this plan and decided in *Open questions*, not silently folded in. **Gap C** is in scope: it is a
  one-line omission in a table list this change already edits.
- Changing what any other section stores.

## Options considered

### Option A — live table + snapshot table, mirroring every other section *(recommended)*

`device_actuation` (live, keyed like every other per-device table) + `preset_actuation` (snapshot,
keyed by `preset_id`). The UI edits live rows; Save Preset copies them; preset apply pushes them
through the existing `SetPerKeyActuationAsync` / `SetKeyActuationAsync`.

*Why it wins:* it is the pattern the preset system already runs on six times over, so preset
switch, discard, reset, duplicate, delete-cascade and the guest→user migration all keep working
without special cases. A reviewer can check it against `preset_key_bindings` line for line.

*Trade-off:* it makes the App a second source of truth alongside the board's flash, which is
exactly what the earlier plans avoided. Mitigated by the no-op rule (a preset with no actuation
rows never writes actuation) and by seeding live rows from the device on first open.

### Option B — snapshot only, no live table

Save Preset reads the current values straight out of `_perKeyActuation` and writes
`preset_actuation`; no live table.

*Rejected:* the live row is what makes "discard unsaved changes" and "reset preset" work — without
it there is nothing to revert *to* except the device, and the device has already been written
(actuation applies immediately, debounced 400 ms). It would also diverge from every other section
for no saving: `preset_settings` proves the two-table shape is what the dirty/discard flow needs.

### Option C — store actuation inside the existing `preset_settings` key/value table

`preset_settings` is `(preset_id, key TEXT, value REAL)` — actuation could ride as
`actuation.press`, `actuation.slot.34.press`, …

*Rejected:* per-key actuation is up to ~100 slots × 5 values, so it would put ~500 rows per preset
into a table designed for a handful of scalars, and every read becomes string parsing. The typed
table is cheaper to query and impossible to typo.

### Option D — leave flash as the only truth, and just document it

*Rejected:* it is the current behaviour and it is what the user is asking to change.

## Recommended approach

**Option A.** Concretely:

**1. Schema** (`LocalStorageService.EnsureSchema`, alongside the existing tables):

```sql
-- Live actuation, app-truth. slot = -1 is the "all keys" (uniform) row; >= 0 is a matrix slot.
CREATE TABLE IF NOT EXISTS device_actuation (
    owner_kind      TEXT NOT NULL,
    owner_id        TEXT NOT NULL,
    vendor_id       INTEGER NOT NULL,
    product_id      INTEGER NOT NULL,
    device_key      TEXT NOT NULL DEFAULT '',
    slot            INTEGER NOT NULL,
    press_centi_mm  INTEGER NOT NULL,
    release_centi_mm INTEGER NOT NULL,
    rapid_trigger   INTEGER NOT NULL,
    quick_press_centi_mm   INTEGER NOT NULL,
    quick_release_centi_mm INTEGER NOT NULL,
    PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, slot)
);
CREATE TABLE IF NOT EXISTS preset_actuation (
    preset_id       INTEGER NOT NULL,
    slot            INTEGER NOT NULL,
    press_centi_mm  INTEGER NOT NULL,
    release_centi_mm INTEGER NOT NULL,
    rapid_trigger   INTEGER NOT NULL,
    quick_press_centi_mm   INTEGER NOT NULL,
    quick_release_centi_mm INTEGER NOT NULL,
    PRIMARY KEY (preset_id, slot),
    FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
);
```

The `slot = -1` sentinel exists because an older DeviceHost returns an empty per-key map
(`add-per-key-actuation` documents that degradation), and All-keys mode is a single uniform write.
A board reporting per-key values stores one row per slot and no `-1` row; a board without them
stores only `-1`.

**2. Live rows are written where the device is written.** `ApplyActuationToDeviceAsync`
(`Actuation.cs:536`) already knows the exact values that reached the board — after a successful
write it updates `_perKeyActuation` (line 581–592). The same block persists them, so SQLite and the
board can only disagree if the write failed, which is already surfaced. Nothing is persisted for a
failed write.

**3. Live rows are NOT seeded from the device on open.** *(Changed during implementation — the
plan originally called for seeding; it was dropped, and here is why.)* Seeding would write the
board's current values into `device_actuation` on every open. `PresetMatchesLiveState` compares
rows, and every preset saved before this change has **no** `preset_actuation` rows — so the moment
live rows existed and the snapshot's did not, the two would differ and **Save Preset would light up
on a page the user never touched**. That is the exact hazard `docs/device-defaults.md` warns about
at lines 156-159, and the codebase already carries `BackfillPresetSettings` as scar tissue from the
same class of bug.

So: live rows are written **only by a real edit the board accepted**. A preset carries actuation
once the user has actually tuned it, and never before. This costs nothing the request asked for —
press force is still saved and restored — and it makes the migration rule (criterion 3) hold by
construction rather than by a guard: an untouched install has no actuation rows anywhere, so
nothing can be written to a board on upgrade.

**4. Save Preset snapshots.** The table-to-table copy lives in storage, not the view model:
`LocalStorageService.SavePresetSnapshot` (line 1736) wipes and re-copies exactly the six existing
`preset_*` tables (lines 1748–1755), and `LocalStorageService.LoadPresetSnapshotToDevice`
(line 1596) restores the live rows from them (lines 1608–1615). `preset_actuation` is added to both
lists — that is the whole of the persistence change, and it inherits preset duplicate, delete and
migration behaviour for free. The view-model side is only the gate on
`Device?.SupportsActuation == true`.

**5. Preset apply pushes.** `ApplyCurrentStateToDeviceCoreAsync` (`Presets.cs:714`) is the
full-state push used by Save Preset and preset switch. It gains an actuation step that:
- **does nothing at all when the preset has no `preset_actuation` rows** — the migration rule that
  keeps every pre-existing preset from wiping a board's flash;
- otherwise issues one `SetPerKeyActuationAsync` per distinct value-set (grouping slots that share
  values, so a uniform board is one call), or a single `SetKeyActuationAsync` for a `-1` row.

**6. Dirty tracking.** Actuation edits call `NotifyPresetChanged()` (`Presets.cs:71`) like every
other section, so Save Preset lights up and the unsaved-changes guard covers actuation.

**7. Reset preset** (`ConfirmResetPreset`, line 1220) restores actuation to the device defaults
already available in `_actuationInfo` (the same values `RestoreActuationDefaults`, line 378, uses).

## File-level change list

| File | Action | Intent |
|------|--------|--------|
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | edit | The two tables above; `GetDeviceActuation` / `SetDeviceActuation` / `ClearDeviceActuation`, `GetPresetActuation` / `ReplacePresetActuation`, and inclusion in the guest→user migration and the preset duplicate/delete paths that already copy the other `preset_*` tables. |
| `src/ProoveGaming.App/Models/ActuationRecord.cs` | create | Plain record for one actuation row (`Slot`, press/release, rapid trigger, quick press/release) so storage and view model pass a typed value rather than a 5-tuple. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs` | edit | Persist to `device_actuation` after a successful write; seed live rows from the device read on first open; call `NotifyPresetChanged()` on edit; expose an internal apply entry point the preset push can call. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` | edit | Snapshot actuation on save; push it in `ApplyCurrentStateToDeviceCoreAsync` (no-op when the preset holds no rows); restore defaults on reset preset. |
| `src/ProoveGaming.App/Services/LocalStorageService.cs` (Gap C) | edit | Add `preset_key_macro_bindings` to `BackfillPresetSnapshotFromLive`'s table list (lines 1956-1963), which omits it today. |
| `docs/device-defaults.md` | edit | Add actuation to "Preset reset — what it restores" (restored from the board's own reported defaults, applies to actuation-capable keyboards); note under Fields/Fallbacks that actuation declares no catalog default and why; correct the stale T800 note at lines 74-75. |
| `docs/CONTEXT.md` | edit | Glossary entries for the two new tables and for "preset-scoped actuation", per the repo's documentation rule that every new domain term is recorded in the same change. |
| `tests/ProoveGaming.App.Tests/LocalStorageServiceActuationTests.cs` | create | Storage-level: live-row upsert/read/delete, preset snapshot replace, delete-cascade, owner/device scoping, guest→user migration. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsActuationPresetTests.cs` | create | View-model level: the actuation half of the preset contract (save → switch → switch back), the no-op rule for actuation-less presets, and the unsupported-board no-op. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsPresetCoverageTests.cs` | create | The audit half: a per-keyboard round-trip proving every section a board supports survives save → switch → return. |

**Flags:** no new NuGet package, no TFM bump, no new project in `src/ProveGaming.slnx`, no
`Directory.Build.props` change, **no gRPC/proto change**, no installer or uninstaller impact. Two
new SQLite tables — additive only, created by `CREATE TABLE IF NOT EXISTS`, so an existing database
upgrades in place with no migration step and no data loss.

## Acceptance criteria

1. Given a Cyrex, Aress or T800 with a preset selected, when the user changes the actuation press
   force and clicks Save Preset, then `preset_actuation` holds a row set matching the values on the
   board.
2. Given that preset saved, when the user switches to another preset with different actuation
   values and back, then the board is written the first preset's actuation values and the Actuation
   sliders show them.
3. Given a preset created **before** this change (no `preset_actuation` rows), when it is applied,
   then **no** actuation write is issued at all and the board's existing values are untouched.
4. Given a keyboard with `SupportsActuation: false` (Raster, T-Racer, Rampage), when any preset is
   saved or applied, then no actuation row is written and no actuation RPC is issued.
5. Given actuation is changed, then Save Preset becomes enabled and leaving the page raises the
   unsaved-changes prompt — the same as changing RGB.
6. Given a preset is reset to defaults, then actuation returns to the **board's own reported
   defaults** (`GetKeyActuation`'s `defaults`, the same values the Actuation section's "Restore
   defaults" uses) and the board is written those values — and `docs/device-defaults.md`'s
   "Preset reset — what it restores" table lists actuation as an aspect, so the document and the
   code agree.
7. Given a preset is deleted, then its `preset_actuation` rows are gone (FK cascade).
8. Given a guest signs in, then their `device_actuation` rows migrate to the user owner exactly as
   the other per-device tables do.
9. Given per-key actuation values differing across keys, when the preset is saved and re-applied,
   then each key's own values are restored — not flattened to a uniform board.
10. For **each** of Raster, T-Racer, Rampage, Cyrex, Aress and T800: given every section that board
    supports has been edited, when the preset is saved, switched away from and switched back, then
    every edited value returns. This is the audit criterion and must hold per board.
11. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

This is an `add-*` change, so the tests below are required coverage. The touched paths are already
densely covered and that suite is the characterisation baseline — it must stay green:
`LocalStorageServicePresetsTests` (35 tests over snapshot save/load/reset/copy/cascade),
`DeviceDetailsPresetsViewModelTests` (42, the command surface), `DeviceDetailsPresetApplyTests`
(~55, apply/dirty/device-write), `DeviceDetailsAudioPresetTests`, and
`DeviceDetailsActuationViewModelTests` (24, the actuation view model). Actuation itself has no
preset coverage to preserve, because it has no preset behaviour — that absence is the gap.

### `tests/ProoveGaming.App.Tests/LocalStorageServiceActuationTests.cs` (new)

- `LiveActuation_RoundTrips` — write per-key rows, read them back identical.
- `LiveActuation_IsScopedByOwnerAndDevice` — two devices / two owners do not see each other's rows.
- `PresetActuation_ReplaceOverwritesTheWholeSet` — saving twice leaves no stale slots behind.
- `DeletingAPreset_CascadesItsActuationRows`.
- `GuestToUserMigration_CarriesActuation` — mirrors the existing migration tests.

### `tests/ProoveGaming.App.Tests/DeviceDetailsActuationPresetTests.cs` (new)

Driven through the real commands with the existing `FakeUsbHidDeviceService`, on the Cyrex and T800
harnesses that `DeviceDetailsKeyboardMacroBindingTests` already provides:

- `SavePreset_CapturesActuation`.
- `SwitchingPresets_RestoresActuation` — the golden path, values differ per preset.
- `ApplyingAPresetWithNoActuationRows_WritesNoActuation` — **the migration guard**, and the single
  most important test here: it is what stops old presets wiping a tuned board.
- `PerKeyActuation_SurvivesTheRoundTrip_WithoutFlattening` — distinct values on two slots.
- `ActuationEdit_MarksThePresetDirty`.
- `UnsupportedBoard_NeverWritesActuation` — Raster harness, asserts zero actuation calls on the fake.
- `ResetPreset_RestoresDeviceDefaultActuation`.

### Gap C coverage (`LocalStorageServicePresetsTests.cs`, extend)

- `BackfillPresetSnapshotFromLive_AlsoBackfillsKeyMacroBindings` — the omission is invisible today
  because nothing asserts it; this is the test that pins the sixth table into the list.

### `tests/ProoveGaming.App.Tests/DeviceDetailsPresetCoverageTests.cs` (new)

The audit, one `[Theory]` case per keyboard: edit every section the board supports (RGB, key remap,
macro binding, and actuation where supported), save, switch to a second preset, switch back, assert
every value returned. This is the test that would have caught the actuation gap, and is what makes
"presets work correctly for all keyboards" a checkable claim rather than an opinion.

**Harness note for `/test-writer`:** `DeviceDetailsPresetApplyTests`'s `IUsbHidDeviceService` fake
returns `null` from `GetKeyActuationAsync` (lines 77–82), which is precisely why the existing preset
tests have no actuation involvement. The new tests need a fake that returns a real `KeyActuationInfo`
with a populated `PerKeyValues` map — `DeviceDetailsActuationViewModelTests`'s `FakeHid` already
does exactly that and is the one to copy.

### Deliberately **not** automated

- Real HID/flash behaviour — the fake stands in, as everywhere else in this suite. Whether the
  board *physically* honours a restored actuation value is QA step 4.
- The Actuation section's sliders and the debounce timing — UI-thread and timer behaviour with no
  headless harness; covered by QA steps 2–3.
- T-Racer's per-transport RGB flag — no fake transport exists; QA step 7.

## QA (manual test steps)

1. Connect a Cyrex (or Aress, or T800). Open its page → Actuation. Note the current press force.
   *Expected:* the sliders show the values the board is actually using.
2. Change the press force and Rapid Trigger, then click Save Preset.
   *Expected:* the Save Preset button was enabled by the edit; after saving it goes quiet.
3. Create a second preset, give it clearly different actuation values, save it, then switch back to
   the first preset.
   *Expected:* the sliders return to the first preset's values, and the keys physically actuate at
   that depth again.
4. With the first preset selected, press a few keys.
   *Expected:* the board behaves at the saved depth — the setting reached the hardware, not just the
   screen.
5. Switch to Per-key mode, give two different keys clearly different values, save the preset, switch
   away and back.
   *Expected:* each key keeps its own value; the board is not flattened to one uniform setting.
6. **Upgrade check (important).** Using a profile that already had presets before this build, select
   an old preset.
   *Expected:* your actuation settings are *not* changed by selecting it — old presets leave
   actuation alone entirely.
7. Repeat steps 1–3 on a Raster, a T-Racer and a Rampage.
   *Expected:* those boards have no Actuation section at all, and their presets keep working exactly
   as before (RGB, key assignments and macros all save and restore).
8. For every keyboard in turn: change something in each section it offers (RGB colour and effect,
   a key remap, a macro bound to a key, and actuation where present), save the preset, switch to
   another preset, switch back.
   *Expected:* everything you changed comes back, on every board.
9. Delete a preset that had actuation saved, then re-open the device.
   *Expected:* no errors, the remaining presets are intact.
10. Sign in with an account after using the app as a guest.
    *Expected:* your presets, including their actuation values, are still there.
11. On a Cyrex, Aress or T800, open the presets list → row menu → **Reset**, and confirm.
    *Expected:* the actuation sliders return to the board's own factory values (the same result as
    the Actuation section's own "Restore defaults" button), and the board actuates at that depth.

## Review focus

- `null-safety` — `_perKeyActuation` and `_actuationInfo` are nullable and null for an unsupported
  or still-probing board; every new read path must tolerate that.
- `async/await` — the actuation push joins an existing async device-write chain; no `.Result`, and
  the debounced apply must not be bypassed or double-fired by the preset path.
- `thread-safety` — the actuation write is debounced on a timer and the preset push is user-driven;
  the two must not interleave into a half-written board.
- `performance hot-path` — a per-key snapshot is up to ~100 rows; save/apply must not turn that into
  one SQLite round-trip per row per keystroke of the sliders.

## Performance & quality

- **Hot paths:** the actuation sliders already debounce at 400 ms
  (`ActuationApplyDebounceMs`) before touching the device; persistence rides that same debounce, so
  dragging a slider must not produce a SQLite write per pixel. Preset save/apply is user-driven and
  infrequent.
- **Allocations:** the per-key map is a `Dictionary<int, KeyActuationKeyValues>` that already
  exists; the snapshot should reuse it rather than materialising parallel lists per row.
- **Device round-trips:** slots sharing identical values must be grouped into one
  `SetPerKeyActuationAsync` call (the RPC already takes a slot collection), so a uniform board costs
  one write, not one per key.
- **Thread-safety:** all new state is UI-thread view-model state; `LocalStorageService` serialises
  its own access under its existing lock. No new shared state on the singleton device services.
- **Resource lifetime:** no new `IDisposable`, no new event subscriptions.

## Risks

1. **Reversing "flash is the only truth" is the real risk of this change.** Two truths now exist,
   and they disagree whenever the board is changed by something other than this App (vendor
   software, another PC, a firmware reset). The rule that keeps this safe is that the *device read
   on open* still drives what the UI shows — the App never silently asserts its stored values over
   the board except on an explicit preset apply. Worth stating in review, because it is easy to
   "simplify" that away.
2. **Old presets must never write actuation.** If the no-op rule is missed, every user who selects a
   pre-existing preset after upgrading gets their tuned board reset to whatever a zeroed row
   implies. This is the highest-severity failure in the change and has a dedicated test and QA step.
3. **Uniform-vs-per-key ambiguity.** A board whose keys all share a value can be stored as one `-1`
   row or as N identical rows; if save and apply disagree on which, a preset can flatten a per-key
   setup. The `-1` sentinel must mean exactly "the board was written uniformly" and nothing else.
4. **Per-key slot identity — checked, and it is safe.** Actuation slots are
   `KeyAssignmentOptionViewModel.MatrixIndex` taken straight from the catalog
   (`BuildActuationWriteRequest`, `Actuation.cs:297-311`), and all three actuation boards are
   `HasNoKeyOverrideLayer` families whose `ResolveMatrixSlot` returns the catalog index untouched.
   So actuation slots are **not** probe-derived and do not carry the two-candidate hazard that
   `key_macro_bindings` has. No extra column is needed. Worth re-checking in review only if a future
   board makes actuation slots probe-dependent.
5. **Audit scope creep.** Criterion 10 asks for a per-board round-trip; if it uncovers gaps beyond
   actuation, fixing them here would balloon the change. Record them and fix separately.

## Open questions

1. **[ANSWERED — yes, push it.]** **What should an actuation-carrying preset do on device *connect*?** Preset apply on connect
   already exists (`ReapplyLastCustomPresetAsync`, `Presets.cs:558`, plus the `device_applied_preset`
   first-connect push). Pushing actuation there makes presets fully authoritative; not pushing it
   leaves the board's flash in charge until the user switches presets by hand. **Recommendation:
   push it**, for consistency with every other section — but it is a behaviour change a user could
   notice (the board's own settings being overwritten on plug-in), so it should be a deliberate
   decision rather than a side effect. Decide before `/coder` runs.
2. **Gap A — should "Reset preset" restore key assignments and macro bindings?**
   `docs/device-defaults.md` already carries this as an explicit *"Known gap: keyboard key
   assignments are not reset — out of scope for now, to be closed later"* (lines 106-122), and
   gives the reason it was deferred rather than guessed: *every other aspect has a defined default
   to restore to; a key map does not* — there is no default-layout mechanism anywhere in the
   codebase, and a key map is per-board. So closing it is not a bug fix, it is **inventing what a
   keyboard's factory layout means**. The document suggests the likely intent is "clear every custom
   assignment so the board returns to its factory layout", buildable from
   `LocalStorageService.DeleteKeyBinding`. **Recommendation: keep it deferred and out of this
   change**; if you want it, it deserves its own plan because the design decision — not the code —
   is the hard part.
3. **Gap B — should the connect-time re-apply stop skipping keyboards?** Today a re-plugged keyboard
   does not get its preset's remaps and macro bindings re-asserted. **Recommendation: yes**, and it
   pairs naturally with Open question 1 (whether preset apply on connect should push actuation) —
   both are the same question about how authoritative a preset is over a board's own memory. Doing
   B and question 1 together is coherent; doing one without the other is not. Note the cost: a
   keyboard would be written on every connect, so the push must be diffed or cheap.
4. **Scope call.** Gap A wants its own plan (it is a design decision, per above). Gap B pairs with
   question 1 and could go either way. Keeping all three out of this change leaves the actuation
   work reviewable on its own — **recommended split**.
5. ~~Are actuation matrix slots probe-derived or catalog-fixed?~~ **Answered while writing this
   plan: catalog-fixed.** See Risk 4 — the storage shape above needs no change.

## Review findings

*Filled in by `/reviewer`.*

### Round 1 — Ship: no

Surface: the uncommitted working-tree delta (5 modified files) plus 4 untracked paths
(`ActuationRecord.cs`, 3 test files, this plan folder), reconciled against
`git status --porcelain`. The one commit on this branch (`f4c53db`, the selected-key macro badge)
is a **different change** already reviewed in its own plan journal and is out of scope here — but
see **S3**. Build clean; `dotnet test ./src/ProveGaming.slnx` green at 594 + 703.

#### Plan conformance

Plan: `docs/plans/add-actuation-to-presets/add-actuation-to-presets-plan.md`

- **Implemented as planned:** both tables with the documented shape and the `-1` sentinel; live
  rows written only from an accepted device write; actuation added to all six snapshot paths
  (save, load, reset, copy, delete-cascade, dirty comparison) plus guest wipe and guest to user
  migration; the no-opinion rule; reset-to-board-defaults; Gap C; `docs/device-defaults.md` and
  `docs/CONTEXT.md`; all three test files.
- **Deviates from plan (recorded, and the right call):** live rows are not seeded from the board on
  open. The plan documents why at *Recommended approach* step 3 — seeding would make
  `PresetMatchesLiveState` false for every pre-existing preset and light up Save Preset on an
  untouched page. Plan and code agree.
- **The plan's own premise was wrong, and the code inherited it.** Step 5 says
  `ApplyCurrentStateToDeviceCoreAsync` is *the full-state push used by Save Preset and preset
  switch*. It is also the target of the 250 ms staged-edit debounce (`DebounceApplyAsync`,
  `Presets.cs:136`), which all 15 `StageChangeAndApply()` call sites feed. See **B1**.
- **Accepted limitation (per plan):** Gap A and Gap B stay open. Residual risk unchanged — a
  re-plugged keyboard still does not get its preset's remaps, macro bindings **or actuation**
  re-asserted, so "push actuation on connect" holds only for the first-connect default-preset push
  and for explicit preset switches. Worth saying plainly to the requester, who asked for connect.

#### Code

- [x] **B1. Every staged keyboard edit re-pushes actuation to the board — flash wear on T800** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:740` — Blocker
  **Problem:** the actuation push was added inside `ApplyCurrentStateToDeviceCoreAsync`'s keyboard
  branch. That method is not only the preset-apply path: `DebounceApplyAsync` (`Presets.cs:136`)
  calls it 250 ms after *any* staged edit, and `StageChangeAndApply()` has 15 call sites — RGB
  colour, RGB effect, key remap, macro bind.
  **Why it blocks:** on a T800 with any tuned actuation, dragging the RGB brightness slider now
  issues a full actuation write 250 ms after it settles. T800 actuation is `CmdSetTrip` **with the
  flash-enable byte**, `MaxAttempts = 3`, `PostWriteSettleMs = 300`, written page-wise per slot —
  so an unrelated colour change costs hundreds of milliseconds of device I/O and a flash commit.
  `DeviceDetailsViewModel.Actuation.cs:34-43` documents flash wear as the reason its own queue
  coalesces edits; this reintroduces exactly what that design avoids, from a section that has
  nothing to do with actuation. Per-key boards make it worse: one write per distinct value group,
  on every RGB tweak.
  **Solution options:**
  - Option A (correct, matches the plan's intent): push actuation from the **preset paths only** —
    `ApplyPresetToDeviceCoreAsync` after `LoadPresetSnapshotToDevice`, `ConfirmSavePreset`, and the
    first-connect `EnsureDefaultPresetAppliedAsync` — instead of from the shared full-state push.
  - Option B (smaller diff): give `ApplyCurrentStateToDeviceCoreAsync` a `bool pushActuation`
    parameter, true from the preset callers (`:266`, `:512`, `:1108`, `:1251`) and false from
    `DebounceApplyAsync`.
  - Option C (complement, not a substitute): remember the last values pushed to the board in memory
    and skip the write when unchanged. Cuts the cost but leaves the semantic wrong.

- [x] **B2. A pending actuation edit survives a preset switch and lands on the incoming preset** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:717` — Blocker
  **Problem:** `ApplyStoredActuationToDeviceAsync` neither cancels `_actuationDebounceCts` nor
  clears `_actuationEditPending`, and the preset path cancels only `_deviceApplyDebounceCts`
  (`Presets.cs:93/263/432/1101/1250`). The actuation debounce is 400 ms; nothing in a preset switch
  touches it.
  **Why it blocks:** on a Cyrex — (1) user drags the press slider; (2) within 400 ms clicks preset
  B; (3) `LoadPresetSnapshotToDevice` replaces `device_actuation` with B's rows and the push writes
  B's values to the board; (4) the stale debounce fires and writes **A's abandoned value** over
  them; (5) that write succeeds, so `PersistActuationState` (`Actuation.cs:672`) stores it into the
  live rows — which now belong to **B** — and `NotifyPresetChanged()` marks B dirty. The user is
  looking at B, the board holds a value from A, B's live rows have been overwritten with it, and B
  reports unsaved changes it never received. Saving makes it permanent. `DiscardUnsavedChanges`
  has the same hole.
  **Solution options:**
  - Option A (correct): drop any pending actuation edit at the top of
    `ApplyStoredActuationToDeviceAsync` — set `_actuationEditPending = false` and cancel
    `_actuationDebounceCts` — mirroring what the preset path already does for its own debounce, and
    what `DiscardUnsavedChanges_CancelsAnyPendingDebouncedApply` pins for the device debounce. An
    edit belonging to the outgoing preset must not survive the switch.
  - Option B (deeper, also closes the in-flight case): route the preset push through
    `ApplyActuationToDeviceAsync`'s queue so it serialises with slider writes instead of running
    beside them. Option A leaves a narrow window where a write already awaiting the device still
    lands afterwards; Option B closes it.

- [x] **S1. A failed actuation write during a preset apply is silently swallowed** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:734,748` —
  Should-fix
  **Problem / why it matters:** both calls discard the returned `bool`. The edit path treats a
  refused write as a first-class event — `ActuationWriteFailed = true` plus a re-read so the
  sliders snap to reality (`Actuation.cs:598-606`). Here, a board that rejects the preset's values
  leaves the UI showing them (`ReflectStoredActuation` runs regardless) while the hardware holds
  something else, with no banner and no log. The Smart Infinite service has a deterministic
  `Rejected` outcome for exactly this.
  **Fix:** capture the results; on any failure set `ActuationWriteFailed = true` and skip
  `ReflectStoredActuation` (or re-read via `LoadActuationFromDeviceAsync(clearWriteFailure: false)`)
  so the section shows the board, not the preset.

- [x] **S2. The Gap C fix is unreachable in production, and its test bypasses the gate that makes
  it so** — `src/ProoveGaming.App/Services/LocalStorageService.cs:2148-2150`;
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:650` — Should-fix
  **Problem / why it matters:** `preset_key_macro_bindings` and `preset_actuation` were added to
  `BackfillPresetSnapshotFromLive`'s table list. But its only caller,
  `BackfillSelectedPresetSettings`, returns at `if (!Device.SupportsDpiStageSwitch) return;`, and
  `SupportsDpiStageSwitch` is `DeviceTypeLabel.Contains("mouse")` (`DeviceCardViewModel.cs:142-143`).
  **No keyboard ever reaches the backfill** — and keyboards are the only devices that have macro
  bindings, key bindings or actuation at all. So the "one-line omission" the plan describes as a
  bug fix changes nothing observable; `preset_key_bindings` has been equally inert since it was
  added. `BackfillPresetSnapshotFromLive_AlsoBackfillsKeyMacroBindingsAndActuation` calls the
  storage method directly, so it passes while the production path is unreachable — a green test for
  code that cannot run.
  **Fix:** decide which it is and say so. Either (a) the DPI gate is the real defect — move it to
  cover only the stage/rate value collection and let the snapshot backfill run for every
  configurable device, which needs its own thought about absorbing unsaved edits; or (b) accept
  that the keyboard entries are forward-looking, and re-describe Gap C in the plan as dead-code
  hygiene rather than a fixed bug so the next reader is not misled.

- [x] **S3. Two unrelated changes share one branch** — repo state — Should-fix
  **Problem / why it matters:** `features/selected-key-macro-badge` holds the committed macro-badge
  work (`f4c53db`) and, uncommitted on top, the entire actuation-preset change. They share no code
  and were reviewed against different plans. Committing as-is produces a branch whose name
  describes half its content, and makes either half hard to revert alone.
  **Fix:** branch the actuation work off `main` (e.g. `features/actuation-in-presets`) and commit
  it there; the badge branch stays as reviewed.

#### Tests

- [x] Coverage is strong and matches the plan's Test strategy: 15 storage cases, 8 view-model
  cases, 12 per-keyboard audit cases, all mutation-checked by the author. The migration guard
  (`ApplyingAPresetWithNoActuationRows_WritesNoActuation`) is present and is the right test to have
  written first.
- [x] **No test covers B1** — nothing asserts that a staged non-actuation edit (RGB, key remap)
  does *not* write actuation. Add to `DeviceDetailsActuationPresetTests`: tune actuation, save,
  then make an RGB edit and assert the actuation write count does not increase after the staged
  debounce. This is the test that would have caught it.
- [x] **No test covers B2** — add one that arms an actuation edit, switches preset inside the
  debounce window, and asserts the board's final values and the live rows are the incoming
  preset's. The existing `FakeHid` plus `Thread.Sleep(DebounceSettleMs)` is enough.
- [x] `ClearData`'s `preset_actuation` cascade is not asserted — `ClearData_RemovesGuestActuation`
  covers only the live table.

#### Nits

- `ReflectStoredActuation` takes `rows[0]` for the slider values before recomputing
  (`Actuation.cs:778`). For a per-key set that is an arbitrary slot ordered by id; harmless because
  `ShowActuationMajorityValues()` overwrites it two lines later, but only when `_perKeyActuation` is
  non-null. On a host with no per-key map restoring a per-key preset, the sliders keep the
  lowest slot's values.
- `LiveActuation_IsScopedByOwnerAndDevice` uses Aress's real PID (12910) as the "other model" for a
  Cyrex-keyed test. Correct as a scoping check, mildly confusing as documentation.

#### What's done well

- **The no-opinion rule is the right design and is enforced by construction**, not by a guard that
  can be forgotten: an install that never tuned actuation has no rows anywhere, so no upgrade path
  can write to a board. Dropping the planned seeding to protect it — and writing down why, against
  the `docs/device-defaults.md` precedent — is the best decision in this change.
- **The debounce-ordering bug in Preset Reset was found and fixed by the author during
  self-review**, with the reason recorded on `ResetActuationToDefaultAsync` and a test pinning that
  the values reach the live rows synchronously. That is the same class of bug as B2, caught in the
  one place the author looked for it.
- **The audit test is the durable part of this change.** Pinning the snapshot contract per board at
  the storage layer means the next section added without being wired into both table lists fails a
  test instead of shipping — which is precisely how actuation went missing for three releases.
- Storage follows `preset_key_bindings` line for line, so delete-cascade, duplicate, guest wipe and
  guest to user migration all came out correct without special cases.

#### Verdict

Ship: **no** — two blockers, both about *when* the push runs rather than what it stores. B1 turns
every RGB tweak on a T800 into a flash write, on a device whose own actuation code is built around
avoiding exactly that; B2 lets an abandoned slider edit follow the user onto the next preset and
dirty it. The stored data model, the migration rule and the test coverage are sound and should not
be touched. First: **1)** move the push to the preset paths only (B1), **2)** cancel the pending
actuation edit before pushing (B2), **3)** add the two regression tests named above.

### Round 2 — blockers closed

Both blockers fixed; the three should-fixes (S1 silent write failure, S2 unreachable Gap C, S3
branch hygiene) remain open, as do the two nits and the `ClearData` cascade test gap.

- [x] **B1 — fixed by the review's Option B.** `ApplyCurrentStateToDeviceAsync` /
  `...CoreAsync` gained a `bool pushActuation` parameter, defaulting to **false** so a future
  caller cannot acquire the flash write by accident. The four preset call sites opt in explicitly
  (`Presets.cs:266` Save Preset, `:512` preset switch, `:1118` new preset, `:1261` reset);
  `DebounceApplyAsync` (`:136`) — the staged-edit path behind all 15 `StageChangeAndApply()` call
  sites — stays at the default and no longer touches actuation. Option B was taken over Option A
  because it keeps one decision point instead of four call sites that must be kept in step, and
  the ordering against `ResyncKeyMacroBindingsAsync` stays visible in one place.
- [x] **B2 — fixed with Option A plus the in-flight half of Option B.**
  `ApplyStoredActuationToDeviceAsync` now clears `_actuationEditPending` and cancels
  `_actuationDebounceCts` **before** its guards, so the drop happens even when the incoming preset
  holds no actuation rows (otherwise the stale edit would land on a preset that deliberately has no
  opinion). Writes already awaiting the device cannot be cancelled, so a generation fence closes
  that window too: `_actuationApplyGeneration` is bumped by every preset push, and
  `ApplyActuationToDeviceAsync` captures it and returns before updating the cache, persisting, or
  draining its queue if it has changed. Same shape as the existing `_keyLayoutGeneration` guard.
  **Residual:** a superseded write may still have reached the board before the fence catches it, so
  the hardware can briefly hold a value from the outgoing preset. The stores stay correct and the
  next preset apply or device read corrects the UI — the data-corruption half is closed, the
  transient-hardware half is not.

Both fixes are pinned by regression tests in `DeviceDetailsActuationPresetTests`, each
mutation-verified to fail without its fix:

- `AStagedRgbEdit_DoesNotWriteActuation` — tunes actuation first so the test cannot pass on the
  no-opinion early-out, then drives a staged RGB edit through the effect tile's own
  `SelectCommand` and asserts zero actuation writes after the debounce. Fails when `:136` is
  flipped to `pushActuation: true`.
- `APendingActuationEdit_DoesNotFollowTheUserOntoTheNextPreset` — arms an actuation edit under
  preset A, switches to B inside the 400 ms window, and asserts B's values are what the board and
  the live rows hold and that B is not dirty. Fails when the cancel and fence are removed.

Build clean; `dotnet test ./src/ProveGaming.slnx` green at **594 + 705**.

### Round 3 — should-fixes and nits closed

- [x] **S1 — a refused preset push now behaves like a refused slider edit.**
  `ApplyStoredActuationToDeviceAsync` captures the result of every write (breaking out of the
  per-key loop on the first failure); on failure it raises `ActuationWriteFailed` and re-reads via
  `LoadActuationFromDeviceAsync(clearWriteFailure: false)` instead of reflecting the preset, so the
  section shows what the board actually holds. Covered by
  `APresetPushTheBoardRefuses_RaisesTheBanner_AndDoesNotShowThePresetsValues`.
- [x] **S2 — the Gap C claim was corrected rather than the gate widened.** The audit section now
  states that the missing entry was latent, not active: `BackfillPresetSnapshotFromLive`'s only
  caller returns on `!Device.SupportsDpiStageSwitch` (mouse-only), so no keyboard has ever reached
  it and the omission could not be observed. The three keyboard-only entries stay in the list with
  a comment saying they are currently unreachable **and why the gate is deliberately not widened**:
  the backfill's contract is "record what the preset was already effectively doing", which holds
  for a catalog-resolved DPI stage but not for a key remap or actuation value the user edited and
  chose not to save — widening it would silently promote unsaved work into the snapshot. That is
  its own change, with its own risk.
- [x] **S3 — the two changes are now on separate branches.** This work moved to
  `features/actuation-in-presets`, branched from `main`; the macro badge stays on
  `features/selected-key-macro-badge` at `f4c53db`, untouched. The only file both changes touched
  was `docs/CONTEXT.md`, so this branch carries the actuation glossary entry only.
- [x] **Nit — `ReflectStoredActuation` no longer reads an arbitrary row.** The sliders are filled
  from the all-keys row when the board has one; for a per-key set they come from the refreshed
  cache via `ShowActuationMajorityValues`. A per-key snapshot restored on a host that reports no
  per-key map now leaves the sliders on the device read rather than on the lowest slot's values.
- [x] **Nit — test constant renamed** `OtherPid` → `OtherModelPid` with a comment saying what it is
  for.
- [x] **`ClearData` cascade covered** — `ClearData_RemovesGuestActuation_LiveAndSnapshot` now
  asserts the snapshot rows go with the preset, not just the live table.

Build clean; `dotnet test ./src/ProveGaming.slnx` green at **594 + 678** on this branch (678 is
main's 640 plus this change's 38 — the macro badge's tests live on its own branch).

**Nothing from Round 1 is left open.** Gap A and Gap B remain out of scope by the plan's own
Scope section, and the B2 residual is recorded in Round 2: a superseded actuation write may still
reach the board before the generation fence catches it, so the hardware can briefly hold a value
from the outgoing preset. The stores stay correct and the next apply or device read corrects the
UI; closing it fully means routing the preset push through the actuation write queue.

### Round 4 — external review (PR #91), M1 and the costly minors fixed

boikoserhii84's review on PR #91 (both reviewers approved). One major and seven minors; the major
and the three with a real device or data cost are fixed, the rest are dispositioned below.

- [x] **M1 — a QUEUED actuation edit still drained onto the next preset.** Round 2 closed two of the
  three states an in-flight edit can be in — inside the debounce (cancelled) and awaiting the device
  (generation fence) — and missed the third: an edit that has left the debounce but sits in
  `_actuationQueuedWrites` behind an in-flight write. Nothing cleared that queue on a preset switch
  (only `LoadActuationFromDeviceAsync(clearWriteFailure: true)` does, and that runs on device change
  only), so it drained on the user's *next* edit, writing the outgoing preset's value to the board
  and into the incoming preset's live rows. Fixed with the reviewer's Option A —
  `_actuationQueuedWrites.Clear()` next to the generation bump, deliberately **not** in the fence
  branch, because a write queued *after* the switch belongs to the new preset and must still land.
  Reachable with two slider edits inside one write window: ~300 ms on Cyrex/Aress, ~1 s on T800.
- [x] **m1 + m2 — redundant flash writes, fixed together by the reviewer's option (б).** A new
  `_actuationOnDevice` field records what the board is believed to hold: seeded from the device read
  on open, updated on every accepted write. `ApplyStoredActuationToDeviceAsync` skips the push
  entirely when the stored rows equal it. That removes the Save Preset re-write (live rows are by
  construction what the board last accepted, so Save was always a no-op write — a full flash commit
  per click on T800) **and** the second write in Preset Reset, where the reset had already pushed the
  same values itself. `BuildActuationRows` now sorts by slot so the comparison against
  `GetDeviceActuation`'s `ORDER BY slot` is valid.
- [x] **m3 — a refused push left the stores asserting the preset.** After the banner and re-read,
  the re-read is now written back to the live rows, so `device_actuation` follows the board rather
  than the values it rejected. `PresetMatchesLiveState` therefore reports the preset **dirty**,
  which is the honest outcome — it genuinely is not applied — instead of silently calling it clean.
  This also covers the part-way per-key failure the reviewer noted: the board is left split, and the
  stores now say so.

Four regression tests added to `DeviceDetailsActuationPresetTests`, each mutation-verified to fail
without its fix. `AQueuedActuationEdit_DoesNotDrainOntoTheNextPreset` needed a `WriteGate`
(`TaskCompletionSource`) on the fake to hold a write in flight — the queued state is unreachable
with a synchronously-completing fake, which is exactly why the internal review missed it.

**Not fixed, with reasons:**

- **m4** (the per-board audit theory executes identical code for all six rows, and the capability
  matrix is hand-copied from `appsettings.json`) — a fair hit. The suggested fix, a test that parses
  `appsettings.json` and diffs it against `Keyboards()`, is worth doing but is a new test-side
  dependency on the DeviceHost config layout; better as its own change than smuggled into a
  review-fix round.
- **m5** (a per-key snapshot restored on a host with no per-key map leaves sliders and board
  disagreeing) — the suggested re-read costs a device round-trip on a path that is already rare
  (it needs a preset saved on a newer DeviceHost and restored on an older one). Recorded rather than
  fixed blind.
- **m6** (`ReplaceDeviceActuation` is a synchronous SQLite transaction on the UI thread, one INSERT
  per slot, up to 128 rows per accepted edit) — real, and the reviewer correctly notes the class is
  pre-existing across `LocalStorageService` while the *volume* is new. Batching the inserts is easy;
  moving persistence off the UI thread is not, and doing half of it invites the wrong conclusion
  that it is handled. Left for a dedicated change.
- **m7** (`ConfirmSavePreset` does not flush a pending actuation edit before snapshotting) — the
  window is practically unreachable (Save Preset goes through a dialog) and a correct fix needs an
  awaited flush, which means making `ConfirmSavePreset` async. The comment it contradicts is worth
  amending; not worth an async signature change in this round.

App project and both test projects build clean; **594 + 682** tests green. A solution-wide build
also reports MSB3027 on `ProoveGaming.DeviceHost` because that service is running on this machine
and holds its own output DLLs — environmental, unrelated to this branch, which does not touch
DeviceHost.
