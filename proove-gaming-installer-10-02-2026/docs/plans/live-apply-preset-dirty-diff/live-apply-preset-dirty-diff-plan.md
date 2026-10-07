# Plan: Apply Device Details edits live, make Save Preset reflect an actual value diff

> **Amended (2026-08-22, `ui-thread-storage-async`):** staging writes now go through the storage executor, so `StagePresetDirtyOnly` settles the flag in two steps: the synchronous `PresetMatchesLiveState` compare still gives the instant answer for synchronously-staged edits, and a generation-guarded recompute is additionally enqueued behind any pending queued writes (FIFO) to correct the flag once they land; when the queue is non-idle and the sync compare reads "clean", the flag is set optimistically dirty until that recompute settles.

## Goal

Every Device Details setting (RGB, DPI stage/table, report rate, mouse settings, button
bindings, keyboard key remaps) currently only *stages* an edit — it writes the change to SQLite
and flips `HasUnsavedPresetChanges` to `true`, but the physical device is only ever updated when
the user clicks **Save Preset** or switches presets (`ApplyCurrentStateToDeviceAsync`, called
from exactly three places). This means the device visibly lags every edit until an explicit save,
and the Save Preset button is a one-way latch: once dirty, it stays dirty even if the user
undoes their own edit by hand.

This plan makes edits apply to the physical device shortly after the user makes them (debounced,
not on every keystroke/drag tick), for every settings section and every device type — mouse and
keyboard alike — and makes **Save Preset**'s enabled state track a real comparison between the
live device state and the *currently selected preset's saved snapshot*, so reverting a value back
to what the preset already has correctly re-disables the button instead of leaving it stuck on.

## Scope

DPI was the example used to describe the desired behavior when this plan was requested, but the
mechanism below is **not DPI-specific** — it replaces the single shared dirty-notification choke
point (`NotifyPresetChanged()`) that every staged setting already funnels through, so fixing that
one method fixes all of them at once. The list below is exhaustive, not illustrative.

**In — every one of these currently stages-only and will apply live + drive the new dirty diff:**
- **DPI** — active stage selection (`SelectDpiPreset`) and the DPI table / per-stage colours
  (`ApplyDpiRecommendations`).
- **Report / polling rate** — `SelectPollingRate`.
- **RGB** — effect selection, colour, brightness, speed, on/off toggle
  (`SaveRgbStateForCurrentDevice` / `StageRgbSettings`).
- **Mouse settings** — button debounce, lift-off distance (`ApplyMouseSettingsAsync`).
- **Button bindings** — which action (including which macro) a remappable button points at, for
  every call site in `DeviceDetailsViewModel.Buttons.cs` (`ResetButtonBindingsToDefault`, the
  hotspot-assignment handler, the macro-bind/unbind handlers).
- **Macro content, when the macro is already bound to a mouse button** —
  `ReplaceEditedMacro`/the macro-delete path already call the shared dirty notifier today
  (`if (rebound.Count > 0) NotifyPresetChanged();` / the equivalent on delete) precisely because
  editing or deleting a macro that's currently on a button changes what that button will send —
  this is a button-binding value, not a macro-library concept, and is in scope for the exact same
  reason button bindings are. Renaming every call site in `Buttons.cs` (see File-level change
  list) covers this automatically; no separate code path needed.
- **Keyboard key remaps on the staged path** (Raster/T-Racer plain remaps via
  `SelectAssignmentTarget`).
- This applies uniformly to mice and keyboards — `ApplyCurrentStateToDeviceAsync`'s existing
  per-device-type branching (`IsKeyboardDevice` → RGB + key overrides; mouse → the unified
  `ApplyDeviceConfigurationAsync` coordinated write) is reused unchanged, not duplicated.
- `HasUnsavedPresetChanges` becomes a computed value (live state vs. the selected preset's saved
  snapshot), recomputed after every debounced apply, instead of a one-way latch — for all of the
  above uniformly, via the single shared `PresetMatchesLiveState` comparison (five table pairs,
  covering RGB, RGB effects, button bindings, mouse/DPI/rate settings, and key bindings).

**Out (explicitly not touched by this plan — each already an immediate-write path today, or not
a preset value at all):**
- The generic "app preferences" block (`ScrollOneScreenAtATime`, `ScrollLines`,
  `DoubleClickSpeed`, `SleepTimer`, the generic `LiftOffDistance` property in
  `DeviceDetailsViewModel.Settings.cs`) — OS/software-level behavior with no device write path
  and no preset scoping (`app_preferences` table, owner-only). Not a preset value; unaffected.
