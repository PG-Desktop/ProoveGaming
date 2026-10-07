# add-api-v21-notifications-sync — Bell notifications ↔ API v2.1 Notifications Management

Date: 2026-08-31. Contract: `docs/knowledge-base/proove-gaming-launcher-api-v2.1-en.md`
(§21). Sibling plan for the other v2.1 family:
[add-api-v21-device-roster](../add-api-v21-device-roster/add-api-v21-device-roster-plan.md).
Both extend the v2.0 sync machinery from
[add-api-v2-live-sync](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md) and inherit the
shared decisions of
[add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md). The two
plans touch the same seam files (`ISyncApiService`, `SyncApiDtos.cs`, `SyncCoordinator`,
`SettingsSyncService`), so run them **sequentially**, not on parallel branches.

## Goal

API v2.1 adds a per-account Notifications Management family
(`api/notifications/add|list|read|unread|delete|read_all|delete_all`, `list` returns
`unread_count`). Integrate it so the signed-in user's bell is account-backed: entries raised
on one machine appear on the others, read/unread and deletions follow the account, and —
the strategic payoff — anything the backend inserts into the account's list (firmware
notices, announcements; audit `docs/audits/2026-08-21-api-needs.md` item 9) reaches the bell
without any further client work.

## Scope

**In:**
- `ISyncApiService`/`SyncApiService` methods + DTOs for all seven endpoints.
- Schema: `notifications` table gains `server_id`, `type`, `is_dirty`, `local_rev`;
  `sync_tombstones` gains kind `notification`; two `sync_meta` marks
  (`notifications.read_all`, `notifications.delete_all`).
- Push (SyncCoordinator pass step): unsynced rows via `notifications/add` (then a follow-up
  `read` if already read locally), read-state changes via `read`/`unread`, deletions via
  `delete`, plus the batched `read_all`/`delete_all` marks.
- Pull: a `notifications/list` merge step in the sign-in pull **and** a periodic re-list
  (default 15 min) while a user is signed in, so server-originated entries arrive without a
  re-login.
- A `type` slug per notification (new `AddNotification` parameter at every creation site)
  with a type→bell-icon map so pulled rows render; unknown/empty type falls back to the
  Info icon.

**Out:**
- Guest bell (stays fully local — standing rule).
- Any UI change: the bell panel, grouping, counts and commands are untouched; the local
  unread count stays client-derived (`unread_count` from the server is bound but unused —
  the local list is the source of truth for what is on screen).
- Cross-machine language consistency: rows are stored/pushed as displayed
  (already-localized history, per `persist-devices-and-notifications`); a row pulled from
  another machine (or from the backend) shows in whatever language it was written. Accepted.
- Retroactive sync of rows created before this change (they have no `type`/`server_id` and
  are left local-only; only the `read_all`/`delete_all` batch calls touch server state that
  predates them).

## Options considered

### A. Full two-way mirror (recommended)
Signed-in bell rows push to the server; server list merges back; state changes sync both
ways.
- Pros: matches the API's design (the doc calls it "the notifications list in the
  Launcher", with `add` for the client); gives cross-machine continuity consistent with the
  presets/devices story; server-originated announcements ride the same merge for free;
  account deletion server-side already wipes them.
- Cons: machine-local events (device connected, low battery) become account-visible noise
  on other machines; localized history crosses machines in the writing machine's language.

### B. Pull-only announcement feed
Never call `add`; only merge server rows and push read/delete state for them (the audit
item 9 reading: "not a sync of existing local rows").
- Pros: no cross-machine noise; smaller.
- Cons: leaves `add` deliberately unused (a repeat of the `profile/notifications` phase-4
  story) and the account's list empty until the backend starts writing to it — the visible
  outcome of the integration would be *nothing*; two sources of truth (local rows vs server
  rows) need a marker column anyway, so the saving is smaller than it looks.

**Decision: A**, with one deliberate filter kept open to product (Open question 1): if
cross-machine device-event noise proves unwanted, the push can be limited by type slug
without touching the machinery — the filter is one predicate at the marking site.

## Recommended approach

### Schema / storage (`LocalStorageService`, guarded-ALTER migrations)

