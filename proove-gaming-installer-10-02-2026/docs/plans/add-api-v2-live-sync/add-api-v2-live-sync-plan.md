# add-api-v2-live-sync — Ongoing two-way sync while signed in

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Phase 2 of [add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md).
Depends on [add-api-v2-pull](../add-api-v2-pull/add-api-v2-pull-plan.md) (pull pipeline, list/get
API surface) and [add-api-v2-sync](../add-api-v2-sync/add-api-v2-sync-plan.md) (uid columns,
create push).

## Goal

Make the server follow the signed-in user's edits: creating, editing or deleting a preset or
macro, changing app preferences, or applying a preset pushes the change to API v2.0 with
`updated_at` conflict handling — so a second machine's sign-in pull sees current data. This
also closes the two accepted gaps of the upgrade push: an interrupted push now converges on
the next sync pass, and macros under device types without a device card are pushed because
the pass enumerates dirty rows, not device cards.

## Scope

**In:**
- Local change tracking: `is_dirty` + `local_rev` on `presets`/`macros`, a
  `sync_tombstones` table for deletes, a `sync_meta` table for preferences and per-type
  device-state `updated_at` + dirty marks.
- A `SyncCoordinator` background service: debounced push pass over dirty/unsynced rows,
  tombstones, preferences and device state; a reconcile pass on sign-in (after the pull).
- `SyncApiService` methods for `presets/update`, `presets/delete`, `macros/update`,
  `macros/delete`; `preferences/update` and `devices/update` gain `updated_at`.
- Conflict handling per the umbrella policy: client wins with one refresh-retry.

**Out:**
- Pull-side merging of diverged records (pull stays additive; a record that diverged on two
  machines converges to whichever machine pushes last — accepted last-writer-wins).
- Any UI (no sync status indicator; log-only).
- Guest rows (never tracked, never pushed).
- Per-unit device state (API limitation; the collapse rule from the upgrade push stands).

## Options considered

### A. Push inline from every ViewModel mutation site
Call the API directly where `SaveMacro`/`SavePresetSnapshot`/etc. are invoked.
- Pros: no new tracking state.
- Cons: dozens of call sites across `DeviceDetailsViewModel.*` partials; every future
  mutation site must remember to push; offline edits are lost (nothing records that a push
  is owed); retries and ordering become per-call-site problems. Rejected.

### B. Dirty flags in storage + debounced coordinator (recommended)
`LocalStorageService` mutators mark rows dirty (they are the single choke points — VMs
already funnel through them) and raise one event; a `SyncCoordinator` singleton debounces
(3 s) and runs a push pass. Deletes write a tombstone in the same transaction as the local
delete. `local_rev` (bumped on every mutation) lets the pass clear `is_dirty` only if the
row was not re-edited mid-flight.
- Pros: offline-safe (dirty state persists across restarts and is retried on the next pass);
  one place owns ordering, retries and conflict policy; reconcile-on-sign-in is the same
  pass.
- Cons: schema growth; mutators gain a side responsibility (kept to two lines each).

### C. Operation queue table (event-sourced outbox)
- Pros: perfect fidelity of intermediate states.
- Cons: heavy for a settings app — the server only needs the latest state per uid, so a
  queue collapses to "dirty set" anyway. Rejected as premature.

**Decision: B.**

## Recommended approach

### Schema (LocalStorageService migrations, same guarded-ALTER pattern as uid)

- `presets` / `macros`: `is_dirty INTEGER NOT NULL DEFAULT 0`,
  `local_rev INTEGER NOT NULL DEFAULT 0`.
- `sync_tombstones(owner_kind, owner_id, kind TEXT ('preset'|'macro'), uid TEXT,
  vendor_id, product_id, server_updated_at TEXT NULL, PRIMARY KEY (owner_kind, owner_id,
  kind, uid))` — written by `DeletePreset`/`DeleteMacro` inside the same transaction, only
  for user-owned rows that have a uid; removed after a successful server delete (or
  `*_NOT_FOUND`, which is the same outcome).
- `sync_meta(owner_kind, owner_id, key TEXT, value TEXT, PRIMARY KEY(owner_kind, owner_id,
  key))` — keys: `preferences.updated_at`, `preferences.dirty`,
  `devicestate.<vid>:<pid>.updated_at`, `devicestate.<vid>:<pid>.dirty`.

### Dirty marking (inside existing mutators, user-owned rows only)

- Macro content: `SaveMacro` → `is_dirty=1, local_rev=local_rev+1`.
- Preset metadata/content: `InsertPreset`, `UpdatePreset`, `PromotePresetToDefault` (both
  affected rows), `SavePresetSnapshot`, `ResetPresetSnapshot`, `CopyPresetSnapshot` (the
  new preset), `BackfillPresetSnapshotFromLive` — same two-column bump on the preset row.
