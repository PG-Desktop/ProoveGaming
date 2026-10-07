# Fix owner-scoped state persistence

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-08-27
Branch: `features/newapi`
Status: implemented; reviewed round 1 (2026-08-27, Ship: yes) — pending the developer's final review and merge

Related plans (all describe behaviour this change touches):

- [`fix-duplicate-device-cards`](../fix-duplicate-device-cards/fix-duplicate-device-cards-plan.md) —
  authoritative for card identity, `AddDevice`'s dedupe rule and the one-time heal.
- [`persist-devices-and-notifications`](../persist-devices-and-notifications/persist-devices-and-notifications-plan.md) —
  original contract for `devices`, `notifications`, `DeleteDevice`, and the sign-in/sign-off hooks.
- [`add-per-device-identity`](../add-per-device-identity/add-per-device-identity-plan.md) —
  owns the `device_key` / two-identical-units contract.
- [`ui-thread-storage-async`](../ui-thread-storage-async/ui-thread-storage-async-plan.md) —
  owns the async shape of the notification writes and the deferral list that names `DeleteDevice`.
- [`contact-us-and-update-check`](../contact-us-and-update-check/contact-us-and-update-check-plan.md) —
  asserts the update-available bell entry "persists into the owner's stored history".

## Goal

Five defects sit in one layer — owner-scoped local state, split between
`HomeViewModel` (the adopt/revive/bell logic) and `LocalStorageService` (the `devices` and
`notifications` tables). Two of them can destroy a user's stored device cards irrecoverably, one
means the notification bell has **never** persisted a single row on some launch paths, one is a
product decision to confirm, and one is a latent invariant mismatch between two delete paths. This
plan fixes them together, because they share the same invariant — *what identifies a stored row,
and who owns it* — and fixing them separately would mean three passes over the same two methods.

## Scope

In scope:

1. **D1 — `HomeViewModel.ReloadOwnerScopedState` writes before it reads** (`:832-864`). The adopt
   loop persists every on-screen card (`PersistDeviceCard` → `LocalStorageService.AddDevice`)
   *before* the revive loop reads `GetDevices`. The revive pass therefore classifies rows that the
   adopt pass has already mutated, and its correctness depends on a destructive side effect in the
   storage layer rather than on a stated rule.
2. **D2 — `LocalStorageService.HealDuplicateDeviceRows` (`:931`) can delete a second physical
   unit.** The heal groups by `(owner_kind, owner_id, name)` and keeps `MAX(id)`, ignoring
   `device_key` entirely. Its stated safety argument holds only for a two-row group.
3. **D3 — bell notifications raised before an owner exists are never persisted**
   (`AddNotification:432-466`), and are then wiped from the screen by `LoadNotificationsForOwner`
   when the owner resolves.
4. **D4 — guest notification lifetime** (`ClearSession:2483`, `ClearData:2536`): confirm or change.
5. **D5 — `LocalStorageService.DeleteDevice(int id)` (`:3894`) is not owner-scoped**, while
   `AddDevice`'s delete-by-id was made owner-scoped on this branch (`:3829-3838`).

Out of scope (recorded under Open questions / Risks, not changed here):

- Whether the adopt pass should adopt **disconnected** cards belonging to a departing guest into
  the arriving user's account (a product decision — see Open questions Q1).
- Moving `ReloadOwnerScopedState`'s synchronous SQLite calls off the UI thread (see
  Performance & quality).
- Anything in `device_settings` / `presets` / `macros`.

### Constraint that shaped the options

**The device catalog is only available for physically present devices.** `deviceservice.proto`
exposes no catalog-enumeration RPC — the App learns a product's `merge_transports`,
`settings_product_id` and display name only when a unit of that product is *connected right now*.
Every option below that would need to ask "is this stored row a merged product?" at startup, or
during a schema migration, is therefore impossible: the answer is only knowable for hardware that
happens to be plugged in at that moment. This is the reason D2's heal has to guess, and the reason
the recommended fix for D2 makes the guess *conservative* instead of trying to make it *correct*.

## Options considered

### D1 — adopt/revive ordering

| Option | Trade-offs |
| --- | --- |
| **A. Snapshot `GetDevices` before the adopt loop, keep the revive filter as-is (`CardIdentity` match only).** | Simple, but **regresses shipped behaviour**: a legacy row stored with `merge_transports = 0` under a name whose live card is merged has a *different* `CardIdentity`, so it would be revived as a permanently-disconnected ghost card — exactly what `fix-duplicate-device-cards` AC "then the device list contains **one** card for that name and it is the connected one" forbids, and what `SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne` pins. |
| **B. Snapshot before the adopt loop **and** make the revive filter apply the same *swallow rule* `AddDevice` applies** (merged on-screen card swallows every stored row with the same name, case-insensitively; a non-merged card swallows the exact `(name, vid, pid, key)` tuple). | Behaviour-identical to today on every case we can construct, but the revive decision no longer depends on the delete having already happened. One rule, stated once, used by both sides. Slightly more code than A. |
| **C. Revive first, then adopt.** | Works, but leaves the revived view-models to be adopted in the same pass, so the loop mutates the collection it is iterating and the "adopt everything on screen" set changes underneath it. More moving parts than B for the same result. |

**Chosen: B.**

