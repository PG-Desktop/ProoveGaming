# add-api-v2-pull — Hydrate local data from the server on sign-in

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Phase 1 of [add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md).
Builds on [add-api-v2-sync](../add-api-v2-sync/add-api-v2-sync-plan.md) (uid/server_updated_at
columns, `SyncApiService`, `PresetConfigurationBuilder`, `SettingsSyncService` push).

> **2026-09-01 note (API v2.1 notifications):** the pipeline gained a sixth step —
> `notifications/list` (`PullNotificationsAsync`): server-id-keyed merge into the bell
> (insert with the type-slug icon, adopt the server read flag on non-dirty rows), with the
> pending-tombstone/batch-mark snapshot taken before the list call. The same merge also runs
> on a 15-minute poll while signed in, raising `NotificationsPulled` when rows changed. See
> `docs/plans/add-api-v21-notifications-sync/`.
>
> **2026-08-31 note (API v2.1 device roster):** the pull pipeline gained a fifth step after
> device state — `devices/list` (`PullDeviceRosterAsync`): hydrate an offline card (via the
> DeviceHost's `GetDeviceCatalogEntry` gRPC lookup) for a server-rostered model with no local
> user card, skip pairs with a pending `deviceroster.*.remove` mark, and mark
> `deviceroster.*.add` (signal: false) for local cards the server list lacks. Every pull now
> ends with one `devices/list` call. See `docs/plans/add-api-v21-device-roster/`.
>
> **2026-08-25 note (live sync landed):** phase 2
> ([add-api-v2-live-sync](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md)) now exists.
> Two touchpoints of this pull changed with it: the pull's writes pass `markDirty: false` to
> `SaveMacro`/`InsertPreset`/`PromotePresetToDefault`/`SetAppliedPreset` so pulled server
> content is never re-pushed as a local edit, and `ISettingsSyncService` gained a
> `SyncCompleted` event (raised after every push/pull) that `SyncCoordinator` uses as its
> reconcile trigger. The pull pipeline itself is otherwise as described here.
>
> **2026-08-25 note (live-sync review round 1, B1 fix):** the additive merge gained a third
> skip condition — a pulled uid with a **pending `sync_tombstones` entry** for the owner is
> skipped, not inserted. The uid is absent locally because the user deleted it, and
> re-inserting would resurrect the deleted record permanently.
>
> **2026-08-26 correction (PR #120 review, M2):** the note above relied on the coordinator's
> tombstone pass running only after this pull, which was not something the code guaranteed —
> `SyncCoordinator`'s constructor signalled a pass as soon as a restored session existed and
> `App.axaml.cs` started this pull immediately after, so the two ran concurrently and the
> pass could drop a tombstone mid-pull. The guarantee is now real and the guard has two
> halves:
>
> 1. **Ordering.** The coordinator no longer signals from its constructor. On a restored
>    session the reconcile arrives as the `SyncCompleted` this pull raises on every exit
>    (success, exception or network abort), so the coordinator's tombstone deletes run
>    strictly *after* the merge — the order the B1 fix assumed.
> 2. **A snapshot taken before the first network call.** `PullCoreAsync` reads both tombstone
>    kinds as its first storage action, ahead of `macros/list`. Every list response it merges
>    was therefore built after the snapshot, so a tombstone dropped later belongs to a uid the
>    snapshot already holds — a tombstone the merge must honour cannot be missed. Reading the
>    set after the list call (where it used to live) is what let the drop slip through.
>
> A per-item `ExistsSyncTombstone` point read stays in the same storage delegate as each
> `SaveMacro`/`InsertPreset`, covering the opposite direction: a delete the user makes *while*
> the pull is running, which no pre-pull snapshot can know about. Both halves are load-bearing;
> neither alone covers both directions.

## Goal

When a user signs into an existing account (or the app restores a remembered session), pull
their cloud presets, macros, preferences and device state and add them to the local database
— so a second machine, or a reinstall, starts with the account's data instead of an empty
state. Today nothing pulls: `presets/list`, `macros/list`, `preferences/get`, `devices/get`
are unused.

## Scope

**In:**
- A background pull triggered after successful sign-in and after remembered-session restore.
- `SyncApiService` methods + DTOs for `presets/list`, `macros/list`, `preferences/get`,
  `devices/get`.
- **Additive merge**: server records whose `uid` is absent locally are inserted; existing
  local rows are never modified or deleted. Convergence of diverged records is phase 2.
- `PresetConfigurationApplier` — the reverse of `PresetConfigurationBuilder`: deserializes a
  pulled `configuration` object into the preset snapshot tables, resolving `macro_uid`
  references to local macro ids.
- Applying pulled `active_preset_uid` (devices/get) only where no local applied-preset row
  exists; the actual hardware application happens through the existing
  reapply-preset-on-connect flow when the device connects.
  - *2026-09-10:* that hand-off was incomplete — the connect-time reapply reads the LIVE
    device rows, which are empty for a preset this machine never applied, so a pulled preset
    reached neither the board nor the sections. The pull now also sets a pending-apply mark
    (`devicestate.<vid>:<pid>.pending_apply`) that the first device page open — or the
    connect-time reapply for a mouse — consumes by loading the preset's snapshot into the
    unit's live rows and pushing it. Owner of that behaviour:
    [`../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md`](../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md).
- Preferences applied only when the local owner has zero preference rows.

**Out:**
- Overwriting/merging diverged records, deletes, and pushing local changes (phase 2).
- Storing preferences/device-state `updated_at` (phase 2's `sync_meta`).
- Live UI refresh of an open Device Details view mid-pull — pulled data appears when the
  view reloads (documented limitation; Home's owner-scoped reload after the pull covers the
  common path).
- Guest sessions (never pull).

## Options considered

### A. Pull synchronously inside the sign-in flow
Await the pull before navigating Home so the UI opens complete.
- Pros: no "data appears later" window.
- Cons: sign-in stalls on N HTTP calls; a slow server freezes the wizard. Rejected for the
  same reason the upgrade push is backgrounded.

### B. Background pull, additive merge (recommended)
`ISettingsSyncService` gains `OnSignedInAsync(customerId)`; implementation mirrors the push:
capture token, `Task.Run`, internal `PullTask` seam, storage via `RunOnStorageAsync`,
best-effort logging. After the pull completes, ask `HomeViewModel` to reload owner-scoped
state so device cards/preset lists reflect new rows.
- Pros: consistent with the push design; sign-in never blocks; additive policy makes it safe
  to ship before phase 2 (cannot clobber local edits that were never pushed).
- Cons: pulled data is not instantly visible in an already-open details view.

### C. Full reconcile pull (server wins on divergence)
- Pros: closest to "cloud is truth".
- Cons: without phase 2's push, local edits made after registration exist ONLY locally —
  server-wins would silently destroy them. Rejected until two-way sync exists.

**Decision: B.**

## Recommended approach

1. **API surface** (`ISyncApiService`/`SyncApiService`/`SyncApiDtos.cs`):
   - `ListMacrosAsync(token)` → `api/macros/list` with empty body `{}` (no VID/PID filter —
     pull everything, including device types with no local device card).
   - `ListPresetsAsync(token)` → `api/presets/list`, empty body.
   - `GetPresetAsync(token, uid)` → `api/presets/get` — used per-preset when the list
     response does not carry `configuration` (the contract does not document the list shape;
     the DTO makes `configuration` optional and the pull falls back to `get` per uid when
     it is null — assumption recorded in Open questions).
   - `GetMacroAsync(token, uid)` → `api/macros/get`, same fallback for `events`.
   - `GetPreferencesAsync(token)` → `api/preferences/get` — must tolerate the documented
     `"preferences": []` empty-array quirk (custom converter or `JsonElement` field).
   - `GetDeviceStateAsync(token, vendorId, productId)` → `api/devices/get`.
   - List DTOs: `MacroListResponse { macros: MacroPullDto[] }`, `PresetListResponse
     { presets: PresetPullDto[] }`; pull DTOs carry `uid`, `vendor_id`, `product_id`,
     `name`, `updated_at`, plus macro fields (`repeat_count`, `repeat_mode`, `events[]`)
     and preset fields (`description`, `is_default`, `configuration` as `JsonElement?`).
     All nullable; an item that fails to bind is skipped with a log line, never throws.
2. **`PresetConfigurationApplier`** (new, App/Services): given a preset's local id and the
   parsed `configuration` (`JsonElement`), writes the snapshot tables via existing
   `LocalStorageService` setters (`SavePresetSnapshot`-family / per-table writers — add thin
   per-table insert helpers where only bulk writers exist). Maps `macro_uid` → local macro id
   using a dictionary built from the macros inserted/found in step 3; unresolvable or null
   `macro_uid` → binding stored without a macro (same explicit-null stance as the builder).
   Unknown JSON keys are ignored (forward compatibility). Synchronous, called inside one
   `RunOnStorageAsync` delegate per preset, mirroring the builder.
3. **Pull pipeline** (`SettingsSyncService`):
   - `OnSignedInAsync(int customerId, CancellationToken)` on `ISettingsSyncService`;
     no-op guard identical to the push (id > 0, token non-empty); `internal Task PullTask`
     seam; never throws.
   - Order: macros → presets → preferences → device state (presets need the macro-uid map).
   - Macros: for each pulled macro absent locally (lookup by uid across the user's rows —
     new `GetMacroIdsByUid(ownerKind, ownerId)` bulk read), insert via `SaveMacro` with the
     pinned uid and set `server_updated_at` from the pulled `updated_at`. Present uid → skip.
     Pulled macros land under `device_key = ''` (the server has no per-unit scope), and
     `GetMacros` unions that unit-agnostic scope into every unit-keyed read for the same
     (owner, vid, pid) — so pulled macros are visible to each physical unit's library,
     binding composition and key-macro resolution, while one unit's own macros never leak
     to another (2026-08-25, review round 1 B1 fix).
   - Presets: same absence check by uid; insert via `InsertPreset` (pinned uid), apply
     configuration via the applier, set `server_updated_at`. `is_default` handling preserves
     the local invariant (exactly one default per (owner, vid, pid)): if the device type has
     no local default, honor the server flag via `PromotePresetToDefault`; otherwise drop
     the incoming flag (local default wins; logged at Debug).
     (2026-08-28, `docs/plans/fix-duplicate-default-preset/`: local-wins now applies only to
     a local default that has synced. A never-synced local default (`server_updated_at IS
     NULL`) is a seed that raced this pull — it is deleted with its snapshot via
     `LocalStorageService.DeleteNeverSyncedPreset` (guard re-checked inside the delete's
     transaction; no tombstone, per the never-synced rule) and the server's flag is honored.)
   - Preferences: if the owner has saved none of the five known preference keys (the
     `ReadPreferences` null check — an unrelated preference row does not block hydration)
     and the server returned a non-empty object, write the five known keys via existing
     setters; unknown keys ignored (wording corrected 2026-08-25, review round 1 S1).
   - Device state: for each distinct (vid, pid) among pulled presets, `GetDeviceStateAsync`;
     if `active_preset_uid` resolves to a local preset and no local applied row exists for
     that type (any device_key), `SetAppliedPreset` with `device_key = ''`. On the read
     side, `GetAppliedPreset` falls back to the `''` row when the queried unit key has no
     row of its own (an exact-key row always wins), which is how the reapply-on-connect
     flow picks up the cloud active preset (2026-08-25, review round 1 B1 fix).
   - Network error aborts the remaining pipeline (same `Aborts` helper as the push).
4. **Triggers**:
   - `MainWindow.OnSignInSucceeded`: after `_profileViewModel.LoadFromServerAsync()`,
     fire-and-forget `_settingsSync.OnSignedInAsync(_userProfile.CustomerId)`.
   - Remembered-session restore: in the `App.OnFrameworkInitializationCompleted` path that
     rehydrates `UserProfile` from saved auth, call the same method once the profile/token
     are in place (coder locates the exact post-restore point; the guard makes a premature
     call harmless).
   - NOT triggered after guest→register (fresh account has nothing to pull; the push runs).
   - After the pull completes with ≥1 inserted record, the service raises its
     `PullCompletedWithChanges` event (outside the pipeline's try, so a faulting subscriber
     cannot read as a pull failure); `MainWindow` subscribes in its ctor, unsubscribes in
     `OnClosed`, and posts `HomeViewModel.ReloadOwnerScopedState()` to the UI thread so
     cards/lists refresh (shape corrected 2026-08-25, review round 1 S1 — the service
     stays UI-free instead of calling the view-model directly).

## File-level change list

1. `src/ProoveGaming.App/Services/ISyncApiService.cs` — edit: six new pull methods.
2. `src/ProoveGaming.App/Services/SyncApiService.cs` — edit: implement them over
   `ApiHttpClient`.
3. `src/ProoveGaming.App/Services/SyncApiDtos.cs` — edit: list/get/preferences/device-state
   response DTOs (nullable, quirk-tolerant).
4. `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs` — new: configuration
   JSON → snapshot tables, macro_uid resolution, unknown-key tolerance.
5. `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: `GetMacroIdsByUid` /
   `GetPresetIdsByUid` bulk lookups; thin per-table snapshot insert helpers the applier
   needs (only where missing); `SaveMacro`/`InsertPreset` honor a pre-set `ServerUpdatedAt`
   on insert (avoids a second write). Added 2026-08-25 (round 1 B1 fix): `GetMacros` unions
   the `''` scope into unit-keyed reads; `GetAppliedPreset` falls back to the `''` row when
   the unit has none (exact key wins).
6. `src/ProoveGaming.App/Services/ISettingsSyncService.cs` — edit: add `OnSignedInAsync`.
7. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — edit: pull pipeline + `PullTask`
   seam.
8. `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — edit: trigger in `OnSignInSucceeded`.
9. `src/ProoveGaming.App/App.axaml.cs` — edit: trigger on remembered-session restore.
10. `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` — new: see Test strategy.
11. `tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs` — new: see Test
    strategy.
12. `docs/CONTEXT.md` — edit: `PresetConfigurationApplier`, `Sign-in pull` entries.
13. `docs/plans/add-api-v2-sync/add-api-v2-sync-plan.md` — edit: dated note that the pull
    phase now exists (its "Out of scope: pull" statement gains a pointer here).

## Acceptance criteria

- [ ] Given a signed-in account whose server has 2 macros and 1 preset (with configuration)
  unknown locally, when sign-in completes and `PullTask` finishes, then the local DB has
  those rows under the user owner with the server uids, `server_updated_at` set, and the
  preset's snapshot tables populated (rgb/settings/bindings/actuation) with macro bindings
  resolved to the inserted macros' local ids.
- [ ] Given a local preset with the same uid as a server preset but different content, when
  the pull runs, then the local row and its snapshot are byte-for-byte unchanged.
- [ ] Given a local device type that already has a default preset, when a server preset with
  `is_default=true` for the same type is pulled, then the local default keeps the flag and
  exactly one default exists for the type.
- [ ] Given the owner has saved none of the five known preference keys and the server
  returns a preferences object, when the pull runs, then the five known keys are written
  locally; given any of the five keys already has a local row, nothing is written
  (wording corrected 2026-08-25, round 1 S1).
- [ ] Given `devices/get` returns an `active_preset_uid` resolving to a pulled preset and no
  local applied-preset row exists for that type, when the pull runs, then an applied-preset
  row with `device_key = ''` exists; given any local applied row exists, nothing is written.
- [ ] Given pulled rows in the `''` scope, when a unit-keyed reader queries the same
  (owner, vid, pid) with a non-empty device_key, then `GetMacros` returns the union of the
  unit's own macros and the `''` macros (never another unit's), and `GetAppliedPreset`
  resolves the `''` row unless the unit has an exact-key row, which wins (added 2026-08-25,
  round 1 B1 fix).
- [ ] Given the server answers `preferences/get` with `"preferences": []`, when the pull
  runs, then it treats it as "no preferences" without logging an error or throwing.
- [ ] Given the first list call fails with a network error, when the pull runs, then no
  further API calls are made and `PullTask` completes without throwing.
- [ ] Given `OnSignedInAsync` is called, when it returns, then it has made no HTTP call and
  no storage read on the caller's thread.
- [ ] Given a guest session, when the app runs, then no pull endpoint is ever called.

## Test strategy

New feature (`add-*`) — mandatory coverage, in `tests/ProoveGaming.App.Tests`, following the
`SettingsSyncServiceTests` pattern (temp-file `LocalStorageService` + recording fake
`ISyncApiService`):

- `SettingsSyncServicePullTests` (new): golden path (macros+preset+prefs+device state land
  locally, ordering macros→presets→prefs→devices); uid-collision skip (local untouched);
  default-flag arbitration; preferences zero-rows gate both ways; applied-preset gate both
  ways; `[]`-quirk tolerance; network-error abort; synchronous-return; guest/no-token
  no-op; item that fails DTO binding is skipped while the rest of the list proceeds;
  configuration-null fallback to `presets/get`; unit-keyed readers see the pulled `''`
  rows — union for `GetMacros`, exact-key-wins fallback for `GetAppliedPreset` (round 1
  B1).
- `PresetConfigurationApplierTests` (new): round-trip with `PresetConfigurationBuilder`
  (build → apply into a fresh preset → build again → identical JSON) — the strongest
  correctness check available without a server; unknown JSON keys ignored; null/unresolvable
  `macro_uid` stored as no-macro binding; malformed section (string where array expected)
  skips that section, applies the rest.

Deliberately not covered: live HTTP; the `MainWindow`/`App` trigger wiring (QA steps 1–2);
UI refresh timing of an open details view.

## QA (manual test steps)

1. On machine A (or a first app install), sign into an account, create a macro and a preset
   with a distinctive name, change an RGB effect inside the preset, apply the preset. Wait
   ~10 seconds. (This machine's data reaches the cloud via the existing push/sync.)
2. On machine B (or after uninstall + reinstall on the same machine), sign into the same
   account. Expected: within a few seconds of reaching the Home screen, the device card is
   present; opening the device shows the macro and the preset from step 1 with the same
   names, and the preset contains the RGB change.
3. On machine B, connect the physical device. Expected: the preset that was applied on
   machine A gets applied to the hardware (RGB matches) without any manual action.
4. On machine B, before signing in, use the app as a guest and create a preset named
   "LOCAL-ONLY" on the same device type. Then sign in. Expected: "LOCAL-ONLY" is NOT in the
   signed-in account's preset list (guest data stays with the guest), and the account's
   pulled presets are all present.
5. Disconnect the network, then sign in with a remembered session ("Remember me" restore).
   Expected: the app opens normally with locally stored data; no error dialogs; reconnecting
   the network and restarting the app brings the cloud data in.

## Review focus

1. `thread-safety` — all pull storage writes on the executor; the applier runs inside one
   executor item per preset; no UI-thread storage access; `ReloadOwnerScopedState` posted to
   the UI thread.
2. `async/await` — `OnSignedInAsync` returns without I/O; no sync-over-async.
3. `null-safety` — every pulled DTO field is nullable; per-item skip instead of loop NRE.
4. `DI lifetimes` — applier registered like the builder (singleton over LocalStorageService).

## Performance & quality

- Pull is background, sequential HTTP, not latency-sensitive; no hot paths touched.
- Bulk uid lookups (`GetMacroIdsByUid`) avoid a per-item storage round-trip per pulled
  record: one read per record kind, then dictionary checks in memory.
- Startup: the restore-path trigger must not delay first paint — it fires after the existing
  restore completes, and does nothing synchronously.

## Risks

- **Undocumented list response shapes** — if `presets/list` embeds `configuration`, the
  per-uid `get` fallback never fires; if it embeds nothing, the pull costs one extra call
  per preset. If the real shape binds to neither DTO, items are skipped and logged — pull
  degrades to a no-op rather than corrupting data. Confirm with backend (Open question 1).
- **Applier vs. builder drift** — the round-trip test pins them together; any future
  configuration field must be added to both.
- **Duplicate uid across device types** — uids are globally unique locally (unique index),
  and lookups are by uid alone; a server bug returning the same uid under two types inserts
  once and logs on the second.

## Open questions

1. Do `presets/list` / `macros/list` responses embed `configuration` / `events`? (Assumed
   optional with per-uid `get` fallback.)
2. Should the pull also run periodically / on reconnect rather than only at sign-in? (Phase
   2's reconcile pass subsumes this; out of scope here.)

## Review findings

### Round 1 (2026-08-25) — Ship: no

Scope note: the working tree also carries the already-reviewed add-global-token-expiry
phase (`ApiHttpClient.cs`, `ApiHttpClientTokenRejectionTests.cs`, the MainWindow
token-rejection hunks, `Session_Expired*` strings, the refactor-change-password-flow plan
note) — reviewed in that plan's own journal, out of scope here. This round covers the pull
phase's delta only. Every `git status` entry was attributed to one of the two phases or to
their plan documents; nothing is unaccounted for.

#### Plan conformance

Plan: `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md`

- Implemented as planned: all 13 file-level items are present. Six pull methods + DTOs
  (`ISyncApiService.cs`, `SyncApiService.cs`, `SyncApiDtos.cs` — every response field
  nullable, `JsonElement?` for `configuration`/`preferences`, `"{}"` body on body-less
  calls); `PresetConfigurationApplier` (synchronous, unknown-key/malformed-section
  tolerant, macro_uid → local id resolution with the NOT-NULL drop for key-macro
  bindings); bulk uid lookups + six per-table snapshot insert helpers + insert-time
  `server_updated_at` in `LocalStorageService`; pull pipeline in the plan's
  macros → presets → preferences → device-state order with additive uid-skip, default-flag
  arbitration via `PromotePresetToDefault`, per-uid get fallback, `[]`-quirk tolerance,
  abort-on-network-error; both triggers; applier registered singleton
  (`App.axaml.cs:204`); `docs/CONTEXT.md` and the sync-plan pointer updated.
- Deviations (the coder's five logged ones, each verified):
  1. **`PullCompletedWithChanges` event** instead of the service calling
     `HomeViewModel.ReloadOwnerScopedState()` directly — verified sound: the service stays
     UI-free, `MainWindow` subscribes (ctor) / unsubscribes (`OnClosed`) and marshals via
     `Dispatcher.UIThread.Post` (`MainWindow.axaml.cs:191–197`); raised outside the
     pipeline try so a faulting subscriber cannot read as a pull failure, and only when
     ≥1 record was written. A better shape than the plan's.
  2. **`Aborts` gains an operation label** (`"Upgrade push"` / `"Sign-in pull"`) and now
     also aborts on `TOKEN_REQUIRED` / `INVALID_OR_EXPIRED_TOKEN`
     (`SettingsSyncService.cs:667–692`) — verified; the token-rejection abort is the
     token-expiry phase's cross-cutting requirement and is tested on both the push
     (`Push_TokenRejectedMidPush…`) and pull (`Pull_TokenRejectedOnFirstCall…`) sides.
  3. **Pulled macros land under `device_key = ''`** — logged, but its consequence was
     not: the `''` scope is invisible to every unit-scoped reader. This is **B1**.
  4. **Restore trigger after MainWindow resolution** (`App.axaml.cs:135–141`) — verified
     correct: `UserProfile.SessionToken` is rehydrated at `App.axaml.cs:78` before the
     trigger fires, and `MainWindow`'s `PullCompletedWithChanges` subscription is armed
     (ctor) before the pull can complete. First paint is not delayed (synchronous return).
  5. **Preferences gate = "owner has none of the five known keys"** (via `ReadPreferences`
     returning null, `SettingsSyncService.cs:593`) rather than the plan's literal "zero
     `app_preferences` rows" — verified harmless and arguably more precise (an unrelated
     preference row cannot block hydration of the five sync'd keys); plan wording is now
     stale → S1.
- Missing / partial: none at the DB level. At the UX level QA steps 2 (macro visible) and
  3 (auto-apply on connect) do not hold for serial-keyed units — see B1.
- Accepted limitation (per plan): no live refresh of an open Device Details view mid-pull;
  guest sessions never pull (verified: `OnSignedInAsync` is not called on the guest paths,
  and the no-token guard makes a stray call a zero-HTTP no-op — tested).
- Acceptance criteria: all nine are met as written (they are DB-level) and each has a
  matching test in `SettingsSyncServicePullTests` / `PresetConfigurationApplierTests`.
  Criterion 4's wording inherits the S1 staleness.

#### Code

- [ ] **B1. Pulled macros and the pulled applied-preset row live in the `''` device-key
  scope that no unit-scoped reader ever queries** —
  `src/ProoveGaming.App/Services/SettingsSyncService.cs:528` (SaveMacro with
  `string.Empty`) and `:692` (SetAppliedPreset with `string.Empty`) — Blocker
  **Problem:** `DeviceKey` is the Windows Container ID (per physical unit, empty only when
  unreadable — `DevicesHelper.cs:21–24`), and `SettingsDeviceKey` mirrors it for every
  product except merged-transport ones (TAIPAN), which alone use `''`
  (`HomeViewModel.cs:1168–1193`). Every consumer is an exact-key lookup: the macro library
  loads `GetMacros(..., SettingsDeviceKey)` (`DeviceDetailsViewModel.Buttons.cs:138–140`),
  device-write binding composition drops macro bindings whose macro is not in that scoped
  list (`DeviceDetailsViewModel.Presets.cs:903–904`, comment: "dropped rather than sent
  bodiless"), key-macro resolution searches the same collection
  (`DeviceDetailsViewModel.KeyAssignments.cs:1352`), and reapply-on-connect reads
  `GetAppliedPreset(..., SettingsDeviceKey)` (`DeviceDetailsViewModel.Presets.cs:790–791`)
  with no `''` fallback.
  **Why it blocks:** machine B, signed into an account with a macro and an applied preset,
  connects a keyboard whose Container ID is readable (the normal case). The pull correctly
  lands the macro under `''` and the applied row under `''` — and then: the Macros page is
  empty (QA step 2 fails), a pulled preset's macro bindings are dropped or unresolvable at
  device-write time, and the cloud's active preset is never auto-applied on connect (QA
  step 3 fails). The feature's stated goal ("a second machine starts with the account's
  data") holds only for merged-transport products and serial-less devices. Note the
  applied-preset half is plan-conformant — the plan itself prescribes
  `device_key = ''` and assumes the reapply flow consumes it, which is false — while the
  macro half is the coder's logged deviation 3; either way the same fix is needed.
  **Solution options:**
  - Option A (correct): make `''` a model-wide shared scope on the read side —
    `GetMacros` unions the unit scope with the `(owner, vid, pid, '')` rows (dedupe by
    id), and the connect path falls back to the `''` applied row when no unit row exists
    (e.g. in `ReapplyLastCustomPresetAsync`, optionally re-keying it to the unit on first
    use). Matches the push's scope collapse in the other direction; writes stay additive.
    Verify the macro-scope tests (`LocalStorageServiceMacroScopeTests`) still express the
    per-unit isolation the migrate-macros-per-device plan chose — the union must not leak
    unit-A macros to unit B, only `''`-scoped ones to both.
  - Option B (quick, product decision): ship presets-only hydration now — amend this
    plan's Scope/QA to state macros and auto-apply become visible in the live-sync phase,
    and log the deferral. Guts two QA steps; needs an explicit human owner's sign-off,
    not a silent downgrade.
- [ ] **S1. Plan prose has drifted from the shipped shape** —
  `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` (Recommended approach §4,
  Acceptance criterion 4) — Should-fix
  **Problem / why it matters:** the plan still describes the trigger as posting
  `HomeViewModel.ReloadOwnerScopedState()` from the service (the code raises
  `PullCompletedWithChanges` and MainWindow marshals) and gates preferences on "zero
  preference rows" (the code gates on "none of the five known keys"). Per the repo's
  plan-keeping rule the live sections must match reality or the next round reviews the
  wrong contract.
  **Fix:** update §4's trigger bullet and acceptance criterion 4's wording (and the
  Preferences bullet in §3) to the event-based reload and the five-known-keys gate, as a
  dated correction. Two sentences; no code change.

#### Tests

- [x] The gap that would have caught B1: no test reads pulled data through a **unit-keyed**
  reader. Add (alongside the B1 fix) a test asserting that after a pull, `GetMacros` for a
  non-empty device_key surfaces the `''`-scoped macros, and that the connect path resolves
  the `''` applied row for a unit key — `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs`.
- Existing coverage is otherwise complete against the Test strategy: golden path with
  ordering, uid-collision byte-for-byte no-touch (builder round-trip comparison),
  default-flag arbitration both ways, preferences gate both ways, applied-preset gate,
  `[]` quirk, network-error and token-rejection aborts, synchronous return (gated
  semaphore), guest no-op, per-item binding skip, per-uid get fallback, and the
  build→apply→build round-trip plus tolerance tests for the applier. All nine acceptance
  criteria map to at least one test.

#### Nits

- `(byte)e.KeyCode` silently wraps out-of-range server key codes (e.g. 300 → 44) —
  `src/ProoveGaming.App/Services/SettingsSyncService.cs:519`; the applier's `GetByte`
  range-checks, the macro event path could skip the same way.
- `(MacroRepeatMode)(dto.RepeatMode ?? 0)` stores out-of-enum values unvalidated — same
  file, line 510; harmless today, worth a clamp when convenient.
- A fast sign-out/re-sign-in overlaps two pulls for the same owner; both read their uid
  maps up front, so the loser of a same-uid insert dies on the unique index and logs at
  Error despite being benign. The code comment acknowledges the scenario
  (`SettingsSyncService.cs:358–362`); chaining the new pull onto the old `PullTask` would
  silence it.

#### What's done well

- The applier's tolerance design (per-section kind checks, per-item skip, PK dedupe before
  insert) paired with the build→apply→build round-trip test is exactly the right contract
  for an undocumented shape — future field drift fails a test instead of corrupting data.
- Executor discipline is airtight: preset insert + configuration apply share one
  `RunOnStorageAsync` delegate, bulk uid reads replace per-item round-trips, and every
  `default(T)`-on-shutdown result is treated as "stop pulling".
- The synchronous-return test (semaphore-gated first HTTP call, asserting the method
  completed while the pull is visibly in flight) pins the plan's no-I/O-before-return
  criterion better than any comment could.
- Insert-time `server_updated_at` on `SaveMacro`/`InsertPreset` (one statement instead of
  insert-then-set) with the "every other caller leaves it null" note.
- `docs/CONTEXT.md` entries and the cross-plan pointers were not forgotten.

#### Verdict

Ship: no — B1: the pull faithfully lands the account's macros and active-preset state in
a scope that no unit-keyed reader consults, so on the common serial-keyed device the
feature's own QA steps 2 and 3 fail; presets themselves (VID/PID-keyed) hydrate and
display correctly. Everything else — API surface, applier, additive merge, triggers,
tests — is solid and should not be reworked while fixing it.
First: 1) B1 (read-side `''` union + connect-path fallback, or an owner-approved
presets-only descope), 2) the B1-shaped test in `SettingsSyncServicePullTests`, 3) S1 plan
reconciliation.