- Cyrex/Aress keyboard key remaps. Per the code's own comment (`DeviceDetailsViewModel.
  KeyAssignments.cs:853-856`): these "write straight to the device... predate the preset system
  and stay on their own unstaged, immediate-write path" — already immediate today, but not part
  of the preset snapshot system at all (no `key_bindings`/`preset_key_bindings` row involved).
- **Keyboard macro-library resync** (`ResyncKeyMacroBindingsAsync` in
  `DeviceDetailsViewModel.KeyAssignments.cs:629`) — for keyboards, the whole macro library is
  already uploaded to the device's macro slots immediately, unconditionally, on every macro
  create/edit/delete and on device connect (`SetMacrosAsync`, fire-and-forget). This already does
  what the user wants for keyboards; it predates the preset system, is not part of any
  `preset_*` snapshot table, and is unaffected either way by this plan.
- Both "Out" items above are the same shape as each other (already immediate, already outside
  the snapshot system) — folding either into preset-based dirty tracking would mean inventing a
  new snapshot table where none exists today, which is materially bigger than what was asked.
  Flagged again under Open Questions in case the user wants that as an explicit follow-up.
- Any changes to device write protocols (`TelinkMouseService`, `EasenMouseService`,
  `SmartInfiniteKeyMappingService`, DeviceHost, gRPC contract). This plan is entirely
  App-side/ViewModel orchestration: *when* the existing `ApplyCurrentStateToDeviceAsync()` gets
  called, and how `HasUnsavedPresetChanges` gets computed. No new device-write code paths.

## Current behavior (confirmed by reading the code)

Every settings section follows the identical pattern, each with an explicit comment saying so:

- `DeviceDetailsViewModel.Rgb.cs`: `StageRgbSettings()` → `SaveRgbStateForCurrentDevice()`
  persists to `device_rgb`/`device_rgb_effect` and calls `NotifyPresetChanged()`. Slider drags
  (brightness/speed) go through a 300ms debounce (`DebounceApplyRgbSettingsAsync`) before staging.
  Comment at `:334`: *"RGB edits stage into the preset... The LED is written on Save Preset /
  preset switch."*
- `DeviceDetailsViewModel.Settings.cs`: `ApplyMouseSettingsAsync` (debounce/lift-off, 300ms
  debounced), `SelectDpiPreset`, `ApplyDpiRecommendations`, `SelectPollingRate` — same "stage via
  `_settings.SaveXAsync(...)`, then `NotifyPresetChanged()`" pattern, same comment wording.
- `DeviceDetailsViewModel.Buttons.cs`: every button-binding assignment path calls
  `_localStorage.SetButtonBinding(...)` then `NotifyPresetChanged()`. Comment at `:626`: *"Stage
  into the App store... The device is written on Save Preset / preset switch, not per edit."*
- `DeviceDetailsViewModel.KeyAssignments.cs`: the Raster/T-Racer staged remap path
  (`SelectAssignmentTarget` → stages into `key_bindings` + `_stagedKeyOverrides`) calls
  `NotifyPresetChanged()` the same way. Cyrex/Aress and macro-bound keys stay on their own
  pre-existing immediate-write path (out of scope, see above).

`NotifyPresetChanged()` (`DeviceDetailsViewModel.Presets.cs:64-70`) is the current dirty setter:

```csharp
internal void NotifyPresetChanged()
{
    if (_suppressAutoApply) return;
    if (_suppressPresetDirty) return;
    if (SelectedPreset is null) return;
    HasUnsavedPresetChanges = true;
}
```

`_suppressAutoApply` stops mid-bulk-reload property setters (e.g. `LoadRgbStateForCurrentDevice`
setting every effect's colour/brightness/speed one field at a time) from cascading into this at
all. `_suppressPresetDirty` wraps `ApplyPresetToDeviceAsync` (preset switch / new-preset capture)
for the same reason at a coarser grain. Both guards are load-bearing and must keep working
unchanged — they are the only thing standing between a bulk section reload and a device write
mid-load with half-updated state.

`ApplyCurrentStateToDeviceAsync()` (`DeviceDetailsViewModel.Presets.cs:255-309`) is the **only**
place that ever writes real hardware today, called from `ConfirmSavePreset` (Save Preset),
`ApplyPresetToDeviceAsync` (preset switch / Discard), and `AppendNewPresetAsync`. It already
branches by device type and already carries every existing guard rail (`_dpiStageKnown`/
`_reportRateKnown` gating so an unknown stage/rate is never fabricated and pushed,
`NoteAssertedDpiStage` cache bookkeeping). Nothing in this method needs to change — this plan
only changes *how often* it gets called.

## Critical hardware constraint

BLAZE's device write **must be one single coordinated write** covering LED + buttons + DPI +
settings together — separate single-aspect writes fired in a burst have previously bricked the
buttons on real hardware (see `docs/mouse/blaze/`). This rules out inventing narrow per-aspect
"just push RGB" / "just push this one button" immediate-write paths for the live-apply feature.
The only hardware-safe way to apply immediately is to keep reusing the existing, already-safe
`ApplyCurrentStateToDeviceAsync()` — i.e. this plan is ViewModel-level orchestration (when to call
an unchanged method), not new protocol code.

TAIPAN has separate per-table writes (DPI table / button map / MsConfig) with documented
write-lock contention and cold-start timing history (`docs/mouse/taipan/protocol.md`). Firing a
full coordinated apply on every single field edit — even debounced — is more write traffic than
today. Debouncing/coalescing (below) is the mitigation; this needs hardware verification per the
QA section, not a code-level fix beyond the debounce itself.

## Options considered

**Dirty-comparison mechanism:**

- **A. DB-level diff (chosen).** Add `LocalStorageService.PresetMatchesLiveState(...)`: a single
  query comparing the live `device_rgb`/`device_rgb_effect`/`button_bindings`/`device_settings`/
  `key_bindings` rows against the selected preset's `preset_rgb`/`preset_rgb_effect`/
  `preset_button_bindings`/`preset_settings`/`preset_key_bindings` rows, using a symmetric
  `NOT EXISTS (... EXCEPT ...)` pair per table (standard SQL set-equality idiom) ANDed together
  into one scalar boolean. Both sides are already persisted tables — no new cache to keep in sync,
  and it is exactly as correct as the tables themselves are, which this codebase already trusts
  for `SavePresetSnapshot`/`LoadPresetSnapshotToDevice`.
- **B. In-memory baseline diff.** Capture live values into an in-memory snapshot when a preset is
  selected/saved, diff current in-memory VM state against that baseline on every edit. Avoids a
  DB round-trip per debounce window, but needs careful invalidation on every `SelectPreset`,
  `ConfirmSavePreset`, and `DiscardUnsavedChanges`, and duplicates state that already exists in
  SQLite in a second form that can drift. Rejected: given edits are already debounced ~250-300ms
  and every edit already does a DB write in that same window today, a handful of extra cheap
  `SELECT`s in the same window is not the wrong trade for removing a whole class of cache-sync
  bugs.

**Apply cadence / coalescing:**

- **1. Single shared debounce for the apply+recompute step only (chosen).** Each section's
  existing *staging* behavior (when it writes to SQLite — synchronous for discrete picks, its own
  300ms debounce for slider drags) is untouched. `NotifyPresetChanged()` is replaced by
  `StageChangeAndApply()`, called at the exact same call sites, which keeps the exact same
  `_suppressAutoApply`/`_suppressPresetDirty` guards, optimistically sets
  `HasUnsavedPresetChanges = true` for instant UI feedback, then (re)schedules ONE shared ~250ms
  debounce. When that debounce settles, it calls the unchanged `ApplyCurrentStateToDeviceAsync()`
  once and then recomputes `HasUnsavedPresetChanges` for real via `PresetMatchesLiveState`. Two
  edits in different sections within the same 250ms window (e.g. nudge DPI, then tweak an RGB
  colour) collapse into one device write, not two.
- **2. Per-section immediate apply, no shared debounce.** Simpler to write, but a user rapidly
  clicking through several DPI stages, or the RGB slider debounce settling right as a button
  assignment lands, can fire multiple overlapping full-device writes back to back. Not unsafe
  (each write is still a complete, correct coordinated write) but wasteful and, for TAIPAN
  specifically, a real latency risk given its write-lock contention history. Rejected.
- **3. Narrow per-aspect immediate writes** (e.g. a dedicated "just write RGB now" call separate
  from "just write buttons now"). Rejected outright — this is exactly the pattern that bricked
  BLAZE's buttons in the past (see Critical hardware constraint above).

## Recommended approach

Option A (DB diff) + Option 1 (single shared apply debounce), concretely:

1. **`LocalStorageService.PresetMatchesLiveState`** — new method:
   ```csharp
   public bool PresetMatchesLiveState(int presetId, string ownerKind, string ownerId,
       int vendorId, int productId, string deviceKey)
   ```
   One `SELECT` combining five symmetric-`EXCEPT` boolean terms (device_rgb↔preset_rgb,
   device_rgb_effect↔preset_rgb_effect, button_bindings↔preset_button_bindings,
   device_settings↔preset_settings, key_bindings↔preset_key_bindings), ANDed together, returning
   a single scalar bool. Mirrors the existing symmetric scoping already used by
   `SavePresetSnapshot`/`LoadPresetSnapshotToDevice` for the same five table pairs — no new
   scoping concept, just a read instead of a copy.

2. **`DeviceDetailsViewModel.Presets.cs`** — replace `NotifyPresetChanged()`'s body with
   `StageChangeAndApply()` (keep an internal alias or just rename every call site — see file
   list). New shared state: `CancellationTokenSource? _deviceApplyDebounceCts` and a
   `private const int DeviceApplyDebounceMs = 250;`.
   ```csharp
   internal void StageChangeAndApply()
   {
       if (_suppressAutoApply) return;
       if (_suppressPresetDirty) return;
       if (SelectedPreset is null) return;

       HasUnsavedPresetChanges = true; // optimistic — corrected below once the apply settles

       _deviceApplyDebounceCts?.Cancel();
       var cts = new CancellationTokenSource();
       _deviceApplyDebounceCts = cts;
       _ = DebounceApplyAndRecomputeDirtyAsync(cts.Token);
   }

   private async Task DebounceApplyAndRecomputeDirtyAsync(CancellationToken token)
   {
       try { await Task.Delay(DeviceApplyDebounceMs, token); }
       catch (TaskCanceledException) { return; }

       await ApplyCurrentStateToDeviceAsync();
       RecomputeHasUnsavedPresetChanges();
   }

   private void RecomputeHasUnsavedPresetChanges()
   {
       if (SelectedPreset is null || Device is null || _localStorage is null) return;
       var owner = _ownerProvider?.GetCurrent();
       if (owner is null) return;

       HasUnsavedPresetChanges = !_localStorage.PresetMatchesLiveState(
           SelectedPreset.Id, owner.Value.Kind, owner.Value.Id,
           Device.VendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
   }
   ```
   `ConfirmSavePreset` and `AppendNewPresetAsync` keep their existing explicit
   `ApplyCurrentStateToDeviceAsync()` calls as a safety-net full resync (see Open Questions —
   confirmed as "keep" below, not left ambiguous). `DiscardUnsavedChanges`'s existing
   `ApplyPresetToDeviceAsync(SelectedPreset)` call is unchanged and now does real, meaningful
   work (reverting real live edits, not just a staged flag).

3. **Every section file** — rename every `NotifyPresetChanged()` call to `StageChangeAndApply()`.
   No other change to these files: staging logic, existing per-field debounces (RGB slider, mouse
   settings) and the `_suppressAutoApply` checks around them are untouched. Concretely, every call
   site (so none get missed):
   - `Rgb.cs`: `SaveRgbStateForCurrentDevice`.
   - `Settings.cs`: `ApplyMouseSettingsAsync` (debounce/lift-off), `SelectDpiPreset`,
     `ApplyDpiRecommendations`, `SelectPollingRate`.
   - `Buttons.cs`: `ResetButtonBindingsToDefault`, the hotspot-assignment handler
     (`SelectButtonAction`/`ApplyAssignmentTargetAsync`-equivalent), the macro-bind handler, the
     macro-unbind/delete path (`~:504`), **and `ReplaceEditedMacro`'s `if (rebound.Count > 0)
     NotifyPresetChanged();` (`~:569`)** — this last one is what makes a macro-content edit apply
     live to a mouse whenever that macro is currently on a button; do not skip it.
   - `KeyAssignments.cs`: the staged (Raster/T-Racer) remap call site only
     (`SelectAssignmentTarget`, `~:935`). Leave `ResyncKeyMacroBindingsAsync` and the Cyrex/Aress
     immediate-write path untouched — both already apply live today (see Scope).

4. **Audit `_suppressAutoApply`/`_suppressPresetDirty` coverage** across all four section files
   as part of this change (not a new mechanism, just confirming the existing guard already covers
   every call site that will now trigger a real device write instead of a flag set) — this is the
   single highest-risk item in this plan (see Risks) and must be checked file-by-file, not
   assumed.

## File-level change list

- `src/ProoveGaming.App/Services/LocalStorageService.cs` — add `PresetMatchesLiveState(...)`
  (read-only, no schema change).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — replace
  `NotifyPresetChanged()` with `StageChangeAndApply()` + `DebounceApplyAndRecomputeDirtyAsync` +
  `RecomputeHasUnsavedPresetChanges`, add the shared debounce CTS field. Update the doc comment
  that currently describes `NotifyPresetChanged()`'s two guards to describe the new method
  instead (the guards themselves are unchanged).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — rename the
  `NotifyPresetChanged()` call (in `SaveRgbStateForCurrentDevice`) to `StageChangeAndApply()`.
  Update the `:334` comment ("The LED is written on Save Preset / preset switch") — it is no
  longer accurate once this ships.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs` — same
  rename at the four call sites (`ApplyMouseSettingsAsync`, `SelectDpiPreset`,
  `ApplyDpiRecommendations`, `SelectPollingRate`); update their matching stale comments.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` — same
  rename at every `NotifyPresetChanged()` call site; update the `:626`-area comment.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` — same
  rename at the staged (Raster/T-Racer) call site only; leave the Cyrex/Aress immediate-write path
  untouched (out of scope).
- `tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs` — new tests for
  `PresetMatchesLiveState` (see Test strategy).
- `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs` — extend the existing
  `ApplyConfigCallCount`-based fake-service tests to cover live-apply debouncing and the
  revert-to-original dirty recompute (see Test strategy).

## Acceptance criteria

The DPI case below is the concrete worked example; the same statement holds — and must be
verified — for **each** of report rate, RGB, mouse settings (debounce/lift-off), button bindings,
a bound macro's content, and staged keyboard key remaps. `/reviewer` should check all of them, not
just DPI.

- Given a device with a selected preset → the user changes a setting (DPI stage, report rate, an
  RGB colour/effect/brightness/speed/on-off, debounce, lift-off, a button's assigned action, the
  content of a macro currently bound to a button, or — on a Raster/T-Racer keyboard — a staged key
  remap) away from what the selected preset has saved → within ~250ms the physical device (fake
  service in tests / real hardware in QA) receives the new value, and Save Preset becomes enabled.
- Given the same scenario, for each setting above → the user then changes that same setting back
  to the value the selected preset has saved → within ~250ms the device receives the original
  value again, and Save Preset becomes **disabled** (the live state matches the selected preset's
  saved snapshot again, verified via `PresetMatchesLiveState`, not a manual flag).
- Given rapid sequential edits across *different* settings within one debounce window (e.g. an RGB
  colour change followed 50ms later by a DPI stage change, or a report-rate change followed by a
  button reassignment) → exactly one device apply happens for the pair, not one per field.
- Given a keyboard device (not a mouse) → the same live-apply and revert-to-not-dirty behavior
  holds for RGB and staged key remaps (Raster/T-Racer), using the existing
  `ApplyCurrentStateToDeviceAsync` keyboard branch.
- Given a bulk reload sequence (preset switch, device connect, Discard) → no intermediate,
  partially-updated state is ever pushed to the device mid-reload — only the final, fully-loaded
  state is applied, exactly once, exactly as today.
- Given Cyrex/Aress key remaps, or a keyboard's macro library (`ResyncKeyMacroBindingsAsync`) →
  behavior is unchanged: both remain immediate-write exactly as they are today, and neither is
  reflected in Save Preset's dirty state (unaffected by this plan, per Scope).

## Test strategy

This is a behavior change to an already-tested feature
(`DeviceDetailsPresetApplyTests.cs`, `DeviceDetailsPresetsViewModelTests.cs`,
`LocalStorageServicePresetsTests.cs` all exist and pass today). Confirm existing coverage stays
green, extend for the new behavior — do not leave assertions in place that encode the old
"dirty is a one-way latch" or "device is only written on Save/Switch" behavior as correct.

- **`tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs`**:
  - `PresetMatchesLiveState_ReturnsTrue_WhenLiveStateEqualsSnapshot` — save a snapshot, assert
    match.
  - `PresetMatchesLiveState_ReturnsFalse_WhenAnyOneTablePairDiffers` — parameterized/separate
    cases for each of the five table pairs (RGB head, RGB effects, button bindings, settings, key
    bindings) changed independently, confirming each alone is detected.
  - `PresetMatchesLiveState_ReturnsTrue_AfterChangingThenRevertingAValue` — the exact
    800→1200→800 scenario from the acceptance criteria, at the storage layer.
- **`tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`** (uses the existing fake
  `IUsbHidDeviceService` with `ApplyConfigCallCount`):
  - Extend or add a test asserting a single edit results in exactly one
    `ApplyConfigCallCount` increment after the debounce window elapses (use a short/injectable
    debounce or `Task.Delay` + margin in the test, matching however the existing RGB-slider-debounce
    tests, if any, already handle timing — if none exist yet, this is the first and should set the
    pattern).
  - New: two edits within the debounce window → still exactly one `ApplyConfigCallCount`
    increment (coalescing).
  - New: revert-to-original scenario, repeated per setting family (not just DPI) — DPI stage,
    report rate (`LastReportRateHz`), an RGB field, a button reassignment, and a bound macro's
    content edit — each ending with `HasUnsavedPresetChanges == false` and the final
    `ApplyConfigCallCount` call's captured value (`LastActiveStage`/`LastReportRateHz`/
    `LastBindings`, as applicable) reflecting the reverted (original) value, not the intermediate
    one.
  - New: keyboard device variant (`IsKeyboardDevice = true`) exercising the same revert scenario
    through the keyboard branch of `ApplyCurrentStateToDeviceAsync`.
