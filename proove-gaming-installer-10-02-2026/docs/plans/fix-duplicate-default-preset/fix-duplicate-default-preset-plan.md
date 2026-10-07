# fix-duplicate-default-preset — Stop the seed racing the sign-in pull

Date: 2026-08-28. Field report with screenshot: a Rampage shows two default-role presets,
"Default" (en) and "За замовчуванням" (uk), the latter carrying the badge.

## Goal

`CreateDefaultPresetIfNotExists` seeds a default preset the moment a device page opens over an
empty scope. On a signed-in account whose cloud already holds presets, "empty" is usually a
**lie in transit**: the sign-in pull hydrates the scope moments later. The seeded row carries a
brand-new uid, so the pull's uid dedup cannot connect it to the server's existing default; the
`SyncCoordinator` then pushes it, and the **server** accumulates one default per wipe/re-install
— each named in whatever UI language was active at seed time. Verified against the reporter's
live DB: all five user presets were inserted by one pull (created_at within 60 ms), and the
Ukrainian default was born server-side six minutes earlier, exactly one local-DB wipe apart
from the English one. The fix has two halves: gate the seed on the pull having settled, and
teach the pull's default arbitration to supersede a **never-synced** seeded default instead of
keeping it beside the server's.

## Scope

**In:**
1. **Seed waits for the pull.** In `DeviceDetailsViewModel.LoadPresetsForDeviceAsync`
   (~`:1741`), when the owner is a signed-in user, await `ISettingsSyncService`'s pull task
   before `CreateDefaultPresetIfNotExists`. The pull task never throws and completes on
   network failure/abort, so this is bounded; guests and the no-pull path are unaffected.
2. **Pull supersedes an unsynced seeded default.** In `SettingsSyncService.PullCoreAsync`'s
   preset default arbitration: when the local scope's only default is **never-synced**
   (`server_updated_at IS NULL`) and an incoming server preset for the same scope carries
   `is_default`, delete the local seeded row (cascade its snapshot) and honor the server's
   flag. Deleting is safe under exactly this predicate: the row was auto-created seconds-to-
   minutes ago, has never reached the server, and its snapshot is a copy of the live device
   state that still exists in the `device_*` tables. A local default that HAS been synced is
   never touched (current local-wins arbitration stands).
3. Tests for both halves; repro first (bug-fix rule).