- `notifications` += `server_id INTEGER NULL` (the server's row id; NULL = never synced),
  `type TEXT NOT NULL DEFAULT ''`, `is_dirty INTEGER NOT NULL DEFAULT 0`,
  `local_rev INTEGER NOT NULL DEFAULT 0`.
- Mutator marking (user-owned rows only, same transaction, event outside the lock —
  identical shape to presets/macros):
  - `AddNotification` sets `is_dirty = 1`, `local_rev = 1` (and now takes/stores `type`).
  - `SetNotificationRead(id, isRead)` bumps `is_dirty`/`local_rev`.
  - `SetAllNotificationsRead(owner)` clears per-row dirty read-marks it supersedes is NOT
    attempted — it simply batches the UPDATE as today and writes the
    `notifications.read_all` `sync_meta` generation mark.
  - `DeleteNotification(id)`: if the row has a `server_id`, write a `sync_tombstones` row
    (`kind = 'notification'`, `uid` = the server id as string) in the same transaction; a
    never-synced row deletes without a tombstone (the standing never-synced skip).
  - `DeleteAllNotifications(owner)` writes the `notifications.delete_all` mark and, in the
    same transaction, drops the owner's pending notification tombstones and read marks
    (delete_all subsumes them).
  - Pull-shaped inserts/updates pass `markDirty: false` and raise no event.
- `MigrateGuestToUserAsync`: migrated guest notification rows get `is_dirty = 1`
  (signal: false) so the reconcile pushes them to the new account.

### SyncCoordinator — new pass step 8 (after the roster step)

Order inside the step: `delete_all` mark → `read_all` mark → tombstones → dirty rows.

1. `notifications.delete_all` mark → `notifications/delete_all`; success ⇒ guarded clear.
2. `notifications.read_all` mark → `notifications/read_all`; success ⇒ guarded clear.
3. Notification tombstones → `notifications/delete` with `notification_id`;
   success or `NOTIFICATION_NOT_FOUND` ⇒ remove tombstone.
4. Dirty rows (`GetDirtyNotifications(owner)`):
   - `server_id NULL` → `notifications/add` (title truncated to 255, subtitle as `message`
     — empty subtitle sends the title again, `MESSAGE_REQUIRED` is a validation error —
     type truncated to 50). Success stores `server_id` and, if the row is read locally,
     follows with `notifications/read`. Rev-guarded dirty clear.
   - `server_id` set → `notifications/read` or `notifications/unread` per the row's
     `is_read`; `NOTIFICATION_NOT_FOUND` ⇒ the server row is gone (deleted elsewhere):
     delete the local row too (log line), clear dirty.
   - Validation errors (`TITLE_REQUIRED` etc.): log warning, rev-guarded clear (resending
     the same payload cannot succeed).
5. Network error / token rejection / 429 aborts the pass; dirty state persists.

No `updated_at` conflicts exist in this family (id-based, boolean state) — the conflict
machinery is deliberately not extended.

### Pull + periodic poll

- **Sign-in pull** (`SettingsSyncService.PullCoreAsync`, new step): `notifications/list` →
  merge into the owner's rows keyed by `server_id`:
  - Unknown `server_id` → insert (markDirty: false): title, message→subtitle, `type`,
    icon resolved from the type map, `created_at` parsed with the invariant format the
    contract uses (unparseable ⇒ now), `is_read` as listed — or read outright while a
    `read_all` mark is pending (the batch will mark the server row read anyway; PR #127,
    m7). Only the list's newest 100 items are merge-candidates (Round 1 B1 / PR #127 M1 —
    the beyond-cap tail must never insert), and the cap trim never evicts `is_dirty = 1`
    rows (an owed push, PR #127 M1 residual).
  - Known `server_id`, local row **not** dirty → adopt the server `is_read` (suppressed
    while `read_all` is pending).
  - Known `server_id`, local row dirty → leave it (the local intent pushes next pass).
  - A **snapshot-time** local row with a `server_id` the FULL list no longer carries →
    delete it locally (`tombstone: false`, no signal) — deletions follow the account
    (PR #127, M2); dirty rows are exempt (their own push meets `NOTIFICATION_NOT_FOUND`
    and converges there), local-only rows are never touched.
  - `server_id` present in a pending tombstone → skip (never resurrect an in-flight
    delete — live-sync review finding B1's lesson, applied from day one).
  - A `notifications.delete_all` mark pending → skip the whole merge (everything listed is
    about to be deleted).
- **Periodic poll**: a small timer loop in `SettingsSyncService` (started when a user
  token exists, stopped on sign-out — same lifecycle the coordinator's token check uses)
  re-runs just the list-merge step every 15 min (constant, injectable for tests). New rows
  reach the bell through `HomeViewModel`'s existing owner-scoped reload: the merge raises a
  dedicated `NotificationsPulled` event on `ISettingsSyncService`; `HomeViewModel`
  subscribes and calls `LoadNotificationsForOwner()` (already generation-guarded and
  UI-thread-safe) when the current owner matches.
- The server's `unread_count` is bound in the DTO but intentionally unused (local count
  derives from the loaded rows; a divergence would only mean rows beyond the local cap).

### Types

New constants class (App/Services, e.g. `NotificationTypes`): `device_connected`,
`device_disconnected`, `new_device`, `low_battery`, `app_update`, `session`, `contact`,
`info`. Each `AddNotification` call site passes its slug; the type→icon map reuses the four
existing bell icon constants in `HomeViewModel` (unknown → Info). No localized strings are
added or renamed — no resource changes.

### Implementation notes (2026-09-01, coder)

- **The type rides on the icon, not on a parameter.** Instead of adding a `type` argument
  to all 16 `AddNotification` call sites, `HomeViewModel.NotificationIcon` gained a `Type`
  field and its constants are built from `NotificationTypes.VisualFor` — the icon a call
  site picks IS the entry's category, and the visuals table has a single owner (the pull
  resolves icons from the same table). Consequences: `MainWindow.axaml.cs` needed no change
  (its untyped call lands as `info`), the plan's `new_device` slug collapsed into
  `device_connected` (same icon, and the distinction is not load-bearing server-side), and
  file-level item 8 became a no-op.
- **`notifications/add` has no idempotence, so no add is ever retried.** Unlike
  macros/presets there is no client uid — a repeated add mints a duplicate server row.
  Every non-abort outcome that cannot bind a server id (validation rejection, unparseable
  2xx, confirmed success without an `id`) clears the dirty flag and leaves the row
  local-only with a warning: one unsynced entry is recoverable, a duplicate per pass is
  not. This supersedes the plan's "unparseable 2xx leaves the mark set" for the ADD path
  only; read/unread keep it (they are idempotent).
- **Batch marks keep-on-failure.** A non-abort failure of `read_all`/`delete_all` leaves
  the mark for the next pass — dropping a `delete_all` mark while the server kept its rows
  would let the next list merge resurrect the whole deleted bell.
- **A pending `read_all` also suppresses read-state adoption** in the merge: mark-all-read
  deliberately writes no per-row dirty flags, so without this guard a poll landing between
  the batch UPDATE and its push would adopt the server's stale unread flags and visually
  un-read the bell.
- **The `PullTask` chain gained a gate** (`_pullChainGate`): the poll loop is the chain's
  second writer alongside `OnSignedInAsync`, and the read-then-replace swap is now
  serialized so no link can drop from the awaitable property.
- **`SettingsSyncService` is now `IDisposable`** (cancels the poll loop on container
  teardown); the poll interval is injectable via an internal constructor, the coordinator
  debounce convention.
- The pull's snapshots (notification tombstones, both batch marks) are read **before** the
  `notifications/list` call — the ordering the device-roster review round 1 (S1) fixed,
  applied here from day one as this plan required.
- Test files (items 12–15): this run only kept the fakes compiling (three
  `RecordingSyncApi` doubles grew the seven methods, two `FakeSettingsSyncService` doubles
  the event, pull endpoint-order assertions the trailing `notifications/list`); the
  Test-strategy coverage itself is /test-writer's round.

### Test notes (2026-09-01, test-writer)

The Test strategy's four blocks are covered — 35 new tests (13
`LocalStorageServiceSyncTrackingTests` incl. the legacy-DB migration extension, 9
`SyncCoordinatorTests`, 10 `SettingsSyncServicePullTests` incl. two poll tests via the
internal interval seam, 3 `HomeViewModelPersistenceTests`), 1514/1514 green in Debug and
**Release** (the CI configuration). The coder deviations are pinned too:
add-without-server-id clears dirty and never retries
(`NotificationAdd_NoServerIdInResponse_…NeverRetries`), a rejected `delete_all` keeps its
mark (`NotificationDeleteAll_Rejected_…`), a pending `read_all` suppresses read-state
adoption (`Pull_PendingReadAll_…`), and a pending `delete_all` skips the merge before the
list call (`Pull_PendingDeleteAll_…`). Not covered (as planned): real 15-minute timer
cadence (interval injected short), live HTTP, and the bell panel's rendering of pulled
rows (manual QA steps 2 and 5).