- **`tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs`**: confirm a bulk reload
  path (`LoadPresetsForDevice` → `ApplyPresetToDeviceAsync`) still results in exactly the expected
  single apply call, not one per section reloaded (guards against the highest-risk regression in
  this plan — see Risks).
- **Not covered by automated tests**: real device latency/perceptibility of the ~250ms debounce,
  and TAIPAN write-lock contention under rapid real edits — both covered by the QA section
  instead, since they need real hardware timing, not a fake service.

## QA (manual test steps)

1. Connect a BLAZE (or any mouse). Open Device Details.
2. Change the DPI stage from one value to another using the on-screen picker. Expected: the
   physical DPI indicator / cursor speed changes within about a quarter second, without clicking
   Save. Save Preset becomes enabled.
3. Change the DPI stage back to its original value. Expected: the device reflects the original
   value again within about a quarter second, and Save Preset becomes disabled again.
4. Drag the RGB brightness or speed slider around, then release. Expected: the LED updates
   shortly after you stop dragging (not on every pixel of drag), and Save Preset reflects whether
   the final value differs from the active preset.
5. Change the polling/report rate to a different value, then change it back to its original
   value. Expected: same live-apply-then-revert-disables-Save behavior as DPI in steps 2-3.
6. Reassign a button to a different action, then reassign it back to its original action.
   Expected: the physical button behaves accordingly at each step, and Save Preset ends up
   disabled once you're back to the original assignment.
7. Bind a macro to a button, edit that macro's recorded steps, and try it. Expected: the button
   plays the updated macro shortly after editing it, without needing to click Save Preset.
8. Adjust the mouse's debounce or lift-off distance slider, then set it back to its original
   value. Expected: same live-apply-then-revert-disables-Save behavior.
9. Repeat steps 2-3 on a keyboard device (RGB colour and, if the model supports staged remaps,
   a key reassignment). Expected: same live-apply and revert-to-not-dirty behavior.
10. Switch presets, or leave and reopen Device Details, while mid-edit. Expected: no stutter, no
    crash, no device receiving a half-updated state (e.g. new DPI table but old button map).
11. On a TAIPAN specifically, make several rapid edits in a row (DPI, then RGB, then a button)
    within a second or two. Expected: the app stays responsive (no freeze), and the device
    eventually reflects the final state — note and report any perceptible lag for follow-up.

## Review focus

- `thread-safety` / `async/await` — the shared debounce CTS (`_deviceApplyDebounceCts`) must be
  cancelled/replaced correctly with no race between a just-cancelled task and a newly-scheduled
  one; no `.Result`/`.Wait()` anywhere in the new code.
- `UI-thread affinity` — `ApplyCurrentStateToDeviceAsync()` and the new recompute must not block
  the UI thread; confirm the debounce `Task.Delay` and the gRPC calls it wraps stay fully async.
- `null-safety` — `RecomputeHasUnsavedPresetChanges`'s early-outs (`SelectedPreset`, `Device`,
  `_localStorage`, owner) mirror the existing `NotifyPresetChanged` null-guards; confirm parity.
- `performance hot-path` — this is not a per-frame path, but it is now a per-edit path across
  every settings section; confirm the debounce genuinely coalesces (see Test strategy) rather
  than firing once per field.

## Performance & quality

Not a per-frame hot path, but this plan meaningfully increases how often
`ApplyCurrentStateToDeviceAsync()` fires (from "twice ever, on Save/Switch" to "roughly once per
~250ms burst of edits"). The single shared debounce is the mitigation for both device-write
pressure (TAIPAN's write-lock contention) and UI-thread cost (each apply is already async/
`await`ed, not synchronous). No new allocations of note — `PresetMatchesLiveState` is a single
SQL round trip per settle, comparable in cost to the `SaveXAsync` writes that already happen on
every edit today.

## Risks

- **Bulk-reload cascade (highest risk).** If any section's bulk-reload path (device connect,
  preset switch, `LoadRgbStateForCurrentDevice`, `RebuildDeviceSettings`, `LoadButtonBindings`,
  the key-assignments equivalent) sets a property without going through the existing
  `_suppressAutoApply`/`_suppressPresetDirty` guards, `StageChangeAndApply()` will now trigger a
  **real device write** with partially-loaded state, instead of just an incorrect flag as today.
  This is strictly worse than the current failure mode (a stray dirty flag vs. a bad device
  write), so the file-by-file guard audit in Recommended-approach step 4 is not optional — treat
  it as the first thing to verify, and the first thing `/reviewer` should check.
- **TAIPAN write frequency.** Even debounced, this is more hardware writes than today. If QA step
  11 shows real lag, the mitigation (increasing `DeviceApplyDebounceMs`, or a longer
  TAIPAN-specific value) is a follow-up, not a blocker for this plan — flag it, don't silently
  work around it here.
- **Optimistic dirty flag briefly wrong.** `HasUnsavedPresetChanges = true` is set optimistically
  before the debounce settles; for ~250ms after an edit that nets out to "no real change" (the
  revert scenario), Save Preset is briefly enabled when clicking it would be a no-op save. Accepted
  — clicking Save during that window still produces a correct (if redundant) result, and 250ms of
  a button being enabled one edit too long is a minor cosmetic gap, not a correctness bug.

## Open questions

- **`ConfirmSavePreset` / `AppendNewPresetAsync`'s existing explicit
  `ApplyCurrentStateToDeviceAsync()` calls** — this plan keeps both as safety-net full resyncs
  (cheap, already existing, guards against any edge case where a debounced apply hasn't
  landed yet when the user clicks Save). Confirm this is the right call, or say to remove them as
  dead weight once live-apply is trusted.
