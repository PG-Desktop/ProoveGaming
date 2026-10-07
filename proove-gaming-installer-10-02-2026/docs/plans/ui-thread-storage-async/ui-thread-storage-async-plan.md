# ui-thread-storage-async — move synchronous SQLite off the UI thread

> **Note, 2026-09-24:** the remaining UI-thread storage writes in sign-in, lockout, saved-auth and device delete were moved onto this plan's executor by `docs/plans/fix-repository-audit-findings/` (A4). Device add during sign-in (`PersistDeviceCard` from `ReloadOwnerScopedState`) is still synchronous; that plan's A4b records why.

Fixes the confirmed "synchronous SQLite on the UI thread" family from
[`docs/audits/2026-08-21-performance-memory-audit.md`](../../audits/2026-08-21-performance-memory-audit.md):
findings **C3, C4, C5, C6, C17**.

## Goal

Every storage path this plan touches currently executes blocking SQLite I/O (plus a blocking
`SemaphoreSlim.Wait()`) on the Avalonia dispatcher thread: the `SettingsRepository` async facade
completes synchronously (C3), preset save/switch runs multi-table transactions inline in
RelayCommands (C4), the guest→user migration transaction runs on whatever thread awaits it —
the UI thread (C5), the app shell and `HomeViewModel` call the synchronous storage API directly
(C6, C17). The repo rule is explicit: the UI thread must never run blocking I/O, and any visible
hitch is a bug. The goal is to move this I/O to a background thread **without breaking the
ordering guarantees the current design gets for free** from everything running sequentially on
one thread, and without introducing re-entrancy storms.

## Scope

**In scope (App project only):**

- A serialized background storage executor added to `LocalStorageService` (FIFO task chain,
  completion-representing tasks, drain support).
- `SettingsRepository` — all methods become genuinely asynchronous by routing through the
  executor (C3), plus a batched `SetDeviceSettings` write so restore-defaults is one
  transaction instead of 4×N auto-committed statements.
- Preset save (`ConfirmSavePreset`) and preset switch (`ApplyPresetToDeviceCoreAsync`) —
  `SavePresetSnapshot` / `LoadPresetSnapshotToDevice` / `SetAppliedPreset` move to the
  executor (C4).
- `MigrateGuestToUserAsync` — body executes on the executor (pool thread), ordered after any
  pending queued writes (C5), plus the `ClearSession`/`ClearData` calls in the same sign-in
  flow.
- `HomeViewModel` notification and session-flush paths (C17): `AddNotification` (with its
  synchronous `StorageId` consumption), `SetNotificationRead` from a `PropertyChanged`
  handler, mark-all-read (batched into one `UPDATE`), delete-one/delete-all,
  `LoadNotificationsForOwner`, `FlushSession`.
- App-exit drain so queued writes reach disk before `LocalStorageService.Dispose()`.

**Out of scope (deliberately deferred — see Risks/Open questions):**

- The `App.axaml.cs` startup reads (`GetSavedAuth`, `GetSessionState`, `GetDevices`) — they run
  before `MainWindow` exists; pure startup latency, no visible hitch (per the audit panel).
- `MainWindow` single-row auth calls outside the migration flow (`SaveAuth`,
  `GetLockoutUntil`, `SaveLastSignIn`, …) — click-gated single-row ops, 1–10 ms.
- `HomeViewModel` single-row device ops whose returned id is consumed immediately
  (`_localStorage.AddDevice` on the signed-in path, `DeleteDevice`) — bounded single-row
  writes; converting them ripples the `StorageId`-continuation pattern into the card delete
  flow for marginal gain. The guest path's heavy write (`Flush`) *is* converted.
- Converting the rest of `LocalStorageService`'s ~50 public methods or its other callers —
  `DeviceDetailsViewModel` already offloads its reads via `Task.Run` (the `SetDeviceAsync`
  pattern); those keep working unchanged.
- Enabling WAL journal mode (would cut fsync cost further) — separate decision, see Open
  questions.
- No new NuGet package, no DeviceHost/Installer/Common changes.

## Options considered

### Option A — full async conversion of `LocalStorageService` (WaitAsync + `Sqlite*Async` APIs)

Convert all ~50 public methods to `async`, take the lock with `WaitAsync`, use
`ExecuteNonQueryAsync` etc., propagate `await` to every caller.

- **Fatal flaw:** Microsoft.Data.Sqlite executes its async APIs synchronously (the provider has
  no async I/O) — finding C5 is the proof: `MigrateGuestToUserAsync` is already fully "async"
  and still freezes the UI. The I/O would still run inline on the dispatcher; only the lock
  wait would yield.
- Several call sites are `PropertyChanged` handlers or sync `RelayCommand`s that cannot await;
  a `s/sync/async/` sweep would push them into fire-and-forget with no ordering guarantee.
- ~3190-line churn in one file plus every caller — unreviewable in one pass.

Rejected.

### Option B — per-call-site `Task.Run` (extend the existing `SetDeviceAsync` pattern)

Wrap each UI-thread storage call in `await Task.Run(() => _localStorage.X(...))`, the pattern
`DeviceDetailsViewModel` already uses for reads.