## File-level change list

| # | File | Intent |
|---|------|--------|
| 1 | `src/ProoveGaming.App/Services/ISyncApiService.cs` | Seven notification methods. |
| 2 | `src/ProoveGaming.App/Services/SyncApiService.cs` | Implement them (`list`/`read_all`/`delete_all` send `EmptyBody`). |
| 3 | `src/ProoveGaming.App/Services/SyncApiDtos.cs` | `NotificationAddRequest`, `NotificationIdRequest`, `NotificationAddResponse`, `NotificationListResponse` + `NotificationPullDto` (all-nullable). |
| 4 | `src/ProoveGaming.App/Services/LocalStorageService.cs` | Column migrations; mutator marking incl. tombstones and the two batch marks; `GetDirtyNotifications`; pull-shaped upsert helpers; migration dirty-marking. |
| 5 | `src/ProoveGaming.App/Models/NotificationRecord.cs` | `ServerId`, `Type`, dirty/rev read-side fields. |
| 6 | `src/ProoveGaming.App/Services/NotificationTypes.cs` | **New.** Type slug constants + type→icon map. |
| 7 | `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` | `AddNotification` gains the type argument (all call sites updated); subscribe `NotificationsPulled` → owner-guarded reload. |
| 8 | `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | Its `AddNotification` call site gains the type slug. |
| 9 | `src/ProoveGaming.App/Services/SyncCoordinator.cs` | Pass step 8 with the order and idempotent-success rules above. |
| 10 | `src/ProoveGaming.App/Services/SettingsSyncService.cs` | Pull-merge step; periodic poll loop; `NotificationsPulled` event. |
| 11 | `src/ProoveGaming.App/Services/ISettingsSyncService.cs` | `NotificationsPulled` event. |
| 12 | `tests/ProoveGaming.App.Tests/LocalStorageServiceSyncTrackingTests.cs` | Notification mutator-mark matrix (see Test strategy). |
| 13 | `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs` | Push-step behaviour (see Test strategy). |
| 14 | `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` | List-merge cases (see Test strategy). |
| 15 | `tests/ProoveGaming.App.Tests/HomeViewModelPersistenceTests.cs` | Type flows through add→store→reload; pulled-row icon resolution. |
| 16 | `docs/CONTEXT.md` | Terms: **Notifications Management API**, **Notification type slug**, **server_id**. |
| 17 | `docs/plans/persist-devices-and-notifications/persist-devices-and-notifications-plan.md` | Dated note: the "no cross-device sync value" schema comment is superseded for signed-in users by this plan. |
| 18 | `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` | Dated note: pull gained the notifications step. |

## Acceptance criteria

- [ ] Given a signed-in user receives a bell entry, when the coordinator pass runs, then one
  `notifications/add` with title/message/type reaches the fake API, the row stores the
  returned `server_id`, and `is_dirty` clears; an entry already read locally is followed by
  exactly one `notifications/read` for that id.
- [ ] Given the user toggles read/unread on a synced row, then the matching single call is
  made; a re-toggle while the push is in flight leaves the row dirty (rev guard) and the
  follow-up pass pushes the newer state.
- [ ] Given the user deletes a synced row, then `notifications/delete` is sent and the
  tombstone drops (also on `NOTIFICATION_NOT_FOUND`); a never-synced row deletes with no
  tombstone and no API call.
- [ ] Given mark-all-read / delete-all, then exactly one `read_all` / `delete_all` call is
  made (no per-row storm) and the corresponding mark clears; delete-all also cancels the
  owner's pending notification tombstones and dirty rows.
- [ ] Given `notifications/list` returns a row with an unknown `server_id`, when the pull or
  the periodic poll runs, then a local row appears with the mapped icon, no dirty mark, and
  the bell reloads for the matching owner; the 100-per-owner cap is respected.
- [ ] Given a listed `server_id` with a pending tombstone (or a pending `delete_all` mark),
  then the merge skips it and the delete still pushes.
- [ ] Given a non-dirty local row whose `server_id` the full `notifications/list` no longer
  carries (deleted on another machine, or by a pushed `delete_all`), when the merge runs,
  then the local row is deleted (no tombstone, no signal); a dirty row and a local-only row
  survive the sweep. (Added 2026-09-01 — PR #127, M2.)
- [ ] Given a listed `server_id` whose local row is dirty, the merge leaves the local state;
  not dirty, the merge adopts the server `is_read`.
- [ ] Given a `read`/`unread` push answers `NOTIFICATION_NOT_FOUND`, then the local row is
  removed and logged.
- [ ] Given a guest session, no notification marks, tombstones or API calls are produced;
  given a guest upgrade, the migrated rows push to the new account.
- [ ] Given the app sits signed-in for >15 min, the poll re-lists and merges without any
  user action; sign-out stops the poll (no further calls, no errors above Debug).
- [ ] Given rows created before this change (no type), the bell renders them unchanged and
  they are never pushed.

## Test strategy

New feature (`add-*`) — mandatory coverage, `tests/ProoveGaming.App.Tests`, existing
fake-`ISyncApiService`/in-memory-storage style:

- `LocalStorageServiceSyncTrackingTests` (extend): the mutator matrix above — add/read
  marking with rev bumps, guest no-ops, tombstone-on-synced-delete vs never-synced skip,
  delete_all subsuming tombstones+marks, migration dirty-marking without signal, legacy-DB
  column migration preserving rows.
- `SyncCoordinatorTests` (extend): step order (`delete_all → read_all → tombstones → dirty
  rows`); add→store-server_id→follow-up-read; read/unread selection by `is_read`;
  `NOTIFICATION_NOT_FOUND` on state push deletes the local row; validation-error clears with
  warning; network abort preserves everything; rev-guarded clear under mid-flight re-toggle.
- `SettingsSyncServicePullTests` (extend): merge matrix (insert/adopt/leave-dirty/skip-
  tombstoned/skip-under-delete_all/cap); unparseable list skips the step; `NotificationsPulled`
  raised once per merge that changed rows.
- `HomeViewModelPersistenceTests` (extend): type round-trips add→storage→reload; pulled row
  renders the mapped icon; `NotificationsPulled` for a different owner does not reload.

Deliberately not covered: real timer cadence (poll interval is injectable, tested with a
short value — same convention as the debounce tests; verify in Release per the CI note);
live HTTP; bell UI rendering (manual QA below).

## QA (manual test steps)

1. Sign in on machine A ("Remember me"). Unplug a connected device so a "device
   disconnected" bell entry appears. Wait ~10 seconds.
2. Sign into the same account on machine B. Expected: the same entry is in machine B's bell
   (in machine A's language), unread, with a sensible icon.
3. On machine B, mark it read. Wait ~10 seconds. On machine A, sign out and back in.
   Expected: it shows as read on A.
4. On machine A, delete one entry and then use "delete all". Wait ~10 seconds. On machine B,
   sign out and back in. Expected: the bell is empty on B too.
5. Leave machine A signed in and idle. Have a second entry created from machine B (e.g.
   unplug a device there). Wait up to ~20 minutes. Expected: the entry appears on machine A
   without re-login and the app stays responsive throughout.
6. As a guest, trigger bell entries, mark them read, delete them. Expected: everything works
   exactly as before and nothing reaches the account (sign in on another machine to
   confirm the bell there is unaffected).

## Review focus

1. `thread-safety` — marking inside existing transactions; merge writes vs the UI-thread
   bell reload (generation guard) — no torn reload; poll loop single-flight with the pull.
2. `async/await` — poll loop fully async, cancellation on sign-out/dispose, no timer leak;
   no sync-over-async anywhere in the step.
3. `UI-thread affinity` — `NotificationsPulled` handling marshals through the existing
   `LoadNotificationsForOwner` path only; no storage access on the UI thread.
4. `null-safety` — `NotificationPullDto` all-nullable; a null-bound item is skipped, never
   thrown on.

## Performance & quality

- Mutator overhead: O(1) extra SQL in already-open transactions; the event is a delegate
  call.
- The poll is one HTTP call per 15 min per signed-in session; the merge allocates per
  changed row only. Bell reload reuses the existing capped (100) load path.
- No per-frame/per-keystroke path touched; `AddNotification`'s hot path gains only the type
  string.

## Risks

- **Cross-machine noise** (device events from machine A on machine B's bell) — accepted for
  now; the type-slug filter is the designed-in mitigation if product objects (Open
  question 1).
- **Server cap unknown**: the contract documents no server-side list cap; a long-lived
  account accumulates rows the client never shows (local cap 100). Harmless to the client;
  flagged to backend.
- **`created_at` parsing**: contract format has no timezone; rows are grouped by local
  date headers, so a server-written timestamp may group under a slightly wrong day.
  Accepted (history cosmetics), noted for QA.
- **Ordering between the sibling plans**: both add a coordinator step and pull step —
  whichever lands second must slot after the first without renumbering assumptions in
  tests (assert relative order, not absolute indices).

## Open questions

1. Product: should machine-local event types (`device_connected`, `device_disconnected`,
   `low_battery`) be excluded from the push (server keeps only durable/account-level
   entries)? Default in this plan: push everything; the filter is a one-line predicate on
   the type slug.
2. Poll interval — 15 min proposed; confirm (each poll is one cheap list call).
3. Backend: will the server itself insert rows (firmware/announcements)? The merge supports
   it as designed; if yes, agree on `type` slugs so icons map (unknown types fall back to
   Info).

## Review findings

### Round 1 — 2026-09-01 — Ship: no

#### Summary
The delta delivers the full v2.1 Notifications Management integration: schema
(`server_id`/`type`/`is_dirty`/`local_rev`, notification tombstones, the two batch marks),
transactional mutator marking with the presets/macros conventions, the seven API methods,
the coordinator's final pass step (delete_all → read_all → tombstones → dirty rows, with
the no-retry rule on `add` and keep-on-failure batch marks), the pull's server-id-keyed
merge with pre-list snapshots, a 15-minute poll on a gated `PullTask` chain, and the
type-slug→icon table shared between HomeViewModel and the pull. 35 new tests, 1514/1514
green in Debug and Release. Review surface: commit `b18a04d` ("api v2.1 integration",
committed directly on main by the author mid-round — it bundles both v2.1 plans' code) plus
the five uncommitted files (this round's tests + plan notes); all inventory accounted for.
One blocker: the merge mishandles a server list longer than the local cap.

#### Plan conformance
Plan: this file (read in full, including Implementation notes and Test notes).
- Implemented as planned: all 18 file-level items (11 code, 4 test, 3 docs), with item 8
  (MainWindow) a recorded no-op under the icon-carried-type deviation.
- Recorded deviations, all verified in code and each pinned by a test: icon-carried type
  (zero call-site churn, `new_device` collapsed into `device_connected`); no-retry
  `notifications/add` (no uid idempotence — an unmappable outcome leaves the row local-only
  rather than minting a duplicate per pass); keep-on-failure batch marks; `read_all`
  suppressing read-state adoption; the `_pullChainGate`; `SettingsSyncService : IDisposable`
  with the injectable poll interval.
- Accepted limitations (per plan), residual risks restated: cross-machine language of
  pulled rows; machine-local event noise on other machines (Open question 1's filter
  remains a one-line predicate); `created_at` has no timezone so date grouping can be off
  by a day; pre-sync rows stay local-only forever.
- Acceptance criteria: all code-facing criteria are test-backed; the bell panel's rendering
  of pulled rows stays manual QA (steps 2/5), as the Test notes record.
- Scope note: the author's commit `b18a04d` bundles the sibling
  `add-api-v21-device-roster` delta (reviewed in that plan's Round 1, S1/S2 closed) — no
  unplanned code found in either half.

#### Code
- [x] **B1. A server list longer than the local cap corrupts bell order and churns rows on
  every merge** — *fixed 2026-09-01 with Option A: the merge loop now stops after the
  list's newest `MaxNotificationsPerOwner` items (index-bounded `considered` counter — no
  Linq import), and the now-dead batch-cap `RemoveRange` block was deleted. Pinned by
  `Pull_ServerOutgrewTheCap_LeavesAFullMirroredBellAloneOnEveryMerge` (bell at the cap
  mirroring the server's newest 100 + a 5-entry tail → both merges are byte-for-byte
  no-ops), verified to FAIL against the unbounded pre-fix loop and pass with the bound.* — `src/ProoveGaming.App/Services/SettingsSyncService.cs:904` (the insert
  branch walks ALL list items) with `:946-968` (insert order) — Blocker
  **Problem:** the merge treats every listed item local rows don't cover as insertable. With
  the local bell at its 100-row cap and the server holding more than 100 rows (no server cap
  is documented, and two machines pushing device events accumulate past 100 in normal use),
  the beyond-cap TAIL of the list — the account's *oldest* entries — is "unknown locally"
  and gets inserted. Local ordering is by row id, so those old entries land with the
  HIGHEST ids and surface at the TOP of the bell, while `AddNotification`'s trim evicts the
  lowest-id rows — which are the genuinely recent entries. The next poll finds the evicted
  recent entries "unknown" again and re-inserts them, evicting the old ones back:
  **every 15-minute tick reshuffles the bell**, with months-old entries pinned above
  today's.
  **Why it blocks:** a self-sustaining, user-visible malfunction of the feature's core loop
  at a state ordinary accounts will reach (>100 server rows), recurring on every poll — not
  an edge case that degrades once. The `:948` comment's claim that the trim evicts "the
  chronologically oldest" is exactly what this path violates (ids stop tracking chronology
  the moment an old entry inserts late).
  **Solution options:**
  - Option A (correct, small): bound the merge to the server's newest
    `MaxNotificationsPerOwner` items — iterate `items.Take(LocalStorageService.
    MaxNotificationsPerOwner)` (the list is newest-first per contract §21.2), so the
    beyond-cap tail is never considered for insertion and the batch-cap trim at `:950`
    becomes dead in practice but harmless. A local row whose server id lies beyond the
    newest 100 merely misses read-flag adoption — it is about to age out anyway.
  - Option B (heavier): key the local ordering on `created_at` instead of row id —
    correct-by-construction but touches `GetNotifications`/trim/the bell's load path and is
    out of proportion to the bug.
- [x] **S1. `ISettingsSyncService.PullTask` doc no longer matches the chain's writers** —
  *fixed 2026-09-01: the comment now names both writers (sign-in pulls and poll ticks) and
  keeps the bounded/never-faults contract explicit.* —
  `src/ProoveGaming.App/Services/ISettingsSyncService.cs:24-30` — Should-fix
  **Problem / why it matters:** the comment still says the property is "the background pull
  chain started by the last OnSignedInAsync call", but the notification poll now extends
  the same chain every tick. `DeviceDetailsViewModel`'s seed gate and `SyncCoordinator`'s
  pass both await this property on the strength of that comment — the next reader deciding
  whether awaiting it is bounded needs to know a poll tick can be in the chain.
  **Fix:** extend the comment: the chain is also extended by the periodic notification poll
  (one list+merge per tick, same never-faults contract), still bounded.

#### Tests
- [x] B1's scenario has no test: local bell at the cap with a matching server prefix, list
  longer than the cap → the merge must insert nothing, preserve order, and a second merge
  must be a no-op (the churn assertion). Fails on the current tail-insert, passes with
  Option A — `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs`.
  *closed 2026-09-01 with the B1 fix; fail-before/pass-after confirmed by temporarily
  lifting the bound. Both poll-tick-lock and test-comment nits were also taken (Task.Run
  link in the poll tick, rewritten delete_all/read_all rationale). Suite: 1515/1515 in
  Debug and Release.*
- Otherwise coverage matches the Test strategy point for point, including all four coder
  deviations; the mid-flight re-toggle test was made interleaving-tolerant rather than
  racing the debounce. 1514/1514 in Debug and Release.

#### Nits
- The poll tick calls `RunPollTickAsync` inside `_pullChainGate`, so the tick's synchronous
  head (up to its first real await) runs under the lock; `OnSignedInAsync` wraps its link
  in `Task.Run` instead. Bounded (one executor enqueue) but inconsistent —
  `SettingsSyncService.cs` (`PollNotificationsLoopAsync`).
- A sign-in pull that merged notification rows reloads the bell twice — once via
  `NotificationsPulled`, once via `PullCompletedWithChanges` → `ReloadOwnerScopedState`.
  Benign (generation-guarded) — `SettingsSyncService.cs:974`.
- `NotificationsStep_DeleteAllRunsBeforeReadAll`'s rationale comment trails off mid-thought
  ("...would resurrect nothing... in the wrong order") — the assertion is right, the prose
  isn't — `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs`.

#### What's done well
- The no-duplicate discipline on `notifications/add` is the round's best judgment call:
  recognizing the missing uid idempotence and trading a rare local-only entry for a
  guaranteed absence of duplicate spam, with the trade-off written down and test-pinned.
- The pre-list snapshots (tombstones + both batch marks) apply the sibling plan's S1 lesson
  from day one, and `read_all` suppressing adoption closes a subtle un-read-the-bell race
  the plan never spelled out.
- One visuals table (`NotificationTypes`) now serves both the live bell and the pull — the
  second-copy extraction rule applied preemptively, with zero churn at 16 call sites via
  the icon-carried type.
- The batch marks' keep-on-failure asymmetry (vs the roster's drop-on-validation) is
  reasoned from consequences — a dropped `delete_all` resurrects the bell — not copied from
  the neighbouring step.

#### Verdict
Ship: no — B1 is a recurring, user-visible corruption of the bell at a state normal
accounts will reach, in the merge both the pull and every poll tick run; its fix is one
bounded enumeration plus a regression test. S1 is a doc line. First: 1) B1 (Take(cap) +
churn test); 2) S1 comment; 3) optionally the poll-tick lock nit while in the file.

### Round 2 — external review (PR #127, boikoserhii84) — all findings addressed 2026-09-01

The PR review ran against commit `b18a04d`; its **M1** duplicated Round 1's B1 (already
fixed in `37b7a54`) and its **m1** (no notification tests) was closed by the same commit's
35 tests. The genuinely open items, all fixed with regression tests (suite 1524/1524 in
Debug and Release):

- [x] **M1 (residual). The cap trim silently evicted dirty unpushed rows** — the trim's
  DELETE now carries `AND is_dirty = 0`: an owed push is never trimmed, the table may
  briefly exceed the cap by the dirty count, the display read stays capped. Regression:
  `AddNotification_Trim_SparesDirtyRowsBeyondTheCap`.
- [x] **M2. Deletions did not propagate between machines** (Goal + QA step 4 promised
  them) — the merge gained the deletion sweep: a snapshot-time local row whose `server_id`
  the FULL list no longer carries (absence is unambiguous even beyond the cap, since the
  whole list's ids are collected while only the newest 100 are merge-candidates) is deleted
  locally with `tombstone: false` and no signal; dirty rows are exempt (their push meets
  `NOTIFICATION_NOT_FOUND`), local-only rows untouched, and a malformed/unsuccessful list
  never sweeps. Regressions:
  `Pull_ServerNoLongerListsASyncedRow_DeletesItLocallyKeepingTheRest`,
  `Pull_EmptyList_EmptiesTheBellOfSyncedRows` (the QA-step-4 shape),
  `Pull_DirtySyncedRowAbsentFromTheList_SurvivesTheSweep`.
- [x] **m2. Pre-sync rows were pushed on their first read click** — `IsUserNotificationCore`
  now gates the dirty mark on the row being sync-tracked (`server_id` set OR non-empty
  `type`), and the sign-up migration marks only tracked rows, so the "never pushed"
  acceptance criterion holds. Regressions:
  `SetNotificationRead_PreSyncRow_TogglesWithoutMarkingOrRaising`,
  `MigrateGuestToUser_LeavesPreSyncRowsUnmarked`.
- [x] **m4. A hung poll tick could stall `PullTask` consumers for the HttpClient's ~100 s
  default** — each tick's network time is now capped by a linked 20 s timeout
  (`DefaultNotificationPollTimeout`, injectable via the internal ctor); a timed-out tick
  warns and retries next tick, and the `PullTask` doc records the bounded contract.
  Regression: `NotificationPoll_HungListCall_IsCutOffByTheTickTimeout`.
- [x] **m5. A row deleted while its `notifications/add` was in flight resurrected via the
  server** — `SetNotificationServerId` now reports whether the row still existed; when it
  does not, the freshly minted server id gets a notification tombstone
  (`WriteNotificationTombstone`, raising the sync event) and the next pass deletes it
  remotely; the read follow-up is skipped. Regression:
  `NotificationAdd_RowDeletedMidFlight_TombstonesTheMintedIdAndDeletesItServerSide`.
- [x] **m7. A server entry arriving while `read_all` was pending inserted as unread** — the
  insert branch now applies `IsRead = listed || readAllPending`, matching the adoption
  guard. Regression: `Pull_PendingReadAll_InsertsNewRowsAsRead`.
- **m3 / m6** belong to the sibling roster plan — see its Round 2.

Live sections updated in the same change: the pull merge's branch list (deletion sweep,
read_all insert rule, dirty-exempt trim) and a new deletion acceptance criterion.