- **Cyrex/Aress key remaps** — confirmed out of scope for this plan (see Scope). Flag if you want
  a follow-up plan to bring them into preset-based dirty tracking too.
- **`DeviceApplyDebounceMs = 250`** — chosen to sit close to the existing 300ms slider-stage
  debounce without adding a second full window on top. Confirm 250ms "feels" immediate enough, or
  give a different number.

## Implementation outcome

Implemented and hardware-confirmed by the user ("working now as expected"). The plan's core
design held — one shared debounce reusing the unchanged `ApplyCurrentStateToDeviceAsync`, plus a
DB-level `PresetMatchesLiveState` diff (Option A + Option 1). Everything below is what the plan
did **not** anticipate, recorded because most of it came out of real hardware testing, not review.

### Built as planned

- `LocalStorageService.PresetMatchesLiveState(...)` — one SQL statement, symmetric
  `NOT EXISTS (… EXCEPT …)` per table pair across all five live↔preset pairs.
- `StageChangeAndApply()` in `DeviceDetailsViewModel.Presets.cs`, with the shared
  `DeviceApplyDebounceMs = 250` / `_deviceApplyDebounceCts` debounce.
- Every production `NotifyPresetChanged()` call site across `Rgb.cs` / `Settings.cs` /
  `Buttons.cs` / `KeyAssignments.cs` switched to it, including `ReplaceEditedMacro`'s
  bound-macro-content path. Stale "written on Save Preset / preset switch" comments corrected.
- `ConfirmSavePreset` / `AppendNewPresetAsync` kept their explicit applies (Open question 1,
  resolved as "keep").

### Deviations and additions

1. **`NotifyPresetChanged()` kept as its own method** rather than replaced. ~24 existing tests
   call it directly to simulate a dirty preset with no `Device` set; folding it into
   `StageChangeAndApply` would have had them scheduling real device writes against disposed
   fixtures. `StageChangeAndApply` wraps it conceptually; production calls only the latter.
2. **Dirty recompute is synchronous, not post-apply.** The plan put
   `RecomputeHasUnsavedPresetChanges()` after the awaited apply. In use that made the SAVE PRESET
   button visibly lag the user's own click by the debounce plus real hardware write latency. It
   now runs inline in `StageChangeAndApply` (`TryRecomputeHasUnsavedPresetChanges`) — safe because
   each section's staging write to the live tables is already synchronous by the time it is
   called. The post-apply recompute was removed as redundant: an apply never mutates the tables
   the comparison reads. The plan's "optimistic flag briefly wrong" risk is therefore gone.
3. **`PersistDisplayedDpiAndRate()` — the actual root cause of the revert bug.** A preset only got
   a DPI-stage / report-rate row in its snapshot if the user had *explicitly clicked* one before
   saving. A preset saved while the UI merely displayed its defaults stored neither, so a Discard
   had nothing to restore, silently left the abandoned edit on the hardware, and the re-opened
   page then read that live value back off the device and showed it as if the preset owned it.
   Every snapshot capture now persists the displayed stage/rate first, making a preset a genuinely
   complete unit of settings.
4. **`restoreUnsetToDefaults` on `ApplyPresetToDeviceAsync`** — presets saved before (3) still
   hold no stage/rate. User-driven reverts (Discard / preset switch / delete) fall back to the
   defaults the preset visually represents; `EnsureDefaultPresetAppliedAsync`'s first-connect push
   passes `false` so an unset stage stays unsent, preserving the B1 guard.
5. **`_applyGate` (`SemaphoreSlim`) serializing every apply.** Cancelling `_deviceApplyDebounceCts`
   only pre-empts a call that has not started. An abandoned edit's write already in flight raced
   Discard's revert with no ordering guarantee, and whichever finished last won.
6. **`_deviceApplyDebounceCts.Cancel()`** at the head of `ApplyPresetToDeviceAsync` (plus
   `ConfirmSavePreset` / `AppendNewPresetAsync`), so a pending abandoned apply cannot fire after
   an authoritative reload.
7. **`LoadPersistedDeviceStateAsync`'s `forcePersisted` else-branch** — when a preset stores no
   stage/rate, the reload no longer leaves the abandoned edit's selection in place.
8. **Regression fixed in `SetDevice`** — its opening `SaveRgbStateForCurrentDevice()` is
   housekeeping for the *outgoing* device, but after the rename it flowed into
   `StageChangeAndApply`, marking the preset dirty and scheduling a device write on *every* page
   open. Now wrapped in `_suppressPresetDirty`. Introduced by this work; caught by the
   end-to-end test.

### Tests

`tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs` — 7 for
`PresetMatchesLiveState` (equal, each of the five pairs differing independently, and the
change-then-revert case). `DeviceDetailsPresetApplyTests.cs` — debounce timing, cross-section
coalescing, revert-clears-dirty (mouse + keyboard), the in-flight-write race, the full
save→edit→discard→reopen flow, and immediate button-state update proven to land before any
device write. Each bug-fix test was verified to **fail** against the pre-fix code before being
kept. Suite: 363 passing, stable across repeated runs.

Three pre-existing tests were switched from `settings: null` to a real `SettingsRepository`
(`NewVmWithRealSettings`): the dirty check compares *persisted* live state against the snapshot,
so a DPI/rate change only registers when it actually persists. `settings: null` cannot occur in
production DI. This is a genuine behavioural property worth knowing: **a change counts as dirty
only if it persists somewhere comparable.**

### Still open

- QA step 11 (TAIPAN write-frequency / perceptible lag under rapid edits) is unverified —
  the user's confirmation covered BLAZE-style flows, not TAIPAN latency.
- Keyboard live-apply is covered by tests but was not confirmed on real keyboard hardware.

## Review findings

### Round 1 — Ship: yes

Surface reviewed: commit `aff0465` plus the staged and unstaged working-tree delta (16 files,
+1425/−157). No untracked files. Both governing plans read in full including their
`Implementation outcome` sections. Build clean (0 errors); 363/363 tests pass, stable across runs.

#### Plan conformance

Plans: `docs/plans/live-apply-preset-dirty-diff/live-apply-preset-dirty-diff-plan.md` and
`docs/plans/share-presets-by-device-type/share-presets-by-device-type-plan.md`.

- **Implemented as planned:** `PresetMatchesLiveState` (symmetric `NOT EXISTS … EXCEPT` over all
  five live↔preset table pairs, scoped identically to `SavePresetSnapshot` — verified); the shared
  250 ms `StageChangeAndApply` debounce reusing the unchanged coordinated apply; every production
  `NotifyPresetChanged()` call site migrated across `Rgb.cs` / `Settings.cs` / `Buttons.cs` /
  `KeyAssignments.cs` including `ReplaceEditedMacro`; stale "written on Save Preset" comments
  corrected; preset rescope to `(owner, vendor, product)` with `device_key` left vestigial.
- **Deviations (all disclosed in the outcome sections, all justified):** `NotifyPresetChanged`
  retained as its own method; dirty recompute moved from post-apply to synchronous
  (`DeviceDetailsViewModel.Presets.cs:92`); `PersistDisplayedDpiAndRate`, `restoreUnsetToDefaults`,
  `_applyGate`, the debounce-cancels and the `forcePersisted` else-branch all added beyond the
  plan; the share-presets migration removed at the user's direction.
- **Accepted limitation (per plan):** legacy presets holding no stage/rate now get the *default*
  forced on a switch/discard (`Presets.cs:358-362`). Residual risk: switching from a preset with
  rate 500 to a legacy preset silently moves the device to 1000 Hz. Self-heals on next save.
- **Acceptance criteria:** met for DPI, report rate, button bindings and keyboard key remaps.
  RGB and bound-macro-content reverts are implemented but not covered by a test — see Tests.

#### Code

