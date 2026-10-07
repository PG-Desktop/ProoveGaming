# Fix Home→Details navigation delay (SQLite off the UI thread)

> **Superseded in part (2026-08-22, `ui-thread-storage-async`):** `SettingsRepository` no longer completes synchronously — every method now routes through the serialized storage executor in `LocalStorageService` (`RunOnStorageAsync`), and `PersistRgbStateForCurrentDeviceAsync` returns the executor write-completion task instead of a `Task.Run` wrapper. The write-through-synchronous assumptions this plan documents (e.g. "rows are in place for the snapshot") are now provided by queue FIFO ordering instead. The `Task.Run` loader pattern for one-off reads in `SetDeviceAsync` is unchanged.

## Goal

Opening Device Details from Home visibly stalls the UI: `MainWindow.OnDeviceDetailsRequested`
runs `DeviceDetailsViewModel.SetDevice(...)` synchronously on the UI thread, and that method
performs a long sequence of synchronous SQLite round-trips through `LocalStorageService` /
`SettingsRepository` (outgoing RGB flush, macro library read, button-binding read, RGB state
read + persist, default-preset seed + snapshot + preset list + applied-preset lookup) before the
view is even shown. This plan makes the SetDevice path asynchronous: the Details view appears
immediately with the device's identity (name, image, nav sections), while all storage I/O runs on
a background thread and only the results are applied back on the UI thread. Per CLAUDE.md, the UI
thread must never run blocking I/O.

## Scope

**In scope**

- `DeviceDetailsViewModel.SetDevice` → `SetDeviceAsync`: a synchronous UI-state prologue followed
  by an awaited chain of loaders whose storage I/O runs off-thread.
- The five storage-touching loaders on the SetDevice path: `LoadMacroLibrary`,
  `LoadButtonBindings`, `LoadRgbStateForCurrentDevice`, `PersistRgbStateForCurrentDevice`,
  `LoadPresetsForDevice` — each split into off-thread fetch + on-thread apply.
- `LoadPersistedMouseSettingsAsync` (inside `RebuildDeviceSettings`) and the storage read inside
  `RebuildAudioSettingsAsync` — today these are "async" methods whose `SettingsRepository` calls
  complete synchronously, so they execute entirely on the UI thread; their storage reads move
  into `Task.Run`.
- `MainWindow.OnDeviceDetailsRequested` — show the view immediately, run the load concurrently.
- A minimal test seam in `LocalStorageService` to observe which thread storage calls run on.
- Updating the existing test suites that call `SetDevice` (54 call sites across 7 files).

**Out of scope**

- Batching the per-key ViewModel builds in `LoadKeyLayoutForDevice` (separate optimization; it
  does no storage I/O and stays in the synchronous prologue).
- Restructuring the eagerly-instantiated section views in `DeviceDetailsView.axaml` (first-show
  layout cost; separate plan if pursued).
- Making `LocalStorageService` / `SettingsRepository` truly async end-to-end (see Options).
- The fire-and-forget tail of SetDevice (`RefreshFromDeviceAsync`, `LoadActuationFromDeviceAsync`,
  `LoadPersistedDeviceStateAsync`, `EnsureAppPreferencesLoadedAsync`,
  `EnsureDefaultPresetAppliedAsync`) keeps its current fire-and-forget shape; only its kick-off
  point moves to after the awaited chain so today's ordering (presets loaded before
  `EnsureDefaultPresetAppliedAsync`) is preserved.

## Options considered

**A. Monolithic fetch/apply snapshot.** One `Task.Run` block reads everything into a single DTO
(preset records, macro records, binding rows, RGB state), then one UI-thread apply pass populates
the VM. *Pros:* single thread hop; one guard point; strictest ordering. *Cons:* restructures five
loaders into DTO plumbing that duplicates their logic; the preset-apply path
(`ApplyPresetToDeviceAsync` calls `LoadButtonBindings` / `LoadRgbStateForCurrentDevice` directly)
would remain synchronous and UI-blocking; the many load-bearing ordering comments inside the
loaders would be stranded away from the code they describe.

