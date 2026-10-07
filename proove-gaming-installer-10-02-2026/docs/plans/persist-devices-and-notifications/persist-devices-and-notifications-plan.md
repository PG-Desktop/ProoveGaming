# Persist device cards and notifications across app restarts

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

> **Superseded in part (2026-08-22, `ui-thread-storage-async`):** the notification and guest-flush storage writes no longer run synchronously on the UI thread. `AddNotification` inserts into the UI collections first and enqueues the row insert on the storage executor (`StorageId` is assigned by a completion continuation; delete/read races are handled explicitly), mark-all-read issues one batched `SetAllNotificationsRead` UPDATE, `LoadNotificationsForOwner` clears synchronously and reloads through the executor behind a generation guard, and `FlushSession` snapshots the device list and enqueues `Flush`. The "mark all read walks up to 100 UPDATEs" and "AddNotification runs synchronously on the UI thread" risk notes below are resolved.

> **Extended (2026-09-01, `add-api-v21-notifications-sync`):** the schema note below that
> notification rows are "per-machine, already-localized history with no cross-device sync
> value" is superseded for **signed-in** users: the `notifications` table gained
> `server_id` / `type` / `is_dirty` / `local_rev`, user-owned entries now sync to the API
> v2.1 Notifications Management list (push via SyncCoordinator, merge via the sign-in pull
> and a 15-minute poll), and rows pulled from the account render their icon from the type
> slug. The localized-text-as-history semantics stand — a pulled row shows in the language
> it was written in. Guest rows and rows created before the sync stay local-only. See
> `docs/plans/add-api-v21-notifications-sync/`.

> **Extended (2026-08-31, `add-api-v21-device-roster`):** the user-owned `devices` rows this
> plan added now also feed the API v2.1 server-side device roster: `AddDevice` /
> `DeleteDevice` / `MigrateGuestToUserAsync` write `deviceroster.*` intent marks into
> `sync_meta` (user rows only; delete marks only when the last card of a settings identity
> goes), `SyncCoordinator` pushes them as `devices/add` / `devices/remove`, and the sign-in
> pull hydrates offline rows for server-rostered models this machine has never seen. Guest
> behaviour is unchanged. See `docs/plans/add-api-v21-device-roster/`.

> **Superseded in part (2026-08-26, `fix-duplicate-device-cards`):** the `devices` upsert key described below — `(owner, vendor_id, product_id, device_key, name)` — is no longer the whole rule. A row is now keyed by the card's **identity**: for a merged-transport product (new `merge_transports` column) that is `(owner, name)` alone, because its vendor/product/key follow whichever transport is live and keying on them stored one row per transport; every other card keeps the tuple below, with the name compared case-insensitively. `AddDevice` additionally deletes the card's previous row by id, and `ReloadOwnerScopedState` compares stored rows to on-screen cards by the same identity. See `docs/plans/fix-duplicate-device-cards/`.

*2026-08-19. QA: "Якщо закрити застосунок і відкрити заново, то зникаються всі сповіщення і девайси
які були раніше" — closing and reopening the app loses every notification and every previously-seen
device card. Expected: the state survives a restart.*

## Problem

Two separate gaps, both real:

1. **Notifications are memory-only.** `HomeViewModel.Notifications` is an `ObservableCollection`
   with no storage behind it. Every restart starts from an empty bell for guest and signed-in users
   alike.
2. **Device cards persist only inside the guest session.** The `devices` table is written by
   `LocalStorageService.Flush(UserSession)` and read back by the guest-session restore in
   `App.OnFrameworkInitializationCompleted`. A **signed-in** user has no `UserSession` at all
   (`SessionService.EndSession()` runs on sign-in), so:
   - `HomeViewModel.LoadDevices()` restores nothing on launch;
   - `AddDeviceToSession` no-ops for every discovered or manually added card;
   - sign-in even wipes the `devices` table (`ClearSession`).
   Only currently-connected devices reappear (live discovery re-creates their cards); every
   offline card is gone.

## Design

Reuse the existing owner-scoping (`owner_kind` / `owner_id`, `ICurrentOwnerProvider`) that every
other persisted table already uses. Guest = `('guest', <session-uuid>)` for notifications (so they
ride the existing guest→user migration); the guest **devices** flow keeps its session-scoped
semantics unchanged (rows implicitly guest-owned).

### Devices