- [ ] **S1. `_suppressPresetDirty` is a non-reentrant bool spanning `await`s, so overlapping
  reverts clear it early** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:312-389` — Should-fix (top priority)
  **Problem / why it matters:** `ApplyPresetToDeviceAsync` sets `_suppressPresetDirty = true`, then
  `await`s a long sequence (`RebuildDeviceSettings`, `LoadPersistedDeviceStateAsync`, `_applyGate`,
  the gRPC write), and resets the flag to `false` in its `finally`. The flag is a plain bool with
  no nesting count, so if two of these run concurrently the **first to finish clears suppression
  while the second is still reloading**. Concurrency is reachable: `DiscardUnsavedChanges`
  (`:1078`) closes its dialog *before* awaiting the revert, leaving the page interactive — the user
  can click another preset and enter `SelectPreset` → `ApplyPresetToDeviceAsync` while Discard's
  revert is still in flight (seconds, on a TAIPAN). `EnsureDefaultPresetAppliedAsync`
  (`DeviceDetailsViewModel.cs:777`) is fire-and-forget from `SetDevice` and has no command guard at
  all. `AsyncRelayCommand` (CommunityToolkit 8.4.2) only prevents a command re-entering *itself*.
  Concretely: Discard on preset A starts → user clicks preset B → A's revert finishes and clears
  the flag → B's still-running reload cascades through `SaveRgbStateForCurrentDevice` →
  `StageChangeAndApply` (the exact path the `:60-64` comment says `_suppressPresetDirty` exists to
  stop) → a real device write with half-loaded state plus a spurious dirty flag; the two racing
  `SetAppliedPreset` calls can also persist the wrong active preset, so the next open shows the
  preset the user did not pick. Not a blocker: `_applyGate` still guarantees each write is one
  coordinated `ApplyDeviceConfigurationAsync` (BLAZE stays safe), and the state is recoverable —
  but this is the closest thing to one in the branch, and it is in the area the plan named its
  highest risk.
  **Fix:** Option A (correct) — serialise the whole method: take a dedicated
  `SemaphoreSlim` at the top of `ApplyPresetToDeviceAsync` and hold it across the entire body, so a
  second revert queues rather than interleaves. Option B (minimal) — replace the bool with an `int`
  depth counter (`Interlocked.Increment`/`Decrement`, suppress while `> 0`), which fixes the
  early-clear but still lets two reloads interleave their reads. Option A is preferred; it also
  makes the `_deviceApplyDebounceCts.Cancel()` at `:308` unambiguously sufficient.

- [ ] **S2. `PersistDisplayedDpiAndRate` fires the persist calls and immediately depends on them
  having landed** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:249-258` (called at `:217` and in `AppendNewPresetAsync`) — Should-fix
  **Problem / why it matters:** it uses `_ = _settings.SaveActiveDpiStageAsync(...)` /
  `_ = SavePollingRateAsync(...)` (fire-and-forget), and the very next statement
  (`SavePresetSnapshot`) reads `device_settings` expecting those rows to be present. It works today
  only because `SettingsRepository.SaveActiveDpiStageAsync`/`SavePollingRateAsync`
  (`SettingsRepository.cs:141,161`) happen to be synchronous — they call `_storage.SetDeviceSetting`
  and `return Task.CompletedTask`. Nothing states or enforces that. If either is later made
  genuinely async (a real `await` on the connection, a batching layer), the snapshot silently
  captures a preset *without* its stage/rate — reintroducing precisely the root-cause bug this
  branch exists to fix, with no test failing (the current tests would still pass, since they
  exercise the synchronous implementation).
  **Fix:** make `PersistDisplayedDpiAndRate` `async Task` and `await` both calls, then `await` it
  from `ConfirmSavePreset` (which would become `async Task`) and `AppendNewPresetAsync` (already
  async). Alternatively, if keeping `ConfirmSavePreset` synchronous matters, call
  `_localStorage.SetDeviceSetting(...)` directly — it is genuinely synchronous by contract.