- Works for **awaited** calls (ordering preserved by the await chain).
- **Loses ordering for fire-and-forget writes**: two `_ = Task.Run(write)` posts can be picked
  up by different pool threads and reach `_lock.Wait()` in either order (`SemaphoreSlim` is
  not FIFO). Concrete breakages: two debounced saves of the same settings key can apply
  oldest-last; `PersistDisplayedDeviceState`'s stage/rate/RGB writes could land *after* the
  `SavePresetSnapshot` that must include them — reintroducing the exact
  "preset saved without its DPI stage" bug the code comments document as a past regression.
- `AddNotification`'s synchronous `StorageId` consumption still needs bespoke handling.

Rejected as the general mechanism; kept as the mechanism for independent one-off reads.

### Option C — serialized background storage executor on `LocalStorageService` (recommended)

A single FIFO execution chain owned by `LocalStorageService`: callers enqueue delegates
(`Action`, `Func<T>`, or `Func<Task>`); items run one at a time, in enqueue order, on thread
pool threads; each returned `Task` completes **only when the delegate has actually run** (not
when enqueued), so awaiting it is a completion barrier and fire-and-forgetting it still keeps
FIFO order. Implementation is a task chain (`_tail = _tail.ContinueWith(...)` under a small
lock, `TaskScheduler.Default`), so it needs no dedicated thread, no channel, no new package.
An exception in one item faults that item's task (logged at fire-and-forget sites) but never
breaks the chain.

- Ordering (constraint **a**): all ordering-sensitive writes go through one FIFO queue — the
  global `SemaphoreSlim` semantics for UI-originated writes are preserved exactly.
- `PropertyChanged` handlers (constraint **b**): they *enqueue* (a non-blocking list append)
  and return — no await needed, ordering still guaranteed.
- Re-entrancy (constraint **c**): completions that must touch VM state marshal back via
  `Dispatcher.UIThread.Post` and only set non-notifying state (`StorageId`) or are guarded by
  generation counters — no bound-property writes from storage continuations.
- The existing `_lock` stays: executor items still take it, so they remain correctly mutually
  excluded against the `Task.Run` readers elsewhere (`DeviceDetailsViewModel`), which keep the
  same semantics they have today.

## Recommended approach

Option C, applied per finding:

### 1. The executor (in `LocalStorageService`)

```
public Task RunOnStorageAsync(Action work)
public Task<T> RunOnStorageAsync<T>(Func<T> work)
public Task RunOnStorageAsync(Func<Task> work)      // for MigrateGuestToUserAsync's body
public Task DrainStorageQueueAsync()                 // completes when everything enqueued so far has run
```

Contract (state it in code comments — callers depend on it):

- Strict FIFO in enqueue order; items never run concurrently with each other.
- The returned task completes only after the delegate has executed (or faulted) — it is a
  write barrier, not an enqueue acknowledgement.
- Items run on pool threads, never the caller's thread.
- A faulted item does not stop later items.
- Enqueue itself is cheap and non-blocking (safe from `PropertyChanged` handlers).

`Dispose()` first drains the queue with a bounded wait (e.g. 3 s) so queued writes are not cut
off mid-flight, then takes the lock and disposes the connection as today. The existing
`OnBeforeStorageAccess` test seam keeps firing for every storage access, which is what the
threading tests key on.

### 2. C3 — `SettingsRepository`