- `devices` gains `owner_kind TEXT NOT NULL DEFAULT 'guest'` and `owner_id TEXT NOT NULL DEFAULT ''`
  via `AddColumnIfMissing`. Pre-existing rows become guest-owned — exactly what they were.
- Guest path unchanged in behaviour: `Flush(session)` rewrites the guest rows, `GetDevices()`
  (session restore) reads only guest rows, `ClearData`/`ClearSession` delete only guest rows —
  which is the fix that stops sign-in and guest expiry from wiping a signed-in user's cards.
- New user path (write-through, no exit flush needed):
  - `GetDevices(ownerKind, ownerId)` — load the user's cards on launch;
  - `AddDevice(ownerKind, ownerId, record)` — upsert keyed by `(owner, vendor_id, product_id,
    device_key, name)` so re-adopting an on-screen card is idempotent. `device_key` (the card's
    Container ID, `''` when unknown) is part of the key because VID/PID/name alone collapse two
    identical units of one model — discovery keeps such units apart precisely by that key
    (PR #112 M2);
  - `DeleteDevice(id)` — card delete.
- `HomeViewModel`:
  - `LoadDevices()` — when there is no guest session but the owner is a user, load
    `GetDevices(user)`;
  - `PersistDeviceCard` (renamed from `AddDeviceToSession`) — session path as before, user path
    writes through to storage;
  - `OnDeviceAdded` and `ExecuteConfirmDelete` — same branch;
  - `OnDeviceAdded`'s guest branch now flushes immediately (previously the manual add only reached
    disk at exit, so a crash lost it).
- **Sign-in / sign-up hook**: `HomeViewModel.ReloadOwnerScopedState()`, called from
  `MainWindow.OnSignInSucceeded` / `OnNicknameChosen` after the session teardown. It re-registers
  the on-screen cards under the user owner (mirror of `RegisterExistingCardsInSession`, which does
  this for guest) and pulls in the user's stored offline cards that are not already on screen
  (matched by vendor/product/device-key/name).