Honest severity note for the orchestrator/reviewer: after reading both rules, `AddDevice`'s
delete predicate and the revive skip predicate **coincide today**, so the reorder is
behaviour-preserving and *no reproduction test can be written that fails on current code for D1
alone*. D1 is a real defect of construction, not of current output: the moment either rule moves
independently — which D2 does to the population of rows, and which a future device sync would do
by writing `devices` rows from the server — the revive pass starts silently deleting or
resurrecting cards. It is treated here as a characterization-test-first change (see Test strategy),
not as a bug fix with a failing repro. See Open questions Q4.

### D2 — the heal migration

| Option | Trade-offs |
| --- | --- |
| **A. Leave it.** | On the first launch of the new build, any `(owner, name)` group holding ≥2 distinct `(vendor_id, product_id)` pairs loses every row but `MAX(id)`. A group containing *two units of one model* plus *one extra transport row for one of them* has 2 distinct pairs, so both units collapse — the second unit's card silently disappears and both are flagged `merge_transports = 1`. One-shot and irreversible. |
| **B. Make the heal non-destructive (flag only), collapse in the read path.** | Nothing is ever deleted on a guess; the physical collapse happens later under catalog authority (the next `AddDevice` for that card). But `GetDevices`/`LoadDevices` would need a de-dup pass they do not have today, and the shipped AC "then one row remains (the one with the highest id)" becomes false. Larger blast radius than the defect. |
| **C. Keep the delete, add a bail-out guard: never touch a group that holds evidence of two physical units** — i.e. two rows sharing `(vendor_id, product_id)` but differing in `device_key`. | The reported RAMPAGE ghost (one row per transport, all pairs distinct, no repeated pair) still collapses at startup exactly as shipped. A group that *proves* two units exist is left completely alone: no delete, no flag. Cost: such a user keeps their ghost card until the device next connects, when `AddDevice` collapses it correctly. |

**Chosen: C.** It preserves every shipped acceptance criterion, is expressible in the same single
SQL statement pair, and turns an irreversible guess into a conservative one. Option B is the right
answer if the product later decides no migration may ever delete a row; it is recorded as Q3.

### D3 — notifications raised before an owner exists

Established facts: `HomeView` is in `MainWindow`'s visual tree from window open, so
`HomeView.OnLoaded` → `HomeViewModel.Start()` begins discovery while the user is still on the
Welcome screen. On a launch with remembered auth or a restored guest session the owner already
exists at that point and everything persists. The gap is the launch that lands on Welcome with no
owner: every "New device connected" entry raised in that window is memory-only, then
`LoadNotificationsForOwner` clears the list at sign-in. The user's live database confirms the end
state — the `notifications` table has zero rows and no `sqlite_sequence` entry, i.e. not one row
was ever inserted, while `devices`/`presets`/`macros` rows for the same user exist.

| Option | Trade-offs |
| --- | --- |
| **A. Do not raise notifications while there is no owner.** | Smallest change, and consistent with `MainWindow.SignOutToFreshSignIn`'s deliberate position ("With no owner left the entry stays in-memory only — right, it belongs to no account"). But the user plugs a device in on the Welcome screen, signs in, and the bell says nothing ever happened. It also silently contradicts `contact-us-and-update-check`'s AC that the update entry persists. |
| **B. Buffer pre-owner entries and flush them to the **first** owner that resolves; after any owner has existed, a no-owner entry stays memory-only.** | The events describe hardware present at launch, which the arriving owner is about to see connected anyway, so attributing them to that owner matches what the user expects. The "first owner only" latch is what stops the other direction leaking: an entry raised **after** a sign-out (the session-expired toast) must never flush into the *next* account that signs in on the same machine. | 
| **C. Defer `Start()` until an owner exists.** | Delays device discovery behind the sign-in form — the Home screen would then populate from scratch after sign-in, costing seconds of visible emptiness, and it changes device-connection behaviour to fix a notification bug. Rejected. |

**Chosen: B**, implemented without a second collection: the items to flush are exactly the ones
already in `Notifications` with `StorageId == 0`, captured before the list is cleared. A one-shot
latch (`_ownerEverResolved`) is set the first time `LoadNotificationsForOwner` sees an owner, so
the flush can happen at most once per process and the post-sign-out case keeps today's semantics.

### D4 — guest notification lifetime

Options are: keep (deleted at sign-in and at guest expiry, migrated only on guest→register) or
preserve guest history across sign-in. **Chosen: keep, unchanged, and document it.** The rationale
is already load-bearing in three places and this change must not quietly reverse it: guest rows are
keyed by the finished session's uuid, so after sign-in no future owner can address them (they would
be permanent orphans, not reusable data); `OnSignInSucceeded` states that pulling this machine's
guest data into an existing account "was reported as a bug"; and guest→register is the one
transition where the guest *is* the new account, which is why `MigrateGuestToUserAsync` re-keys
them there. Preserving them across sign-in would mean re-keying guest rows into an account that
already owns its history — the exact migration the team removed. No code change. Recorded as a
product-owner confirmation (Q2), not a blocker.

### D5 — `DeleteDevice`

Options: leave it (every current caller is guarded by an owner check) or scope it like `AddDevice`.
**Chosen: scope it.** Two delete paths in one table disagreeing about the same invariant is how the
next caller gets it wrong; a guest's `StorageId` comes from `UserSession`'s own counter starting at
1, so an unscoped delete-by-id is one unguarded call away from deleting a stranger's row.

## Recommended approach