- [ ] **S3. The per-edit dirty check runs a synchronous multi-`EXCEPT` SQL query on the UI thread**
  — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:121-131` — Should-fix
  **Problem / why it matters:** `TryRecomputeHasUnsavedPresetChanges` is called from
  `StageChangeAndApply`, which runs on the UI thread (button clicks, property-changed handlers, and
  inside `Dispatcher.UIThread.Post` in `OnDpiStageChanged`). `PresetMatchesLiveState` takes
  `LocalStorageService._lock.Wait()` — a *synchronous* wait on the shared storage semaphore. That
  same lock is held across `await`s by `MigrateGuestToUserAsync` (`LocalStorageService.cs`, the
  guest→user sign-in migration). If a user signs in while the Device Details page is open and then
  touches any setting, the UI thread blocks for the entire migration. CLAUDE.md's "never block the
  UI thread" rule is absolute. Mitigating context, honestly stated: these handlers *already* make
  synchronous storage calls (`SetButtonBinding`, `SetDeviceSetting`), so this adds a read to a path
  that was already synchronous, and the query itself is trivial against tiny tables — which is why
  this is a Should-fix and not a blocker.
  **Fix:** Option A (correct) — make the recompute async (`await`-based `PresetMatchesLiveStateAsync`
  using `_lock.WaitAsync()`), updating `HasUnsavedPresetChanges` when it completes; the button would
  then lag by one DB round-trip (microseconds) rather than by the device write, preserving the UX
  win. Option B (targeted) — leave it synchronous but stop `MigrateGuestToUserAsync` holding the
  lock across `await`s, which is the actual freeze source and a latent risk for every other
  synchronous caller too.

#### Tests

- [ ] Plan Test strategy asked for the revert-to-original scenario "repeated per setting family …
  DPI stage, report rate, **an RGB field**, a button reassignment, and **a bound macro's content
  edit**". DPI, rate, button and keyboard key bindings are covered; **RGB and bound-macro-content
  reverts have no test**. Both are live code paths (`Rgb.cs:769`, `Buttons.cs` `ReplaceEditedMacro`)
  — add two cases to `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs` mirroring
  `StageChangeAndApply_RevertToTheSavedValue_ClearsDirty`.
- [ ] No test covers the S1 concurrency scenario (two overlapping `ApplyPresetToDeviceAsync`).
  If S1 is fixed, add one: start a revert with `DelayFirstCallMs` set, invoke `SelectPreset` for a
  second preset mid-flight, and assert exactly one `SetAppliedPreset` winner and no spurious dirty.
- Note: `dotnet-ci.yml` builds only the App csproj and does **not** run `tests/` — all of this
  coverage is local-only. Pre-existing, but worth stating since this branch leans heavily on tests.

#### Nits

- `_applyGate` (`Presets.cs:404`) and the replaced `_deviceApplyDebounceCts` are `IDisposable` and
  never disposed. Consistent with the VM's existing debounce CTS fields and its app-lifetime
  singleton scope — noted for consistency, not worth changing alone.
- The `_deviceApplyDebounceCts?.Cancel()` calls in `ConfirmSavePreset` (`:226`) and
  `AppendNewPresetAsync` are self-documented as "not fixing a bug". With `_applyGate` serialising
  and a trailing apply now being idempotent, they are genuinely redundant belt-and-braces — the one
  in `ApplyPresetToDeviceAsync` (`:308`) *is* load-bearing (proven by
  `DiscardUnsavedChanges_CancelsAnyPendingDebouncedApply_…`). Harmless; drop them only if
  simplifying.
- No `docs/CONTEXT.md` glossary entry was added for the new live-apply / preset-dirty-diff concept,
  though CLAUDE.md makes the glossary mandatory for new domain terms. The share-presets work did
  update its `Default preset` entry.
- QA step 11 (TAIPAN write frequency under rapid edits) and keyboard live-apply on real hardware
  remain unverified — correctly disclosed in the outcome section, repeated here so it is not lost.

#### What's done well

- The BLAZE single-coordinated-write constraint is not just preserved but **strengthened**: every
  apply still goes through one `ApplyDeviceConfigurationAsync`, and `_applyGate` now guarantees two
  applies can never interleave. No per-aspect write path was introduced anywhere.
- `PersistDisplayedDpiAndRate` fixes the actual root cause (snapshots were incomplete) rather than
  patching the revert path again — the earlier rounds' symptom-chasing was correctly abandoned.
- Every bug-fix test was verified to fail against the pre-fix code before being kept, and the
  `DelayFirstCallMs` seam makes the in-flight-write race genuinely testable rather than assumed.
- The `Implementation outcome` sections are honest about deviations, the regression this work
  introduced in `SetDevice`, and what remains unverified.
- The `restoreUnsetToDefaults` split correctly preserves the earlier B1 guard (first-connect must
  not fabricate a stage) while still making user-driven reverts authoritative.

#### Verdict

Ship: yes — no blockers; the branch is user-confirmed working, well tested, and preserves the
BLAZE write constraint. S1 is the one finding I would fix before this reaches users: it is a real
concurrency defect in the exact area the plan flagged as highest-risk, and Option A is a small
change. First: S1 (serialise `ApplyPresetToDeviceAsync`), then S2 (await the persist before
snapshotting), then the RGB / bound-macro revert tests.

### Round 1 addendum — S1 correction (severity was over-stated)

- [x] **S1** — addressed, but the finding itself was **over-stated and is corrected here.**

  **What I got wrong.** I graded S1 from code shape (a non-reentrant bool held across `await`s)
  without first constructing a failing case. On attempting to reproduce it I could not: a test that
  forces two reverts to overlap (`OverlappingPresetReverts_AreSerialised_…`, using
  `DelayFirstCallMs` to keep the first revert's device write in flight) **passes with the gate
  removed, and still passes with the save/restore removed as well.** My own evidence rule says a
  finding that cannot be reproduced must be downgraded — so S1 is downgraded from Should-fix to a
  latent fragility.

  **Why it does not bite today.** `ApplyPresetToDeviceCoreAsync`'s reload is effectively
  synchronous up to the device write: every `SettingsRepository` load
  (`LoadDpiPresetsAsync`, `LoadActiveDpiStageAsync`, `LoadPollingRateAsync`,
  `LoadMouseSettingsAsync`) returns an already-completed `Task`, so `await` on them never yields.
  The first real yield is the gRPC write itself, by which point the reload has finished. A second
  revert therefore cannot interleave *mid-reload*, and `_applyGate` already orders the writes and
  the `SetAppliedPreset` calls that follow them. The scenario I described needs a yield during the
  reload — plausible on a keyboard path (`LoadKeyLayoutForDevice` can await a real HID probe), but
  I did not demonstrate it there either.

  **What was changed anyway (hardening, not a bug fix):**
  - `_presetApplyGate` (`Presets.cs`) serialises the whole revert, making the ordering explicit
    instead of incidental to `_applyGate`'s timing.
  - `_suppressPresetDirty` now saves and restores its previous value in both scopes
    (`Presets.cs` `ApplyPresetToDeviceCoreAsync`, `DeviceDetailsViewModel.cs` `SetDevice`) instead
    of blindly resetting to `false`, so nested suppressors cannot clear it early.
  - `OverlappingPresetReverts_AreSerialised_…` kept as a behavioural guard on the outcome (last
    preset picked wins, clean, correctly recorded), with a comment stating plainly that it does
    **not** prove the serialisation is load-bearing and is the test that should start failing if a
    settings load ever becomes genuinely async.

  These are cheap and reduce a real latent fragility in the area this feature has repeatedly
  regressed in, but they are insurance — reverting them would not reintroduce any demonstrated bug.
  S2, S3 and the Tests items from Round 1 are unaffected by this correction and still stand.

  Suite after the change: 364 passing, build clean.

### Round 2 (external review) — M1 fixed

- [x] **M1. `PersistDisplayedDpiAndRate` bypassed the `_dpiStageKnown` guard and stored the UI's
  structural default as a real user choice** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:249-258` (callers: `ConfirmSavePreset`, `AppendNewPresetAsync`) — **confirmed and fixed**

  **Verified, not taken on trust.** Every link in the reported chain checked against the code:
  `RebuildDpiPresets` (`DeviceDetailsViewModel.cs:366-384`) ends with `_dpiStageKnown = false` **and**
  `DpiPresets[0].IsSelected = true`; `ApplyKnownDeviceState`'s no-live-value branch
  (`DeviceDetailsViewModel.cs:288-293`) keeps stage 0 highlighted with an explicit comment that it
  "must not be read as 'the device is on stage 0' by a later device write"; and
  `ApplyCurrentStateToDeviceCoreAsync` honours `_dpiStageKnown` before sending a stage.
  `PersistDisplayedDpiAndRate` was the only place reading `IsSelected` without the flag.

  **Failure scenario (reproduced by test).** A preset is already applied, so nothing re-applies on
  open and no revert marks the stage known: `_dpiStageKnown == false` with row 0 highlighted. The
  user edits something unrelated (an RGB colour) and hits SAVE PRESET → stage 0 is written to
  `device_settings` and copied into the snapshot. From then on it counts as known:
  `LoadPersistedDeviceStateAsync` reads it back, sets `_dpiStageKnown = true`, and
  `AssertActiveDpiStageAsync` physically writes stage 0 — silently dropping a mouse the user had
  put on 12000 with the physical button back to 800. Exactly the regression class `_dpiStageKnown`
  exists to prevent.

  **Fix — Option A as proposed.** Both writes now sit behind the same flags the device-write path
  uses (`_dpiStageKnown` / `_reportRateKnown`). This does **not** reintroduce the incomplete-preset
  bug that `PersistDisplayedDpiAndRate` was added for: when the stage genuinely is unknown the
  preset stores nothing, and `restoreUnsetToDefaults` already gives such a preset a sane revert
  target; when the user has actually established a stage, it is known and still persisted.

  The report's secondary point was also correct and is closed: the report rate was safe here only
  as a *side effect* of `ApplyKnownDeviceState` deselecting every `PollingRateOptions` row when the
  rate is unknown. It now has an explicit `_reportRateKnown` check rather than depending on another
  method's behaviour.

  **Test added** (the one the report specified, plus the rate half):
  `ConfirmSavePreset_WithNoKnownDpiStage_DoesNotPersistTheStructuralDefaultStage` in
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`. It uses `LoadPresetsForDevice`
  rather than `SelectPreset` to establish the active preset, because a revert would legitimately
  mark the stage known and mask the bug. **Verified to fail without the guard** (`dpi.activeStage`
  present in storage) and pass with it.

  Suite after the change: 365 passing, build clean.

- [x] **m1 (minor). `PersistDisplayedDpiAndRate` snapshotted a DPI stage and report rate for
  keyboards** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs`
  — **confirmed and fixed**

  **Confirmed, and the M1 guard did not already cover it.** `RebuildDpiPresets`
  (`DeviceDetailsViewModel.cs:368`) falls back to `DefaultDpiStages` for any device declaring none
  — keyboards included — and highlights row 0. `_dpiStageKnown` alone was not enough: the
  `restoreUnsetToDefaults` fallback in `ApplyPresetToDeviceCoreAsync` does
  `stage ??= DpiPresets.FirstOrDefault()?.StageIndex` and then sets `_dpiStageKnown = true`, so
  simply selecting a preset on a Raster/Cyrex marked a fabricated stage 0 as known. The same
  fallback marks a 1000 Hz rate known, so the report rate had the identical problem — worth fixing
  together rather than only the half that was reported.

  **Fix.** The method now returns early unless `Device.SupportsDpiStageSwitch`, gating both the
  stage and the rate. Deliberately a **capability check, not a device-type check** (an earlier
  revision used `!IsKeyboardDevice` for the rate and was changed at the user's request): active
  stage and report rate are the same class of mouse-style tuning, so a device that cannot switch
  DPI stages has no rate to snapshot either. Onboarding a new device type then means getting that
  capability right in `DeviceCardViewModel` — the single place it is derived — with nothing to
  update here. Harmless to hardware either way (neither field is sent down the keyboard apply
  branch), but it keeps junk out of the snapshot that `PresetMatchesLiveState` compares on. Test
  added: `ConfirmSavePreset_OnKeyboard_DoesNotSnapshotDpiStageOrReportRate`, verified to fail
  without the capability check.

  Note: `ApplyCurrentStateToDeviceCoreAsync` still branches on `IsKeyboardDevice` to pick the whole
  apply strategy (RGB + key overrides vs. the unified mouse configuration write). That predates
  this work and is a genuine strategy split rather than a per-field capability, so it was left
  alone — but it is the next place to look if device-type checks are to be phased out.

  Suite after the change: 368 passing, build clean.

#### Test-infrastructure flake (found while verifying the above — not a product bug)

`PhysicalDpiButtonPress_WhilePresetSelected_MarksPresetDirty` and
`PhysicalPollingRateButtonPress_WhilePresetSelected_MarksPresetDirty` fail intermittently in full
suite runs — measured at roughly **1 run in 4** — with
`System.InvalidOperationException : The calling thread cannot access this object because a
different thread owns it` from `Dispatcher.UIThread.RunJobs()`. Both pass reliably in isolation.

This is the process-wide Avalonia dispatcher thread-affinity fragility already documented in
`tests/ProoveGaming.App.Tests/AssemblyInfo.cs`: `Dispatcher.UIThread` binds to whichever thread
touches it first, and disabling xUnit parallelisation does not pin every test to one thread. It is
pre-existing, but this branch **amplified it** — the new debounce/apply tests do fire-and-forget
async work whose thread-pool continuations shift which thread wins that race.

Not fixed here: the standard remedy is bootstrapping a headless Avalonia app once per assembly,
which needs the `Avalonia.Headless` NuGet package — a dependency addition that CLAUDE.md requires
be raised explicitly rather than slipped into an unrelated change. Flagged for a decision. Until
then a red suite run should be re-run once before being treated as a real failure, and CI would be
flaky if `tests/` were ever wired into `dotnet-ci.yml` (today it only builds the App csproj).
### Round 3 — Ship: yes

Reviews the uncommitted working-tree delta on `main` (4 files, no commits): the fix for the
Cyrex/Aress "RGB effects broken until the App is closed" regression this feature introduced, its
regression test, plus two unrelated Aress card-artwork changes the author added in the same tree.

#### Plan conformance

Plan: `docs/plans/live-apply-preset-dirty-diff/live-apply-preset-dirty-diff-plan.md` (this file) —
the feature whose implementation regressed. The SVG / `DoubleCard.axaml` edits belong to no plan.

- **Root cause was a gap in this plan, not a coding slip.** The plan's chosen approach (Options
  considered → *Apply cadence*, item 1) is explicitly "calls the **unchanged**
  `ApplyCurrentStateToDeviceAsync()` once", and the Critical hardware constraint frames the whole
  feature as "ViewModel-level orchestration (when to call an unchanged method), not new protocol
  code". That premise silently assumed the apply method has no side effect back into staging. It
  does, on the keyboard branch only: `ApplyCurrentStateToDeviceCoreAsync` → `PushRgbToDeviceAsync`
  → `SendLedEnabledToggleAsync(true)` → `ApplyRgbSettingsAsync`, whose synchronous prefix calls
  `SaveRgbStateForCurrentDevice()` — which this plan rewired to end in `StageChangeAndApply()`.
  Each apply therefore scheduled the next one, forever, at `DeviceApplyDebounceMs` (250 ms).
- **The Risks section caught the class but not this variant.** "Bulk-reload cascade (highest
  risk)" correctly predicted "`StageChangeAndApply()` will now trigger a **real device write**"
  from an unguarded path, and directed the guard audit at the *load* paths
  (`LoadRgbStateForCurrentDevice`, `RebuildDeviceSettings`, `LoadButtonBindings`, …). The audit
  never covered the *apply* path re-entering staging, because the plan treated the apply target as
  inert. Worth carrying into any future plan that reroutes a shared notify method: audit both what
  feeds it **and** what it feeds.