**Out:**
- Healing servers that already hold two synced defaults (the reporter's current state). Both
  rows are real synced data; auto-deleting either is the class of destructive guess this repo
  refuses. The spurious one is deletable by hand — it no longer carries the badge, so the ⋮
  menu allows it. Recorded under Risks as the accepted residual.
- Storing a language-neutral default-preset name. The name is user data (renameable); the
  role is already carried by `is_default` + the localized badge. Once duplicates cannot be
  created, a name frozen in its seed-time language is cosmetic. Flagged as an Open question
  for product, not implemented.
- Any change to guest seeding, the guest→user upgrade push, or `CreateDefaultPresetIfNotExists`
  itself (its is_default-based dedup is correct; the bug is who calls it, when).
  - *Dated note (2026-08-28, round-1 fix pass):* one narrow exception landed after review —
    the Case 3 seed INSERT now marks user-owned rows `is_dirty = 1` (guest rows stay clean,
    matching `InsertPreset`'s rule). The review tightened the supersede guard to
    `server_updated_at IS NULL AND is_dirty = 1` (finding S2), and the seed was born clean
    (its raw INSERT bypassed `InsertPreset`, relying on the schema default), so without this
    the guard could never match the very row it exists for. The seed is user-born content
    the coordinator is expected to push, so dirty is also the honest state. The is_default
    dedup logic itself is untouched.

## Options considered

### A. Gate the seed on the pull + supersede unsynced seeds in the pull (recommended)
Both halves are needed: the gate closes the common races (sign-in on a fresh machine, click
the card before hydration lands), the supersede closes the offline path the gate cannot see
(DB wiped, seed + coordinator push fail offline, sign-in later).
- Pros: no schema change; the supersede predicate (`server_updated_at IS NULL`) is exact —
  it cannot fire on anything a user has had long enough to sync.
- Cons: the seed path gains an await on a background task; must not deadlock or stall the
  page open when the pull hangs on network (it cannot — `Aborts` bounds every call and the
  task completes on abort).

### B. Ask the server before seeding
Check `presets/list` for the scope before seeding a default.
- Cons: puts a network round-trip on the device-page open path (forbidden — page opens must
  not wait on the network), and still fails offline. Rejected.

### C. Only the gate, no supersede
- Cons: the offline wipe path keeps minting server-side duplicates; that is exactly the
  reporter's second default. Rejected as incomplete.

**Decision: A.**

## File-level change list

1. `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` — extend: repro + supersede
   coverage (see Test strategy). Lands first.
2. `tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs` (or the closest
   existing presets-VM suite) — extend: the seed waits for a parked pull task; guests do not.
3. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — pull default arbitration gains the
   supersede branch; expose the pull-settled task the VM awaits if the existing `PullTask`
   seam is not already sufficient.
4. `src/ProoveGaming.App/Services/LocalStorageService.cs` — a `DeleteNeverSyncedPreset(int id)`
   (cascade, guarded on `server_updated_at IS NULL` inside the same transaction) if
   `DeletePreset` cannot express the guard; no tombstone for a never-synced row (existing
   rule).
5. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — await
   the pull before the seed for user owners; comment stating why.
6. `docs/CONTEXT.md` — one-liner for the seed-vs-pull ordering rule.
7. `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` — dated note: default arbitration
   extended with the unsynced-seed supersede.

Added in the round-1 fix pass (2026-08-28):

8. `src/ProoveGaming.App/Services/LocalStorageService.cs` — S2: `DeleteNeverSyncedPreset`
   guard tightened to `server_updated_at IS NULL AND is_dirty = 1`; the Case 3 seed in
   `CreateDefaultPresetIfNotExists` marks user rows dirty (see the dated Scope note);
   `GetPresets` now populates `IsDirty` so the arbitration pre-check can read both halves
   of the predicate.
9. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — arbitration pre-check reads
   `{ ServerUpdatedAt: null, IsDirty: true }` to match the guard.
10. `src/ProoveGaming.App/Services/SyncCoordinator.cs` — S1 hardening: `RunPassAsync`
    awaits `ISettingsSyncService.PullTask` before enumerating dirty rows, extending the
    constructor's pass-after-pull ordering to mid-pull signals.
11. `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` — refuse-path test:
    a clean row with a NULL `server_updated_at` (server-held shape) is kept.

## Acceptance criteria

- [x] Given a signed-in user whose server holds a default preset for a scope and an empty
  local scope, when the device page opens while the pull is still in flight, then after both
  settle the scope holds exactly ONE default-role preset — the server's — and no new preset
  was pushed. (Repro: fails today — a second default with a fresh uid is seeded and pushed.)
- [x] Given the same but the pull already completed before the page opens, then no seed
  happens at all (scope is non-empty by the time the seed runs).
- [x] Given a local never-synced seeded default and an incoming server default for the same
  scope, when the pull merges, then the local row and its snapshot rows are deleted, the
  server's preset carries the flag, and no tombstone is written.
- [x] Given a local default that HAS a `server_updated_at`, when a server default for the same
  scope arrives, then the existing local-wins arbitration is unchanged (server flag dropped).
- [x] Given a guest session, when a device page opens over an empty scope, then the seed runs
  immediately — no pull is awaited.
- [x] Given the pull aborts on a network error, then the awaited task completes and the seed
  proceeds (offline-first behaviour preserved).

## Test strategy

Bug fix: repro first, proven failing pre-fix (detached worktree at HEAD, removed after).

- `SettingsSyncServicePullTests` (extend): the supersede — local unsynced default + server
  default → local deleted (row + snapshot), server flag honored, no tombstone; the negative —
  local synced default keeps winning; abort-path still completes the task.
- Presets-VM suite (extend): with the fake sync service's pull task parked, the seed does not
  run until it is released (user owner); runs immediately for a guest; runs after a
  network-abort completion.

Not covered: the real multi-machine/server round trip (QA), and healing pre-existing synced
duplicates (out of scope).

## QA (manual test steps)

1. On a signed-in account that already has presets on the server (machine A configured them),
   delete the local database, launch the app, sign in, and immediately open the device's page.
   Expected: after a few seconds the preset list shows the account's presets with exactly one
   "default" badge — no extra "Default"/"За замовчуванням" pair appears, in either language.
2. Repeat step 1 with the interface in the other language. Expected: same result; the default
   preset keeps its original name.
3. Turn networking off, delete the local database, launch and use the device page as a guest —
   expected: a default preset appears immediately (guests never wait). Then sign in once the
   network is back. Expected: the account's server presets appear and only one carries the
   badge.
4. Existing duplicates from before this fix: open the ⋮ menu on the non-badged duplicate and
   delete it. Expected: it deletes normally and does not come back after a re-login (the
   delete tombstones it server-side).

## Review focus

1. `async/await` — the new await on the page-open path must be bounded and never block a
   guest or offline open.
2. `thread-safety` — the supersede delete runs inside the pull's storage-executor items; the
   never-synced guard must be evaluated in the same transaction as the delete.
3. `null-safety` — arbitration touches optional server fields.

## Performance & quality

The await is on an already-running background task, not new work; page opens for guests and
offline users are unchanged. The supersede adds one guarded delete inside an existing
executor item. No hot paths.

## Risks

- **Existing server-side duplicates are not healed.** Accepted: hand-deletable via the UI,
  and auto-deleting synced data is worse than the symptom. If support volume says otherwise,
  a server-side cleanup is the right tool.
- The seed now depends on the pull task's "completes on every exit" contract; if a future
  pull path forgets that, device pages for signed-in users would hang at open. The VM-level
  test with a parked task guards the wait; the contract itself is pinned in the sync tests.
  The coordinator's `RunPassAsync` awaits the same task (round-1 fix pass), so the same
  contract failure would also stall push passes — one more reason the contract stays pinned.
- **Coordinator-push vs supersede race (round-1 finding S1) — accepted bounded residual.**
  A coordinator pass overlapping the sign-in pull (a mutator signal firing mid-pull, or a
  re-sign-in starting a NEW pull mid-pass) can interleave its preset create with the
  supersede delete. Worst cases: the create wins and the server accretes exactly one
  duplicate default (the pre-fix symptom, once — manually deletable, no badge); or the
  delete wins mid-create and the duplicate exists server-side only, resurrected locally as
  a non-default row by the next pull. Neither is open-ended accretion and no data is lost.
  The round-1 fix pass narrowed the window by having `RunPassAsync` await `PullTask` before
  enumerating dirty rows (a pass no longer overlaps a pull already running when it starts);
  the remaining window — a pull *starting* mid-pass — needs a re-sign-in timed inside a
  push pass and is accepted.

## Open questions

1. Product: should the default preset's stored name be language-neutral (rendered localized
   until renamed)? Cosmetic once this fix lands; touches the server NAME_REQUIRED contract.

## Review findings

### Round 1 (2026-08-28) — Ship: yes

#### Summary
The change lands both halves of the plan: `LoadPresetsForDeviceAsync` awaits
`ISettingsSyncService.PullTask` before the seed for user owners (interface member added,
threaded through the VM ctor and DI), and the pull's default arbitration deletes a
never-synced local default via the new transactional `LocalStorageService.
DeleteNeverSyncedPreset` (shared `DeletePresetCascadeCore`, no tombstone), honoring the
server's flag. Docs (CONTEXT.md one-liner, dated add-api-v2-pull note) and 7 new tests +
1 modified + 1 fake compile fix. Verified: 1416/1416 App.Tests pass in Release.

#### Plan conformance
Plan: docs/plans/fix-duplicate-default-preset/fix-duplicate-default-preset-plan.md
- Implemented as planned: all 7 File-level change list items. The `PullTask` seam was
  internal-only, so widening it onto the interface is the in-plan "expose … if not already
  sufficient" branch.
- Deviates from plan: `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs:1354` — fake
  gains `PullTask` (compile-forced by the interface change; correct and minimal).
- Missing / partial: Test strategy's VM-level "runs after a network-abort completion" has no
  direct test; behaviorally identical to the released-park / CompletedTask tests (the VM
  cannot distinguish an aborted pull's completed task from a successful one), and the
  abort-completes-PullTask contract is already pinned by pre-existing sync tests
  (`Pull_NetworkErrorOnFirstCall_AbortsWithoutFurtherCallsOrThrow`). Note only.
- Verified contracts: `PullTask` never faults on every exit path — `OnSignedInAsync`'s
  early return leaves the previous completed task in place (never null, never replaced with
  a faulting one); nothing in the `Task.Run` chain lambda or `PullAsync` runs outside a
  try/catch; chain induction holds from the initial `Task.CompletedTask`. Reading the
  property mid-chain-replacement (awaiting the old chain while a re-sign-in starts a new
  one) lets the seed race the NEW pull, but half 2 (the supersede) backstops exactly that.
  DI: `ISettingsSyncService` and `DeviceDetailsViewModel` are both singletons registered in
  `App.axaml.cs` (`:228`, `:237`), so the gate is live in production, not just in tests.
- Acceptance criteria: all six are covered by the new/modified tests (criterion 6 via the
  equivalent coverage noted above). Checkboxes in the Acceptance criteria section are still
  unchecked — tick them in the next round.

#### Code
Blockers: none.

- [x] **S1. Coordinator-push vs supersede race: bounded, but unrecorded in Risks** —
  `src/ProoveGaming.App/Services/SettingsSyncService.cs:613` +
  `src/ProoveGaming.App/Services/SyncCoordinator.cs:453` — Should-fix
  **Problem / why it matters:** A `SyncCoordinator` pass can overlap the sign-in pull only
  when a mutator signal fires mid-pull (pull writes are `markDirty:false` and do not raise
  `SyncRelevantDataChanged` — `LocalStorageService.cs:3093`; the startup pass is ordered
  after the pull by constructor design — `SyncCoordinator.cs:112`). Within that overlap,
  walking the create path against the delete: (1) delete commits before `GetDirtyPresets`
  → clean, closed. (2) create + `SetPresetServerUpdatedAt` commit before the delete's
  in-transaction guard → delete refuses (correct), local-wins keeps the seed — but the
  create pushed `IsDefault: true` (`SyncCoordinator.cs:487`), so the **server accretes one
  duplicate default**. (3) delete commits between the coordinator's dirty-read and its
  write-back → the create still fires (`PresetConfigurationBuilder.Build` returns a
  non-null config even for a deleted id), `SetPresetServerUpdatedAt`/`ClearPresetDirty`
  silently no-op on the dead row, no tombstone exists → the server-side duplicate is
  resurrected locally by the next pull as a synced non-default row. Both racy outcomes are
  **bounded** (one duplicate per occurrence, requires a user edit during the sign-in pull
  plus tight timing) and land in the already-accepted residual class (manually deletable,
  no badge, no data loss) — but the plan's Risks section does not record this.
  **Fix:** Option A (doc-only): add this interleaving to Risks as an accepted residual.
  Option B (structural, small): have the coordinator's pass await
  `_settingsSync.PullTask` before enumerating dirty rows — mirrors the constructor's
  pass-after-pull ordering rationale; no deadlock (the pull never awaits the pass). A is
  the minimum; B closes the overlap for the startup/sign-in window entirely.
- [x] **S2. Guard predicate wider than its meaning: `server_updated_at IS NULL` can match
  rows that ARE on the server** —
  `src/ProoveGaming.App/Services/LocalStorageService.cs:3141` (guard SQL) — Should-fix
  **Problem / why it matters:** Two code paths produce a row that exists server-side yet
  has `server_updated_at` NULL: (a) the coordinator's create getting a 2xx **without an
  echoed `updated_at`** clears dirty but never sets `server_updated_at`
  (`SyncCoordinator.cs:509-513`); (b) a pull-inserted preset when list *and* get omit
  `updated_at` (`SettingsSyncService.cs:553`, `:572` — the `??=` fallback shows the author
  treats absence as possible). If such a row is the scope's default when a later pull
  brings a different server default, `DeleteNeverSyncedPreset` deletes **server data**
  with no tombstone — the row is resurrected on the next pull (as non-default), a
  confusing flip rather than loss, but the guard's stated invariant ("the server has
  nothing to remove") is violated.
  **Fix:** tighten the guard to `server_updated_at IS NULL AND is_dirty = 1`. A seeded
  default is born dirty (`LocalStorageService.cs:3060`), pull-inserted rows are born
  clean, and `ClearPresetDirty` only runs after a push reached the server — so
  `IS NULL AND is_dirty = 1` is exactly "local-born, never pushed". Zero cost, strictly
  more precise; add one guard test (clean-but-NULL row refuses).

#### Tests
- [x] If S2 is taken: `DeleteNeverSyncedPreset_CleanRowWithNullServerUpdatedAt_Refuses` —
  a row with `server_updated_at` NULL and `is_dirty = 0` must be kept —
  `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs`.
- Existing new coverage is sound: the supersede test is repro-shaped (seed via
  `CreateDefaultPresetIfNotExists`, snapshot rows prove the cascade, tombstone absence
  asserted); the modified local-wins test now sets `server_updated_at`, which is what
  keeps acceptance criterion 4 honestly tested post-fix; the VM gate tests are
  deterministic (everything before the gate is synchronous, so `Assert.False(load.
  IsCompleted)` cannot flake today — it will start flaking if anyone adds an earlier
  await to `LoadPresetsForDeviceAsync`).

#### Nits
- The superseded seed can leave a dangling `device_applied_preset` row (no FK to presets —
  `LocalStorageService.cs:514`), which also blocks that same pull's step-4 device-state
  write via the `Count > 0` guard (`SettingsSyncService.cs:707`). Pre-existing tolerance
  class — regular `DeletePreset` leaves the same dangle and the load path documents the
  "applied preset was since deleted" fallback (`DeviceDetailsViewModel.Presets.cs:1812`).
  Note only; no action required.
- `FakeSettingsSyncService`'s two events are declared but never raised → CS0067 warnings —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetsViewModelTests.cs:60-61` (the
  SyncCoordinatorTests fake raises its events, so it does not warn).

#### Requested rulings
- **(a) `PullTask` public on the interface — right call.** The consumer is a VM injected
  with `ISettingsSyncService` and faked in tests through the interface; an internal member
  on the sealed concrete class would force concrete coupling in the VM and make the
  interface fakes impossible. Only implementers are the production class and the two test
  fakes, both updated. The contract comment on the interface member pins the semantics the
  VM depends on.
- **(b) Supersede log level — Information is correct.** Rare, one-shot, deletes a local
  row, and has field-diagnostic value ("where did my seeded default go"); Debug would bury
  a deletion, Warning would mis-signal an error. Matches the sibling Information logs in
  the same file.
- **(c) Delete-vs-coordinator-create race — closed in the common orderings, bounded in the
  racy ones.** Full walk under S1. Headline: if create wins, the guard refuses correctly
  and the server accretes exactly one duplicate (the pre-fix symptom, once, manually
  deletable); if the delete wins mid-create, the duplicate is server-side only and
  resurrects locally as a non-default on the next pull. Neither is open-ended accretion;
  the common path (no mid-pull coordinator pass) is fully closed by the gate + supersede.

#### What's done well
- The supersede predicate is re-evaluated inside the delete's own transaction, so the
  earlier-executor-item read can never delete a row that synced in between — and the
  refuse path degrades to the pre-existing local-wins rule instead of a special case.
- `DeletePresetCascadeCore` extraction keeps the legacy-DB FK rationale in one place
  instead of duplicating the 8-statement cascade.
- `DeleteNeverSyncedPreset` deliberately does not raise `SyncRelevantDataChanged` —
  nothing to push, and it avoids waking the coordinator mid-pull.
- The modified local-wins test (adding `SetPresetServerUpdatedAt`) is exactly the edit
  that keeps the old test meaning what it claims after the behavior split.

#### Verdict
Ship: yes — no blockers; S1 is a documentation/hardening decision on a tightly bounded
residual race and S2 is a one-line guard tightening plus one test. First: (1) S2 —
`AND is_dirty = 1` in the guard + refuse test; (2) S1 — record the residual in Risks
(and decide on the pass-after-pull ordering); (3) tick the Acceptance criteria boxes.