1. **`ReloadOwnerScopedState`** reads the owner's stored rows into a local snapshot **before** the
   adopt loop runs, and the revive loop filters that snapshot with a single shared predicate that
   states the swallow rule explicitly. Extract the predicate as an `internal static` method on
   `HomeViewModel` next to `CardIdentity` (same file, same test seam), so `/reviewer` and
   `/test-writer` can drive it directly:

   ```
   // "Is this stored row already represented by a card on screen?" — the read-side twin of
   // AddDevice's delete predicate. Merged card  → same name (case-folded).
   //                                Non-merged  → same (name, vendorId, productId, deviceKey).
   internal static bool IsRepresentedOnScreen(DeviceRecord record, IReadOnlyCollection<CardKey> onScreen)
   ```

   where `CardKey` carries the card's `Name`/`VendorId`/`ProductId`/`DeviceKey`/`MergeTransports`.
   Keep `CardIdentity` as-is and use it for the identity half; the swallow half adds the
   merged-name case that today only `AddDevice`'s `DELETE … WHERE name = $name COLLATE NOCASE`
   expresses.

2. **`HealDuplicateDeviceRows`** keeps its `UPDATE` + `DELETE` pair inside one transaction and
   gains a bail-out on the flagging `UPDATE`. `device_key` is added at `EnsureSchema:554`, before
   the heal at `:560`, so the column is guaranteed to exist here (in a database old enough that
   every key is `''`, the guard is simply inert and behaviour equals today):

   ```sql
   UPDATE devices SET merge_transports = 1
   WHERE EXISTS (
       SELECT 1 FROM devices AS other
       WHERE other.owner_kind = devices.owner_kind
         AND other.owner_id   = devices.owner_id
         AND other.name       = devices.name COLLATE NOCASE
         AND (other.vendor_id <> devices.vendor_id OR other.product_id <> devices.product_id)
   )
   -- Bail-out: a group that holds two rows with the SAME (vendor_id, product_id) and DIFFERENT
   -- device_key is proof of two physical units of one model (add-per-device-identity). We cannot
   -- ask the catalog whether the product is merged -- it only answers for hardware connected right
   -- now -- so such a group is left entirely alone: not flagged, and therefore not deleted.
   AND NOT EXISTS (
       SELECT 1 FROM devices AS a
       JOIN devices AS b
         ON  a.owner_kind = b.owner_kind
         AND a.owner_id   = b.owner_id
         AND a.name       = b.name COLLATE NOCASE
         AND a.vendor_id  = b.vendor_id
         AND a.product_id = b.product_id
         AND a.device_key <> b.device_key
       WHERE a.owner_kind = devices.owner_kind
         AND a.owner_id   = devices.owner_id
         AND a.name       = devices.name COLLATE NOCASE
   );
   ```

   The `DELETE` statement is unchanged: it only removes rows with `merge_transports = 1`, and a
   bailed-out group now has none. Update the comment block at `:915-930` — its current safety
   claim ("two identical physical units always share one pair … so their group has a single pair
   and is never touched") is false for a group of three and must not survive the fix.

3. **`AddNotification` / `LoadNotificationsForOwner`** gain the one-shot pre-owner flush.
   `LoadNotificationsForOwner` becomes: if no owner has ever resolved and one exists now, capture
   the current `Notifications` items with `StorageId == 0` **before** clearing the list, persist
   each through the existing `PersistNotificationAsync` (which already routes through the storage
   executor), set the latch, and chain `NotificationsLoaded` so it completes only after both the
   flush and the reload. FIFO ordering on the storage executor guarantees the reload's
   `GetNotifications` observes the flushed inserts, so each entry appears exactly once and comes
   back with a real `StorageId`. `AddNotification` itself is unchanged apart from its comment.

4. **`DeleteDevice`** becomes `DeleteDevice(string ownerKind, string ownerId, int id)` with
   `DELETE FROM devices WHERE id = $id AND owner_kind = $ok AND owner_id = $oi`, mirroring
   `AddDevice:3833`. Update the single production caller
   (`HomeViewModel.ExecuteConfirmDelete:2266`, which already has the owner in hand) and the two
   test call sites.

5. **D4** is a documentation-only outcome: state the confirmed lifetime in the plan and in
   `docs/CONTEXT.md`; no code change.

## File-level change list

Tests land first (items 1–3), per the bug-fix rule.

1. `tests/ProoveGaming.App.Tests/LocalStorageServiceDeviceIdentityTests.cs` — edit: add the D2
   reproduction (`LegacyDb_TwoUnitsPlusAnExtraTransportRow_KeepsBothUnits`) and a guard-inertness
   case proving the shipped RAMPAGE collapse still happens. Reuses the file's existing
   `CreateLegacyDevicesDb` helper and temp-file store.
2. `tests/ProoveGaming.App.Tests/HomeViewModelPersistenceTests.cs` — edit: the D3 reproduction
   (`NotificationsRaisedBeforeAnyOwner_ArePersistedToTheFirstOwner`), a companion asserting the
   post-sign-out entry is **not** flushed to the next owner, and a D1 characterization test that
   the revive decision is taken against the pre-adopt snapshot.
3. `tests/ProoveGaming.App.Tests/LocalStorageServiceOwnerScopedPersistenceTests.cs` — edit: D5
   (`DeleteDevice_DoesNotTouchAnotherOwnersRow`) plus the two existing `DeleteDevice` call sites
   updated to the new signature.
4. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — edit: `ReloadOwnerScopedState` snapshots
   before adopting; new `internal static IsRepresentedOnScreen` swallow predicate beside
   `CardIdentity`; `LoadNotificationsForOwner` gains the one-shot pre-owner flush and the latch;
   `ExecuteConfirmDelete` passes the owner to `DeleteDevice`; stale comments at `:836-847` and
   `:448-453` corrected.
5. `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: `HealDuplicateDeviceRows` gains
   the two-units bail-out and a corrected comment; `DeleteDevice` gains owner scoping and a doc
   comment stating the invariant it now shares with `AddDevice`.
6. `docs/CONTEXT.md` — edit: one-liners for "swallow rule", "adopt pass / revive pass",
   "pre-owner notification flush", and the confirmed guest-notification lifetime.
7. `docs/plans/fix-duplicate-device-cards/fix-duplicate-device-cards-plan.md` — edit: dated note
   that the heal now bails out on two-unit groups, and that the revive comparison is taken against
   a pre-adopt snapshot. Its `## Review findings` journal is left untouched.
8. `docs/plans/persist-devices-and-notifications/persist-devices-and-notifications-plan.md` — edit:
   dated note correcting the `DeleteDevice(id)` signature line and recording that notifications
   raised before the first owner now flush to that owner.
9. `docs/plans/add-per-device-identity/add-per-device-identity-plan.md` — edit: dated note that the
   two-identical-units guarantee is now enforced in the heal itself.
10. `docs/plans/ui-thread-storage-async/ui-thread-storage-async-plan.md` — edit: dated note that
    `DeleteDevice` changed signature (its deferral list names it) and that the notification flush
    rides the existing storage executor.
11. `docs/plans/contact-us-and-update-check/contact-us-and-update-check-plan.md` — edit: dated note
    that the update-available entry's persistence claim is now true on the Welcome-screen launch
    path as well.

No new NuGet dependency, no new project, no `.slnx` or `Directory.Build.props` change, no installer
or uninstaller impact, no `.proto` change.

## Acceptance criteria

- [x] Given a legacy database (no `merge_transports` column) holding, for one owner and one name,
      two rows with the same `(vendor_id, product_id)` and different `device_key` **plus** a third
      row with a different pair, when `LocalStorageService` is constructed over it, then **all
      three rows remain** and none is flagged `merge_transports = 1`. (Fails today: one row
      survives.)
- [x] Given a legacy database holding, for one owner and one name, rows whose
      `(vendor_id, product_id)` pairs are all distinct (the reported RAMPAGE shape), when
      `LocalStorageService` is constructed over it, then exactly one row remains, it is the
      `MAX(id)` row, and its `merge_transports` is 1. (Unchanged from today.)
- [x] Given a legacy database whose `device_key` values are all empty, when it is healed, then the
      result is byte-for-byte what the current build produces.
- [x] Given a signed-in owner, one connected merged card on screen and one stored row for the same
      name under a different transport identity, when `ReloadOwnerScopedState` runs, then the
      device list holds exactly one card for that name, it is the connected one, and `devices`
      holds exactly one row for it. (Unchanged from today — the reorder must not regress it.)
- [x] Given a signed-in owner with a stored offline card whose identity is not on screen, when
      `ReloadOwnerScopedState` runs, then that card is revived, and the decision to revive it is
      taken from the row set as it was **before** the adopt loop wrote anything.
- [x] Given `ReloadOwnerScopedState` runs twice in a row (sign-in, then the post-pull refresh),
      when the second run completes, then the card count and the stored row count are identical to
      after the first.
- [x] Given the app launches to the Welcome screen with no owner and a device connects, when the
      user then signs in, then the "New device connected" entry is present in the bell **and** the
      `notifications` table holds a row for it under that user's owner key.
- [x] Given the same launch, when the user instead continues as guest, then that entry is persisted
      under the guest owner key.
- [x] Given a user signs out (session expired) so that a bell entry is raised with no owner, when
      a **different** account signs in on the same machine, then that entry is neither shown nor
      persisted for the arriving account.
- [x] Given a guest session with stored notifications, when the guest signs in to an existing
      account, then the guest notification rows are gone and the account's own bell history is
      shown. (Confirmed as-is — D4.)
- [x] Given two owners each holding a `devices` row, when `DeleteDevice` is called with owner A's
      key and owner B's row id, then no row is deleted.
- [x] Given the ⋮ → Delete flow on a signed-in user's card, when it completes, then that card's row
      and only that row is gone. (Unchanged from today.)

## Test strategy

Bug fix (`fix-*`): reproduction tests are written **first** and must fail on the current code.
All tests live in `tests/ProoveGaming.App.Tests` (there is no second project in play). The suite
already has every fixture needed — no new harness:

- `LocalStorageServiceDeviceIdentityTests` builds legacy databases through its own
  `CreateLegacyDevicesDb(params (string OwnerId, string Name, int Vid, int Pid, string Key)[])`
  over a temp file (`pg-…-{Guid}.db`, `SqliteConnection.ClearAllPools()` + delete on `Dispose`).
- `HomeViewModelPersistenceTests` / `HomeViewModelNotificationThreadingTests` each carry a
  `NewVm(...)` helper returning `Harness(HomeViewModel, LocalStorageService, SessionService,
  UserProfile)` with a `FakeHid : IUsbHidDeviceService`, a synchronous-dispatcher `SessionService`,
  a real `CurrentOwnerProvider`, and an in-memory store (`Data Source=:memory:`). The pre-owner
  case needs `signedIn: false` **and** no guest session, so `CurrentOwnerProvider.GetCurrent()`
  returns `null` — verify that the existing `NewVm` overload can produce that state, and extend it
  rather than forking a third copy of the harness if it cannot.

**Verify each reproduction fails at HEAD before fixing anything.** Use the technique earlier rounds
in this repo used: create a detached `git worktree` at HEAD, copy the new test file(s) in, and run
`dotnet test` there. That keeps the "fails before, passes after" evidence honest without stashing
the working tree.

Reproductions (must fail at HEAD):

1. `LegacyDb_TwoUnitsPlusAnExtraTransportRow_KeepsBothUnits` — D2. Legacy rows
   `(A, V1,P1,"K1")`, `(A, V1,P1,"K2")`, `(A, V2,P2,"K1")`; construct the store; expect 3 rows.
   Today: 1.
2. `NotificationsRaisedBeforeAnyOwner_ArePersistedToTheFirstOwner` — D3. Build the VM with no
   owner, call the `internal AddNotification(title, subtitle)` seam, then make the user owner
   current and call `ReloadOwnerScopedState`; await `NotificationsLoaded`; expect one
   `notifications` row for that owner and one visible item. Today: zero rows, empty bell.
3. `DeleteDevice_DoesNotTouchAnotherOwnersRow` — D5. Two owners, ids that collide; expect the
   foreign row to survive. Today it does not (it is deleted by id alone).

Characterization / regression (must pass before *and* after — they pin what the reorder must not
change):

4. `ReviveDecision_IsTakenAgainstThePreAdoptSnapshot` — D1. A stored offline row plus an on-screen
   connected card; assert the revived set is what the pre-adopt row set implies. Written against
   the new `IsRepresentedOnScreen` seam so it fails loudly if the two rules drift apart later.
5. The existing `SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne`,
   `RepeatedReloadOwnerScopedState_IsIdempotent_ForMergedCards`,
   `SignIn_WithTwoStoredUnitsOfANonMergedModel_KeepsBothCards`,
   `LegacyDb_RampageShapedPair_CollapsesToTheHighestIdWithTheFlagSet`,
   `LegacyDb_TwoIdenticalUnits_AreBothPreserved`,
   `LegacyDb_SameProductDifferentKey_IsNotCollapsed`,
   `LegacyDb_Heal_RunsOnlyOnce_AndDoesNotDeleteOnLaterConstructions` must all stay green
   unmodified. If any of them needs editing, the change has altered a shipped contract — stop and
   raise it rather than adjusting the test.
6. `PostSignOutNotification_IsNotFlushedToTheNextOwner` — the D3 latch's other direction.
7. `BecomingGuest_ReplacesTheBellWithTheGuestHistory` and
   `OwnerSwitchMidLoad_NeverShowsTheStaleOwnersNotifications` must stay green: the flush runs
   before the clear and before the generation bump, so the stale-owner guard is untouched.

Deliberately **not** covered by automated tests:

- The real Welcome→sign-in UI transition (Avalonia UI-thread affinity; `MainWindow` is not
  test-constructible here). Covered by the QA checklist below.
- Real HID discovery timing — the "device connects while on Welcome" trigger is simulated through
  the `AddNotification` seam, not through a real device.
- SQLite behaviour under concurrent processes; the store is single-process by design.

Run the suite in **Release** against `src/ProveGaming.slnx` before pushing — CI runs Release and
these tests touch the storage executor's timing.

## QA (manual test steps)

1. Install and launch the app. Do **not** sign in — stay on the Welcome screen. Plug in a device
   (any supported mouse or keyboard) and wait about 10 seconds.
   *Expected:* nothing visibly happens on the Welcome screen (there is no bell there).
2. Sign in with an existing account.
   *Expected:* the Home screen opens and the bell icon shows an unread badge with a
   "New device connected" entry naming the device you plugged in at step 1.
3. Close the app completely and launch it again (stay signed in).
   *Expected:* the bell still shows that entry — it survived the restart.
4. Repeat steps 1–2, but at step 2 choose "Continue as guest" instead of signing in.
   *Expected:* the bell shows the same entry for the guest session.
5. From that guest session, sign in to an existing account.
   *Expected:* the bell now shows **that account's** history; the guest entries are gone and do not
   come back after a restart. (This is intended behaviour — please confirm it matches what the
   product expects.)
6. Sign out and wait for the "session expired" message if it appears, then sign in with a
   **different** account.
   *Expected:* the second account's bell does not contain any entry produced while nobody was
   signed in, and does not contain the first account's entries.
7. If you have **two identical units of the same model** (e.g. two of the same mouse) and a
   database from an older build: connect unit 1, wait for its card, disconnect it; connect unit 2,
   wait for its card, disconnect it; also connect one of them over a second connection type
   (dongle/Bluetooth) if the model supports it. Then close the app and launch the new build.
   *Expected:* both units still have their cards on the Home screen. Neither disappears.
8. With a keyboard that supports both a dongle and Bluetooth (e.g. Rampage) and an older-build
   database that shows a duplicate greyed-out "NO CONNECTION" card for it: launch the new build.
   *Expected:* the duplicate card is gone at startup; exactly one card for that keyboard remains,
   and it is the connected one.
9. On a signed-in account with at least two device cards, use ⋮ → Delete on one card, confirm, then
   restart the app.
   *Expected:* only the deleted card is gone; the other cards are still there.
10. Throughout steps 1–9: the app must never freeze, and switching to the Home screen after
    sign-in must be immediate (no visible pause while cards load).

## Review focus

- `async/await` — the notification flush must ride the existing storage executor (FIFO), never be
  awaited on the UI thread, and never block the reload it precedes.
- `UI-thread affinity` — `ReloadOwnerScopedState` and `LoadNotificationsForOwner` run on the
  dispatcher and mutate `ObservableCollection`s; the snapshot and the latch must be read/written on
  that thread only.
- `thread-safety` — `AddNotification` is reachable from the discovery thread via
  `CheckLowBattery`/`Dispatcher.Post`; the new latch must not be written from two threads.
- `performance hot-path` — startup: the heal's new `NOT EXISTS` join runs once on a migrating
  database, and `ReloadOwnerScopedState` still issues one `GetDevices` per invocation, not two.

## Performance & quality

- **Startup (migration).** The bail-out adds a correlated self-join to a one-shot `UPDATE` over the
  `devices` table, which holds one row per card per owner — tens of rows at most. It runs only on
  the construction that adds the `merge_transports` column, inside the existing transaction.
  Negligible.
- **`ReloadOwnerScopedState` is *not* made async here, and that is a deliberate deferral.** It
  already calls `_localStorage.AddDevice` synchronously once per on-screen card and `GetDevices`
  once, all on the UI thread, each opening a SQLite transaction —
  [`ui-thread-storage-async`](../ui-thread-storage-async/ui-thread-storage-async-plan.md) deferred
  these as "bounded single-row writes". A loop over N cards is less obviously bounded than a single
  write, but converting it means restructuring an owner-transition path that must also mutate
  `Devices` on the dispatcher; that is its own change with its own risk. This plan must not make it
  worse: the snapshot **replaces** the existing read rather than adding a second one, so the number
  of synchronous storage calls per invocation is unchanged.
- **Allocations.** The revive filter builds one collection of card keys per invocation, as today.
  The swallow predicate takes the already-materialised card list; do not rebuild it per record (that
  would turn the revive pass into O(cards × rows) allocations) — build the key set once outside the
  loop.
- **The swallow predicate is a linear scan, deliberately** (added 2026-08-27 after review round 1,
  N1). `IsRepresentedOnScreen` walks a materialised `List<CardKey>` per stored row, so the revive
  pass costs O(rows × cards) comparisons rather than the O(rows) hash lookups the previous
  `CardIdentity`-keyed `HashSet` gave. That is inherent to the rule, not an oversight: the swallow
  relation is **asymmetric** — a merged card swallows a non-merged row but never the reverse — and
  an asymmetric relation cannot be decided by a single hash lookup on a symmetric key. N is
  single-digit cards per owner, so this is a few hundred comparisons once per sign-in. The
  requirement that actually mattered — build the key set once, outside the loop — is honoured.
- **No new disposables**, no new event subscriptions, no new timers.

## Risks

- **The heal's bail-out leaves a ghost card for a narrow population.** A user who owns two units of
  a *merged* product and has a legacy database now keeps the extra row until that device next
  connects, instead of having it deleted at startup. That is the intended trade: a visible,
  self-healing ghost beats a silent, permanent deletion. Call it out in the PR.
- **Flushing pre-owner notifications changes what a fresh account sees.** The first sign-in on a
  machine with devices attached now lands 1–N "device connected" entries in that account's history.
  Bounded by `MaxNotificationsPerOwner` (100) and by the number of devices physically present.
- **The `DeleteDevice` signature change touches an existing test file** and a plan that names the
  old signature in its deferral list; both are in the change list. Any caller added on another
  branch between now and merge will fail to compile — that is the point.
- **D1's reorder is behaviour-preserving by construction, so its tests cannot prove much.** The
  protection against regression is that the shipped identity tests stay green *unmodified*.
- **Merge conflict surface**: `HomeViewModel.cs` and `LocalStorageService.cs` are both hot files on
  `features/newapi`. Rebase before starting.

## Open questions

- **Q1 (recommendation attached, not blocking).** At sign-in from a guest session, the adopt loop
  adopts **every** on-screen card into the arriving user's account, including the guest's
  *disconnected* ones — while `OnSignInSucceeded` states "Deliberately NO guest-data migration
  here", and `ClearOwnerScopedState` argues that a departing owner's disconnected cards "must not
  stay visible to (or be adopted and persisted by) whoever signs in next". Those two positions are
  in tension. Recommendation: restrict the adopt loop to **connected** cards, matching
  `ClearOwnerScopedState`'s reasoning — hardware physically present is the arriving owner's
  business, the departing guest's offline list is not. Not included in this plan's change list
  because it is a product decision; if the product owner agrees, it is a two-line change in the
  same method and one added test.
- **Q2 (product owner, not blocking).** D4 is planned as *no change*: guest notifications are
  deleted at sign-in and at guest expiry, and follow the user only on guest→register. Confirm this
  is the intended product behaviour. If it is not, the change is not a small one — it means
  re-keying guest rows into an account that already owns its history, i.e. reinstating the
  guest→user migration that was removed at sign-in.
- **Q3 (deferred).** Should *no* schema migration ever delete a row? If the answer is yes, D2's
  Option B (flag-only heal + read-path collapse) supersedes the chosen Option C, and the
  `fix-duplicate-device-cards` acceptance criterion "then one row remains (the one with the highest
  id)" has to be rewritten. Not blocking: Option C is strictly safer than what ships today.