- Preferences: `SettingsRepository.SaveAppPreferencesAsync`'s storage delegate sets
  `preferences.dirty` in `sync_meta` (value = a fresh generation, see the 2026-08-25 note
  below).
- Applied preset: `SetAppliedPreset` sets `devicestate.<vid>:<pid>.dirty` the same way.
- After each marking write, `LocalStorageService` raises `event Action?
  SyncRelevantDataChanged` (outside `_lock`, only when the mutated owner is a user).
- The pull (phase 1) inserts rows with `is_dirty=0` and does NOT raise the event.

### SyncCoordinator (new singleton, App/Services)

- Subscribes to `SyncRelevantDataChanged`; debounces 3 s (restart-on-signal, like the
  app-preferences save debounce); also triggered by `ISettingsSyncService.SyncCompleted`,
  which every push and pull raises on completion — that is the reconcile, and on a restored
  session it is also what starts the first pass. Construction itself signals nothing
  (2026-08-26 fix pass — PR #120 review, M2: a self-signal there raced the sign-in pull).
- Single-flight: one pass at a time; a signal during a pass schedules one follow-up pass.
  `internal Task? CurrentPassTask` seam for tests. Token/customer read from `UserProfile`
  at pass start; empty token → idle no-op.
- **Pass order** (all storage via `RunOnStorageAsync`, all HTTP sequential):
  1. Dirty/unsynced macros (`GetDirtyMacros(owner)` — includes `server_updated_at IS NULL`):
     no `server_updated_at` → `macros/create` (ALREADY_EXISTS → treat as needing update:
     fall through to `macros/get` for `updated_at`, then update); else `macros/update` with
     the stored `updated_at`. On `MACRO_CONFLICT` → `macros/get`, resend once with the
     fetched `updated_at` (client wins). On success store the new `updated_at` and clear
     dirty guarded by rev: `UPDATE macros SET is_dirty=0 WHERE id=$id AND local_rev=$rev`.
  2. Macro tombstones → `macros/delete` (with stored `server_updated_at` when present);
     `MACRO_NOT_FOUND` = success; `MACRO_CONFLICT` → get → retry once; success removes the
     tombstone.
  3. Dirty/unsynced presets — same create/update/conflict/clear-by-rev shape, with the
     configuration built by `PresetConfigurationBuilder` (macro-uid map from a bulk read).
  4. Preset tombstones → `presets/delete`; `DEFAULT_PRESET_CANNOT_BE_DELETED` → log warning
     and drop the tombstone (local already deleted; server keeps its default — the next
     default change pushes over it).
  5. Preferences if `preferences.dirty`: `preferences/update` with `sync_meta` `updated_at`
     (absent → omit); `PREFERENCES_CONFLICT` → `preferences/get` → retry once; success
     stores `updated_at`, clears the dirty key.
  6. Device state per dirty `devicestate.*` key: resolve applied preset uid (collapse rule
     as in the upgrade push — a unit-keyed row wins, `device_key = ''` is only the fallback;
     `SettingsSyncService.ChooseAppliedPresetRow`, and log when units disagree),
     `devices/update` with stored `updated_at`;
     `DEVICE_STATE_CONFLICT` → `devices/get` → retry once; `ACTIVE_PRESET_NOT_FOUND` → log,
     clear dirty (the preset push next pass will re-dirty via its own flow — coder note:
     re-dirty the device key after a preset create succeeds for its type).
  - Network error aborts the pass (dirty state persists; next signal retries). Non-network
    per-item failure: log, leave dirty, continue — with a per-pass cap (skip an item after
    3 consecutive failed passes… no persistent counter: keep it simple, log-and-leave-dirty;
    an eternally rejected item retries once per pass, bounded by pass frequency).
- Sign-out / `ClearSignedInState`: coordinator's next pass sees an empty token and idles;
  no unsubscription complexity.
- The upgrade push (`OnGuestUpgradedAsync`) is unchanged; rows it fails to push stay
  `server_updated_at NULL` and the coordinator's reconcile pass picks them up — this is the
  re-push mechanism.

### API surface

- `ISyncApiService`/`SyncApiService`: `UpdateMacroAsync(token, MacroUpdateRequest)`,
  `DeleteMacroAsync(token, uid, updatedAt)`, `UpdatePresetAsync(token,
  PresetUpdateRequest)`, `DeletePresetAsync(token, uid, updatedAt)`; extend
  `UpdatePreferencesAsync` and `UpdateDeviceStateAsync` with optional `updatedAt`.
- Update requests = create requests + `updated_at`; DTOs in `SyncApiDtos.cs`.

### Implementation notes (2026-08-25, coder)

- **sync_meta dirty flags are generation-guarded, cleared after success** (2026-08-25 fix
  pass, supersedes the original clear-first + restore-on-failure deviation — Review
  findings Round 1, S1). `MarkSyncDirty` writes a fresh generation (clock-seeded counter)
  as the flag's value instead of `"1"`; the pass reads it, pushes, and on success removes
  the key only `WHERE value = <read generation>` (`RemoveSyncMetaIfValue`). A save landing
  mid-flight wrote a newer generation, so the stale clear misses and the follow-up pass
  pushes the newer values — the same property clear-first was defending — while failure
  paths simply leave the flag alone, so an app exit mid-push can no longer drop the
  restore delegate and lose the mark. This is the sync_meta twin of the rows' `local_rev`
  guard. The coordinator's mid-pass re-dirty uses `MarkSyncDirty(signal: false)` (no
  event), so failures still cannot loop pass → fail → signal → pass.