**B. Per-loader internal offload (recommended).** Each loader keeps its name, position, and
responsibility, but internally awaits `Task.Run(...)` around its storage calls and applies results
on the calling (UI) thread. `SetDeviceAsync` awaits the loaders in the exact current order.
*Pros:* minimal structural change; ordering invariants preserved by construction (each loader
still completes before the next starts); `ApplyPresetToDeviceAsync` gets the same fix for free by
awaiting the now-async loaders; matches the version/`ReferenceEquals` guard patterns the VM
already uses (`RebuildAudioSettingsAsync`, `LoadPersistedMouseSettingsAsync`). *Cons:* several
short thread hops per device open (microseconds — negligible against the I/O saved); await gaps
between loaders open small interleaving windows, mitigated by guards (see Risks).

**C. Truly async storage layer.** Convert `LocalStorageService` (~40 methods) and
`SettingsRepository` to real async SQLite. Rejected: `Microsoft.Data.Sqlite` async APIs are
synchronous under the hood, so this is churn without benefit; several call sites depend on the
documented write-through-synchronous behavior ("rows are in place for the snapshot on the very
next line"); the blast radius covers every storage caller in the App.

## Recommended approach

Option B. Concretely:

1. **`SetDeviceAsync` shape.** `public async Task SetDeviceAsync(DeviceCardViewModel device)`
   replaces `SetDevice`. Everything up to the first await is the synchronous prologue and runs
   before the caller regains control: outgoing-RGB payload capture, `Device = device`, hotspots,
   `ActiveSection = NavSection.Presets`, `SyncHotspotCanvasWidth`, `LoadKeyLayoutForDevice`,
   `SavePresetCommand.NotifyCanExecuteChanged`, `InitDeviceOnlySvg`. `MainWindow` then shows the
   view immediately: the device's name, image, and nav render on the first frame while data loads.
2. **Loader split.** Each loader fetches its rows inside `await Task.Run(...)` and applies them to
   VM state after the await (on the UI thread — Avalonia's `SynchronizationContext` brings the
   continuation back; in unit tests, with no sync context, the continuation runs on the pool,
   which is fine because tests await the returned task before asserting). No `Dispatcher` calls
   are added — this keeps the code testable and honors the AssemblyInfo warning about the
   process-wide Dispatcher's first-touch thread affinity.
3. **Ordering invariants preserved.** The awaited chain keeps today's order:
   outgoing flush write → macros fetch/apply → bindings fetch/apply (macro-id resolution needs
   Macros populated) → RGB resolve/apply → RGB persist write → presets seed + snapshot + list +
   applied-id → apply. The seed-branch invariant ("the default preset's first snapshot must
   capture the device_rgb rows") holds because the RGB persist is awaited before
   `LoadPresetsForDeviceAsync` starts.
4. **UI-thread-only inputs are captured before `Task.Run`.** The current owner
   (`_ownerProvider.GetCurrent()`), the in-session RGB cache lookup (`_rgbStateByDevice`), and
   `SeedCatalogDefaultDpiAndRate`'s inputs (`TryGetLastDpiStage` / `TryGetLastReportRate` results,
   `ResolveDefaultDpiStageIndex()`) are evaluated on the calling thread and passed into the
   background block as locals. The background block touches only `LocalStorageService` /
   `SettingsRepository` and plain data.
5. **Suppression flags never span an await.** `_suppressAutoApply` (RGB) and
   `_suppressPresetDirty` are set only around the synchronous apply sections, after the fetch
   await has completed — an awaited gap inside a suppressed region would let unrelated UI-thread
   work observe the suppressed state (the exact trap the existing save/restore comments describe).
6. **Staleness guards.** `SetDeviceAsync` captures `device` and bumps a `_setDeviceGeneration`
   counter in the prologue. After each await, loaders drop their *apply* step when
   `!ReferenceEquals(Device, device)` or the generation moved on (rapid A→B navigation, or
   `ClearDeviceForExit` nulling `Device` mid-load). Storage *writes* in the fetch phase are not
   conditional — they describe the captured device and stay correct regardless.
7. **Preset-apply path.** `ApplyPresetToDeviceAsync` awaits the now-async
   `LoadButtonBindingsAsync` / `LoadRgbStateForCurrentDeviceAsync` at its existing call sites,
   removing that path's UI-thread SQLite as a side benefit. Its own `LoadPresetSnapshotToDevice`
   call also moves into `Task.Run` (same pattern, same method).
8. **Test seam.** `LocalStorageService` gets
   `internal Action? OnBeforeStorageAccess { get; set; }` invoked from a single private
   `AcquireLock()` helper that replaces the ~40 bare `_lock.Wait()` calls (a mechanical rename;
   `Release()` sites are untouched). Instance member, not static — parallel test classes create
   independent stores. The new threading test uses it to record the managed thread id of every
   storage access during `SetDeviceAsync`.
9. **MainWindow.** `OnDeviceDetailsRequested` stays a synchronous void handler:
   it kicks the load (`_ = SetDeviceAsyncSafe(device)` — a small wrapper that awaits and logs
   failures instead of leaving a faulted unobserved task), then sets `_currentStep` and calls
   `UpdateStepUi()`. Because the prologue runs synchronously before the first await, `Device` is
   already set when the view becomes visible.

### As-built deviations (accepted design — see Review findings Rounds 1–2)

The implementation deviates from three points below, deliberately; do not "fix" the code back
toward the original text:

1. **The preset-apply path stays synchronous.** `ApplyPresetToDeviceAsync` does NOT await the
   async loaders or move `LoadPresetSnapshotToDevice` into `Task.Run` — doing so broke 39
   command-driven tests that rely on the documented synchronous completion of storage against
   completed-task fakes. Instead the loaders are split into shared fetch/apply cores with sync
   twins (`LoadButtonBindings`, `LoadRgbStateForCurrentDevice`) for the preset path and async
   variants for `SetDeviceAsync`.
2. **`Settings.cs` / `Audio.cs` are untouched** (their `Task.Run` wrap was reverted for the same
   reason — `RebuildDeviceSettings` is awaited inside the preset apply). Their fire-and-forget
   kicks moved past the chain's first real yield instead, so they never run in the caller's
   synchronous window; residually, their two small write-through reads run on the UI thread in
   the app (accepted — bounded, post-render).
3. **The fire-and-forget tail is sequentially awaited** through `Quietly` (same order, same
   swallowed-failure semantics), so `SetDeviceAsync`'s task covers the whole open — the property
   the updated test suites depend on.

The loaders additionally take the chain **generation** (`_setDeviceGeneration`) and drop stale
applies — see Review findings S1 (same-card rapid-reopen duplication).

4. **Owner resolution is NOT fully hoisted for `SettingsRepository`-routed writes.** §4's
   "owner on the calling thread" rule holds for every direct `LocalStorageService` call, but the
   RGB persist closure (`BuildRgbPersistWrite` → `SaveRgbAsync`) and the DPI/rate seeds inside
   `LoadPresetsForDeviceAsync`'s background block go through `SettingsRepository`, which resolves
   the owner internally — on the pool thread. `CurrentOwnerProvider.GetCurrent()` reads plain
   session fields, so the only divergence window is a login/logout landing exactly inside a
   device-open, where seeded rows could scope to a different owner than the snapshot taken next.
   Accepted as-is (PR #100 review m3); threading a precomputed `SettingsOwner` through
   `SettingsRepository` overloads is the fix if that window ever matters.

5. **The follow-up tail defers the matrix probe to last** (PR #100 review m2): the
   storage-coupled sequence (actuation → persisted state → prefs → default-preset push) keeps
   its original relative order — the change-then-revert dirty comparison depends on it
   (`ExistingDefaultPresetWithEmptySnapshot_ChangeThenRevert…` fails if the default push runs
   before the persisted-state load) — and only `RefreshFromDeviceAsync` (the seconds-long
   keyboard matrix probe, independent of the others) moves behind it, so a first-connect
   keyboard gets its default lighting without waiting for the probe.

## File-level change list

All paths repo-relative; edits only, no new projects, no new NuGet packages, no proto changes,
no installer impact.

- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — replace
  `SetDevice` with `SetDeviceAsync` (sync prologue + awaited loader chain + fire-and-forget tail
  after the chain); add `_setDeviceGeneration`; keep `ClearDeviceForExit` as is.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` — 
  `LoadMacroLibrary` → `LoadMacroLibraryAsync` (fetch `GetMacros` rows off-thread, populate
  `Macros` after the await); `LoadButtonBindings` → `LoadButtonBindingsAsync` (fetch
  `GetButtonBindings` rows off-thread, resolve + populate after); both guard the apply on
  `ReferenceEquals(Device, device)`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — 
  `LoadRgbStateForCurrentDevice` → `LoadRgbStateForCurrentDeviceAsync` (cache lookup on calling
  thread; `GetRgbState` off-thread; suppressed apply after the await, no await inside the
  suppressed region); `PersistRgbStateForCurrentDevice` → returns `Task` (payload + in-session
  cache update on calling thread; `SaveRgbAsync` write inside `Task.Run`, awaited by callers that
  need the rows in place).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — 
  `LoadPresetsForDevice` → `LoadPresetsForDeviceAsync`: seed (`CreateDefaultPresetIfNotExists`),
  `SeedCatalogDefaultDpiAndRate` storage writes, `SavePresetSnapshot`, `GetPresets`,
  `GetAppliedPreset` all inside one `Task.Run` block in current order (inputs pre-captured);
  populate `Presets` + selection after the await. `ApplyPresetToDeviceAsync`: await the async
  loaders; move `LoadPresetSnapshotToDevice` into `Task.Run`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs` — 
  `LoadPersistedMouseSettingsAsync`: wrap the `LoadMouseSettingsAsync` storage read in
  `Task.Run` (fixes both the SetDevice and preset-apply call sites; the existing
  `ReferenceEquals(Device, device)` guard already handles staleness).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Audio.cs` — in
  `LoadAudioSettingsAsync` (called by `RebuildAudioSettingsAsync`), wrap the settings-store read
  in `Task.Run`; the existing `_audioRebuildVersion` guard already handles staleness.
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — add `AcquireLock()` funnel +
  `internal Action? OnBeforeStorageAccess`; mechanically replace bare `_lock.Wait()` calls.
  (`MigrateGuestToUserAsync`'s `WaitAsync` path also routes through the callback via an async
  funnel or a direct invoke before `WaitAsync` — coder's choice, behavior identical.)
- `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — `OnDeviceDetailsRequested` kicks
  `SetDeviceAsync` (logged, not awaited) and shows the view immediately.
- `tests/ProoveGaming.App.Tests/DeviceDetailsSetDeviceThreadingTests.cs` — **new**; the
  reproducing test plus the rapid-switch (last-wins) test; written and confirmed failing before
  the production change lands (see Test strategy).
- `tests/ProoveGaming.App.Tests/{ArtworkKeyOverlayGateTests, DeviceDefaultsTests,
  DeviceDetailsAudioPresetTests, DeviceDetailsKeyAssignmentDialogTests,
  DeviceDetailsKeyboardMacroBindingTests, DeviceDetailsMacroLibraryScopeTests,
  DeviceDetailsPresetApplyTests}.cs` — mechanical update of all 54 `vm.SetDevice(...)` call sites
  to `await vm.SetDeviceAsync(...)` (test methods become `async Task` where not already); no
  assertion changes — these suites are the characterization coverage that must stay green.

## Acceptance criteria

- [ ] Given a `DeviceDetailsViewModel` with an in-memory `LocalStorageService` whose
  `OnBeforeStorageAccess` records thread ids, when `SetDeviceAsync(device)` is awaited from a
  test thread, then at least one storage access occurred and none occurred on the calling thread.
- [ ] Given the same setup, when `SetDeviceAsync` completes, then `Presets`, `Macros`, and button
  assignments hold the same values the old synchronous `SetDevice` produced (existing suites
  assert this — all 7 updated test files pass unmodified except for the await syntax).
- [ ] Given `SetDeviceAsync(A)` started and not yet complete, when `SetDeviceAsync(B)` is started
  and both are awaited, then `Device`, `Presets`, `Macros`, and bindings all describe B, and no
  collection contains a mix of A's and B's rows.
- [ ] Given a fresh device (no presets in storage), when `SetDeviceAsync` completes, then the
  default preset exists, is selected, and its snapshot contains the device_rgb rows resolved
  during the same open (the seed-ordering invariant — covered by existing
  `DeviceDefaultsTests` / preset tests staying green).
- [ ] Given the running app on the Home view with a connected device, when the user clicks the
  device card, then the Details view is visible on the next frame with device name and image
  (manual QA — see below), and preset/macro/RGB content fills in without a frozen window.
- [ ] Given `dotnet test ./src/ProveGaming.slnx`, then all tests pass.

## Test strategy

Change kind: **bug fix** (`fix-*`) — the reproducing test is written **first** and must fail on
the current code.

- **Reproducing test (first, fails today):**
  `DeviceDetailsSetDeviceThreadingTests.SetDeviceAsync_RunsStorageAccessOffTheCallingThread` in
  `tests/ProoveGaming.App.Tests`. Builds the VM exactly as `DeviceDetailsMacroLibraryScopeTests.NewVm`
  does (in-memory store, fake HID, fake owner), hooks `OnBeforeStorageAccess` to record
  `Environment.CurrentManagedThreadId`, awaits `SetDeviceAsync`, asserts accesses happened and
  none on the calling thread. On current code every access is on the calling thread → red.
  (Requires the `AcquireLock` seam to land together with the test; the seam is behaviorally
  inert, so "test first" means: seam + test in one commit, red; async refactor next, green.)
- **New rapid-switch test:**
  `SetDeviceAsync_SecondDeviceWins_WhenSwitchingBeforeFirstLoadCompletes` — start
  `SetDeviceAsync(mouse)` without awaiting, immediately start `SetDeviceAsync(keyboard)`, await
  both, assert the VM describes the keyboard only (macros scoped per device make contamination
  observable — reuse the fixtures from `DeviceDetailsMacroLibraryScopeTests`).
- **Characterization coverage (must stay green):** the 7 existing suites already cover the
  behavior this refactor must not change — macro library scoping and macro-before-bindings
  ordering (`DeviceDetailsMacroLibraryScopeTests`), default seeding and DPI/rate rules
  (`DeviceDefaultsTests`), preset apply/revert (`DeviceDetailsPresetApplyTests`), audio presets,
  key-assignment dialog, artwork overlay gating. They are updated mechanically to
  `await SetDeviceAsync` and their assertions are untouched.
- **Deliberately not covered by automated tests:** real UI-thread affinity under the Avalonia
  dispatcher (tests run without one — the design avoids `Dispatcher` calls specifically so the
  await-context behavior is what tests exercise); perceived navigation latency; real HID/gRPC
  traffic. These are covered by manual QA.

## QA (manual test steps)

1. Launch the app and connect at least one supported device (e.g. a BLAZE or TAIPAN mouse).
2. From the Home screen, click the device card. Expected: the Details screen appears instantly
   (no freeze, no white/hung frame); device name and picture are visible immediately; the presets
   list, macros, and RGB controls appear right after without the window stuttering.
3. Press Back, then click into the same device again 5–10 times quickly. Expected: every
   navigation is instant; the app never hangs; the correct device is always shown.
4. Connect two different devices. Open device A's details, press Back, immediately open device B.
   Expected: B's page shows only B's presets, macros, and lighting — nothing from A.
5. On a device never opened before (or after clearing app data): open its details. Expected: a
   "Default" preset appears and is selected; the device lights up with its default lighting.
6. Open a device's details, change an RGB colour, go Back, reopen the same device. Expected: the
   colour you picked is still shown and still active on the device.
7. Open details for a keyboard (e.g. Raster): assign a macro to a key, go Back, reopen.
   Expected: the assignment is still there.
8. While on the Details page, unplug the device. Expected: no crash; the page reflects the
   disconnect as it did before this change.

## Review focus

- `UI-thread affinity` — no storage call left on the UI thread in the SetDevice path; no new
  `Dispatcher` usage; suppression flags never span an await.
- `async/await` — no sync-over-async; every `Task.Run` result awaited; no fire-and-forget that
  tests then race against; `SetDeviceAsyncSafe` observes exceptions.
- `thread-safety` — background block touches only `LocalStorageService`/`SettingsRepository` and
  captured locals; `_rgbStateByDevice` and other VM state only on the calling thread; staleness
  guards after every await.
- `performance hot-path` — the prologue stays allocation-light; no per-frame path touched.

## Performance & quality

- The hot path here is navigation latency, not a per-frame loop. Target: zero storage I/O on the
  UI thread during `SetDeviceAsync`; the prologue is pure in-memory VM work.
- `LocalStorageService._lock` already serializes all connection use; background fetches take and
  release it per call, so any remaining UI-thread storage call (debounced saves) waits at most
  one call's duration, not the whole batch. `MigrateGuestToUserAsync` (long transaction) never
  overlaps device opens in practice (it runs during sign-up).
- No new allocations of note: the fetch blocks return the same record lists the loaders already
  materialize today.
- Thread hops: 5–7 `Task.Run` dispatches per device open — negligible versus the removed I/O.
- `ConfigureAwait` is not used in the App project today; continuations must return to the calling
  context, so none is added (matches convention).

## Risks

- **Interleaving windows.** Between awaited loaders the UI is live; a very fast user could touch
  RGB/preset controls before the load applies. Guards (`ReferenceEquals`, generation, existing
  suppress flags with save/restore) bound the damage to "last write wins", same class of race the
  existing fire-and-forget tail already tolerates. Watch during review for any apply step missing
  its guard.
- **Missed call site.** Renaming `SetDevice` → `SetDeviceAsync` makes the compiler find every
  caller; the risk is a test awaiting nothing (`_ = vm.SetDeviceAsync(...)`) and going flaky —
  reviewer should grep for discarded `SetDeviceAsync` tasks in tests.
- **Comment drift.** Several load-bearing comments assert write-through-synchronous ordering
  ("rows are in place for the snapshot on the very next line"). They remain true *within* the
  background block, but the coder must update the wording where the guarantee moved threads.
- **`OnBeforeStorageAccess` funnel.** A `_lock.Wait()` site missed during the mechanical rename
  would under-report in the threading test; reviewer should verify no bare `_lock.Wait()`
  remains.
- **Faulted loads.** A SQLite error mid-open now surfaces via the logged wrapper instead of a
  synchronous throw; the page could show partially-loaded state. Same end-user impact as today's
  crash-path, strictly less severe, but worth a log-message check in review.

## Open questions

- None blocking. (If the team prefers not to touch ~40 `_lock.Wait()` sites for the test seam,
  the fallback is asserting thread ids via the `ICurrentOwnerProvider` fake — weaker but
  zero-production-change; the plan recommends the funnel for a durable seam.)

## Review findings

### Round 1 — Ship: yes

#### Plan conformance
Plan: `docs/plans/fix-details-navigation-delay/fix-details-navigation-delay-plan.md` (this file)
- Implemented as planned: `SetDeviceAsync` with synchronous prologue + awaited loader chain and
  `_setDeviceGeneration` staleness guard (`DeviceDetailsViewModel.cs:982–1096`); off-thread
  fetch for macros / button bindings / RGB state / RGB persist / presets seed+list
  (`Buttons.cs`, `Rgb.cs`, `Presets.cs`); `AcquireLock()` funnel + `OnBeforeStorageAccess` seam
  covering all 52 sites plus `MigrateGuestToUserAsync`'s `WaitAsync` and `Dispose`
  (`LocalStorageService.cs:31–48`); MainWindow shows the view before awaiting the load
  (`MainWindow.axaml.cs:489–495`); new `DeviceDetailsSetDeviceThreadingTests` (reproducing +
  rapid-switch tests); all planned test files updated to `await SetDeviceAsync`.
- Deviates from plan (all three are deliberate, verified, and argued from the plan's own
  acceptance criterion that existing suites stay green with only await-syntax changes — but the
  plan text is now stale; see S2):
  1. **Preset-apply path stays synchronous.** The plan had `ApplyPresetToDeviceAsync` awaiting
     the async loaders and moving `LoadPresetSnapshotToDevice` into `Task.Run`. Doing so made 39
     command-driven tests (Actuation/PresetApply/PresetsViewModel suites) race a disposed store —
     they execute `SelectPresetCommand.Execute(...)` and rely on the documented synchronous
     completion of fakes. The code keeps sync twins (`Buttons.cs:167` `LoadButtonBindings`,
     `Rgb.cs:872` `LoadRgbStateForCurrentDevice`) built on shared fetch/apply cores.
  2. **`Settings.cs` / `Audio.cs` `Task.Run` reverted** (net-zero diff on both files). Instead
     the `RebuildDeviceSettings` / `RebuildAudioSettingsAsync` kicks moved past the chain's first
     real yield (`DeviceDetailsViewModel.cs:1053–1057`).
  3. **The fire-and-forget tail is now sequentially awaited** through `Quietly`
     (`DeviceDetailsViewModel.cs:1074–1096`) rather than keeping `_ =` kicks — same order and
     same swallowed-failure semantics, but `SetDeviceAsync`'s task now covers the whole open,
     which is what makes the updated tests deterministic. Side effect worth knowing: the five
     follow-ups now run strictly sequentially in the app too, so e.g. a slow
     `RefreshFromDeviceAsync` matrix probe delays `EnsureDefaultPresetAppliedAsync` instead of
     racing it. No correctness issue found; behavior is more deterministic than before.
- Missing / partial: plan's test-file list missed `DeviceDetailsPresetsViewModelTests.cs`
  (~15 direct `LoadPresetsForDevice()` call sites); it was found and updated anyway.
- Acceptance criteria: the threading test asserts the **pre-await window** ("no storage access
  recorded on the calling thread before the returned task is awaited") rather than the
  criterion's literal "none occurred on the calling thread" — a sound refinement, because after
  the first await the calling thread legally returns to the pool and may pick up `Task.Run`
  work. All other automated criteria verified: 762 App + 610 Infrastructure tests green,
  rapid-switch last-wins test present, seed-ordering suites green. Manual QA checklist not yet
  executed.

#### Code
- [x] **S1. Same-card reopen during an in-flight load can duplicate the macros list** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs:137` — Should-fix
  **Problem:** The loader-level staleness guards check only `ReferenceEquals(Device, device)`;
  the generation check lives one level up, in `SetDeviceAsync`'s `Stale()`
  (`DeviceDetailsViewModel.cs:1040`), and runs only *after* a loader has already applied.
  **Failure scenario:** double-click on a device card (or Back + reopen within a few ms) starts
  chain 1 and chain 2 for the *same* card instance. Chain 2's `LoadMacroLibraryAsync` clears
  `Macros` and starts its read; chain 1's pending macros continuation then lands, passes the
  `ReferenceEquals` guard (same instance!), and appends its rows; chain 2's continuation appends
  the same rows again → every macro appears twice in the UI. Presets self-heal (the newer
  loader's clear runs later), and the binding dictionaries are overwritten idempotently — the
  macros list is the one observable corruption. Narrow window (the SQLite read's in-flight
  time), self-heals on next open, no stored data touched — hence Should-fix, not Blocker.
  **Solution options:**
  - Option A (correct): pass the chain's `generation` into the async loaders and guard
    `generation == _setDeviceGeneration && ReferenceEquals(Device, device)` before applying.
    `LoadPresetsForDeviceAsync` is called from tests directly — give it an optional
    `int? generation = null` that skips the check.
  - Option B (precedented): per-loader version counter like `_audioRebuildVersion`
    (`DeviceDetailsViewModel.Audio.cs:55`), bumped at each loader entry — same effect, no
    signature changes, at the cost of one counter per loader.
- [x] **S2. Plan text is stale against the shipped design** — `docs/plans/fix-details-navigation-delay/fix-details-navigation-delay-plan.md` — Should-fix
  *(closed by the "As-built deviations" section added before the File-level change list,
  which also folds in S3's acceptance note)*
  **Problem / why it matters:** the Recommended approach (§7 preset-apply, §File-level entries
  for `Settings.cs` / `Audio.cs`, the "tail keeps fire-and-forget shape" scope note) now
  contradicts the implementation. A future `/coder` round reading the plan cold could "fix" the
  code back toward the stale plan and re-break the 39 tests.
  **Fix:** amend the plan's Recommended approach and File-level change list to record
  deviations 1–3 above as accepted design (one short "as-built" note per item suffices).
- [ ] **S3. Residual UI-thread SQLite reads via the in-chain rebuild kicks** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:1053–1057` — Should-fix
  **Problem / why it matters:** in the app, the chain's continuations run on the Avalonia
  dispatcher, so `RebuildDeviceSettings` / `RebuildAudioSettingsAsync` still perform their
  write-through settings reads (2 small indexed rows each) on the UI thread — after the view is
  already rendered, so no visible stall, but the plan's stated goal was zero storage I/O on the
  UI thread during `SetDeviceAsync`. Offloading them inside the shared methods is blocked by
  the preset-apply sync contract (deviation 1).
  **Fix:** accept and document (fold into S2's plan amendment), or as a follow-up split these
  two loads fetch/apply like the other loaders so only the SetDevice chain offloads them.
  Bounded risk either way.

#### Tests
- [x] No coverage for the S1 scenario — same-instance rapid reopen during an in-flight load
  (double-click). Closed by
  `DeviceDetailsSetDeviceThreadingTests.SetDeviceAsync_ReopeningTheSameCard_DoesNotDuplicateMacros`,
  written red-first (failed with "collection contained 2 items" on the pre-fix code).
- Note (pre-existing, not this branch): CI (`dotnet-ci.yml`) builds only the App csproj and
  never runs `tests/` — the 1,372-test green run exists only locally.

#### Nits
- `Quietly` swallows with a bare `catch { }` and no trace — parity with the old unobserved
  discard is documented, but a `Debug.WriteLine` would cost nothing —
  `DeviceDetailsViewModel.cs:1092`.
- `PumpingSyncContext`'s `BlockingCollection` is never disposed (test-only, process-lifetime) —
  `tests/ProoveGaming.App.Tests/DeviceDetailsSetDeviceThreadingTests.cs:196`.
- The 8 edited test files were renormalized to LF line endings (git warns "LF will be replaced
  by CRLF"); harmless — git renormalizes on commit — but avoidable churn.

#### What's done well
- The fetch/apply split per loader keeps every load-bearing ordering comment attached to the
  code it describes, and gives the preset path its sync contract without duplicating logic.
- `AcquireLock()` as a single funnel makes the threading seam durable and covers `Dispose` and
  the async migration path too.
- Suppression flags (`_suppressAutoApply`, `_suppressPresetDirty`) provably never span an await
  in the new code — the exact trap the pre-existing comments warn about.
- The rapid-switch test's `PumpingSyncContext` honestly models the dispatcher instead of
  hand-waving the serialization assumption; the reproducing test's pre-await window design is
  immune to thread-pool reuse false positives.

#### Verdict
Ship: yes — no blockers; S1 is a narrow, self-healing UI glitch with a clear fix, S2/S3 are
documentation hygiene on deliberate deviations. First: (1) S1 generation-aware loader guards +
its test, (2) S2 plan amendment folding in S3's acceptance note, (3) run the manual QA
checklist on real hardware before merge.

### Round 2 — Ship: yes

Verification round for the S1 fix, plus two additions made after Round 1 at the user's
request.

- **S1 verified closed.** The chain generation is threaded into all four async loaders
  (`LoadMacroLibraryAsync` / `LoadButtonBindingsAsync` — `DeviceDetailsViewModel.Buttons.cs`;
  `LoadRgbStateForCurrentDeviceAsync` — `DeviceDetailsViewModel.Rgb.cs`;
  `LoadPresetsForDeviceAsync(int? generation = null)` — `DeviceDetailsViewModel.Presets.cs`),
  each guarding `generation != _setDeviceGeneration || !ReferenceEquals(Device, device)`.
  The reproducing test
  (`DeviceDetailsSetDeviceThreadingTests.SetDeviceAsync_ReopeningTheSameCard_DoesNotDuplicateMacros`)
  was confirmed red on the pre-fix code ("collection contained 2 items") and is green now.
  Sync preset-path variants correctly skip the check (they have no await gap).
- **Accepted scope addition: `[NavPerf]` navigation timing** in
  `MainWindow.OnDeviceDetailsRequested` (+ `ILogger<MainWindow>` ctor parameter, resolved by
  the existing `AddLogging`). Diagnostic requested by the user; it located both remaining
  bottlenecks. Keep-vs-strip before merge is an open pre-merge decision.
- **Follow-up perf work** (overlay rebuild coalescing + lazy visibility-gated construction in
  `RasterKeyboardOverlayView`) is reviewed in its own plan:
  `../refactor-lazy-keyboard-overlays/refactor-lazy-keyboard-overlays-plan.md`.
- S2 (this plan's text amended to record the as-built deviations) and S3 (accept-and-document
  the residual UI-thread settings reads) remain open.

Ship: yes — unchanged from Round 1; S2 remains the outstanding documentation item.
