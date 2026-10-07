# add-api-v21-device-roster — Sync the user's device list to API v2.1

Date: 2026-08-31. Contract: `docs/knowledge-base/proove-gaming-launcher-api-v2.1-en.md`
(§19.3–19.5). Sibling plan for the other v2.1 family:
[add-api-v21-notifications-sync](../add-api-v21-notifications-sync/add-api-v21-notifications-sync-plan.md).
Both extend the v2.0 sync machinery from
[add-api-v2-live-sync](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md) and inherit the
shared decisions of
[add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md)
(guest stays fully local; sync never blocks or surfaces in the UI; API growth stays in
`SyncApiService`; nullable snake_case DTOs tolerant of PHP shape quirks). The two plans
touch the same seam files (`ISyncApiService`, `SyncApiDtos.cs`, `SyncCoordinator`,
`SettingsSyncService`), so run them **sequentially**, not on parallel branches; order
between them is free — this one is the smaller.

## Goal

API v2.1 adds a per-user device roster (`api/devices/add`, `api/devices/list`,
`api/devices/remove`): the server keeps the list of device models the customer owns, and
`devices/list` returns each model with its `active_preset_uid`. Integrate it so a signed-in
user's device cards follow the account: adding a card on one machine registers the model
server-side, deleting the last card of a model unregisters it, and signing in on a fresh
machine brings the account's devices back as offline cards.

## Scope

**In:**
- `ISyncApiService`/`SyncApiService` methods + DTOs for `devices/add`, `devices/list`,
  `devices/remove`.
- Push: roster intents recorded as `sync_meta` marks by the `devices`-table mutators, pushed
  by a new `SyncCoordinator` pass step.
- Pull: a `devices/list` step in the sign-in pull that (a) hydrates offline cards for models
  the local DB has never seen, via a new DeviceHost gRPC catalog lookup, and (b) marks adds
  for local user cards the server does not know yet (reconcile).
- Guest→user upgrade: migrated device rows are marked for roster add.
- New gRPC RPC `GetDeviceCatalogEntry(vendor_id, product_id)` in `deviceservice.proto` +
  DeviceHost implementation (thin delegation to the existing
  `HidDeviceMapRegistry.FindCatalogEntryByPidVid`).

**Out:**
- Using `devices/list`'s `active_preset_uid` to replace the pull's per-type `devices/get`
  calls (a round-trip optimization; the existing device-state flow stays authoritative —
  candidate follow-up once the roster is trusted).
- Per-unit roster entries. The server keys a device by `(vendor_id, product_id)` only; two
  identical units of one model collapse into one server row — the same accepted collapse the
  device-state sync already lives with ([[Settings sync API]] sends no `device_key`).
- Server-driven card *removal* (a model missing from `devices/list` is never deleted
  locally — the roster is a union across machines, and local absence is not delete intent).
- Any UI. Cards hydrated from the server render through the existing offline-card path.

## Options considered

### A. Best-effort inline push from HomeViewModel card add/delete
Call `devices/add`/`devices/remove` directly where cards are persisted/deleted.
- Pros: no new tracking state.
- Cons: offline edits are lost (nothing records the owed push); duplicates the exact
  problem the live-sync plan solved with dirty marks; ViewModel grows HTTP concerns.
  Rejected — the repo already has the right machinery.

### B. sync_meta intent marks + a SyncCoordinator step (recommended)
`LocalStorageService.AddDevice`/`DeleteDevice` record `deviceroster.<vid>:<pid>.add` /
`.remove` generation marks in `sync_meta` (user rows only, same transaction), raise
`SyncRelevantDataChanged`; the coordinator pushes them in its pass with generation-guarded
clears. `DEVICE_ALREADY_EXISTS` and `DEVICE_NOT_FOUND` count as success (idempotence).
- Pros: offline-safe, survives restarts, single owner of retries/ordering; reuses
  `MarkSyncDirty`/`GetSyncMetaByPrefix`/`RemoveSyncMetaIfValue` verbatim.
- Cons: two more mutators join the "must mark dirty" list (the known live-sync risk).