- **Sign-off hook** (PR #112 M1/m1): `HomeViewModel.ClearOwnerScopedState()`, called from
  `MainWindow.ClearSignedInState` (logout, invalidated token) and from `OnSessionExpired`'s
  guest branch. Drops the departing owner's **disconnected** cards from the screen — left there,
  the next owner's adopt pass would persist them into that account — and reloads the (now empty)
  bell. Connected cards stay: they describe hardware physically present, and discovery owns them,
  so the next owner adopting them is correct.
- Guest → user **sign-up migration** (`MigrateGuestToUserAsync`) re-keys guest device rows to the
  new user, colliding user rows deleted first (guest wins, like every other table there).

### Notifications

- New table `notifications(id, owner_kind, owner_id, title, subtitle, icon_data, icon_uri,
  icon_width, icon_height, created_at, is_read)`. Localized title/subtitle are stored as shown —
  they are history, not live resources, so a later language switch leaves old entries in the
  language they happened in.
- `LocalStorageService`: `GetNotifications(owner)` (newest first, capped at 100),
  `AddNotification(owner, record)` (trims beyond the cap), `SetNotificationRead(id, isRead)`,
  `DeleteNotification(id)`, `DeleteAllNotifications(owner)`.
- `ClearData` deletes guest notifications (guest-session expiry wipes them with the rest of the
  guest state). `MigrateGuestToUserAsync` re-keys them by the guest session uuid on sign-up.
- `NotificationItemViewModel` gains `StorageId`.
- `HomeViewModel`: `AddNotification` writes through; the item's `IsRead` toggle, per-item delete,
  and delete-all update the store; `LoadNotificationsForOwner()` rebuilds the list from storage on
  construction and on every owner change (guest continue via the existing `IsGuest` handler,
  sign-in/sign-up via `ReloadOwnerScopedState`). Reloading on owner change also stops one owner's
  history from being shown to the next (previously the in-memory list survived logout).

## File-level change list

| File | Change |
|---|---|
| `Services/LocalStorageService.cs` | `notifications` table; `devices` owner columns; guest-scoped deletes in `ClearData`/`ClearSession`/`Flush`/`GetDevices()`; user-scoped device CRUD; notifications CRUD; migration additions. |
| `Models/NotificationRecord.cs` | **New.** Storage row for one notification. |
| `ViewModels/NotificationItemViewModel.cs` | `StorageId`. |
| `ViewModels/HomeViewModel.cs` | Owner-aware device + notification persistence, `ReloadOwnerScopedState`, notification wiring extracted so the load path reuses it. |
| `Views/MainWindow.axaml.cs` | Calls `ReloadOwnerScopedState` after sign-in and sign-up. |

> **Note (2026-08-27, `fix-owner-scoped-state-persistence`):** two updates to the contract above.
>
> 1. **`DeleteDevice(id)` is now `DeleteDevice(ownerKind, ownerId, id)`** — the line at 49 names the
>    old signature. The delete is scoped to its owner, matching what `AddDevice`'s delete-by-id
>    already did: a card's `StorageId` is only a `devices`-table id while a **user** owns it, since
>    under a guest session the ids come from `UserSession`'s own counter starting at 1, so an
>    unscoped delete-by-id was one unguarded caller away from removing a stranger's row. A
>    mismatched owner now deletes nothing.
> 2. **Notifications raised before the first owner exists are no longer lost.** `HomeView.OnLoaded`
>    starts discovery while the user is still on the Welcome screen, so entries could be raised with
>    no owner to key a row by and were then wiped from the screen when the owner resolved — on that
>    launch path the `notifications` table had never held a single row. Such entries are now flushed
>    to the **first** owner that resolves (see [[Pre-owner notification flush]]); a one-shot latch
>    keeps an entry raised *after* a sign-out memory-only, so it can never reach the next account.
>    See `docs/plans/fix-owner-scoped-state-persistence/`.
## Acceptance criteria

- Signed-in: add/discover devices, close the app, reopen → the same cards are shown (offline ones
  included); notifications and their read state are back.
- Guest with an unexpired session: same restart round-trip (previously worked for devices only;
  now notifications too).
- Guest-session expiry still wipes guest devices and notifications — including the on-screen
  offline cards, so the next "Continue as guest" cannot re-register them (connected cards stay
  and re-register: they are live hardware, not expired state).
- Sign-in from the welcome screen shows the account's stored cards; cards already on screen are
  adopted, not duplicated. Signing off first drops the previous owner's offline cards, so one
  account's devices are never persisted into another's.
- Two identical units of one model (same VID/PID/name, different Container ID) keep two stored
  rows; deleting one durably deletes that one.
- Sign-up from a guest session carries guest devices and notifications to the new account.
- Deleting a card or notification removes it durably; delete-all empties the store.

## QA steps

1. Sign in (remember me), connect a keyboard + mouse, disconnect the keyboard, note the bell
   entries. Quit from the tray. Relaunch → both cards present (keyboard offline), bell unchanged.
2. Mark one notification read, delete another, relaunch → both facts survived.
3. Continue as guest, add a device, relaunch within the session window → card present.
4. Let the guest session expire in tray → devices and notifications gone (welcome screen).
5. Register a new account from a guest session with cards → cards and bell entries follow.

## Review findings

### Round 1 — Ship: yes

Review surface: the entire uncommitted working-tree delta (no branch commits). It bundles three
logical changes: (1) this plan's device/notification persistence; (2) the key-assignment
read-failure self-heal loop (owned by a dated note in
`docs/plans/add-smart-infinite-key-assignments/`); (3) the `DotsOverlay` hoist out of the 42px
column in `SingleCard`/`InactiveDeviceCard` (owned by a dated note in
`docs/plans/fix-qa-bug-batch/`). Changes (2) and (3) produced no findings beyond what their notes
record; everything below concerns this plan's scope unless marked otherwise.
`src/ProoveGaming.App/LocalizationManager.cs` re-enables the debug-only force-uk language line —
`#if DEBUG` only, matches its own comment's stated intent, release builds unaffected; flagging so
it is committed deliberately, not by accident.

#### Plan conformance
Plan: this file.
- Implemented as planned: `notifications` table + CRUD with the 100-row cap; `devices`
  owner columns via `AddColumnIfMissing`; guest-scoped deletes in `ClearData` / `ClearSession` /
  `Flush` / `GetDevices()`; user-scoped device CRUD with the (owner, vid, pid, name) upsert;
  guest→user migration for both tables; `NotificationItemViewModel.StorageId`; `HomeViewModel`
  write-through wiring + `LoadNotificationsForOwner` + `ReloadOwnerScopedState`; `MainWindow`
  hooks at sign-in and sign-up; the `OnDeviceAdded` guest-branch immediate flush.
- Deviates from plan: none material.
- Missing / partial: VM-level tests (the plan's Test coverage names only storage-layer tests —
  see S3).

#### Code
- [x] **S1. Stale `StorageId`s survive the user→guest owner transition** — *fixed 2026-08-19:
  the `IsGuest`-true handler zeroes every card's `StorageId` before
  `RegisterExistingCardsInSession`; regression pinned by
  `HomeViewModelPersistenceTests.BecomingGuest_ReRegistersCarriedOverCardsIntoTheGuestSession`.* —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:330-336`, `:1347` — Should-fix
  **Problem:** `ReloadOwnerScopedState` zeroes `StorageId` before adopting cards under the user
  owner, but the mirror transition (logout → continue as guest) does not: the `IsGuest`-true
  handler calls `RegisterExistingCardsInSession`, which skips every card with `StorageId != 0` —
  and cards persisted under the previous user still carry their user row ids.
  **Why it matters:** those cards never enter the new guest session (gone from guest view after a
  restart), and deleting one as guest hits the session branch of `ExecuteConfirmDelete`
  (`:1421-1444`) with a foreign id — the user's row survives, so the "deleted" card resurrects on
  that user's next sign-in. Pre-fix behaviour had the same skip, but the table was wiped on
  sign-in so nothing could resurrect; the persistence this plan adds is what makes the stale id
  visible.
  **Fix:** zero `StorageId` on all device cards in the `IsGuest`-true handler before
  `RegisterExistingCardsInSession()`. Safe: a restored-session card cannot be present when that
  event fires (session restore precedes HomeViewModel construction), so any card on screen at
  that moment belongs to a previous owner context by definition.
- [x] **S2. `devices` CREATE TABLE lacks the owner columns** — *fixed 2026-08-19: the two
  columns (with defaults) are in the CREATE TABLE; `AddColumnIfMissing` stays for old DBs.* —
  `src/ProoveGaming.App/Services/LocalStorageService.cs:100-110` vs `:391-392` — Should-fix
  **Problem / why it matters:** fresh databases create the table without `owner_kind`/`owner_id`
  and immediately ALTER them in. Works, but deviates from the repo's migration convention (the
  `macros` table carries its later columns in the CREATE, with `AddColumnIfMissing` only for
  pre-existing DBs) and leaves the schema's single source of truth incomplete.
  **Fix:** add the two columns (with their defaults) to the CREATE TABLE; keep the
  `AddColumnIfMissing` calls for old DBs.

#### Tests
- [x] VM-level coverage gap (S3) — *closed 2026-08-19:
  `tests/ProoveGaming.App.Tests/HomeViewModelPersistenceTests.cs` (8 tests) constructs the real
  `HomeViewModel` over an in-memory store and covers the signed-in load (bell order/read state,
  device cards), IsRead write-through, per-item and delete-all persistence, the guest-transition
  bell swap, `ReloadOwnerScopedState` adopt/pull/idempotence, and the S1 regression.*
- Standing repo caveat (not this change): `dotnet-ci.yml` builds only the App csproj, so none of
  these tests run in CI; local `dotnet test` is the gate.

#### Nits
- Battery level on a user-owned card is captured only when the row is (re)written; the guest path
  re-captures at exit via `Flush`, so restored user cards may show a staler battery —
  `HomeViewModel.PersistDeviceCard` (`:1356`).
- "Mark all read" walks up to 100 items, each issuing its own `SetNotificationRead` UPDATE on the
  UI thread via the per-item `IsRead` handler; one bulk UPDATE would do — `HomeViewModel.cs:303`.
- `AddNotification`'s storage insert runs synchronously on the UI thread (single-row insert,
  consistent with the existing `FlushSession` convention) — fine today, worth revisiting if
  notification volume grows — `HomeViewModel.cs:173`.

#### What's done well
- The owner-scoping reuses the exact `owner_kind`/`owner_id` + `ICurrentOwnerProvider` machinery
  every other persisted table already uses — no new ownership concept to learn.
- Write-through for user devices (instead of an exit flush) means a crash cannot lose cards; the
  upsert key makes the sign-in adopt idempotent across repeated sign-ins.
- Guest-scoped deletes fix a real latent hazard (sign-in wiping another owner's rows) rather
  than just adding the new tables around it; `fix-remember-me-guest-session`'s journal got the
  superseding note instead of a silent contract change.
- Storage tests pin the exact QA regression (restart round-trip, cap trim, migration collisions)
  and all 1,556 tests pass.

#### Verdict
Ship: yes — no blockers; S1 is a real but narrow edge (logout → guest → delete) that does not
corrupt data, and S2/S3 are hygiene. First: 1) S1 stale-StorageId zeroing in the IsGuest
handler; 2) S2 CREATE TABLE columns; 3) S3 VM-level notification tests.

### Round 2 — external review (PR #112, boikoserhii84) — all findings addressed 2026-08-19

- [x] **M1. Previous user's cards persisted into the next user's account** — fixed by the
  sign-off hook: `HomeViewModel.ClearOwnerScopedState()` (drops disconnected cards + bell,
  keeps connected hardware) called from `MainWindow.ClearSignedInState` and `OnSessionExpired`'s
  guest branch — reviewer's option B, narrowed to offline cards so live hardware stays visible
  through the welcome screen (their option A's insight). Regression:
  `HomeViewModelPersistenceTests.SignOff_DropsOfflineCards_SoTheNextOwnerAdoptsOnlyConnectedHardware`.
- [x] **M2. Upsert key collapsed two identical units of one model** — `device_key` (Container ID)
  added to the `devices` schema, `DeviceRecord`, both read paths, `Flush`, the upsert key and
  `ReloadOwnerScopedState`'s on-screen filter; `RecordToViewModel` restores it onto the card so
  re-adoption hits the same row. Regression:
  `LocalStorageServiceOwnerScopedPersistenceTests.AddDevice_KeepsTwoIdenticalUnitsApartByDeviceKey`.
- [x] **m1. Expired guest session's cards returned on the next "Continue as guest"** — covered by
  the same `ClearOwnerScopedState` call in `OnSessionExpired`; the acceptance criterion now spells
  out that connected cards deliberately survive (live hardware, not expired state).
- [x] **m2. Guest notifications orphaned by sign-in** — `ClearSession` now deletes
  `notifications WHERE owner_kind = 'guest'` in the same transaction; they are uuid-keyed and
  unreachable by any future owner. Regression:
  `LocalStorageServiceOwnerScopedPersistenceTests.ClearSession_RemovesGuestNotifications`.
- [x] **m3/m4. Self-heal loop lifetime** — a `CancellationTokenSource` now retires the loop from
  `ClearDeviceForExit`, the `LoadKeyLayoutForDevice` reset block, and whenever a successor loop
  starts (m4's brief dual-loop window). Note m3's stated cost was smaller than reported: nulling
  `Device` on exit already stopped the loop at its next ≤5 s check via `TryReadSlotKeyedOnceAsync`'s
  guard — the cancel removes even that last wait. Regression:
  `DeviceDetailsCyrexKeyAssignmentTests.LeavingThePage_CancelsTheSelfHealLoop`.
- **m5** — accepted nits, unchanged by agreement (recorded in Round 1's Nits).

### Dated note — 2026-09-01 (PR #125 m3/m4): plan-less Home-surface fixes anchored here

Three small QA fixes on branch `bugfix/PG-642` touch surfaces this plan owns (device cards,
the notification bell) but are too small for plans of their own. Recorded here as the
closest owning plan, per the CLAUDE.md "no owning plan" rule:

- **PG-644 — "NO CONNECTION" card status localized** (`Card_NoConnection` key in both
  `Strings.en.axaml` / `Strings.uk.axaml`; `src/ProoveGaming.App/Views/SingleCard.axaml`).
  Deliberate side effect (PR #125 m4): the status `TextBlock` now carries
  `IsVisible="{Binding !Connected}"`, so on **connected** cards it no longer occupies a
  collapsed-but-spaced slot in the horizontal icons `StackPanel` (`Spacing="10"`). The
  phantom 10 px gap is gone and the icon row sits ~5 px more centered — an intentional
  correction of a layout artifact, not a regression; no compensating margin was added.
- **PG-624 — profile/guest popup chrome stretches to content** (`HomeView.axaml`:
  `MinWidth="204" MaxWidth="280"` + the corner-cap `Grid ColumnDefinitions="16,*,16"`
  chrome replacing the fixed-width `Canvas`). Fixes the uk "Зв'язатися з нами" label
  running flush to the popup edge.
- **Notifications panel scrollbar keeps its style when dragged** (`MainWindow.axaml:394`:
  the bell-panel `ScrollViewer` now uses the shared `pgScrollViewer` class, whose
  `:pressed`/`:pointerover` re-states stop the Fluent theme repainting the thumb).

No acceptance criteria of this plan changed; card persistence and bell behaviour are
untouched.
