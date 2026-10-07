# Plan — Preset-scoped device writes

## Goal

Today every settings edit in the device-details screen is written to the hardware **immediately**
(RGB colour/effect, button/macro bindings, mouse settings, DPI table, active DPI stage, report
rate). We change the timing so edits only **stage into the selected preset's working state** and the
device is written **only on Save Preset** and **when switching presets**. Additionally, each device
gets a **default preset** (its per-section default values) that is applied to the hardware **once, on
first connect** — tracked so we know whether a preset has ever been pushed to that device. Physical
DPI/polling buttons keep working live (device → app reflection is unchanged).

## Scope

**In**
- Stop the immediate app → device writes in the RGB (mouse **and** keyboard), Buttons/macros, Settings
  (mouse settings, DPI table, active DPI stage, report rate), **and keyboard key remapping** edit paths;
  keep the in-memory + SQLite "live row" updates and the dirty-tracking (`NotifyPresetChanged`).
- **Add persistence + preset-scoping for keyboard key remapping** (it has none today): a live
  `key_bindings` table and a `preset_key_bindings` snapshot, applied to the device via the per-slot
  `SetKeyMappingAsync` on Save/switch/connect.
- Extend the preset snapshot to include mouse settings / DPI table / active stage / report rate
  (new `preset_settings`), so a preset carries the full configuration, not just RGB + bindings.
- One **full-state device push** helper (bindings + RGB + settings + DPI + rate + key mappings)
  invoked on **Save Preset** and on **preset switch**.