### C. Full mirror table with per-row dirty columns (like presets)
- Cons: the roster has no mutable content beyond membership — a row-level `is_dirty` model
  is machinery for updates that cannot happen. Rejected as oversized.

**Decision: B.** For pull-side hydration, the catalog metadata (type label, images, merge
rule, settings identity) lives only in DeviceHost's `AllowedDevices` catalog, so the App
gets a read-only gRPC lookup rather than a duplicated App-side catalog (which would rot the
first time a device is added to `appsettings.json` only).

## Recommended approach

### Roster identity

One server row per device **model**. The key pushed is the card's **settings identity**
(`settings_vendor_id`/`settings_product_id` of the `devices` row) — the stable per-model
identity the device-state sync already uses, immune to merged-transport cards whose live
VID/PID follows the transport. `name` sent on add is the card's display name (`devices.name`),
truncated defensively to 255 chars.

### Schema / storage (`LocalStorageService`)

- No new columns. New `sync_meta` key family:
  `deviceroster.<vid>:<pid>.add` and `deviceroster.<vid>:<pid>.remove`, values =
  generations via the existing `MarkSyncDirty` mechanics (no-signal variant available for
  pull/migration call sites).
- `AddDevice(ownerKind, ownerId, record)` (user path): after the upsert, in the same
  transaction, write the `.add` mark for the row's settings identity and delete any pending
  `.remove` mark for it (last intent wins). Raise the changed event as usual.
- `DeleteDevice(ownerKind, ownerId, id)`: read the row's settings identity before deleting;
  if after the delete **no other row of that owner shares the settings identity**, write the
  `.remove` mark and delete any pending `.add` mark, same transaction.
- `MigrateGuestToUserAsync`: after re-keying guest device rows to the user, write `.add`
  marks (signal: false — the upgrade push that follows already ends in `SyncCompleted`).
- Guest-owned mutations produce no marks (standing rule).

### SyncCoordinator — new pass step 7 (after device state)

- Enumerate `deviceroster.` marks (`GetSyncMetaByPrefix`).
- `.add`: read the owner's `devices` row by settings identity for the name (row gone by push
  time ⇒ the delete already swapped the mark; skip). Call `devices/add`;
  success or `DEVICE_ALREADY_EXISTS` ⇒ generation-guarded clear. `NAME_REQUIRED`/
  `NAME_TOO_LONG`/`INVALID_*` ⇒ log warning, clear the mark (re-sending the same payload
  can never succeed; the next card mutation re-marks).
- `.remove`: call `devices/remove`; success or `DEVICE_NOT_FOUND` ⇒ guarded clear.
- Network error / token rejection / 429 aborts the pass like every other step (marks
  persist); the step returns `bool` and joins the abort chain.

### Sign-in pull — new step in `SettingsSyncService.PullCoreAsync`

After the existing device-state step:
1. `devices/list`. Non-success or unparseable ⇒ skip the step with a log line (pull stays
   additive and tolerant).
2. For each listed `(vendor_id, product_id, name)` with **no** local user card matching that
   settings identity: resolve the catalog entry through the new `IDeviceCatalogResolver`
   (below). Resolved ⇒ insert an offline `devices` row built from catalog entry + server
   name (disconnected visuals, `device_key = ''`, no marks raised — mirror of the
   `markDirty: false` pull convention) so `ReloadOwnerScopedState`'s revive pass shows it.
   Unresolved (DeviceHost down, model unknown to this build) ⇒ skip with a log line.
3. For each local user card whose settings identity is absent from the server list: mark
   `.add` (signal: false) — the reconcile that backfills pre-v2.1 accounts.
4. Skip any identity that currently has a pending `.remove` mark (the pull must not
   resurrect a deletion in flight — the lesson of live-sync review finding B1).

### `IDeviceCatalogResolver` (new App service)

Small injected seam over the Device gRPC client: `Task<DeviceCatalogEntry?>
ResolveAsync(int vendorId, int productId, CancellationToken)`. Registered in
`BuildServices()`; the pull consumes the interface so tests fake it without gRPC. The
offline-card factory (catalog entry + name → `DeviceRecord`) lives beside it and follows
`FillConnectionCard`/`RecordToViewModel` conventions for labels/images/merge rule.

### gRPC contract change (flagged)