- **Q4 (for the orchestrator).** D1 is briefed as a blocker with permanent data loss. On reading
  both predicates, `AddDevice`'s delete rule and the revive skip rule coincide, so the ordering
  produces no observable loss on the current code, and a naive "read before write" fix would
  *regress* the shipped ghost-card criterion. This plan therefore treats D1 as a
  correctness-by-construction fix with characterization tests instead of a failing reproduction.
  If the reporter has a concrete sequence where a card was actually lost, attach it — it would
  change the recommended approach from B to something narrower.

## Review findings

### Round 1 (working tree vs 02f8d19, 2026-08-27) — Ship: yes

#### Plan conformance

Plan: `docs/plans/fix-owner-scoped-state-persistence/fix-owner-scoped-state-persistence-plan.md`

- **Implemented as planned:** all 11 File-level change list items. D1 landed as Option B — the
  `GetDevices` snapshot moved above the adopt loop (`HomeViewModel.cs:902`) and the revive filter now
  goes through the extracted `IsRepresentedOnScreen` swallow predicate (`:1288`) with its `CardKey`
  carrier (`:1272`). D2's bail-out is the planned `NOT EXISTS` self-join on the flagging `UPDATE`
  (`LocalStorageService.cs:951-968`), with the false safety claim in the comment block corrected
  (`:919-931`). D3 is the one-shot latch + capture-before-clear (`HomeViewModel.cs:579-583`, `:604`)
  flushed ahead of the reload inside `LoadNotificationsCoreAsync` (`:636`). D5 is the owner-scoped
  `DeleteDevice(ownerKind, ownerId, id)` (`LocalStorageService.cs:3920-3941`) with its single
  production caller updated (`HomeViewModel.cs:2372`). D4 is documentation-only, as planned.