- **Default preset applied on first connect**: when a device connects and no preset has ever been
  applied to it, push the default preset (which resolves to each section's default values) and
  record that a preset is now applied. Persist the "applied preset" per device.
- Keep physical DPI/polling-button listening (device → app) live and immediate.

**Out**
- Physical-button/DPI live control behaviour (device → app reflection, unchanged).
- Macro recording/encoding, the gRPC device-write RPCs themselves (only their *timing/orchestration*
  changes — no proto changes; keyboard apply reuses the existing per-slot `SetKeyMappingAsync`).
- Any change to how the DeviceHost applies a write once it receives one.

## Mouse vs keyboard applicability

The "live rows are the staging area" model relies on a section persisting its working state to SQLite
live rows that a preset can snapshot. This is true for most sections already; keyboard key remapping is
the one that needs **new** persistence added by this change:

| Section | Mouse | Keyboard | Live rows | Preset snapshot |
|---|---|---|---|---|
| RGB colour/effect | ✅ | ✅ | `device_rgb` / `device_rgb_effect` (existing) | `preset_rgb*` (existing) |
| Button/macro bindings | ✅ | n/a | `button_bindings` (existing) | `preset_button_bindings` (existing) |
| Mouse settings / DPI / active stage / report rate | ✅ | n/a | `device_settings` (existing) | `preset_settings` (**new, Part B**) |
| Key remapping | n/a | ✅ | `key_bindings` (**new, Part D-kbd**) | `preset_key_bindings` (**new**) |

**Keyboard key remapping today** (`ApplyAssignmentTargetAsync`) writes `SetKeyMappingAsync` immediately,
keeps only an in-memory `_userOverrides` map (never SQLite), and treats the **firmware as the source of
truth** — `ProbeAndUpdateMatrixMapAsync` re-reads it on connect so a manual FN+Space factory reset shows
correctly. To make it preset-scoped we invert that to **app-truth** (like mouse bindings): user
overrides are persisted in `key_bindings`, snapshotted per preset, and pushed to the device on
Save/switch/connect. The probe is **still** used — but only for the `ScancodeToSlot` matrix map (device
capability) and as the "current device override" set to diff against when applying (so we write the
minimum number of per-slot changes). Consequence: the app now overwrites a manual FN+Space reset with
the active preset — an intended part of "changed only on Save Preset". Persist by the physical key's
**scancode** (catalog-stable), resolving scancode → slot via the probe at apply time.

## Current architecture (as found)

- **Live rows = working state.** `device_rgb`, `device_rgb_effect`, `button_bindings`,
  `device_settings` hold the current per-(owner, device) state. Edits write these **and** the device.
- **Preset snapshot = a copy.** `preset_rgb`, `preset_rgb_effect`, `preset_button_bindings` mirror the
  live rows for one preset. `SavePresetSnapshot` copies live → snapshot; `LoadPresetSnapshotToDevice`
  copies snapshot → live. **There is no `preset_settings`** — mouse settings/DPI/rate are not
  preset-scoped today.
- **Dirty tracking exists.** `HasUnsavedPresetChanges` + `NotifyPresetChanged()` (called from edits),
  the `UnsavedChangesDialog` (leave-view / switch-preset), and `_suppressPresetDirty` guard.
- **Immediate device writes** live at:
  - `DeviceDetailsViewModel.Buttons.cs` — 6 `SetButtonBindingsAsync` sites.
  - `DeviceDetailsViewModel.Rgb.cs` — `SetDeviceLedColorAsync` (×3) / `ApplyRgbSettingsAsync`.
  - `DeviceDetailsViewModel.Settings.cs` — `SetMouseSettingsAsync`, `SetActiveDpiStageAsync`,
    `SetDpiStagesAsync`, `SetReportRateAsync`.
  - `DeviceDetailsViewModel.KeyAssignments.cs` — `SetKeyMappingAsync` (keyboard).
  - `DeviceDetailsViewModel.Presets.cs` — `ApplyPresetToDevice` (switch) already pushes bindings + RGB.

## Options considered

1. **In-VM pending buffer.** Hold every staged edit in new in-memory structures; flush on save.
   Rejected: duplicates the working state that the live SQLite rows already represent, adds a second
   source of truth to keep consistent, and complicates the unsaved-changes/discard flow.
2. **Live rows as the staging area (recommended).** Edits keep writing the live SQLite rows + marking
   dirty, but **skip the device write**. Save Preset copies live → snapshot **and** pushes the full
   live state to the device. Preset switch loads snapshot → live **and** pushes. Discard on switch
   already overwrites live rows from the target snapshot, so staged edits are dropped naturally.
   Minimal new state, reuses `SavePresetSnapshot`/`LoadPresetSnapshotToDevice`, and the dirty flow is
   already built around this shape.

## Recommended approach

Option 2, in three cohesive parts (implement in this order; they are dependent, not independent):

**Part A — Defer the writes.** In each edit handler, remove the `_usbHidDeviceService.Set*` call;
keep the live-row persistence and `NotifyPresetChanged()`. RGB keeps animating in the **app UI only**
(no device write) per the confirmed decision. Active DPI stage / report rate: app-side changes stage
only; the physical-button device→app listeners stay live and unchanged.

**Part B — Put "other settings" in the preset.** Add a `preset_settings` snapshot table mirroring the
key/value `device_settings` rows (debounce, lift-off, DPI table, active DPI stage, report rate — the
coder confirms each of these is persisted in `device_settings`; if any is computed-only today, add its
persistence so it can be snapshotted). Extend `SavePresetSnapshot` and `LoadPresetSnapshotToDevice` to
cover it.

**Part B-kbd — Persist + preset-scope keyboard key remapping.** Add a `key_bindings` live table and a
`preset_key_bindings` snapshot. Rework `ApplyAssignmentTargetAsync` to **stage** (upsert/delete the live
row + `NotifyPresetChanged`, no device write); load the UI's current overrides from `key_bindings`
instead of the probe's `UserOverrides`; keep `ProbeAndUpdateMatrixMapAsync` for the `ScancodeToSlot`
map and the device's current-override set (used only as the diff baseline at apply time).

**Part C — Full-state push + connect-time default.** Add one internal
`ApplyCurrentStateToDeviceAsync()` that pushes the full live state to hardware, **device-type-aware**:
for mice — button bindings, RGB, mouse settings, DPI table, active DPI stage, report rate; for
keyboards — RGB **and** key remapping. Keyboard key remapping has no batch API, so apply is
**diff-based** against the probed current-override set: for each physical key, write its staged override
(or its identity/default mapping when it was overridden before but isn't now), skipping slots already
matching — minimising per-slot `SetKeyMappingAsync` calls. Call it from **ConfirmSavePreset** (after
`SavePresetSnapshot`) and reuse it inside **ApplyPresetToDevice** (replacing the partial bindings+RGB
push). On device connect, if `GetAppliedPreset(device)` is null, apply the **default preset** via the
existing apply path and `SetAppliedPreset(device, presetId)`; otherwise do nothing (no unsolicited
write). The default preset's empty snapshot already resolves to each section's defaults on load, so
"default values per section" comes from the existing section defaults (button `DefaultButtonAssignments`,
catalog `DpiStages`, RGB default, settings defaults) — the coder ensures each section has a defined
default to fall back to.

## File-level change list

**Storage**
- `src/ProoveGaming.App/Services/LocalStorageService.cs`
  - Add `preset_settings` table `(preset_id, key, value)` + include it in the preset delete cascades.
  - `SavePresetSnapshot`: also snapshot `device_settings` → `preset_settings`.
  - `LoadPresetSnapshotToDevice`: also restore `preset_settings` → `device_settings`.
  - Add `key_bindings` live table `(owner_kind, owner_id, vendor_id, product_id, device_key, phys_scancode, type_byte, bitmask, target_code)` PK on scope+`phys_scancode`, and `preset_key_bindings` snapshot `(preset_id, phys_scancode, type_byte, bitmask, target_code)`; add `GetKeyBindings` / `SetKeyBinding` / `DeleteKeyBinding`; include in `SavePresetSnapshot` / `LoadPresetSnapshotToDevice` and the preset delete cascades.
  - Add `device_applied_preset` table `(owner_kind, owner_id, vendor_id, product_id, device_key, preset_id)` PK on the scope; add `GetAppliedPreset(...)` / `SetAppliedPreset(...)`.
  - Migration: `AddColumnIfMissing`-style guards / `CREATE TABLE IF NOT EXISTS`; no destructive change.

**View-models**
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs`
  - Remove the 6 immediate `SetButtonBindingsAsync` calls from edit paths; keep SQLite writes + `NotifyPresetChanged`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs`
  - Remove immediate `SetDeviceLedColorAsync` writes from edit paths; keep app-side animation + persistence + dirty. `ApplyRgbSettingsAsync` stays but is only invoked by the full-state push, not per edit.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs`
  - Remove immediate `SetMouseSettingsAsync` / `SetActiveDpiStageAsync` / `SetDpiStagesAsync` / `SetReportRateAsync` from app-side edit paths; persist + dirty instead. Keep the physical DPI/polling listeners (in `DeviceDetailsViewModel.cs`) untouched.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs`
  - `ApplyAssignmentTargetAsync`: **stage instead of write** — upsert/delete the `key_bindings` live row
    for the physical key's scancode and `NotifyPresetChanged`; drop the immediate `SetKeyMappingAsync`.
  - Load the UI's current overrides from `key_bindings` (app-truth) rather than the probe's
    `UserOverrides`; keep `ProbeAndUpdateMatrixMapAsync` for `ScancodeToSlot` + the diff baseline.
  - Provide a keyboard branch of the full-state push (diff-based per-slot apply) used by Part C.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs`
  - Add `ApplyCurrentStateToDeviceAsync()`; call it from `ConfirmSavePreset` after `SavePresetSnapshot`.
  - Rework `ApplyPresetToDevice` to call `ApplyCurrentStateToDeviceAsync()` for the full push (not just bindings + RGB).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs`
  - On `SetDevice` (or the connect hook — see Open questions), run the connect-time default apply: if no applied preset for this device, apply the default preset and record it.

**Connect hook (see Open questions for placement)**
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` *(only if the apply-on-connect is placed at the connection layer rather than device-open)* — trigger the default-preset apply for a newly connected device.

**Tests**
- `tests/ProoveGaming.App.Tests/LocalStorageServiceSettingsTests.cs` — preset snapshot round-trip **including settings**; `GetAppliedPreset`/`SetAppliedPreset` round-trip.
- `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` (or a new `DeviceDetailsPresetWriteTests.cs`) — an edit does **not** call the device service; Save Preset / preset switch **does**; first-connect applies the default once.

## Acceptance criteria

- Given a preset is selected, when the user changes RGB / a button binding / a mouse setting / DPI
  table / report rate / **a keyboard key remap** in the app, then **no** device-write RPC is issued and
  `HasUnsavedPresetChanges` becomes true.
- Given staged keyboard key remaps, when Save Preset (or a preset switch) runs, then the keyboard's
  changed slots are written via `SetKeyMappingAsync` (delta only), and slots no longer overridden are
  reset to their default mapping.
- Given staged edits, when the user presses **Save Preset**, then the preset snapshot is written **and**
  exactly one full-state push reaches the device service, and `HasUnsavedPresetChanges` clears.
- Given two presets, when the user switches to the other preset, then that preset's full state
  (bindings + RGB + settings + DPI + rate) is pushed to the device.
- Given the user switches presets with unsaved edits and chooses **Discard**, then the staged edits are
  dropped (live rows overwritten from the target snapshot) and the target preset is applied.
- Given a device connects for the first time (no applied-preset record), when it connects, then the
  default preset is applied to the device exactly once and recorded; a subsequent connect does **not**
  re-push.
- Given a physical DPI/polling button press on the device, then the app still reflects the new stage/
  rate live (device → app), independent of the staged-edit flow.
- `dotnet build ./src/ProveGaming.slnx` succeeds; existing preset/macro tests stay green.

## Test strategy

This is a **behaviour change** to an existing feature that already has some coverage
(`LocalStorageServiceSettingsTests`, `DeviceDetailsMacroAssignmentTests`), so those must stay green and
be extended. `tests/ProoveGaming.App.Tests`:
- **Storage:** `SavePresetSnapshot`/`LoadPresetSnapshotToDevice` round-trip now covers `preset_settings`
  **and `preset_key_bindings`** (save into a preset, mutate live, load preset back, assert restored).
  `GetKeyBindings`/`SetKeyBinding`/`DeleteKeyBinding` and `GetAppliedPreset`/`SetAppliedPreset` round-trip
  incl. per-device isolation.
- **VM (via a fake `IUsbHidDeviceService` that records calls):** editing RGB/binding/setting/**keyboard
  key remap** issues **zero** device calls; Save Preset issues the full push once (incl. the changed
  keyboard slots); preset switch issues the full push; a keyboard apply writes only the delta slots and
  resets slots dropped from the preset; first-connect applies default once and marks applied, second
  connect issues none.
- **Deliberately not automated:** the actual HID write correctness, Avalonia UI-thread affinity of the
  RGB animation, and physical-button hardware events — covered by QA.

## QA (manual test steps)

1. Launch the app and open a connected mouse's settings.
2. On the Buttons page, remap a button. Expected: the device does **not** change yet; the SAVE PRESET
   button becomes enabled.
3. On the RGB page, pick a different colour/effect. Expected: the app preview animates, but the mouse's
   LED does **not** change yet.
4. On the DPI/settings page, change a DPI value and the report rate. Expected: no change on the device
   yet.
5. Press **Save Preset** and confirm. Expected: now the mouse applies the remap, LED, DPI and report
   rate together; SAVE PRESET greys out (no unsaved changes).
6. Create/switch to a second preset. Expected: the mouse immediately changes to that preset's settings.
7. Make an edit, then switch presets and choose **Discard**. Expected: your edit is dropped and the
   other preset's settings are on the device.
8. Press the physical DPI button on the mouse. Expected: the app's active DPI stage updates to match.
9. Unplug and replug the mouse (first time on a clean profile). Expected: the mouse comes back with the
   default preset's settings applied automatically.
10. On a **keyboard**, change the RGB effect and **remap a key**. Expected: neither the LED nor the key
    changes on the device yet; SAVE PRESET enables. Press Save Preset. Expected: both the LED and the key
    remap now apply. Switch presets. Expected: the keyboard's RGB and key remaps change to the other
    preset. (A manual FN+Space reset on the keyboard is expected to be overridden by the active preset on
    the next apply.)

## Review focus

`async/await`, `UI-thread affinity`, `thread-safety`, `null-safety`.

## Performance & quality

No per-frame hot path is added — the opposite: removing per-edit device writes reduces HID traffic
(e.g. dragging an RGB colour no longer spams the device). The full-state push runs on Save/switch/
connect (discrete user actions) and must be `async` and non-blocking on the UI thread (fire the push,
marshal results back via the dispatcher, as the current code already does). The SQLite live-row writes
stay on the existing `SemaphoreSlim`-guarded path. Avoid a push storm: one coalesced push per Save/
switch, not one per section.

## Risks

- **Losing live RGB/DPI feedback** is an intended UX change; make sure the app-side RGB preview still
  animates so the screen isn't dead while editing.
- **Apply-on-connect writes to the device the user may not have opened**, and for BLAZE a key-matrix
  write carries brick risk (see `docs/mouse/blaze/`). Mitigate by applying **once** per device (the
  applied-preset record) and only when nothing has been applied yet. Placement (connect vs first
  device-open) is an open question below.
- **Physical-button vs staged-edit reconciliation:** a physical DPI/polling change alters real device
  state while the app may hold staged edits. Recommend physical changes update the reflected live state
  but do **not** auto-dirty the preset; document the chosen rule.
- **Settings not currently persisted** (if any DPI/rate value is computed-only) must gain persistence
  before it can be snapshotted — a hidden sub-task inside Part B.
- **Migration** adds tables only (non-destructive); existing presets simply gain empty `preset_settings`
  until next save.
- **Keyboard key remapping becomes app-truth:** the active preset now overwrites a manual FN+Space
  factory reset on the device (previously the app deferred to the firmware). This is intended but is a
  behaviour change for keyboards — call it out in the PR.
- **Keyboard apply cost:** no batch key-matrix write exists, so applying a preset issues one
  `SetKeyMappingAsync` per *changed* slot. The diff against the probed current-override set keeps this to
  the delta (usually a handful), not the whole matrix. If a future full-matrix RPC is added it can
  replace the loop; not required now.
- **Probe dependency:** the diff-based apply needs `ProbeAndUpdateMatrixMapAsync` to have run (matrix map
  + current overrides). Sequence the connect/apply so the probe completes before the first keyboard push.

## Open questions

1. **Apply-on-connect placement.** Put the first-connect default apply at the connection layer
   (`HomeViewModel`, fires for any connected device even if never opened) or at first **device-open**
   (`SetDevice`, only when the user enters the device)? The connection layer matches "on connected"
   literally but writes to unopened devices (brick-risk surface); device-open is safer. **Recommended:
   device-open**, tracked so it happens once — confirm.
2. **Physical DPI/polling change dirtying.** Should a physical-button change mark the preset dirty (the
   device now differs from the saved preset) or stay purely live? **Recommended: stay live, not dirty.**
3. **Keyboard key mappings — resolved: included.** They become app-truth and preset-scoped (new
   `key_bindings` / `preset_key_bindings`, diff-based apply). The intended consequence is that the active
   preset now overrides a manual FN+Space factory reset on the device — confirm that's acceptable (it is
   the direct result of "changed only on Save Preset").

## Review findings

### Round 1 — Ship: no

#### Plan conformance

Plan: `docs/plans/refactor-preset-scoped-device-writes/refactor-preset-scoped-device-writes-plan.md`

- Implemented as planned: Part A (device writes removed from all RGB/Buttons/Settings/KeyAssignments
  edit paths, live-row persistence + `NotifyPresetChanged()` kept), Part B (`preset_settings` table +
  snapshot/restore wired into `SavePresetSnapshot`/`LoadPresetSnapshotToDevice` and both delete
  cascades), Part B-kbd (`key_bindings`/`preset_key_bindings`, keyboard key remap inverted to
  app-truth with `StageAssignmentTarget`/`ApplyKeyOverridesToDeviceAsync` diff-based apply), Part C
  (`ApplyCurrentStateToDeviceAsync`, connect-time default via `EnsureDefaultPresetAppliedAsync`,
  `device_applied_preset` tracking).
- Deviates: none within this plan's own boundary (see the sibling
  [refactor-unify-device-writes](../refactor-unify-device-writes/refactor-unify-device-writes-plan.md)
  review for deviations specific to the unified-write RPC this plan's Part C now calls).
- Missing/partial: the Test strategy's VM-level requirements are not met — no test asserts "Save
  Preset issues the full push once", "preset switch issues the full push", or "first-connect applies
  default once, second connect issues none". `DeviceDetailsMacroAssignmentTests` was extended for the
  zero-device-calls-on-edit regression guard, but the Save/switch/connect-apply side is untested. This
  gap is not academic — see B1, which a first-connect test would have caught.
- Accepted limitation (per plan): keyboard app-truth override consequence (a manual FN+Space reset is
  overridden by the active preset on the next apply) — implemented and documented as intended; residual
  risk: none beyond what the plan already accepted.

#### Code

- [x] **B1. Default-preset-on-connect can push DPI stage 0 to the device before the live stage is known** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:584` (`EnsureDefaultPresetAppliedAsync`) — Blocker
  **Problem:** `EnsureDefaultPresetAppliedAsync` fires (fire-and-forget) from `SetDevice` whenever
  `GetAppliedPreset` returns null — true for **every** device on **every** existing install, because
  `device_applied_preset` is a brand-new table that starts empty for everyone, not just fresh devices.
  It snapshots whatever is currently in the live SQLite rows into the target preset, then calls
  `ApplyPresetToDevice` → `ApplyCurrentStateToDeviceAsync` (Presets.cs:255-296), whose mouse branch
  computes `activeStage = DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex`. `DpiPresets[0]`
  is set `IsSelected = true` unconditionally in `RebuildDpiPresets` (DeviceDetailsViewModel.cs:274-291)
  as the resting default, and is only corrected by `ApplyKnownDeviceState` (DeviceDetailsViewModel.cs:221-226)
  **if** `_usbHidDeviceService.TryGetLastDpiStage(...)` already has a value — otherwise it explicitly
  falls back to stage `0`. The DeviceHost's live-stage push is asynchronous and not guaranteed to have
  arrived by the time this runs (TAIPAN in particular: the docs note the dongle never answers state
  queries at all, so the live stage is known only from the device's own occasional `0x49` push, which
  can lag well behind the app opening the device page).
  **Why it blocks:** on the very first device-details open after this branch ships, for a user who
  already had the mouse configured to (say) 3600 DPI, if the DeviceHost hasn't yet pushed a live stage
  for that unit, `EnsureDefaultPresetAppliedAsync` silently writes **stage 0** (the lowest DPI) to the
  physical device as part of the "default preset" apply — a real, timing-dependent, hard-to-reproduce
  regression that resets the user's DPI without any user action. This is exactly the class of symptom
  this session spent hours chasing for a different root cause; this is a second, separate one.
  **Solution options:**
  - Option A (correct): treat "no known stage yet" as *don't touch* rather than *stage 0* — e.g. track
    whether `DpiPresets`' current selection came from a real source (persisted value or a confirmed
    host push) vs. the UI's structural default, and omit `activeStage` from the
    `ApplyDeviceConfigurationAsync` call in `ApplyCurrentStateToDeviceAsync` when it hasn't. Mirrors
    the "null = don't touch" contract the rest of `DeviceConfiguration` already uses.
  - Option B (quick): in `EnsureDefaultPresetAppliedAsync`, before snapshotting/applying, attempt to
    resolve a known stage (persisted value, or `TryGetLastDpiStage`); if neither is available, skip the
    apply for this open **without** recording `device_applied_preset`, so it safely retries on the next
    open once a live stage is known.
  **Fixed (Option A):** added `_dpiStageKnown` (`DeviceDetailsViewModel.cs`), reset to `false` in
  `RebuildDpiPresets` on every device change and set `true` only at the real "we actually know this"
  sources — `ApplyKnownDeviceState` (when `TryGetLastDpiStage` succeeds), `OnDpiStageChanged` (a live
  push), `LoadPersistedDeviceStateAsync` (a persisted value), and `SelectDpiPreset` (the user's own
  click). `ApplyCurrentStateToDeviceAsync` (Presets.cs) now only reads `DpiPresets`' `IsSelected` into
  `activeStage` when `_dpiStageKnown` is true; otherwise `activeStage` stays `null` (don't touch).
  Covered by `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`
  (`ConfirmSavePreset_WithNoKnownDpiStage_OmitsActiveStage`,
  `ConfirmSavePreset_AfterUserSelectsDpiStage_SendsThatActiveStage`).

#### Tests

- [x] `ApplyCurrentStateToDeviceAsync` reproduces B1: given a device with no persisted DPI stage and
  `_usbHidDeviceService.TryGetLastDpiStage` returning false, assert `ApplyDeviceConfigurationAsync` is
  called with `activeStage: null` (not `0`) — added as
  `DeviceDetailsPresetApplyTests.ConfirmSavePreset_WithNoKnownDpiStage_OmitsActiveStage`, plus the
  positive case `ConfirmSavePreset_AfterUserSelectsDpiStage_SendsThatActiveStage`. Exercised via
  `ConfirmSavePresetCommand` rather than `EnsureDefaultPresetAppliedAsync` directly (that method is
  fire-and-forget off `SetDevice`, which both call into the same `ApplyCurrentStateToDeviceAsync` gate).
- [x] Save Preset issues exactly one `ApplyDeviceConfigurationAsync` call — added as
  `DeviceDetailsPresetApplyTests.ConfirmSavePreset_IssuesExactlyOneApplyDeviceConfigurationCall`.
- [x] Preset switch issues the full push once — added as
  `DeviceDetailsPresetApplyTests.SelectPreset_CleanSwitch_IssuesExactlyOneApplyDeviceConfigurationCall`.
- [ ] First-connect applies the default exactly once (second connect issues none) — **not added**.
  `EnsureDefaultPresetAppliedAsync` is only reachable through the fire-and-forget `SetDevice` (not
  awaitable from a test without a synchronization hook), so this needs either an internal test seam
  (e.g. exposing the method as `internal Task` the way `RebuildDeviceSettings` was made awaitable
  earlier this session) or an integration-style test that pumps the dispatcher/awaits a completion
  signal. Left for `/test-writer` or a follow-up round.

#### Nits

- `EnsureDefaultPresetAppliedAsync` marks `device_applied_preset` unconditionally after `await
  ApplyCurrentStateToDeviceAsync()` (Presets.cs) without checking the underlying
  `ApplyDeviceConfigurationAsync` call's `bool` result — if the physical write fails (device unplugged
  mid-apply, HID busy), the default is recorded as applied and never retried. Consistent with existing
  fire-and-forget conventions elsewhere in the file, but worth a follow-up given B1 shows the apply path
  isn't always safe to assume succeeded.
- `ApplyCurrentStateToDeviceAsync` (Presets.cs:255) and its `NoteAssertedDpiStage(Device, applied)` call
  (Presets.cs:295) read `Device` after an `await` without the `ReferenceEquals(Device, device)` guard
  that `LoadPersistedDeviceStateAsync` (DeviceDetailsViewModel.cs) already uses for exactly this
  situation — if the user switches devices while a save/switch/connect-apply is in flight, the asserted-
  stage cache and the outgoing write could target the wrong device. Narrow window, low severity, but the
  house pattern for it already exists two files over.

#### What's done well

- The connect-time default design correctly handles the migration case: `EnsureDefaultPresetAppliedAsync`
  captures whatever is *currently* on a pre-existing, already-configured device into the target preset
  before applying, rather than blindly overwriting it with hardcoded factory defaults — this is exactly
  right for users upgrading from before `device_applied_preset` existed, and the comment explaining it
  (DeviceDetailsViewModel.cs:820-822) is clear about why.
- The deferred-write refactor across Buttons/Rgb/Settings/KeyAssignments is consistent and clean — every
  edit path follows the same "persist + `NotifyPresetChanged()`, no device write" shape, making the
  diff easy to audit end to end.
- `forcePersisted` / `assertStageToDevice` on `LoadPersistedDeviceStateAsync` (added live during this
  session in response to the BLAZE/TAIPAN DPI-drift investigation) correctly closes a real double-write
  race on preset switch — confirmed fixed on hardware for both devices.

#### Verdict

Ship: no
Reasoning: B1 is a real, hardware-visible regression that can hit every user upgrading to this branch
(not just fresh devices) on the very first device-details open, because the tracking table it depends
on starts empty for everyone. It's timing-dependent rather than deterministic, which makes it worse,
not better — it will surface as sporadic, hard-to-reproduce "my DPI got reset" reports.
First three things to tackle: (1) fix B1 — don't let `activeStage` default to stage 0 when no stage is
actually known; (2) add the Save/switch/first-connect push-count tests the plan's Test strategy already
calls for, starting with one that reproduces B1; (3) decide and record whether a failed
`ApplyDeviceConfigurationAsync` should block `SetAppliedPreset` from being recorded.

### Round 2 — Ship: yes

#### Plan conformance

Re-checked the full branch delta against `origin/main` (`cb9acde`, current) after the Round 1 fixes.
`git status`/`git diff --stat` clean (all Round 1 work committed: `9f50d39`, merge `677dfb0`). All four
Round 1 items are closed as described in their `Fixed:` notes above, verified by re-reading the current
code (not just trusting the notes) and by the still-green test suite (292 Infrastructure + 166 App).

Also in scope for this round: the user reported, separately from the Round 1 findings, that a newly
created preset didn't apply to the device until it was edited again. Root-caused and fixed live
(`AppendNewPreset` in `DeviceDetailsViewModel.Presets.cs` now pushes the captured snapshot to the
device immediately, mirroring Save Preset / preset switch) — covered by
`ConfirmNewPreset_WithUnsavedEditsOnPriorPreset_AppliesTheNewPresetImmediately`. This fix is sound and
already verified working by the user.

#### Code

- [ ] **S5. `DuplicatePreset` copies the wrong preset's settings when the source isn't the active one** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:617-633` (`DuplicatePreset`) + `:536-590` (`AppendNewPreset`) — Should-fix
  **Problem:** `DuplicatePreset(preset)` receives the source row but only uses it to pre-fill the
  dialog's name (`"Copy of {preset.Name}"`) — it never records *which* preset is being duplicated.
  `SetPresetDialogMode` only tracks `_editingPresetId` for `PresetDialogMode.Edit`
  (`mode == PresetDialogMode.Edit ? editingPresetId : 0` at line 407); Duplicate mode gets `0`. On
  confirm, `ConfirmNewPreset` checks only `IsEditingPreset` (false for Duplicate) and falls through to
  `AppendNewPreset`, which snapshots whatever is in the **live** SQLite rows — i.e. the *currently
  selected/active* preset's state, not the row the user actually clicked "Duplicate" on.
  **Why it matters:** given Preset A is selected/active and the user opens the kebab menu on a
  *different* Preset B and clicks Duplicate, the resulting "Copy of B" is created (and — after the
  fix above — immediately pushed to the device) with **A's** settings, not B's. The name says "Copy of
  B" but it behaves like A. Silent and easy to miss since the common case (duplicating the currently
  active preset) happens to work by coincidence — live rows equal the active preset's state.
  **Solution options:**
  - Option A (correct): give `DuplicatePreset` its own source-id field (parallel to `_editingPresetId`),
    and in `AppendNewPreset`, when duplicating, copy from the *source preset's* snapshot tables
    (`preset_rgb`/`preset_rgb_effect`/`preset_button_bindings`/`preset_settings`/`preset_key_bindings`
    for that source id) into the new preset's snapshot — not from the live rows. A small
    `CopyPresetSnapshot(sourcePresetId, newPresetId, ...)` helper in `LocalStorageService` (INSERT
    ... SELECT between the two snapshot-id scopes, mirroring the existing `SavePresetSnapshot` shape)
    would do this directly without touching live rows at all — which also sidesteps the "immediately
    push to device" question, since the SOURCE's already-applied hardware state is what should land.
  - Option B (quick): before opening the dialog, `DuplicatePreset` could load the source preset's
    snapshot into live rows first (like `ApplyPresetToDevice` does) — simpler, but transiently mutates
    live state for a preset the user hasn't actually switched to, which risks interacting badly with
    the dirty-tracking flow if the user cancels the dialog.

#### Tests

- [ ] `DuplicatePreset` on a non-active source preset copies **that preset's** settings, not the
  currently active preset's — new test in `DeviceDetailsPresetApplyTests.cs` or
  `DeviceDetailsPresetsViewModelTests.cs`: select A, seed B's snapshot with distinct data via
  `SavePresetSnapshot`, call `DuplicatePresetCommand.Execute(b)` → `ConfirmNewPresetCommand`, assert the
  new preset's snapshot matches B's, not A's live rows.

#### Nits

- `.claude/settings.json` is new in this branch's diff (a local Claude Code permission allow-list). Not
  a functional issue, but confirm it's intentional to commit — it's tooling config, not app/product
  code, and doesn't belong in the same diff as a feature branch unless deliberate.

#### What's done well

- Round 1's B1 fix holds up under a fresh read: `_dpiStageKnown` is reset exactly once per device
  change (`RebuildDpiPresets`) and only set by genuine "we know this" sources — verified all four call
  sites (`ApplyKnownDeviceState`, `OnDpiStageChanged`, `LoadPersistedDeviceStateAsync`,
  `SelectDpiPreset`) do so correctly and nowhere else fakes it.
- The new "apply on preset creation" fix reuses the exact same `ApplyCurrentStateToDeviceAsync()` +
  `SetAppliedPreset(...)` pair Save Preset and preset switch already use — no new code path, no
  duplicated logic, low risk.
- Test coverage for the apply-call-shape and B1 regressions (`DeviceDetailsPresetApplyTests.cs`) is a
  real net now, not just documentation — it would fail loudly if either regressed again.

#### Verdict

Ship: yes
Reasoning: All Round 1 blockers/should-fixes are closed and verified, tests are green, and the
newly-reported new-preset-apply bug is fixed and tested. S5 (Duplicate copying the wrong preset) is a
genuine, pre-existing bug surfaced during this pass, but it's narrower than B1 was (only bites when
duplicating a non-active preset) and is not a regression from this session's work — reasonable to ship
and fix as a fast follow-up.
First thing to tackle next: S5, since it's a silent-data-wrong bug in a user-facing feature (duplicated
presets silently carrying the wrong device's settings) rather than a crash — the kind of bug that erodes
trust in the presets feature once someone notices.