`deviceservice.proto` gains:

```proto
rpc GetDeviceCatalogEntry (GetDeviceCatalogEntryRequest) returns (GetDeviceCatalogEntryResponse);
message GetDeviceCatalogEntryRequest { int32 vendor_id = 1; int32 product_id = 2; }
message GetDeviceCatalogEntryResponse { bool found = 1; DeviceCatalogEntry catalog_entry = 2; }
```

DeviceHost handler delegates to `HidDeviceMapRegistry.FindCatalogEntryByPidVid` (already
string-keyed by int-formatted ids, see `DeviceService.GetBatteryLevel`). Both sides
regenerate from the proto per the repo rule.

### Implementation notes (2026-08-31, coder)

- `PushDeviceStateAsync` returns `Task<bool>` again (it stopped being the pass's last step,
  so it rejoined the abort chain); the roster step is the new last step and returns `Task`.
- The key parser became the shared `SyncCoordinator.TryParseVidPidKey(prefix, suffix)`
  (internal — the pull's roster step parses the same `deviceroster.*` keys), replacing the
  device-state-only `TryParseDeviceStateDirtyKey`.
- The pull's roster step handles a network error / token rejection the same as any
  non-success list — log and skip — rather than through `Aborts`: it is the pull's final
  step, so there is nothing left to abort.
- The `.add` push resolves the card name at push time from the owner's `devices` rows; a
  pair whose card vanished mid-pass is skipped without clearing (its delete already swapped
  the mark to `.remove`, which the next pass consumes).
- Items 11–13 (test files): the coder run only kept the fakes compiling (three
  `RecordingSyncApi` doubles grew the roster methods, the pull tests gained a
  `FakeCatalogResolver`, endpoint-order assertions now expect the trailing `devices/list`);
  the Test-strategy coverage itself is /test-writer's round.

### Test notes (2026-09-01, test-writer)

The Test strategy's three blocks are covered — 22 new tests (8
`LocalStorageServiceSyncTrackingTests`, 8 `SyncCoordinatorTests`, 6
`SettingsSyncServicePullTests`), all green in Debug and **Release** (the CI configuration).
One addition beyond the strategy list: `RosterAdd_Unparseable2xx_LeavesTheMarkSet` pins the
`ConfirmedSuccess` discipline on the add path, and
`Pull_RosterItemThatFailsBinding_IsSkippedWhileTheRestHydrates` pins per-item list
tolerance. Not covered (as planned): the DeviceHost `GetDeviceCatalogEntry` handler (no
DeviceHost test project; two-line registry delegation), live gRPC/HTTP, and the Home UI
rendering of hydrated cards (manual QA step 2).

## File-level change list