- **Deviates from plan:** one, and it is minor — the plan sketched `IsRepresentedOnScreen` as
  consuming a set; the implementation keeps a `List<CardKey>` and scans it. See N1: correct, but an
  algorithmic change the plan's Performance section did not name.
- **Missing / partial:** none.
- **Accepted limitation (per plan):** D2's bail-out leaves a ghost row for an owner of two units of
  a *merged* product until that device next connects (plan Risks). Residual risk: a visible,
  self-healing duplicate card for a narrow population — deliberately traded against the silent,
  permanent deletion that ships today. Correctly called out for the PR.
- **Acceptance criteria: 12 of 12 met.** Nine are backed directly by new or existing tests; the
  three UI-transition criteria (Welcome→sign-in, guest continue, guest→account bell replacement)
  are covered at the view-model seam plus the QA checklist, which is what the plan's Test strategy
  specified as deliberately not automated.

Verified independently: D2/D3/D5 reproductions fail at HEAD `02f8d19` in a detached worktree with
the predicted failure modes (D2 `Expected: 3 / Actual: 1` — both physical units collapsed; D3 and
D5 `Assert.Single() Failure: The collection was empty`). A D1 end-state probe **passes** at HEAD,
confirming the reorder is behaviour-preserving exactly as Q4 predicted. 1212 passed / 0 failed in
both Debug and Release (baseline 1198). The nine "must not regress" tests are green and
byte-identical — the whole test diff is additions plus the two mandated `DeleteDevice` call sites.