Every `Save*Async`/`Load*Async` routes its existing body through `RunOnStorageAsync` (owner is
resolved on the calling thread first, exactly like the "owner resolved on the calling thread;
the background block touches storage only" pattern in `DeviceDetailsViewModel.Buttons.cs`).
Loads go through the executor too — not `Task.Run` — so a load enqueued after a save of the
same key is guaranteed to observe it.

`SaveDpiPresetsAsync` calls a new `LocalStorageService.SetDeviceSettings(ownerKind, ownerId,
vid, pid, deviceKey, IReadOnlyList<(string Key, double Value)>)` that performs all upserts in
**one transaction** (one commit fsync instead of 4×N). Existing call sites
(`_ = _settings.SaveActiveDpiStageAsync(...)` etc.) need no change — the fire-and-forget now
genuinely offloads, and FIFO keeps last-writer-wins per key correct.

The stale comment in `DeviceDetailsViewModel.Rgb.cs::BuildRgbPersistWrite` ("SaveRgbAsync
writes through to SQLite synchronously…") must be updated: the visibility guarantee now comes
from queue FIFO (snapshot enqueued after the RGB write), not from synchronous completion.
`PersistRgbStateForCurrentDeviceAsync` must return the *executor* task (write-completion), not
`Task.Run(enqueue)` — the seed branch of `LoadPresetsForDeviceAsync` awaits it as a
rows-in-place barrier.

### 3. C4 — preset save / switch

- `ConfirmSavePreset` becomes `async Task` (source-generated `AsyncRelayCommand` — re-entry is
  blocked while executing). Flow: capture owner/device/target on the UI thread →
  `PersistDisplayedDeviceState()` (its writes are now enqueued via `SettingsRepository`) →
  `await RunOnStorageAsync(SavePresetSnapshot)` → cancel debounce / fire device apply as today
  → `await RunOnStorageAsync(SetAppliedPreset)` → the existing UI selection updates → close
  dialog. FIFO guarantees the snapshot sees the stage/rate/RGB rows enqueued just before it —
  the ordering the current synchronous code depends on.
- `ApplyPresetToDeviceCoreAsync`: `LoadPresetSnapshotToDevice` and the trailing
  `SetAppliedPreset` become `await RunOnStorageAsync(...)`. Everything after already awaits in
  sequence, so the load-bearing step order is unchanged. `_presetApplyGate` still serializes
  concurrent applies.

### 4. C5 — migration

`MigrateGuestToUserAsync` keeps its signature but routes its body through
`RunOnStorageAsync(Func<Task>)`. That (a) moves the transaction to a pool thread — the UI
paints during sign-in — and (b) *automatically* orders it after every pending queued guest
write, so no queued write can recreate a guest row after the rekey (the drain is implicit in
FIFO). In the `MainWindow` registration flow, the `_localStorage.ClearSession()` /
`ClearData()` calls adjacent to the migration are wrapped in `await RunOnStorageAsync(...)`
as well (they are already in async handlers).

### 5. C17 — `HomeViewModel`

- **`AddNotification`** stays a synchronous method (its callers are sync handlers and
  dispatcher lambdas): it builds the item, inserts it into the UI collections immediately with
  `StorageId = 0`, and enqueues the `AddNotification` row insert. The completion continuation
  posts to the UI thread and assigns `notification.StorageId` (a plain non-notifying property
  — no PropertyChanged storm). Two races are handled explicitly:
  - *Deleted before persisted:* the delete handler marks the item (e.g. `IsDeleted`) and, if
    `StorageId == 0`, skips the row delete; the assign-continuation sees the mark and enqueues
    `DeleteNotification(newId)` instead of assigning — no orphan rows.
  - *Read-toggled before persisted:* the row is inserted unread; the assign-continuation
    checks `item.IsRead` and enqueues `SetNotificationRead(newId, true)` if it changed
    meanwhile.
- **`OnNotificationItemPropertyChanged`** (`PropertyChanged` handler — cannot await): when
  `StorageId > 0`, enqueue `SetNotificationRead` fire-and-forget; FIFO keeps the last toggle
  as the final state. A new suppression flag (see next point) short-circuits it during bulk
  operations. The handler still writes no bound properties, so no re-entrancy loop.
- **`MarkAllNotificationsReadCommand`**: sets the suppression flag, loops `n.IsRead = true`
  (UI updates as today), clears the flag, then enqueues **one** call to a new
  `LocalStorageService.SetAllNotificationsRead(ownerKind, ownerId)` (single `UPDATE`) —
  replacing up to 100 individual UPDATE+commit cycles, the audit's worst measured path.
- **`DeleteAllNotificationsCommand`** and the per-item delete: UI clears synchronously as
  today; the storage delete is enqueued.
- **`LoadNotificationsForOwner`**: split into a synchronous clear (so an owner switch never
  shows the previous owner's bell) plus an async reload guarded by a generation counter
  (`_notificationsLoadGeneration`, the `_setDeviceGeneration` pattern): the read runs through
  the executor (so it observes any queued writes for that owner), the continuation re-checks
  the generation and inserts on the UI thread. Called fire-and-forget from the constructor and
  the sync `PropertyChanged` handlers; `ReloadOwnerScopedState`'s async callers may await it.
  An internal `Task NotificationsLoaded` seam (mirroring `OpenCoreLoaded`) lets tests await
  completion.
- **`FlushSession`**: enqueues `_localStorage.Flush(session)` fire-and-forget. FIFO keeps the
  whole-table rewrite ordered after the in-memory `AddDevice` bookkeeping that precedes it on
  the UI thread (the enqueue happens after the session mutation, and successive flushes cannot
  reorder).

### 6. Exit path

`App.axaml.cs` `desktop.Exit` handler: before the final `Flush`/dispose, call
`localStorage.DrainStorageQueueAsync().Wait(bounded timeout)`. A blocking wait is acceptable
here and only here: the window is gone, nothing paints, and the alternative is losing queued
writes (the audit panel makes the same allowance for the existing exit-time `Flush`).
`Dispose()` drains again defensively (idempotent).

## File-level change list

Ordered so characterization/reproducing tests land before the functional change (this is a
`refactor`-kind change; see Test strategy).

| # | File | Intent |
|---|------|--------|
| 1 | `tests/ProoveGaming.App.Tests/SettingsRepositoryThreadingTests.cs` (new) | Reproducing tests: `Save*/Load*Async` must not touch storage on the calling thread before the first await (fails on current code); FIFO ordering of consecutive fire-and-forget saves. |
| 2 | `tests/ProoveGaming.App.Tests/LocalStorageServiceExecutorTests.cs` (new) | Executor contract: FIFO order, completion-representing tasks, faulted item doesn't break the chain, `DrainStorageQueueAsync` barrier, migration ordered after pending queued guest writes (no orphan guest rows), migration body not on calling thread (fails on current code). |
| 3 | `tests/ProoveGaming.App.Tests/HomeViewModelNotificationThreadingTests.cs` (new) | Reproducing tests: `AddNotification`'s synchronous part touches no storage on the calling thread (fails on current code); `StorageId` assigned after persist; delete-before-persist leaves no orphan row; read-toggle-before-persist lands; mark-all-read issues one batched write. |
| 4 | `src/ProoveGaming.App/Services/LocalStorageService.cs` | Add the serialized executor (`RunOnStorageAsync` ×3, `DrainStorageQueueAsync`), batch `SetDeviceSettings`, `SetAllNotificationsRead`; `MigrateGuestToUserAsync` routes its body through the executor; `Dispose` drains first. No change to existing sync method bodies. |
| 5 | `src/ProoveGaming.App/Services/SettingsRepository.cs` | Route every method's storage body through `RunOnStorageAsync` (owner resolved on the caller); `SaveDpiPresetsAsync` uses the batched `SetDeviceSettings`. |
| 6 | `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` | Notification paths per §5: enqueue writes, `StorageId` continuation + delete/read races, mark-all suppression flag + batch write, generation-guarded async `LoadNotificationsForOwner` with `NotificationsLoaded` seam, enqueued `FlushSession`. |
| 7 | `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` | `ConfirmSavePreset` → async command awaiting queued `SavePresetSnapshot`/`SetAppliedPreset`; `ApplyPresetToDeviceCoreAsync` awaits queued `LoadPresetSnapshotToDevice`/`SetAppliedPreset`. |
| 8 | `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` | `PersistRgbStateForCurrentDeviceAsync`/`BuildRgbPersistWrite` return the write-completion task from the (now queue-backed) `SaveRgbAsync`; update the stale synchronous-visibility comments. |
| 9 | `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | Registration/sign-in flow: `ClearSession`/`ClearData` adjacent to the migration wrapped in awaited `RunOnStorageAsync`. |
| 10 | `src/ProoveGaming.App/App.axaml.cs` | `desktop.Exit`: bounded drain of the storage queue before the final `Flush` and container dispose. |
| 11 | Existing test files as needed (`HomeViewModelPersistenceTests.cs`, `NotificationGroupingTests.cs`, `SettingsRepositoryTests.cs`, `DeviceDetailsPreset*Tests.cs`, `LocalStorageServiceSessionTests.cs`) | Await the new completion seams (`NotificationsLoaded`, returned tasks, `DrainStorageQueueAsync`) where they previously relied on synchronous completion; assertions unchanged. |
| 12 | `docs/CONTEXT.md` | Add one-line definitions: *storage executor* (the FIFO background queue inside `LocalStorageService`) and any other new term introduced. |
| 13 | `docs/plans/fix-details-navigation-delay/…` and other plans matched by a grep for the edited files | Reconcile per the CLAUDE.md plan-drift rule (grep `docs/plans/` for `SettingsRepository`, `LocalStorageService`, `ConfirmSavePreset`, `LoadNotificationsForOwner` during implementation; update file-level lists/criteria that describe the old synchronous behaviour). |

## Acceptance criteria

- [ ] Given any `SettingsRepository` `Save*`/`Load*Async` call from the UI thread, when it
  runs, then no storage access occurs on the calling thread before the first await
  (observable via the `OnBeforeStorageAccess` seam — same assertion style as
  `DeviceDetailsSetDeviceThreadingTests`).
- [ ] Given a device page with unsaved changes, when the user clicks SAVE in the Save Preset
  dialog, then the command's synchronous part performs zero storage accesses on the dispatcher
  thread, and the saved snapshot still contains the displayed DPI stage, report rate and RGB
  state (queue FIFO — verified by an ordering test).
- [ ] Given a selected preset, when the user switches to another preset, then
  `LoadPresetSnapshotToDevice` executes off the dispatcher thread and the revert semantics
  (existing `DeviceDetailsPresetApplyTests`) are unchanged.
- [ ] Given a guest database with rows in every migrated table, when the guest registers/signs
  in, then the migration transaction executes off the calling thread, and the migrated data is
  identical to today (existing migration/session tests stay green).
- [ ] Given a write enqueued for a guest owner immediately before sign-in, when migration runs,
  then the write lands before the rekey (no guest-owned orphan rows after migration).
- [ ] Given `AddNotification` fires, when it returns, then the notification is already visible
  in the bell, and its `StorageId` is assigned asynchronously; deleting it before the persist
  completes leaves zero rows for it in the notifications table.
- [ ] Given 100 unread notifications, when the user clicks "mark all as read", then exactly one
  storage write executes (the batched `UPDATE`), and after an app restart all 100 load as read.
- [ ] Given any two writes enqueued in order A then B, when both complete, then B observed A's
  effects (FIFO executor test).
- [ ] Given queued writes are pending, when the app exits cleanly, then those writes are on
  disk before the connection is disposed (drain in the Exit handler; test via drain + reopen).
- [ ] Given a storage delegate throws, when later delegates are enqueued, then they still
  execute (the chain survives; the faulted task is observable).
- [ ] No `PropertyChanged` handler awaits, and no storage completion continuation writes a
  change-notifying VM property (only `StorageId`, which raises nothing) — no re-entrant update
  storm (code review check + the existing suites stay green).
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

> **Note (2026-08-23, PR #119 review M2/m2/m4/m5):** four follow-up corrections to the executor
> integration, superseding details above and in the Round 1/2 journal:
> - `RecomputeUnsavedPresetChangesAsync` (the device-open dirty check) now runs through
>   `RunOnStorageAsync` instead of `Task.Run`, so it is FIFO-ordered behind the open chain's
>   backfill writes — a direct read raced them on a second open of the same device and lit
>   SAVE PRESET on an untouched page (M2; regression test
>   `RecomputeUnsavedPresetChanges_WithBackfillStillQueued_EndsClean`).
> - The two-step dirty check's forced-dirty probe is now scoped to the edited device:
>   `SettingsRepository.HasPendingDeviceWrites(vid, pid, deviceKey)` (a per-scope pending-save
>   counter) replaced the executor-wide `LocalStorageService.IsStorageQueueIdle`, which unrelated
>   queue traffic (notifications, another device) could trip. `IsStorageQueueIdle` is removed (m2).
> - `LocalStorageService.Dispose` shortened its defensive drain to 500 ms + 500 ms tail: the Exit
>   handler has already spent the sanctioned 3 s drain, and stacking a second full interval held
>   the UI thread up to ~7 s after the window closed (m4).
> - The teardown drop-guard's `default(T)` result is now an explicit contract:
>   `LocalStorageService.IsShutDown` is public, and every `SettingsRepository.Load*` short-circuits
>   to its null ("nothing saved") result on it instead of enqueueing against a disposing
>   connection (m5).
>
> Two hardening follow-ups after CI run 32644137293 exposed races in the two-step dirty check:
> - Every explicit "flag = clean" decision (selection change, ConfirmSavePreset,
>   ConfirmResetPreset, Discard) goes through `SettlePresetClean()`, which bumps the recompute
>   generation so recomputes scheduled before the decision — which compare against the OLD
>   snapshot — cannot land afterwards and resurrect a stale "dirty".
> - `StagePresetDirtyOnly` probes `HasPendingDeviceWrites` BEFORE the sync compare: a queued
>   save that lands between the compare (pre-write rows) and a probe taken after it would be
>   visible to neither, leaving the button dark for a real edit.

> **Note (2026-08-27, `fix-owner-scoped-state-persistence`):** two notes on the deferral list above.
>
> 1. **`DeleteDevice` changed signature** to `DeleteDevice(ownerKind, ownerId, id)`. It is named in
>    the deferred call sites at lines 47, 435 and 468; it stays **synchronous** and stays deferred —
>    only its owner scoping changed, closing the mismatch with `AddDevice`'s already-scoped
>    delete-by-id. Likewise `ReloadOwnerScopedState`'s synchronous `GetDevices` + per-card
>    `AddDevice` loop stays on the UI thread as this plan deferred it; that pass was reordered to
>    read before it writes, and the read **replaces** the previous one rather than adding a second,
>    so the number of synchronous storage calls per invocation is unchanged.
> 2. **The new pre-owner notification flush rides this executor.** Entries raised before any owner
>    existed are persisted through the existing `PersistNotificationAsync` → `RunOnStorageAsync`
>    path, awaited inside `LoadNotificationsCoreAsync` and never on the dispatcher. FIFO ordering is
>    load-bearing there: it is what guarantees the `GetNotifications` that follows observes the
>    flushed inserts, so each entry comes back exactly once with a real `StorageId` instead of being
>    shown twice. See `docs/plans/fix-owner-scoped-state-persistence/`.
## Test strategy

Kind of change: **refactor of existing behavior** (perf fix, contract preserved). The touched
features already have substantial coverage (`LocalStorageService*Tests`,
`SettingsRepositoryTests`, `HomeViewModelPersistenceTests`, `NotificationGroupingTests`,
`DeviceDetailsPreset*Tests`, `DeviceDetailsSetDeviceThreadingTests`) — those must stay green.
The *threading* behavior has no coverage yet, so reproducing tests land **first** (files 1–3 in
the change list) and fail on current code, mirroring the established style of
`DeviceDetailsSetDeviceThreadingTests` (xUnit v2, in-memory `LocalStorageService("Data
Source=:memory:")`, the `OnBeforeStorageAccess` thread-id seam, `PumpingSyncContext` where
await interleaving matters). All in `tests/ProoveGaming.App.Tests`.

**New tests:**

- `SettingsRepositoryThreadingTests` — (reproducing) each Save/Load never touches storage on
  the calling thread pre-await; two fire-and-forget saves of the same key applied in enqueue
  order; `SaveDpiPresetsAsync` round-trips via the batch write.
- `LocalStorageServiceExecutorTests` — FIFO order across mixed `Action`/`Func<Task>` items;
  returned task completes only after execution (barrier semantics); faulted item does not
  break the chain; `DrainStorageQueueAsync` completes after all prior items; (reproducing)
  `MigrateGuestToUserAsync` runs off the calling thread; a guest write enqueued before
  migration is visible under the user owner afterwards.
- `HomeViewModelNotificationThreadingTests` — (reproducing) `AddNotification` sync part
  storage-free on the calling thread; `StorageId` assigned after drain; delete-before-persist
  → zero rows; read-before-persist → row persisted read; mark-all → single storage access
  (count via the seam) and persisted read-state after reload; owner switch mid-load
  (generation guard) never shows the wrong owner's notifications.
- Preset ordering: extend `DeviceDetailsPresetCoverageTests` (or a sibling) — save-preset with
  a displayed stage/rate and confirm the snapshot contains them when all writes are queued.

**Deliberately not covered by automated tests, and why:** the real Avalonia dispatcher (tests
use `PumpingSyncContext` as its stand-in), actual perceived stall duration and disk fsync
timing (hardware-dependent — manual QA), and the exit-handler drain wiring inside
`App.axaml.cs` (requires the full Avalonia lifetime; the drain primitive itself is unit-tested).

## QA (manual test steps)

1. Install and launch the app without signing in (guest). Connect a Proove mouse (e.g. BLAZE
   or TAIPAN over cable). Expected: the device card appears; the app stays responsive.
2. Open the mouse's device page, change DPI stage, polling rate, RGB color and effect, drag
   the DPI slider quickly back and forth. Expected: no cursor hitching or input stutter while
   dragging; every change is reflected immediately.
3. Open the presets dropdown, make a change (e.g. a different DPI stage), click "Save Preset"
   and confirm SAVE. Expected: the dialog closes and the UI stays fully responsive — no
   freeze, even briefly; the preset shows as saved (no unsaved-changes marker).
4. Switch to another preset, then back. Expected: each switch applies the preset's settings
   (DPI stage, polling rate, RGB) with no UI freeze; a change made but not saved is reverted
   by the switch, exactly as before.
5. Restart the app. Expected: the last saved settings and the applied preset are restored
   exactly (nothing lost, nothing reverted to defaults).
6. As a guest, accumulate data: record a macro, remap a button, change RGB on 2 devices, save
   a preset. Then register a new account from the guest session. Expected: the sign-in/register
   screen never freezes (spinner/typing stays live); after signing in, all guest data (macros,
   bindings, RGB, presets) is present under the account.
7. Plug and unplug a device several times to build up notifications in the bell (or wait for
   low-battery ones). Open the bell, click "mark all as read". Expected: all items turn read
   instantly with no UI freeze; the unread badge clears.
8. With the bell open, delete a single notification the moment it appears, and also use
   "delete all". Expected: items disappear immediately; after an app restart the deleted
   notifications do not come back, and read/unread states are exactly as left.
9. Sign out and back in. Expected: the bell shows only that account's history; the transition
   does not freeze the window.
10. Make several quick settings changes, then immediately exit the app (tray icon → Quit).
    Relaunch. Expected: the last changes made just before exit are all persisted.

## Review focus

- `async/await` — no sync-over-async, returned tasks represent completion not enqueue, no
  `.Wait()/.Result` except the documented bounded drain in the Exit handler.
- `UI-thread affinity` — zero storage access on the dispatcher in the converted paths;
  continuations that touch VM state marshal via `Dispatcher.UIThread.Post`.
- `thread-safety` — FIFO ordering claims hold (especially settings-write → snapshot, queued
  guest write → migration); `_lock` still guards against the untouched `Task.Run` readers;
  no re-entrant `PropertyChanged` loops.
- `performance hot-path` — the enqueue path from `PropertyChanged` handlers stays cheap and
  allocation-light (one delegate + one chain link per write).

## Performance & quality

- **Hot paths:** the enqueue call itself runs on the UI thread inside `PropertyChanged`
  handlers and debounced-save callbacks — it must be O(1), non-blocking (a lock only around
  the tail swap), and allocate only the delegate/state it already needs. No per-frame or
  per-keystroke storage path exists after this change; debounces (300 ms) remain.
- **What must not block the UI thread:** everything this plan converts; the single sanctioned
  blocking wait is the bounded drain in `desktop.Exit`, after the window is gone.
- **Shared state:** the executor serializes all queued work; `_lock` remains the guard between
  queued work and the pre-existing background `Task.Run` readers, exactly as today. The
  executor tail and generation counters are the only new shared fields; both are accessed
  under a lock / on the UI thread respectively.
- **Sync-over-async:** none introduced; `MigrateGuestToUserAsync`'s fake-async Sqlite calls
  now at least run on a pool thread. Allocations: one `Task` + closure per storage operation —
  identical order of magnitude to the `Task.Run` pattern already in use.

## Risks

- **Ordering regressions are the whole game.** Any converted write that bypasses the queue
  (accidental `Task.Run`) can reorder against queued writes — the preset-snapshot-vs-settings
  ordering and the migration-vs-pending-writes ordering are the two known landmines; both have
  dedicated tests.
- **`StorageId` races** (delete/read before persist) are handled by explicit continuations;
  the delete path silently skipping `StorageId == 0` rows today is exactly where an orphan row
  would hide — covered by a test.
- **Tests that assumed synchronous completion** will start failing for the *right* reason
  (e.g. asserting a row exists immediately after `AddNotification`); they must await the new
  seams, not be weakened.
- **Queue starvation/latency:** a long queued item (migration, preset snapshot) delays later
  queued reads (e.g. the notification reload right after sign-in). Bounded — the same work
  serialized under `_lock` today — but the reload now happens strictly after migration, which
  is actually the correct order.
- **Crash-loss window widens slightly:** a write that was previously on disk when the handler
  returned is now on disk milliseconds later. The exit drain covers clean exits; a hard kill
  in the window loses at most the queued tail — same class of loss the audit's C4 "Flushed
  now, not at exit" comment already accepts for crashes mid-write.
- **`ConfirmSavePreset` becoming async** changes command timing (dialog closes after the
  awaited writes). CanExecute/re-entry is covered by `AsyncRelayCommand`, but QA step 3 exists
  to catch any visible double-click or dialog-flicker artifact.

## Open questions

1. **WAL journal mode** (`PRAGMA journal_mode=WAL`) would cut every commit's fsync cost and
   benefit all remaining sync call sites for free — but it changes the on-disk format
   (`-wal`/`-shm` files) and interacts with backup/uninstall. Include here or as a follow-up
   plan? (This plan assumes **follow-up**.)
2. Exit-drain timeout value (proposed 3 s) — acceptable worst-case addition to shutdown?
3. Deferred call sites (App startup reads, single-row auth ops, `AddDevice`/`DeleteDevice`)
   — confirm they stay deferred, or should a follow-up plan (`ui-thread-storage-async-v2`)
   be scheduled to finish the sweep?

## Review findings

_filled in by /reviewer_

### Round 1 — Ship: no (2026-08-22)

#### Plan conformance

Plan: docs/plans/ui-thread-storage-async/ui-thread-storage-async-plan.md

- Implemented as planned: file-list items 1-6, 8-13. The executor (RunOnStorageAsync x3,
  DrainStorageQueueAsync, dispose-drain, _disposed drop-guard), batched SetDeviceSettings /
  SetAllNotificationsRead, full SettingsRepository conversion, migration through the
  executor, HomeViewModel notification paths with both StorageId races closed, mark-all
  suppression flag + single batched UPDATE, generation-guarded notification reload with
  NotificationsLoaded seam, snapshot-based queued FlushSession (new
  Flush(IReadOnlyList<DeviceRecord>) overload), exit-handler bounded drain, sign-in flow
  ClearSession wrapped, three sibling plans + docs/CONTEXT.md reconciled.
- Deviates from plan (acceptable, recorded): (a) AddNotification StorageId completion uses
  await-on-captured-context instead of Dispatcher.UIThread.Post — equivalent for the
  UI-thread callers this path has; (b) the preset dirty check became a two-step
  sync-compare + queued authoritative recompute with an IsStorageQueueIdle probe and a
  transient forced-dirty when the queue is busy (DeviceDetailsViewModel.Presets.cs:129-152)
  — a pragmatic extension of the plan FIFO reasoning; it makes the CLEAR direction of the
  SAVE button eventually-consistent (see Tests T1).
- Missing / partial: item 7 converted ConfirmSavePreset / ApplyPresetToDeviceCoreAsync as
  planned, but three sibling paths in the same file now sit on the wrong side of the queue
  boundary the change introduced — B1 / B2 below.
- Accepted limitation (per plan): App startup reads, single-row auth/device ops (SaveAuth,
  AddDevice, DeleteDevice, ...) and WAL stay synchronous — deferred, not unmet criteria.
  Residual risk: click-gated 1-10 ms stalls remain on those paths.

#### Code

- [x] **B1. New-preset and reset-preset snapshots race the queued staging writes** —
  src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:1313-1317,
  1353-1354 (AppendNewPresetAsync) and 1486-1490, 1497-1499 (ConfirmResetPreset) — Blocker
  **Problem:** Both methods call PersistDisplayedDeviceState() — whose stage/rate/RGB writes
  are now ENQUEUED on the storage executor — and then call SavePresetSnapshot synchronously
  on the calling (UI) thread, followed by a sync SetAppliedPreset. The sync call contends
  directly for _lock and almost always beats the queued items.
  **Why it blocks:** Create a new preset (or Reset a preset) while a DPI stage / report
  rate / RGB state is displayed: the snapshot copies the pre-write rows, so the preset is
  saved without its stage/rate/RGB. Re-selecting it later reverts those aspects to
  nothing/defaults, and PresetMatchesLiveState mismatches light the SAVE button on an
  untouched page — exactly the regression the plan Risks section names as the landmine.
  These two paths also still run multi-table SQLite inline on the dispatcher (the C4 stall).
  **Solution options:**
  - Option A (correct, matches the ConfirmSavePreset conversion): capture owner/device
    fields before any await, then await localStorage.RunOnStorageAsync(...) for
    InsertPreset / SavePresetSnapshot / PromotePresetToDefault / SetAppliedPreset — FIFO
    orders the snapshot behind the staging writes and takes the I/O off the dispatcher.
  - Option B (quick, ordering only): await localStorage.DrainStorageQueueAsync() before the
    sync snapshot — fixes the data loss but keeps the UI-thread SQLite stall.
- [x] **B2. Seeded default preset snapshot races the queued stage/rate seeds; comment now false** —
  src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:1718-1727
  (seed branch of LoadPresetsForDeviceAsync), stale comment at 1714-1717 — Blocker
  **Problem:** Inside the Task.Run block, settings.SaveActiveDpiStageAsync /
  SavePollingRateAsync are fired-and-forgotten (now enqueued), and SavePresetSnapshot runs
  directly on the very next line. The adjacent comment still claims SettingsRepository
  writes straight through to SQLite so the rows are in place — no longer true. (The
  analogous path in DeviceDetailsViewModel.cs:656-697 WAS fixed by enqueueing
  BackfillPresetSnapshotFromLive; this one was missed.)
  **Why it blocks:** First open of a device (fresh scope) seeds a default preset whose
  snapshot can miss the stage/rate rows: the default preset misrepresents the device and
  the dirty check lights SAVE on a page the user never touched — the precise bug this seed
  code exists to prevent.
  **Solution options:**
  - Option A (minimal, correct): await the two Save*Async tasks before calling
    SavePresetSnapshot (the block already runs off the UI thread; make the lambda async).
    Update the comment.
  - Option B: enqueue the snapshot behind the saves via RunOnStorageAsync and await it.
- [x] **S1. OnSessionExpired wipe can be overtaken by queued guest writes** —
  src/ProoveGaming.App/Views/MainWindow.axaml.cs:328, 332 — Should-fix
  **Problem / why it matters:** ClearSession() / ClearData() run synchronously while guest
  writes (e.g. a queued FlushSession or notification insert) may still sit in the executor
  queue; a queued write that lands after ClearData resurrects wiped guest rows —
  re-introducing the PR #112 m1 class of bug probabilistically. Pre-change, full
  synchronicity made this impossible. (These calls are outside the plan stated in-scope
  list — the ordering hazard, however, is introduced by this change.) They also remain
  blocking multi-table I/O on the UI thread.
  **Fix:** Route both through the executor (fire-and-forget enqueue is fine in this sync
  handler; FIFO restores wipe-after-pending-writes ordering), mirroring the sign-in flow
  RunOnStorageAsync(_localStorage.ClearSession) conversion.

#### Tests

- [x] **T1.** DeviceDetailsPresetApplyTests.StageChangeAndApply_UpdatesSaveButtonStateImmediately_BeforeTheDeviceWrite
  (tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs:1248) is flaky under
  full-suite load (passes isolated, failed in the full run): after the revert edit, the
  two-step dirty check deliberately forces HasUnsavedPresetChanges = true while the storage
  queue is busy and only the queued recompute clears it — the immediate Assert.False races
  that recompute. Fix in the test: drain the storage queue / pump until the scheduled
  recompute settles before asserting the CLEAR direction (do not weaken the assertion; the
  enable direction and the no-device-write-yet asserts stay as-is).
- [x] **T2.** No coverage for the B1/B2 paths: add reproducing tests asserting the
  new-preset (AppendNewPresetAsync), reset-preset (ConfirmResetPreset) and seeded
  default-preset snapshots contain the displayed/seeded DPI stage, report rate and RGB when
  all staging writes go through the queue —
  tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs or a sibling. They
  should fail before the B1/B2 fixes and pass after.

#### Nits

- RunOnStorageAsync<T> returns default! once _disposed — a reference-typed read (e.g.
  GetNotifications) yields null during shutdown and LoadNotificationsCoreAsync would NRE on
  records.Count; harmless in practice (unobserved task at teardown), but a null-coalesce
  would make the drop-guard airtight — src/ProoveGaming.App/Services/LocalStorageService.cs
  (RunOnStorageAsync<T>), src/ProoveGaming.App/ViewModels/HomeViewModel.cs
  (LoadNotificationsCoreAsync).
- FlushSession snapshots the LIST (ToArray) but shares the DeviceRecord instances with the
  executor; a UI-thread field mutation between enqueue and run is observed by the write.
  Fields are simple types (no torn reads) and persisting the newer value is benign — noting
  for completeness — src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1914-1924.

#### What is done well

- The executor is exactly the plan contract, with disposal handled better than the plan
  asked: drain, then flip _disposed, then drop stragglers instead of faulting through
  awaiters.
- Both StorageId races (deleted-/read-toggled-before-persisted) are closed precisely as
  designed, and IsDeleted is deliberately non-notifying — no re-entrancy storm.
- Flush(IReadOnlyList<DeviceRecord>) snapshot overload kills a mutation race the plan did
  not even call out.
- The stale synchronous-visibility comments in Rgb.cs and DeviceDetailsViewModel.cs were
  updated to state the new FIFO-based guarantee — exactly the drift-prevention the repo
  rules demand.

#### Verdict

Ship: no — B1 and B2 re-create the plan named landmine (snapshots missing queued
stage/rate/RGB) on the three preset paths the conversion skipped; the fix is the same
pattern already applied to ConfirmSavePreset. First: 1) B1, 2) B2 (+ comment), 3) T1/T2,
then S1.