| # | File | Intent |
|---|------|--------|
| 1 | `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | Add `GetDeviceCatalogEntry` RPC + request/response messages. |
| 2 | `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` | Implement the RPC as a thin registry delegation. |
| 3 | `src/ProoveGaming.App/Services/ISyncApiService.cs` | `AddDeviceAsync`, `ListDevicesAsync`, `RemoveDeviceAsync`. |
| 4 | `src/ProoveGaming.App/Services/SyncApiService.cs` | Implement the three one-liners (`list` sends `EmptyBody`). |
| 5 | `src/ProoveGaming.App/Services/SyncApiDtos.cs` | `DeviceAddRequest`, `DeviceRemoveRequest`, `DeviceListResponse` + `DeviceListItemDto` (nullable fields incl. `active_preset_uid`, bound but unused for now). |
| 6 | `src/ProoveGaming.App/Services/LocalStorageService.cs` | Roster marks in `AddDevice`/`DeleteDevice`/`MigrateGuestToUserAsync`; `deviceroster.` key builders; last-card-of-identity check. |
| 7 | `src/ProoveGaming.App/Services/SyncCoordinator.cs` | Pass step 7: push `.add`/`.remove` marks with idempotent success codes and guarded clears. |
| 8 | `src/ProoveGaming.App/Services/SettingsSyncService.cs` | Pull step: `devices/list` merge (hydrate unseen models, reconcile-mark local-only ones, honour pending `.remove`). |
| 9 | `src/ProoveGaming.App/Services/DeviceCatalogResolver.cs` | **New.** `IDeviceCatalogResolver` + gRPC implementation + offline-card record factory. |
| 10 | `src/ProoveGaming.App/App.axaml.cs` | Register `IDeviceCatalogResolver` in `BuildServices()`. |
| 11 | `tests/ProoveGaming.App.Tests/LocalStorageServiceSyncTrackingTests.cs` | Mutator-mark matrix for the roster (see Test strategy). |
| 12 | `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs` | Roster step behaviour (see Test strategy). |
| 13 | `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` | `devices/list` merge cases (see Test strategy). |
| 14 | `docs/CONTEXT.md` | Terms: **Device roster**, **GetDeviceCatalogEntry**. |
| 15 | `docs/plans/persist-devices-and-notifications/persist-devices-and-notifications-plan.md` | Dated note: user device rows now feed the v2.1 server roster. |
| 16 | `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` | Dated note: pull gained the `devices/list` step. |

## Acceptance criteria

- [ ] Given a signed-in user's card is persisted (`AddDevice`, user owner), when the
  coordinator pass runs, then exactly one `devices/add` with the card's settings identity
  and name reaches the fake API and the `.add` mark is cleared; `DEVICE_ALREADY_EXISTS`
  clears it the same way.
- [ ] Given the user deletes the **last** card of a settings identity, when the pass runs,
  then one `devices/remove` is sent and the mark clears (also on `DEVICE_NOT_FOUND`); given
  another card of the same identity remains (second unit), no `.remove` mark is written.
- [ ] Given a card is deleted and immediately re-added (or vice versa) before a pass, then
  only the later intent's mark exists and only its call is made.
- [ ] Given guest edits of any kind, then no roster marks and no `devices/add|list|remove`
  calls are produced.
- [ ] Given a network error on the roster step, then the pass aborts, marks persist, and the
  next signal retries them — across an app restart too.
- [ ] Given `devices/list` returns a model with no local card and the catalog resolver knows
  it, when the sign-in pull runs, then an offline user card row exists afterwards with the
  server's name and catalog visuals, no sync marks were raised, and the card appears in the
  Home list via the revive pass; given the resolver returns null, the model is skipped and
  logged, and the rest of the pull is unaffected.
- [ ] Given a local user card the server list lacks, when the pull runs, then its identity
  gains an `.add` mark and the post-pull coordinator pass registers it.
- [ ] Given a pending `.remove` mark for an identity that `devices/list` still returns, when
  the pull runs, then no local card is created for it and the mark survives.
- [ ] Given a guest session upgrades to a new account, then every migrated device row's
  identity is `.add`-marked and pushed by the reconcile pass.
- [ ] Given DeviceHost is asked `GetDeviceCatalogEntry` for a catalogued VID/PID, it answers
  `found = true` with the same entry discovery would attach; unknown ids answer
  `found = false` without error.

## Test strategy

New feature (`add-*`) — mandatory coverage, all in `tests/ProoveGaming.App.Tests`, mirroring
the existing fake-`ISyncApiService` + in-memory-storage style:

- `LocalStorageServiceSyncTrackingTests` (extend): `AddDevice` user row writes the `.add`
  mark and cancels a pending `.remove` (and vice versa on delete); delete of a non-last unit
  writes nothing; guest rows write nothing; `MigrateGuestToUserAsync` marks migrated
  identities without signalling.
- `SyncCoordinatorTests` (extend, `RecordingSyncApi` grows the three methods): full pass
  order now ends `… → device state → roster`; add/remove push with guarded clear;
  `DEVICE_ALREADY_EXISTS`/`DEVICE_NOT_FOUND` treated as success; validation error clears the
  mark with a warning; network error aborts and preserves marks; empty token idles.
- `SettingsSyncServicePullTests` (extend, fake `IDeviceCatalogResolver`): hydration of an
  unseen model (row shape asserted, no event raised); resolver-null skip; local-only card
  gets `.add`-marked; pending `.remove` suppresses hydration; non-success list response
  skips the step without failing the pull.

Deliberately not covered: the DeviceHost RPC handler (no DeviceHost test project exists; it
is a two-line delegation to `FindCatalogEntryByPidVid`, which discovery already exercises);
live gRPC/HTTP; Home UI rendering of hydrated cards (covered manually below).

## QA (manual test steps)

1. Sign in on machine A ("Remember me"). Connect a supported mouse so its card appears.
   Wait ~10 seconds. Expected: nothing visible changes; the app stays responsive.
2. On machine B, sign into the same account. Expected: within a few seconds of the Home
   screen loading, the mouse from step 1 appears as a disconnected (offline) card with its
   correct name and picture.
3. On machine B, delete that offline card. Wait ~10 seconds. On machine A, delete the same
   card too, then sign out and back in. Expected: the card does not come back on either
   machine.
4. On machine A, disconnect from the network, connect a second supported device (card
   appears), then close and reopen the app and restore the network. Expected: within ~10
   seconds of any further action, machine B's next sign-in shows that device's offline card.
5. As a guest, add and remove devices. Expected: everything behaves as before; after
   registering a new account from that guest session, the guest's devices appear on another
   machine signed into the new account.
6. Throughout: no freezes, no error dialogs; device cards connect/disconnect live exactly as
   before.

## Review focus

1. `thread-safety` — marks written inside the mutators' existing transactions; events
   raised outside the lock; the last-card-of-identity check and mark swap are atomic with
   the delete.
2. `gRPC contract` — proto change is additive; both sides regenerate; App copes with an
   unavailable DeviceHost (resolver returns null, never throws into the pull).
3. `async/await` — resolver and pull step fully async with cancellation; no sync-over-async.
4. `null-safety` — `DeviceListItemDto` fields all nullable; an item that binds to nulls is
   skipped, never thrown on.

## Performance & quality

- Mutator overhead: one or two `sync_meta` writes inside an already-open transaction — O(1).
- The roster step runs only when marks exist; `devices/list` is one extra HTTP call per
  sign-in pull, plus one gRPC lookup per *unseen* model (rare after first sync).
- No hot path touched; nothing runs on the UI thread beyond the existing card-collection
  updates the revive pass already does.

## Risks

- **Identity collapse**: two identical units map to one server row — deleting one unit
  locally keeps the server row (correct: another card of the identity remains); deleting
  both unregisters the model. Documented, accepted, mirrors device-state.
- **Merged-transport identity**: the plan keys on settings identity precisely because live
  VID/PID flaps with the transport; coder must verify `settings_vendor_id/product_id` is
  populated for every card the persist path writes (it is the [[Settings bucket identity]]
  contract — if a legacy row has zeros, fall back to `vendor_id/product_id` and log).
- **Hydrated-card drift**: an offline card built from today's catalog may look different
  from one built by live discovery (e.g. double-card products). Acceptable — first real
  connection rewrites the row through the normal discovery path.
- **Missed mutator**: any future `devices`-table mutator must join the marking list — same
  standing risk as live-sync, pinned by the tracking tests.

## Open questions

1. Should `devices/list`'s `active_preset_uid` replace the pull's per-type `devices/get`
   calls (fewer round-trips)? Deferred out of scope; revisit once the roster is live.
2. Server cap/validation on `name` length is undocumented beyond `NAME_TOO_LONG` — the
   255-char client truncation is a guess; confirm with backend.
3. Product call: should deleting the last card of a model also be offered as "remove from
   account everywhere"? Current behaviour (server row removed, other machines keep their
   local cards until they delete them) is the minimal consistent reading.

## Review findings

### Round 1 — 2026-09-01 — Ship: yes

#### Summary
The branch delivers the full v2.1 device-roster integration as designed: the additive
`GetDeviceCatalogEntry` gRPC RPC with a thin DeviceHost handler; the three roster endpoints
on `SyncApiService` with all-nullable DTOs; transactional `deviceroster.*` intent marks in
`AddDevice`/`DeleteDevice`/`MigrateGuestToUserAsync` (settings-identity keyed, last-card
rule, last-intent-wins swap, guest/pull no-ops); a new final coordinator pass step with
generation-guarded clears and idempotent success codes; the pull's `devices/list` step with
catalog hydration, pending-remove suppression and reconcile marks; and 22 new tests, green
in Debug and Release. Review surface: the complete uncommitted working-tree delta (17
modified files + 4 untracked paths), all accounted for; the two v2.1 plan folders and the
knowledge-base translation are docs-only.

#### Plan conformance
Plan: this file (read in full, including Implementation notes and Test notes).
- Implemented as planned: all 16 file-level items, including the docs (CONTEXT terms, dated
  notes in `persist-devices-and-notifications` and `add-api-v2-pull`).
- Recorded deviations, verified reasonable: `PushDeviceStateAsync` back to `Task<bool>`;
  shared `TryParseVidPidKey`; pull roster step log-and-skip instead of `Aborts` (final
  step); add-name resolved at push time with skip-without-clear when the card vanished.
- Accepted limitations (per plan), residual risks restated: (1) identity collapse — two
  identical units are one server row; (2) **union semantics actively re-add** — a model
  removed on machine B is re-registered server-side the next time machine A (still holding
  the card) signs in, via the reconcile mark. That is what the plan's pull step 3 and Open
  question 3 prescribe, but note it makes a single-machine removal impossible while any
  other machine keeps the card; (3) an alias-unknown legacy pair hydrates under the raw
  server pair.
- Acceptance criteria: all code-facing criteria are met and test-backed; the DeviceHost RPC
  criterion and the Home-UI half of hydration remain manual QA (as the Test notes record).

#### Code
- [x] **S1. Pull's pending-remove snapshot is read AFTER `devices/list` — a mark cleared
  mid-pull can resurrect a deleted model** — *fixed 2026-09-01: the `GetDevices` and
  `rosterMeta` reads moved above the `ListDevicesAsync` call, mirroring `PullCoreAsync`'s
  pre-list tombstone snapshot; pinned by
  `Pull_RemoveMarkClearedWhileDeviceListIsParked_StillDoesNotResurrectTheModel`, verified to
  FAIL against the pre-fix ordering and pass with the move.* —
  `src/ProoveGaming.App/Services/SettingsSyncService.cs:760` (list call) vs `:783`
  (rosterMeta read) — Should-fix
  **Problem:** `PullDeviceRosterAsync` fetches `devices/list` first and reads the
  `deviceroster.*.remove` marks afterwards. The macro/preset pull takes its tombstone
  snapshot **before** its first list call precisely so a pending delete confirmed by a
  concurrent coordinator pass (which clears the mark) cannot slip between the list fetch
  and the mark read — this plan even names that lesson ("applied from day one"), but the
  roster step's ordering re-opens it.
  **Failure scenario:** user deletes their last card of a model (`.remove` mark written) →
  re-signs-in while the previous session's pass is still in flight (the live-sync plan's
  documented "pull starting mid-pass" residual). The new pull's `devices/list` response is
  built before the pass's `devices/remove` lands, so it still lists the pair; the pass then
  removes it server-side and clears the mark; the pull reads marks after that clear → no
  pending remove → hydrates the deleted model back as an offline card. Next sign-in the
  reconcile even re-registers it server-side (`.add` mark) — permanent resurrection of an
  explicitly deleted card.
  **Fix:** move the `rosterMeta` read (and, for the same reason, the `GetDevices` local-set
  read) **above** the `ListDevicesAsync` call, mirroring `PullCoreAsync`'s pre-list
  tombstone snapshot at `SettingsSyncService.cs:392`. One-block move; no behavior change on
  the happy path.
- [x] **S2. Stale doc comment on `PushDeviceStateAsync` contradicts its new contract** —
  *fixed 2026-09-01: comment rewritten to describe the bool-abort contract and the roster
  step that follows.* — `src/ProoveGaming.App/Services/SyncCoordinator.cs:774` — Should-fix
  **Problem / why it matters:** the comment still says "Returns nothing, unlike the
  Push*Async steps above: it is the last step of the pass", but the method now returns
  `Task<bool>`, rejoined the abort chain, and is no longer last (the roster step is). The
  next reader will trust the comment over the diff.
  **Fix:** rewrite the three lines to describe the bool-abort contract; the roster step's
  own header already documents the new last step.

#### Tests
- [x] S1's interleaved scenario has no test: add a pull test where the `.remove` mark is
  cleared while `devices/list` is parked on a semaphore (the
  `OnSignedInAsync_ReturnsWhileTheFirstApiCallIsStillBlocked` gating pattern), asserting the
  model is still not hydrated — it fails on the current after-list read and passes with the
  snapshot move — `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs`.
  *closed 2026-09-01 with the S1 fix; the fail-before/pass-after ordering was confirmed by
  temporarily restoring the after-list read. Suite: 1479/1479 in Debug and Release.*
- Otherwise coverage matches the Test strategy point for point (mutator-mark matrix,
  coordinator step order/idempotence/abort/skip, pull hydration/suppression/reconcile/
  tolerance), 1478 green in Debug and Release.

#### Nits
- `DeviceCatalogResolver.ResolveAsync` creates the channel (`GrpcChannel.ForAddress`)
  outside its try — a malformed configured URL would throw into the pull's catch-all
  instead of degrading to a null lookup; and the catch swallows `OperationCanceledException`
  along with transport errors — `src/ProoveGaming.App/Services/DeviceCatalogResolver.cs:49,:66`.
- `BuildOfflineCardRecord` pins the hydrated card's settings bucket to the queried pair and
  ignores `entry.SettingsProductId` — preferring the catalog value when `> 0` would be
  strictly more correct for a legacy-aliased pair — `DeviceCatalogResolver.cs:95`.
- Every user-card re-persist re-marks `.add`, so each discovery-driven persist burst costs
  one idempotent `devices/add` (answered 409). Bounded and debounced; could be suppressed
  when the upsert replaced an identical row — `LocalStorageService.cs:4389`.
- Hydration racing a first discovery persist (pull's `GetDevices` read → discovery insert →
  hydration insert) can leave a cosmetic duplicate offline card under `device_key ''`;
  narrow window, adopted or user-deletable afterwards — same family as the plan's accepted
  "hydrated-card drift" risk.

#### What's done well
- The intent marks are genuinely transactional with the row mutation (same transaction,
  event outside the lock, generation values), and the add/remove swap makes
  "delete-then-re-add before a pass" naturally converge — pinned by tests.
- The `ConfirmedSuccess` / idempotent-code / validation-drop triage in
  `PushDeviceRosterAsync` matches the established envelope discipline exactly, including
  the unparseable-2xx keep.
- The proto change is additive, the DeviceHost handler is a two-line delegation, and the
  App-side resolver is a proper injected seam that never throws into the pull.
- Documentation discipline: CONTEXT terms, dated notes in both sibling plans, coder and
  test-writer notes recorded in this plan before review.

#### Verdict
Ship: yes — no blockers; S1 is a narrow-window race inherited from a documented live-sync
residual, but the codebase already owns the snapshot pattern that closes it, so it should
land as a follow-up in this branch rather than ship as-is. First: 1) S1 snapshot move +
its interleaved test; 2) S2 comment fix; 3) optionally the resolver try-scope nit while in
the file.

### Round 2 — external review (PR #127, boikoserhii84) — roster items addressed 2026-09-01

Two of the PR's findings land in this plan's scope (the rest belong to the notifications
plan — see its Round 2). Suite: 1524/1524 in Debug and Release.

- [x] **m3. Hydrated offline cards always rendered as `single`** while 6 of the 10 catalog
  models are double-cards — and the "first real connection refills it" mitigation this
  plan's Risks accepted never happens for a card that is offline on that machine by
  definition. `BuildOfflineCardRecord` now carries `entry.CardType` (empty → `single`).
  Regression: the hydration test asserts a `double` catalog entry round-trips.
- [x] **m6. `DeviceCatalogResolver` created its channel outside the `try` and swallowed
  `OperationCanceledException`** — Round 1's nit, escalated by the PR review: a malformed
  configured URL threw into the pull instead of degrading to a null lookup, and teardown
  cancellation read as "model unknown". Channel creation moved inside the `try`;
  cancellation now rethrows (the interface doc records the exception); transport failures
  still answer null. Not separately unit-tested (constructing the resolver needs the real
  `ConfigurationManager`); covered by the compile-level contract and the pull's existing
  resolver fakes.