#### Code

- [x] **S1. The pre-owner flush filter's stated invariant is not self-enforcing** —
  *(Closed 2026-08-27, round 2: Option A applied — `AddNotification`'s owner-present branch now sets
  `_ownerEverResolved`, and the capture comment in `LoadNotificationsForOwner` no longer claims
  `StorageId == 0` means "never persisted".)*
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:579` (`LoadNotificationsForOwner`) — Should-fix

  **Problem:** the capture is `Notifications.Where(n => n.StorageId == 0)`, and the comment above it
  asserts that `StorageId == 0` "means never persisted, which — with the latch still unset — can
  only be an entry raised while nobody owned the bell". That is not what `StorageId == 0` means. It
  means *not yet* persisted: `AddNotification` (`:459-461`) hands the insert to the storage executor
  and the id only lands on the item when that continuation resumes. So any entry raised **with an
  owner present** but **before the first `LoadNotificationsForOwner`** is still `StorageId == 0` at
  capture time, gets swept into `pendingFlush`, and is written to the `notifications` table a second
  time — one bell entry, two rows.

  **Why it is Should-fix and not a Blocker:** I could not construct a reachable path on the current
  code. The only synchronous `AddNotification` that can run before the constructor reaches
  `LoadNotificationsForOwner()` is the belt-and-braces update announcement at `:834`
  (`if (updateCheck.LatestKnownUpdate is { } known) OnUpdateAvailable(known);`), and that is dead
  today: `HomeViewModel` is a singleton (`App.axaml.cs:235`) resolved as a `MainWindow` constructor
  dependency, while `UpdateCheckService.Start()` runs strictly afterwards (`App.axaml.cs:133`), so
  `LatestKnownUpdate` is always null there. The other candidate — a low-battery announcement raised
  by the revive loop's `WireDelete` → `CheckLowBatteryOnUIThread` during `ReloadOwnerScopedState`,
  which runs *before* that method's own `LoadNotificationsForOwner()` — is saved by
  `CheckLowBattery` using `Dispatcher.UIThread.Post` (`:2243`), so the `AddNotification` lands on a
  later dispatcher turn with the latch already set.

  So the invariant holds only because of an ordering accident in a *different* file — and the code
  at `:832-835` exists precisely to defend against that ordering changing. The comment claims an
  invariant the method does not enforce, which is how the next person removes the accident.

  **Solution options:**
  - Option A (correct, one line): set the latch in `AddNotification`'s owner-present branch —
    `if (CurrentOwner is { } owner) { _ownerEverResolved = true; LastNotificationPersist = … }`. An
    entry that was attributed to an owner directly proves an owner has resolved, so nothing raised
    before it needs flushing. This makes the filter's premise true by construction, needs no second
    collection (which the plan explicitly ruled out), and cannot change behaviour on any path that
    is reachable today.
  - Option B (documentation only): leave the code and correct the comment to say the premise depends
    on no owner-present `AddNotification` preceding the first `LoadNotificationsForOwner`, naming
    `App.axaml.cs:133` as what guarantees it. Cheaper, but leaves a tripwire in another file.

#### Tests

- [x] Optional companion for S1 once it is fixed: raise a notification with an owner already present
  but before the first `LoadNotificationsForOwner`, then let the first reload run, and assert the
  `notifications` table holds **one** row for it —
  `tests/ProoveGaming.App.Tests/HomeViewModelPersistenceTests.cs`. This is the only gap I found in
  otherwise thorough coverage; it is not reachable today, so it guards the invariant rather than a
  live bug.
  *(Closed 2026-08-27, round 2: `NotificationRaisedWithAnOwner_BeforeTheFirstReload_IsPersistedExactlyOnce`
  in `HomeViewModelPersistenceTests.cs`. It gates the storage executor with a
  `ManualResetEventSlim` so the insert is provably still queued when the capture runs — without that
  the pool thread finishes the insert during `ReloadOwnerScopedState`'s device work and the bug
  hides. Verified by reverting the one-line fix: the test then fails with
  `Assert.Single() Failure: The collection contained 2 items`, i.e. the duplicated row S1 predicted;
  it passes with the fix restored.)*

#### Nits

- **N1.** `IsRepresentedOnScreen` scans a `List<CardKey>` linearly per stored row
  (`HomeViewModel.cs:1288`), so the revive pass went from O(rows) hash lookups to O(rows × cards)
  comparisons. Irrelevant at real sizes (cards per owner are single digits, so a few hundred
  comparisons once per sign-in) and it does honour the plan's actual requirement — the key set is
  materialised once, outside the loop, so the allocation behaviour the plan cared about is
  unchanged. Worth a sentence in the plan's Performance section rather than a code change: the
  asymmetric swallow rule cannot be expressed as a single hash lookup, which is why the set became
  a list.
- **N2.** The plan's `Status:` line still reads `plan — not implemented`.
- **N3.** `HomeViewModelPersistenceTests.cs` and `LocalStorageServiceOwnerScopedPersistenceTests.cs`
  flipped from LF to CRLF wholesale in the working tree. Harmless — `core.autocrlf=true` with no
  `.gitattributes` normalises to LF in the index, and `git diff --numstat` confirms the delta is
  additions-only with zero deletions in those files — but worth knowing before staging so the CRLF
  is not mistaken for a rewrite.

#### What's done well

- **The bail-out is genuinely conservative, and its scoping is tested rather than assumed.**
  `LegacyDb_BailOut_IsScopedToTheGroupHoldingTheEvidence` is the test that matters most here: it
  proves the correlated `NOT EXISTS` binds to the outer `devices` row and not to its own `a`/`b`
  aliases, which is exactly the subtlety that makes correlated subqueries inside an `UPDATE` easy to
  get silently wrong. Paired with `LegacyDb_WithNoDeviceKeys_HealsExactlyAsBefore` (inertness on
  pre-`device_key` databases) and `LegacyDb_BailOut_IsInertForAGroupWithNoRepeatedPair` (the shipped
  RAMPAGE collapse still happens), the guard is pinned from all three directions instead of just the
  happy one.
- **The false safety claim in the old comment was deleted, not left to rot.** The previous block
  asserted that two identical units "always share one pair … so their group has a single pair and is
  never touched" — true for a group of two, false for the group of three that motivated D2. Removing
  a wrong invariant is worth more than the code change it accompanied.
- **D1 was implemented as the honest version of the fix.** A naive read-before-write would have
  regressed the shipped ghost-card criterion; pairing the reorder with the extracted swallow rule
  keeps `SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne` green *unmodified*,
  which is the only real protection a behaviour-preserving change can have. The characterization
  test computing its expectation *through* `IsRepresentedOnScreen` and then comparing against the
  live outcome is the right shape: it fails loudly if the two rules ever drift apart.
- **The flush ordering is correct for the reason the comment gives.** Awaiting the inserts before
  `GetNotifications`, both on the FIFO storage executor, is what makes each flushed entry come back
  exactly once carrying a real `StorageId` — and `NotificationsLoaded` now completes only after
  both, so the tests observe a settled state instead of racing it. Nothing blocks the dispatcher:
  `LoadNotificationsForOwner` stays synchronous and returns at the first await.
- **The latch introduces no new threading surface.** It is read and written at exactly the same
  point, in the same method, on the same thread as the pre-existing `_notificationsLoadGeneration`
  counter, whose affinity contract the codebase already relies on.
- **The `DeleteDevice` signature change was made the compiler's problem.** Widening the signature
  rather than adding an overload means any caller added on another branch fails to build instead of
  silently keeping the unscoped behaviour.

#### Verdict

Ship: yes — no blockers. Five defects are fixed as planned, all 12 acceptance criteria are met, the
three reproductions are proven to fail at HEAD and pass now, and the shipped contracts are green
unmodified. S1 is a latent-invariant cleanup, not a live bug: I could not reach the duplicate on
current code, and the one-line Option A closes it for good.

First: 1) S1 Option A — set `_ownerEverResolved` in `AddNotification`'s owner-present branch;
2) its companion test; 3) N2, and add N1's one-sentence note to the plan's Performance section.