- **The sign-in pull skips uids with a pending tombstone** (2026-08-25 fix pass — Review
  findings Round 1, B1; mechanism revised 2026-08-26, PR #120 review M2): `PullCoreAsync`
  treats a tombstoned uid as a pending local delete, not a missing record — otherwise the
  pull would re-insert the deleted record and the later server-side delete would leave that
  resurrected copy permanently local-only. The original set read assumed the pull always
  finishes before the coordinator's tombstone pass, which nothing enforced: the constructor
  signalled a reconcile as soon as a restored session existed and `App.axaml.cs` started the
  pull immediately after, so a pass could drop a tombstone mid-pull. The guard now has two
  halves, each covering one direction:
  - **A tombstone dropped during the pull** — covered by *ordering* plus a *snapshot*. The
    coordinator no longer signals from its constructor (below), so the tombstone pass runs
    after the pull; and `PullCoreAsync` reads both tombstone kinds as its first storage
    action, **before `macros/list`**, so every list response it merges was built after the
    snapshot and a tombstone dropped later belongs to a uid the snapshot already holds. A
    set read taken *after* the list call — where it used to sit — is exactly what let the
    drop slip through.
  - **A tombstone created during the pull** (the user deletes while it runs) — covered by a
    point read (`ExistsSyncTombstone`) inside the same `RunOnStorageAsync` delegate as the
    `SaveMacro`/`InsertPreset` it guards. No pre-pull snapshot can know about it.
- **The coordinator does not signal from its constructor** (2026-08-26 fix pass — PR #120
  review, M2): the case the constructor signal existed for — a "Remember me" session restored
  before construction, whose previous run left rows dirty — is exactly the case where
  `App.axaml.cs` then starts the sign-in pull, and `SettingsSyncService` raises
  `SyncCompleted` on every exit from it (success, exception, network abort). So the reconcile
  still happens, one pull later, through the subscription; the self-signal was redundant and
  its only distinct effect was to run the pass *concurrently with* the pull it should follow.
  `Signal()` is `internal` so `SyncCoordinatorTests` can drive a pass explicitly instead of
  depending on a constructor side effect.
- **The pull suppresses marking explicitly**: `SaveMacro`/`InsertPreset`/
  `PromotePresetToDefault`/`SetAppliedPreset` gained a `markDirty` parameter (default true)
  and the pull passes false — rather than inferring "pull-shaped" from a non-null
  `ServerUpdatedAt`, which would misfire on a server item delivered without `updated_at`.
- **The reconcile trigger is an event**: `ISettingsSyncService.SyncCompleted`, raised after
  every push and pull completion (success or failure); `SyncCoordinator` subscribes in its
  constructor alongside `LocalStorageService.SyncRelevantDataChanged`.
- The shared envelope predicates (`Succeeded`/`HasErrorCode`), `BuildMacroRequest` and
  `ReadPreferences` became `internal static` on `SettingsSyncService` and are reused by the
  coordinator instead of being duplicated.
- **The device-state collapse prefers the unit-keyed row** (2026-08-26 fix pass — PR #120
  review, M1): `ChooseAppliedPresetRow` (shared with the upgrade push) returns the first row
  with a non-empty `device_key` and falls back to the `''` row only when no unit has applied
  a preset. The old "`''` wins" rule dated from when `''` meant a merged-transport unit; the
  pull now writes the cloud's applied preset there for every device type, so the old rule
  made a stale pulled value outrank the preset the user applied on this machine — and after
  the first pull, applying a preset stopped reaching the server entirely. The coordinator
  now also logs the units-disagree warning the upgrade push already logged
  (`AppliedPresetRowsDisagree`); before, this step logged nothing at all.
- **An unparseable 2xx never clears a dirty mark** (2026-08-26 fix pass — PR #120 review,
  m1): `SettingsSyncService.ConfirmedSuccess` (`response.Success` alone) gates every
  write-back that clears dirty on a record the server already knows about — `macros/update`,
  `presets/update`, `preferences/update`, `devices/update`. `Succeeded`'s tolerance of a 2xx
  whose body did not bind stays as-is on the **create** paths, where it is harmless because
  the row keeps `server_updated_at NULL` and the next pass re-enumerates it. On an update
  path it was not: clearing `is_dirty` while `server_updated_at` stayed set dropped the row
  out of `GetDirtyMacros`/`GetDirtyPresets` forever and lost the edit silently — and that
  response shape is exactly what the PHP router returns for a route it does not have, i.e.
  "the server did nothing". Unconfirmed 2xx now logs a warning and leaves the mark set.
- **Two small consistency fixes** (2026-08-26 fix pass — PR #120 review, m5/m6):
  `PushDeviceStateAsync` returns `Task`, not an ignored `Task<bool>` (it is the last step of
  the pass, so there is nothing left to abort); and `PushPreferencesAsync` distinguishes
  storage teardown from "the owner saved none of the five keys" via a `Read: true` field in
  the read tuple — teardown aborts the pass like every other storage read, while genuinely
  absent rows now consume the dirty flag instead of leaving it to re-read storage on every
  later pass.

## File-level change list

1. `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: schema migrations
   (`is_dirty`, `local_rev`, `sync_tombstones`, `sync_meta`); dirty marking in the mutators
   listed above; `SyncRelevantDataChanged` event; `GetDirtyMacros`/`GetDirtyPresets`,
   tombstone read/remove (the pull uses both: `GetSyncTombstones` for its pre-list snapshot
   and the `ExistsSyncTombstone` point read at each insert, 2026-08-26 fix pass), `sync_meta`
   get/set, rev-guarded dirty clear helpers.
2. `src/ProoveGaming.App/Services/SettingsRepository.cs` — edit: preferences save marks
   `preferences.dirty`.
3. `src/ProoveGaming.App/Services/ISyncApiService.cs` — edit: update/delete methods;
   `updatedAt` on preferences/device-state.
4. `src/ProoveGaming.App/Services/SyncApiService.cs` — edit: implement them.
5. `src/ProoveGaming.App/Services/SyncApiDtos.cs` — edit: update/delete request DTOs.
6. `src/ProoveGaming.App/Services/SyncCoordinator.cs` — new: debounce, single-flight pass,
   conflict policy, seams.
7. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — edit: signal the coordinator
   when pull/push complete (reconcile trigger); the pull's merge loops skip uids with a
   pending tombstone — snapshotted before the first list call and re-checked per item next to
   the insert (2026-08-25 fix pass, mechanism revised 2026-08-26); the shared
   collapse/envelope helpers `ChooseAppliedPresetRow`, `AppliedPresetRowsDisagree` and
   `ConfirmedSuccess` (2026-08-26 fix pass).
8. `src/ProoveGaming.App/App.axaml.cs` — edit: register `SyncCoordinator`; construct it at
   startup (it self-subscribes), before the sign-in pull whose `SyncCompleted` delivers the
   startup reconcile (2026-08-26 fix pass).
9. `src/ProoveGaming.App/Models/PresetRecord.cs`, `MacroRecord.cs` — edit: `IsDirty`,
   `LocalRev` (read-side).
10. `tests/ProoveGaming.App.Tests/LocalStorageServiceSyncTrackingTests.cs` — new: see Test
    strategy (lands BEFORE the coordinator per the characterization-first rule — the
    mutators being instrumented are existing behavior).
11. `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs` — new: see Test strategy.
12. `docs/CONTEXT.md` — edit: `SyncCoordinator`, `sync_meta`, `tombstone`, `local_rev`.
13. `docs/plans/add-api-v2-sync/add-api-v2-sync-plan.md` — edit: dated note — the
    interrupted-push and no-device-card gaps are closed by this plan's reconcile pass.
14. `docs/plans/presets/presets-plan.md`, `docs/plans/migrate-macros-per-device/…` — edit:
    dated schema notes (`is_dirty`/`local_rev`/tombstones).

## Acceptance criteria

- [ ] Given a signed-in user edits a macro (or preset content/name/default flag), when ~3 s
  pass, then exactly one update/create call for it reaches the fake API, the stored
  `server_updated_at` is refreshed, and `is_dirty` is 0.
- [ ] Given a row is edited again while its push is in flight, when the pass clears dirty,
  then the row stays dirty (rev guard) and a follow-up pass pushes the newer content.
- [ ] Given the user deletes a synced preset/macro, when the pass runs, then the matching
  delete call is made with the stored `updated_at` and the tombstone is gone;
  `*_NOT_FOUND` also removes the tombstone; local deletion is never blocked on the server.
- [ ] Given a locally-deleted record whose tombstone has not been pushed yet, when a
  sign-in pull lists its uid, then the pull skips it (no re-insert) and the tombstone
  survives for the coordinator's delete push — including when the tombstone is removed *while
  the pull is running*, which the snapshot taken before the pull's first list call covers, and
  when the user deletes a record *during* the pull, which the point read at each insert covers.
- [ ] Given a device type with both a pulled `device_key = ''` applied-preset row and a row
  the user applied under a real unit key, when the device-state step runs, then
  `devices/update` carries the uid of the **unit-keyed** preset and the disagreement is
  logged.
- [ ] Given the fake answers `PRESET_CONFLICT`/`MACRO_CONFLICT`/`PREFERENCES_CONFLICT`/
  `DEVICE_STATE_CONFLICT`, when the pass runs, then it fetches the current `updated_at` via
  the matching get and resends the local content exactly once; a second conflict leaves the
  row dirty and logs a warning.
- [ ] Given the upgrade push was aborted mid-way (some rows `server_updated_at IS NULL`),
  when the user signs in later, then the reconcile pass creates the missing records without
  duplicating existing ones.
- [ ] Given a macro exists under a device type with no device card, when a pass runs, then
  it is pushed (enumeration is by dirty/unsynced rows).
- [ ] Given edits happen while offline, when connectivity returns and any signal fires, then
  the accumulated dirty rows push; nothing was lost across an app restart in between.
- [ ] Given a guest session, when any edit is made, then no dirty marks, tombstones or API
  calls are produced.
- [ ] Given changing app preferences or applying a preset, when the pass runs, then
  `preferences/update` / `devices/update` carry the stored `updated_at` and the returned one
  is stored in `sync_meta`.
- [ ] Given the token is empty (signed out mid-debounce), when the pass fires, then it
  idles without API calls and without error logs above Debug.

## Test strategy

New feature (`add-*`) — mandatory coverage in `tests/ProoveGaming.App.Tests`. Order: item 10
(characterization of mutator instrumentation) lands with the schema change, item 11 with the
coordinator.

- `LocalStorageServiceSyncTrackingTests` (new): each listed mutator sets `is_dirty`/bumps
  `local_rev` for user rows and does neither for guest rows; delete writes a tombstone (user
  + uid only) atomically; rev-guarded clear leaves a re-edited row dirty; migration adds
  columns/tables to a legacy DB without touching data; event raised once per mutation and
  not by pull-shaped inserts.
- `SyncCoordinatorTests` (new): debounce collapses a burst into one pass; single-flight with
  follow-up; full pass ordering against a recording fake; create-vs-update selection by
  `server_updated_at NULL`; every conflict retry path (get → resend once → give-up);
  tombstone lifecycle incl. `DEFAULT_PRESET_CANNOT_BE_DELETED`; network-error abort
  preserves dirty; empty-token idle; reconcile-after-signin trigger.
- `SettingsSyncServicePullTests` (edit, 2026-08-25 fix pass): a uid with a pending
  tombstone is skipped by the pull and the tombstone survives; plus (2026-08-26 fix pass)
  the interleaved case — the tombstone is removed while the `macros/list` call is parked on a
  semaphore (as `Pull_…ReturnsWhileTheFirstApiCallIsStillBlocked` does), i.e. after the pull
  started and before its inserts, and the record still must not be re-inserted. This is the
  case the pre-list snapshot exists for, and it fails on a set read taken after the list call.
- `SyncCoordinatorTests` (edit, 2026-08-26 fix pass): device state with **mixed** scopes — a
  pull-shaped `device_key = ''` row plus a user-applied unit-keyed row — sends the
  unit-keyed preset's uid; every existing device-state test uses a single `''` row, which is
  what let the `''`-wins collapse through review. Construction no longer starts a pass, so
  these tests drive one with the internal `Signal()` seam.

Deliberately not covered: live HTTP; timing-accurate debounce values (use the injected/short
debounce the existing debounce tests use — verify in Release too per CI note); UI-thread
wiring.

## QA (manual test steps)

1. Sign in on machine A. Create a macro "SYNC-TEST" and a preset "SYNC-PRESET" on a
   connected device, then rename the preset to "SYNC-PRESET-2" and change its RGB effect.
   Wait ~10 seconds.
2. Sign into the same account on machine B. Expected: "SYNC-TEST" and "SYNC-PRESET-2" (the
   renamed version, with the RGB change) appear.
3. On machine B, delete "SYNC-TEST". Wait ~10 seconds. On machine A, sign out and back in.
   Expected: "SYNC-TEST" is gone on A as well.
4. On machine A, turn off the network, edit the preset again, close and reopen the app,
   then restore the network and make any small change (e.g. move a settings slider).
   Expected: within ~10 seconds the offline edit reaches the cloud (verify by re-login on
   machine B: the edit is there).
5. Edit the same preset on A and B while both are online, a few seconds apart. Expected: the
   later edit ends up on both machines after each re-login; the app never shows an error.
6. As a guest, make edits of every kind. Expected: no network traffic to the sync endpoints
   (no cloud changes on any account), everything still works locally.

## Review focus

1. `thread-safety` — dirty marks inside the existing `_lock`d mutators; event raised outside
   the lock; coordinator single-flight has no race between "pass running" and "signal
   arrived"; rev-guard SQL is atomic.
2. `async/await` — coordinator loop fully async; no sync-over-async; debounce doesn't leak
   timers on dispose.
3. `resource disposal` — coordinator unsubscribes from the storage event on dispose; no
   dangling `Task` that touches a disposed `LocalStorageService` (its post-shutdown null
   reads must be handled like the push does).
4. `performance hot-path` — `SavePresetSnapshot` runs on preset apply/save paths; the dirty
   mark must be O(1) extra SQL in the same transaction, and the event must not fire per-row
   in bulk operations (once per mutator call).

## Performance & quality

- Mutator overhead: one `UPDATE … SET is_dirty=1, local_rev=local_rev+1` per call inside the
  already-open transaction — no extra round-trips; the changed-event is a plain delegate
  invocation.
- The pass allocates per dirty row only; it runs on the pool and is not latency-sensitive.
- No per-frame/per-keystroke path is touched (preferences saves are already debounced before
  they reach storage).

## Risks

- **Missed mutator = silent non-sync.** The mutator list above was derived from ViewModel
  call-site inventory; the tracking test enumerates it, and any future mutator must join it
  (noted in `LocalStorageService`'s event doc comment).
- **Conflict retry storms** between two simultaneously-editing machines: bounded — one
  retry per item per pass, passes only fire on local edits or sign-in.
- **`updated_at` echo mismatch** (server compares string-exact): we store and resend
  verbatim; if the server normalizes the format, conflicts false-positive — the refresh-
  retry absorbs one normalization round; watch logs in QA step 5.
- **Tombstone for a never-synced row** points at nothing server-side; delete answers
  `*_NOT_FOUND` and the tombstone drops — verified by test. Better: `DeleteMacro`/
  `DeletePreset` skip the tombstone when `server_updated_at IS NULL` and `is_dirty` says
  never-pushed… only when `server_updated_at IS NULL` (never synced) — coder implements the
  skip; test pins it.

## Open questions

1. Debounce value (3 s proposed) and whether a periodic background pass (e.g. hourly) is
   wanted in addition to edit-triggered passes — default: edit-triggered + sign-in only.
2. `presets/update` request shape (full payload vs. delta) is undocumented — assumed full
   create-shaped payload + `updated_at`; confirm with backend.

## Review findings

### Round 1 — 2026-08-25 — Ship: no

#### Summary
The phase delivers the full live-sync pipeline as designed: `is_dirty`/`local_rev` columns
plus `sync_tombstones`/`sync_meta` tables with guarded-ALTER migration; dirty marking inside
every listed mutator (O(1) SQL in the already-open transaction, event raised outside `_lock`,
once per call); a debounced single-flight `SyncCoordinator` with the plan's pass order and
client-wins one-refresh-retry conflict policy; the update/delete API surface; and 52 new
tests (31 tracking + 21 coordinator), all green in Release. One blocker: the sign-in pull is
tombstone-blind, so an unpushed delete gets resurrected locally by the next pull.

#### Plan conformance
Plan: `docs/plans/add-api-v2-live-sync/add-api-v2-live-sync-plan.md` (read in full,
including the Implementation notes deviations block).
- Implemented as planned: all 14 file-level items; the Risks section's "skip the tombstone
  when `server_updated_at IS NULL`" is implemented (`WriteTombstoneCore`) and test-pinned
  (`DeleteMacro_NeverSyncedUserRow_DeletesWithoutTombstoneOrEvent`).
- Recorded deviations, all verified in code and each covered by a test:
  1. **sync_meta clear-first + restore-on-failure** — `PushPreferencesAsync` /
     `PushDeviceStateAsync` + `Restore*DirtyAsync` via `SetSyncMeta` (no event; the
     no-fresh-signal property is pinned by
     `PreferencesUpdate_Rejected_RestoresTheDirtyFlagWithoutSignalingANewPass`). Residual
     hole → S1 below.
  2. **Explicit `markDirty` parameter** (default true) on `SaveMacro` / `InsertPreset` /
     `PromotePresetToDefault` / `SetAppliedPreset`; the pull passes false at every write —
     verified at all pull call sites in `SettingsSyncService.PullCoreAsync`.
  3. **`ISettingsSyncService.SyncCompleted` as the reconcile trigger** — raised after push
     and pull, success or failure; coordinator subscribes in its constructor
     (`SyncCompleted_TriggersAReconcilePassThatCreatesUnsyncedRows`).
  4. **`internal static` reuse** of `Succeeded`/`HasErrorCode`/`BuildMacroRequest`/
     `ReadPreferences` and the two `*_UID_ALREADY_EXISTS` constants — no duplicated
     envelope logic in the coordinator.
- Missing / partial: the plan never specified how the **sign-in pull interacts with a
  pending tombstone** — that unspecified seam is B1. (The plan's "Out: pull-side merging of
  diverged records" covers content divergence, not deletions, so B1 is not an accepted
  limitation.)
- Accepted limitation (per plan): an eternally-rejected item retries once per pass with a
  warning log (no persistent failure counter) — residual risk is bounded log noise, passes
  only fire on edits/sign-in. A pulled row that never learns an `updated_at` re-pushes
  (create → ALREADY_EXISTS → get → update) every pass under the same bound.
- Scope drift: none — `App.axaml.cs`'s `OnSignedInAsync` wiring belongs to the already-
  reviewed pull phase, sharing the working tree.
- Review focus: (1) thread-safety — dirty marks are in the mutators' existing transactions,
  every `RaiseSyncRelevantDataChanged` call site is after `_lock.Release()`, the
  `_gate` protocol has no window where a signal is dropped while `_passRunning` flips
  (flag flip and follow-up consumption hold the same lock), and the rev-guard is one atomic
  `UPDATE … WHERE id AND local_rev`; exception: the S2 disposal race. (2) async — the
  coordinator is fully async, no sync-over-async; each `Signal` cancels+disposes the prior
  debounce CTS and `Dispose` kills the last, no timer leak. (3) disposal — `Dispose`
  unsubscribes both events; a running pass aborts on null storage reads per the teardown
  contract; exceptions: S1 (dropped restore) and S2. (4) hot path — `MarkPresetDirtyCore`
  is one UPDATE inside `SavePresetSnapshot`'s transaction; tests count exactly one event
  per mutator call.
- Test-writer's accepted duplication cut (preset twins of covered macro paths): verified
  acceptable. Untested preset-only branches are create→ALREADY_EXISTS fall-through and
  tombstone conflict-retry/NOT_FOUND — verbatim twins of tested macro code — while every
  preset push method is still exercised at least once (`FullPass…`,
  `PresetUpdate_ConflictTwice…`, `PresetTombstone_DefaultPreset…`,
  `PresetCreate_ReDirties…`). Named residual risk: the twins are *copied*, not shared, so a
  future edit to only the preset copy loses the macro tests' protection.

#### Code
- [x] **B1. Sign-in pull resurrects locally-deleted records whose tombstone was not yet
  pushed** — `src/ProoveGaming.App/Services/SettingsSyncService.cs:404` (macro
  `ContainsKey` check in `PullCoreAsync`) and `:503` (preset check) — Blocker
  **Problem:** The pull's additive merge is keyed only on "uid absent locally". A deleted
  row is absent locally but still listed by the server while its `sync_tombstones` entry is
  pending — and on sign-in/startup the pull always runs **before** the coordinator's
  tombstone pass (the reconcile trigger is `SyncCompleted`, raised *after* the pull). The
  pull therefore re-inserts the deleted record (`markDirty: false`, `server_updated_at`
  set), after which the coordinator deletes it server-side and removes the tombstone —
  leaving the resurrected local copy clean and synced-looking, so it is never pushed, never
  deleted, and never reconciled again.
  **Why it blocks:** User deletes a synced preset/macro and closes the app within the ~3 s
  debounce (or deletes while offline). Next launch with "Remember me": the pull re-inserts
  it — the deleted item reappears in the UI permanently (macros land in the shared `''`
  scope, visible on every unit), while the server copy is gone. Violates this plan's delete
  acceptance criterion in spirit and the user's explicit action.
  **Solution options:**
  - Option A (correct, small): in `PullCoreAsync`, read the owner's tombstones once
    (`GetSyncTombstones` for both kinds) before the merge loops and skip any pulled item
    whose uid has a pending tombstone. Add a dated note to
    `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` (its file is being touched) and a
    pull-side test (delete synced row → run pull listing that uid → row stays absent,
    tombstone stays).
  - Option B (weaker): run the tombstone push before the pull on sign-in — does not survive
    a network failure between the two and reorders the reconcile; not recommended.
- [x] **S1. sync_meta dirty flag can be lost across app exit (clear-first restore is
  dropped after storage teardown)** — `src/ProoveGaming.App/Services/SyncCoordinator.cs:609`
  (`PushPreferencesAsync` clear) / `:690` (device-state clear), restores at `:659`/`:778` —
  Should-fix
  **Problem / why it matters:** The pass persists the flag *removal* before the HTTP call;
  the compensating restore is a later `RunOnStorageAsync` enqueue. If the app exits while
  the HTTP call is in flight, the exit path drains the queue and disposes storage, and the
  restore delegate is **dropped** by the executor's teardown contract (`RunOnStorageAsync`
  runs nothing once `_disposed`). After restart the preference / applied-preset edit is
  silently never synced — no dirty flag, nothing enumerates it — until the user happens to
  make another edit of the same kind. The row paths don't have this hole (their dirty flag
  persists until a rev-guarded clear *after* success).
  **Fix:** give sync_meta the same guard the rows have instead of clear-first: let
  `MarkSyncDirty` store an incrementing generation (e.g. ticks or a counter) rather than
  `"1"`, have the pass read it, push, and clear-after-success only
  `WHERE value = $readGeneration`. That removes both the mid-flight-edit hazard the coder's
  deviation 1 was defending against *and* the exit-window loss, and deletes the restore
  choreography.
- [x] **S2. `Signal` reads `cts.Token` outside `_gate` — ObjectDisposedException race with
  `Dispose`** — `src/ProoveGaming.App/Services/SyncCoordinator.cs:113` — Should-fix
  **Problem / why it matters:** `Signal` creates the CTS and stores it in `_debounceCts`
  under the lock, but evaluates `cts.Token` after releasing it. Container disposal at app
  exit (`desktop.Exit` → `(_services as IDisposable)?.Dispose()`) can run
  `Dispose` → `_debounceCts?.Cancel(); Dispose()` in that window, and
  `CancellationTokenSource.Token` on a disposed source throws ObjectDisposedException —
  synchronously, up through `RaiseSyncRelevantDataChanged` into the mutator that was saving
  (the event's own doc comment says subscribers must not throw). Shutdown-window only, but
  it is a crash path on a storage-executor/pool thread.
  **Fix:** capture `var token = cts.Token;` inside the `lock (_gate)` block and pass that
  local to `DebounceThenStartPassAsync`. (Post-capture disposal is already safe: `Dispose`
  cancels first, so the captured token is latched canceled and the delay exits.)

#### Tests
- [x] B1's scenario has no test on either side — add the pull-side tombstone-skip test with
  the fix (see B1 Option A) — `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs`.
- Otherwise coverage matches the Test strategy point for point (mutator matrix incl. guest
  no-ops and pull-shaped inserts, tombstone matrix incl. the never-synced skip, rev guard,
  legacy-DB migration, debounce collapse, single-flight follow-up with a parked HTTP call,
  full pass order, every conflict path, network/token abort, empty-token idle, reconcile
  trigger, ACTIVE_PRESET_NOT_FOUND + same-pass re-dirty). All 52 pass in **Release**
  (matching the CI configuration).

#### Nits
- `GetDirtyMacros` loads the entire `macro_events` table (no `WHERE macro_id IN …` /
  owner filter) whenever at least one dirty macro exists — full-table scan per pass;
  harmless at current scale, easy `IN` clause if libraries grow —
  `src/ProoveGaming.App/Services/LocalStorageService.cs` (`GetDirtyMacros`, events query).
- `SaveMacro`'s update path computes `marked` from the parameters without checking the
  UPDATE's row count, so saving a nonexistent macro id still raises
  `SyncRelevantDataChanged` (a no-op pass) — `LocalStorageService.SaveMacro`.

#### What's done well
- The `_gate` protocol is genuinely airtight for its stated contract: flag flips only under
  the lock, passes outside it, and the follow-up request is consumed in the same critical
  section that releases `_passRunning` — no dropped-signal window. The
  parked-HTTP-call tests prove the race rather than asserting around it.
- Rev-guarded clear as a single atomic UPDATE, and the tombstone written in the same
  transaction as the delete, are exactly the right shapes — no read-then-write races in
  the storage layer.
- The never-synced tombstone skip, the `ACTIVE_PRESET_NOT_FOUND` → preset-create re-dirty
  handshake (consumed later in the *same* pass, no fresh signal), and the restore-without-
  event discipline show careful loop-prevention thinking throughout.
- Documentation discipline: CONTEXT.md entries for all four new terms, dated notes in all
  four sibling plans, and the coder's deviations recorded in this plan before review.

#### Verdict
Ship: no — B1 is a user-visible correctness bug (deleted data resurrects and permanently
diverges local from server) in the feature's core delete flow, with a realistic trigger
(delete then close within the debounce, or offline delete). Its fix is small and the rest of
the phase is in very good shape; S1/S2 are narrow shutdown-window races that should ride
along in the same round. First: 1. B1 (pull skips tombstoned uids + test), 2. S2 (one-line
token capture), 3. S1 (generation-guarded sync_meta clear).