- **Option 3 ("narrow per-aspect immediate writes") is NOT violated by the fix.** Flagging this
  because the new `WriteRgbToDeviceAsync()` looks like exactly the rejected pattern. It is not: it
  is reachable only from `PushRgbToDeviceAsync`, which is called only in the **keyboard** branch of
  `ApplyCurrentStateToDeviceCoreAsync` — keyboards already apply per-aspect by design ("keyboards
  get RGB + key remaps via their per-aspect paths"). `SetDeviceLedColorAsync` with the same six
  bytes was already the call that branch made. The BLAZE / mouse path still goes through the single
  coordinated `ApplyDeviceConfigurationAsync`. The set of device writes is unchanged; only the
  persistence / staging side effect was removed.
- **Acceptance criteria** — unaffected and still met; this restores behaviour the feature broke on
  keyboards. The "Optimistic dirty flag briefly wrong" accepted limitation is untouched.
- **Open question now answerable** — "`DeviceApplyDebounceMs = 250` … confirm 250ms feels
  immediate enough": 250 ms was not the problem and needs no change. The symptom traced entirely
  to re-entry, not cadence.

#### Code

- [x] **S1. `ApplyRgbSettingsAsync` is now dead production code** — `DeviceDetailsViewModel.Rgb.cs:385` — Should-fix
  **Problem / why it matters:** The fix removed `SendLedEnabledToggleAsync`, which was
  `ApplyRgbSettingsAsync`'s last production caller. Grepping `src/**` for `ApplyRgbSettingsCommand`
  and `ApplyRgbSettingsAsync` now returns **zero** production hits — no XAML binds the generated
  command, and no code calls the method. Its only remaining caller is the new test at
  `DeviceDetailsPresetApplyTests.cs:704`. A `[RelayCommand]` that nothing binds reads as a live
  entry point to the next person and invites a caller to be re-added on the very path that just
  caused this regression (it is the method whose prefix closes the loop).
  **Fix:** Option A (preferred) — delete `ApplyRgbSettingsAsync` and its command, and re-point the
  regression test at a production trigger (see T1). Option B — keep it if the RGB section is meant
  to bind an explicit "Apply" button later, and add a comment saying it is currently unbound and
  must never be called from `PushRgbToDeviceAsync`.

- [x] **S2. First-connect apply no longer persists a keyboard's RGB state** — `DeviceDetailsViewModel.Rgb.cs:350` — Should-fix
  *(Fixed — see "Round 2 fix — S2 / PR#78 M1" below. Also raised as M1 on PR #78.)*
  **Problem / why it matters:** `PushRgbToDeviceAsync` previously reached
  `SaveRgbStateForCurrentDevice()` (via `ApplyRgbSettingsAsync`) and so persisted `device_rgb` as a
  side effect of pushing. It no longer does — correct in principle, but it removes the only write
  that populated `device_rgb` on a keyboard's first page-open, since `EnsureDefaultPresetAppliedAsync`
  (`DeviceDetailsViewModel.cs:729`) is called from `SetDevice`, and `SetDevice`'s own
  `SaveRgbStateForCurrentDevice()` at line 702 flushes the **outgoing** device, not the incoming one.
  **Failure scenario:** open a Cyrex/Aress details page, never touch RGB, go back Home (does not
  call `SetDevice`), unplug and replug the keyboard. `HomeViewModel.ReapplyRgbOnConnect` →
  `ReapplyPersistedRgbAsync` reads `GetRgbState(...)`, gets `null`, and returns early
  (`Rgb.cs:414`), so the App's default lighting it pushed on first open is not restored — the board
  comes back on firmware default. Before this fix the defaults had been persisted and would be
  re-pushed. Narrow (it closes as soon as the user edits RGB or opens any other device, which
  triggers the outgoing flush), which is why this is Should-fix and not a Blocker.
  **Fix:** Option A (correct) — split `SaveRgbStateForCurrentDevice` into a persist-only
  `PersistRgbStateForCurrentDevice()` (in-session cache + `SaveRgbAsync`) plus the
  `StageChangeAndApply()` tail, and have the keyboard push call the persist-only half. Keeps the
  loop closed while restoring the persistence. Option B (cheap) — have `SetDevice` persist the
  **incoming** device's resolved RGB state once after `LoadRgbStateForCurrentDevice()`, under the
  existing `_suppressPresetDirty` guard. Option C — accept and document that unconfigured keyboards
  keep firmware lighting across a reconnect.

#### Tests

- [x] **T1. The regression test drives the loop through a path production cannot reach** —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs:704` — the trigger is
  `vm.ApplyRgbSettingsCommand.Execute(null)`, and per S1 nothing in `src/**` binds or calls that
  command. The loop it detects is the real one, so the test is valid today, but if S1 Option A is
  taken the test stops compiling, and either way it does not exercise how a user actually starts
  the loop. Re-point it at a production trigger — `vm.IsRgbEnabled = false` (reaches
  `SaveRgbStateForCurrentDevice` through the property setter and avoids `StartEffectAnimation`'s
  `DispatcherTimer`, which needs an Avalonia dispatcher this project does not initialise), or a
  `SelectEffect` / colour-apply path.
- [x] **T2. The test asserts the loop stopped but not that RGB still reaches the device** — same
  file, `StagedRgbEdit_OnKeyboard_DoesNotSelfRetriggerForever` — it captures `settled` and asserts
  the count is unchanged 800 ms later. A "fix" that made `PushRgbToDeviceAsync` a no-op would pass
  it. Add `Assert.True(settled >= 2)` (the immediate write plus the debounced apply) so the test
  pins both halves: the write still happens, and it stops happening.
- Verified on the reviewed tree: `dotnet build ./src/ProveGaming.slnx` → 0 errors;
  `dotnet test ./src/ProveGaming.slnx` → 720 passing (365 App + 355 Infrastructure).
  The new test was confirmed to **fail** against the pre-fix behaviour (expected 3, actual 7 — one
  LED rewrite per 250 ms, unbounded), so it genuinely pins the regression.

#### Aress card artwork (unplanned, reviewed separately)

Two changes: `PG_Aress_card.svg` viewBox cropped `0 0 1571.695 1563.529` → `34.5 541 1503.5 481.5`
(plus matching `width` / `height` and a leading explanatory comment), and a comment-only edit in
`DoubleCard.axaml:113-116`. Scope drift relative to any plan, but self-contained and cosmetic.

Verified rather than eyeballed, because a non-zero viewBox origin is genuinely risky here —
`SvgImageView.Rasterize` (`ProoveGaming.Common/Controls/SvgImageView.axaml.cs`) scales by
`CullRect.Width/Height` but never translates by `CullRect.Left/Top`, so content left in original
coordinates would have rendered off-canvas (a blank card). Loading the file through the real
Svg.Skia 5.1.1 path shows that does not happen — Svg.Skia normalises the viewBox transform into the
picture:

- `PG_Aress_card.svg` → `CullRect L=0 T=0 W=1504 H=482`, rasterises **99.7 % non-transparent**,
  ink bbox `x 0..1502, y 0..480`. Renders correctly, and the leading XML comment parses fine.
- Content-vs-crop measurement (alpha bbox of both embedded rasters, plus a coordinate sweep of all
  266 paths and the `text-blur` / `text-solid` rects): base raster `x 35.24..1536.76,
  y 540.80..1021.16`; `keys-rgb-blur` / `keys-rgb-solid` `x 66.70..1505.79, y 572.23..990.12`;
  legends `x 89.83..1481.08, y 587.64..937.59`. All inside the crop. The `keys-rgb-blur-filter`
  region (`y=-35%`, `height=170%`) nominally exceeds it, but at `stdDeviation=8` the visible spread
  is ~24 units → `y 548..1014`, still contained.
- Card sizing: at `MaxWidth=336` / `MaxHeight=160` (`DoubleCard.axaml:124-125`), Aress goes from
  height-capped (~154 px of board) to width-capped `336 × 108`, effectively matching Cyrex
  (`336 × 110`). The comment's "roughly half the width the other boards get" is accurate, as is
  "framed like `PG_Cyrex_card.svg`" — Cyrex measures 100 % flush.

- [ ] **S3. T-801 has the identical defect and is left as-is, now documented as intended** —
  `src/ProoveGaming.App/Views/DoubleCard.axaml:113-116` — Should-fix
  **Problem / why it matters:** `PG_T801_card.svg` is a `0 0 1920 1920` canvas whose board occupies
  only `x 178..1909, y 672..1267` — rasterising it produces a bitmap that is **27.5 %** ink, and it
  renders height-capped at `160 × 160` with the board about 145 px wide, against 336 px for every
  other board. That is the same defect just fixed for Aress. The edited comment now reads as though
  this is the desired outcome ("T-801 … is held by the height instead of overflowing into the
  labels below"), which will discourage the next person from fixing it.
  **Fix:** Apply the same crop to `PG_T801_card.svg` — `viewBox="177 671 1735 598"` with matching
  `width` / `height` covers the measured ink with a ~1-unit margin. If it is deliberately deferred,
  reword the comment to say T-801 is *still* uncropped and renders small, rather than implying the
  height cap is doing something desirable for it.

#### Nits

- Sub-pixel top clip on Aress — `PG_Aress_card.svg:10`: the base raster's opaque content starts at
  `y 540.80` and the crop starts at `541.0`, so 0.20 user units are cut. At card scale that is
  ~0.045 px, i.e. invisible; `541` → `540.5` closes it for free if the file is touched again.
- `PushRgbToDeviceAsync` is now a one-line alias for `WriteRgbToDeviceAsync` —
  `DeviceDetailsViewModel.Rgb.cs:350`. Two names for one behaviour; fold them and keep the
  explanatory comment on the survivor.
- `ApplyRgbSettingsAsync` writes the LED twice per invocation — once directly (`Rgb.cs:401`) and
  once ~250 ms later via the `StageChangeAndApply` it triggers at `Rgb.cs:392`. Harmless and
  bounded, and moot if S1 Option A is taken.
- `PG_Aress_card.svg` is now the only card SVG with a non-zero viewBox origin, and the only one
  listing `width` / `height` before `viewBox`. No functional impact; noted for consistency.
- `DoubleCard.axaml` says Aress is no longer near-square while
  `Views/DeviceDetails/Sections/RgbKeyboardView.axaml:19` still says "Aress is near-square". Both
  are correct — the latter is about the layered base/keys artwork, which the crop deliberately did
  not touch — but saying "card artwork" explicitly in one of them would save the next reader the
  trip.

#### What's done well

- The fix targets the actual re-entry rather than papering over it. The tempting shortcut — a
  blanket "suppress staging while an apply is in flight" flag — would have silently swallowed any
  genuine user edit made during a ~400 ms Cyrex/Aress key write. Making the push path pure has no
  such failure mode.
- Reusing `BuildLedConfig()` for the off state instead of re-hardcoding `0,0,0,0xED,0,3` removed
  the duplicate wire mapping that `ApplyRgbSettingsAsync` and `SendLedEnabledToggleAsync` had
  drifted into keeping in two places. The two encodings were verified byte-identical before the
  merge.
- The comment on `PushRgbToDeviceAsync` records the loop and names the constraint ("must never
  route back through `ApplyRgbSettingsAsync`"), which is what stops this from being re-broken.
- Cropping the artwork rather than special-casing Aress in `DoubleCard.axaml` keeps the card view
  free of per-device branching, and leaves the layered base/keys assets — where the glow and legend
  geometry live in canvas units — untouched. It also cuts that card's raster from ~2.46 M px to
  ~0.72 M px.

#### Verdict

Ship: yes — the regression fix is correct, minimal, verified against the real device-write paths,
and pinned by a test proven to fail without it; the artwork crop is verified safe through the actual
Svg.Skia render path. No blockers. S1 / S2 are follow-ups on the fix's edges (an orphaned command
and a narrow lost-persistence case), S3 is a pre-existing defect the change surfaced rather than
caused. First: S2 (the only one with user-visible behaviour), then S1 + T1 together, then T2.

### Round 3 fix — S2 / PR#78 M1 (RGB persistence restored)

Confirmed first, on both revisions, with a throwaway probe (keyboard + real `SettingsRepository`,
reading back exactly what `ReapplyPersistedRgbAsync` reads):

| path | `main` | branch before this fix |
|---|---|---|
| `SelectPresetCommand` → apply | record present (`Static`, 5 effects) | **null** |
| full `SetDevice` (page open) | record present | **null** |

LED writes were 1 in both cases — the board was lit, the App just kept no record of it.

**Why the obvious fix was rejected.** The review comment's preferred option (persist from the push
path) was measured against the store and backfires: with `preset_rgb` empty and `device_rgb`
populated, `PresetMatchesLiveState` reports a difference, so SAVE PRESET lights up on a page the
user never edited. On an untouched keyboard `preset_rgb` is empty *precisely because* of this bug,
so option A would have sprung that trap immediately — the same class Round 1 was spent on.

**What was implemented (three parts, all needed):**

1. **Split persistence from staging** — `SaveRgbStateForCurrentDevice` (`Rgb.cs`) now delegates to a
   new `PersistRgbStateForCurrentDevice()` (in-session cache + `SaveRgbAsync`, nothing else) and
   then calls `StageChangeAndApply()`. Housekeeping callers take the persist-only half, so writing
   RGB down never marks a preset dirty or schedules a device write.
2. **Persist the INCOMING device on page open** — `SetDevice` calls `PersistRgbStateForCurrentDevice()`
   straight after `LoadRgbStateForCurrentDevice()`. The existing flush at the top of `SetDevice`
   saves the device being *left*, never the one being opened, which is why an untouched keyboard
   had nothing stored.
3. **Seed the freshly-created default preset's snapshot** — `CreateDefaultPresetIfNotExists` now
   returns `(int Id, bool Seeded)`, and `LoadPresetsForDevice` captures a snapshot when (and only
   when) `Seeded` is true. Parts 1–2 alone did **not** fix M1: the probe still returned null,
   because `EnsureDefaultPresetAppliedAsync` loads the brand-new default preset's *empty* snapshot
   onto the device and wipes the rows that were just written. `Seeded` is the only safe guard here —
   an existing preset can be legitimately empty (after Reset), and overwriting an existing snapshot
   from live state is the destructive step `EnsureDefaultPresetAppliedAsync` explicitly warns
   about. A promoted legacy row (Case 2) is never seeded, so upgrades cannot hit it.

`PersistDisplayedDpiAndRate` → **`PersistDisplayedDeviceState`**, now covering RGB as well as DPI
stage / report rate, so every later `SavePresetSnapshot` keeps `preset_rgb` in step with
`device_rgb`. Deliberately *not* used for the seed in part 3: `LoadPersistedDeviceStateAsync` has
not resolved DPI/rate for the incoming device at that point, and seeding the previous device's
selection would be worse than seeding nothing (the B1 regression).

Resulting first-open sequence, all verified by test: resolve RGB → persist → seed default snapshot
from it → `EnsureDefaultPresetAppliedAsync` loads that snapshot back (a no-op, not a wipe) and
pushes → live matches snapshot, so the preset stays clean and a reconnect has something to restore.

**Tests** (`DeviceDetailsPresetApplyTests`, both fail without the fix):
- `OpeningKeyboardPage_PersistsRgbState_AndLeavesThePresetClean` — pins both halves at once: RGB is
  persisted after a page open, **and** the preset is not dirty (`PresetMatchesLiveState` true), so
  the trap above cannot be reintroduced by a future "just persist it" change.
- `SavingPreset_CapturesRgbIntoTheSnapshot_EvenWhenUserNeverTouchedRgb` — wipes the live rows first
  so it can only pass if the save path re-captured them.

Suite after the change: 722 passing (367 App + 355 Infrastructure), full solution builds clean.

Still open from Round 2: S1 (`ApplyRgbSettingsAsync` now unreferenced in `src/**`), T1/T2 (the loop
test's trigger and its missing "a write still happens" assertion), S3 (the T-801 card artwork).

### Round 3 fix — S1 / T1 / T2 (PR#78 m1, m2)

**S1 — dead command removed.** `ApplyRgbSettingsAsync` and its generated
`ApplyRgbSettingsCommand` are deleted. Nothing in `src/**` referenced either after
`SendLedEnabledToggleAsync` went away, and leaving an unbound `[RelayCommand]` behind on the one
method whose prefix closed the apply→stage→apply loop was an invitation to re-wire it. Nothing is
lost: a user RGB edit still reaches the hardware through
`StageRgbSettings` → `SaveRgbStateForCurrentDevice` → `StageChangeAndApply` → the debounced
`ApplyCurrentStateToDeviceAsync` (keyboards via `PushRgbToDeviceAsync`, mice via the coordinated
`ApplyDeviceConfigurationAsync` + `BuildLedConfig`).

The two comments that pointed at it were rewritten rather than deleted — they carry the
anti-regression warning. `PushRgbToDeviceAsync` now states the rule in terms of the *behaviour*
("anything here that stages schedules the very apply that called it — persisting is fine, staging
is not") instead of naming a method that no longer exists, and keeps the historical account of what
the loop did on Cyrex/Aress.

**T1 — the loop test now uses a trigger that exists in production.** It was driven by
`ApplyRgbSettingsCommand`, which nothing bound; it now clicks an effect tile
(`RgbEffects.First(e => e.EffectKey == "Static").SelectCommand`).

One correction to the Round 2 write-up, which suggested `IsRgbEnabled = false` for this: that would
have been **wrong**, and the test would have passed against the very bug it pins. The old loop ran
through the *enabled* branch of the LED toggle — a disabled trigger never re-entered
`ApplyRgbSettingsAsync` at all. The trigger has to leave lighting ON, which the test now asserts
explicitly (`Assert.True(vm.IsRgbEnabled)`) so the point cannot be lost in a later edit. "Static" is
chosen because its animation path allocates no `DispatcherTimer`, which has no Avalonia dispatcher
to run on in this project.

**T2 — the test now pins that the write still happens.** `Assert.True(settled > beforeEdit)` before
the "stopped growing" assertion, so an implementation where `PushRgbToDeviceAsync` writes nothing at
all can no longer satisfy it.

Re-verified by reintroducing the loop (a temporary stage-then-write prefix on
`PushRgbToDeviceAsync`): the rewritten test fails 2 → 6 writes across the 800 ms window, and the new
`settled > beforeEdit` assertion passes on the way through — so both halves are load-bearing.

Suite: 367 App + 355 Infrastructure passing, three consecutive clean runs, solution builds clean.

#### Pre-existing flaky tests (not caused by this branch — new finding, unfixed)

- [ ] **F1. `PhysicalDpiButtonPress_WhilePresetSelected_MarksPresetDirty` and
  `PhysicalPollingRateButtonPress_WhilePresetSelected_MarksPresetDirty` fail intermittently** —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs:476` and `:499`

  Both failed together on 1 run in 3 during this work. Measured at **HEAD with this branch's changes
  stashed**: 1 failing run in 5 (365/365 on the other four), so it predates the RGB work and is not
  a regression from it — but it is real, and it undermines every "suite is green" claim made against
  this file.

  **Cause (likely, not proven):** each test calls `SelectPresetCommand.Execute(a)` and then asserts
  on the dirty flag without awaiting the apply. `ApplyPresetToDeviceAsync` holds
  `_suppressPresetDirty = true` across its awaits; when a yield happens (parallel test load is
  enough to change which awaits actually yield), the simulated device event arrives while that flag
  is still set, `StageChangeAndApply` early-returns, and `HasUnsavedPresetChanges` never flips.

  **Fix options:** (A) make the tests await the apply rather than racing it — expose the in-flight
  task, or poll `HasUnsavedPresetChanges` with a short timeout instead of asserting immediately;
  (B) drive the preset selection through a synchronous seam so no apply is in flight when the event
  is raised. (A) is closer to what the test means to assert. Worth doing before anyone reads a red
  CI run here as a real regression.
