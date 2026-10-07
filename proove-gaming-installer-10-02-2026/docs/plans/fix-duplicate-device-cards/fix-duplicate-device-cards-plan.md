# fix-duplicate-device-cards — One stored row per merged-transport device

Date: 2026-08-26. Ships **inside the current API v2.0 PR** (branch `features/newapi`), because
that branch made the defect reproduce twice per sign-in (see Goal). Reported from the field:
signing out of account A into B and back into A leaves two RAMPAGE cards — one connected, one
permanently "NO CONNECTION".

## Goal

A device card is persisted under its **routing** identity (`VendorId` / `ProductId` /
`DeviceKey` — `HomeViewModel.PersistDeviceCard:1977`), but for a merged-transport product that
identity changes with the transport: RAMPAGE enumerates as `1389/49271` + the receiver's
Container ID over the 2.4 GHz dongle, and as `10473/12288` with an **empty** key over Bluetooth
(`ApplyBluetoothIdentity:1864` never sets `DeviceKey`). The `devices` table therefore
accumulates two rows for one physical keyboard. `ReloadOwnerScopedState:832` adopts the live
card under its current identity and then re-inserts every stored row that is not on screen —
materialising the other row as a second card. That card can never be revived, because live
discovery matches merged products **by name alone** (`HomeViewModel:1089`) and only ever
adopts the first match. Nothing in the codebase prunes stale device rows.

The fix is to make **storage agree with the in-memory matching rule**: for a merged-transport
product the card identity is the name, and exactly one row may exist per owner per name.

Why it belongs in this PR: phase 1 of the API work added
`PullCompletedWithChanges → ReloadOwnerScopedState` (`MainWindow.axaml.cs:86`), so the
adopt-and-revive pass now runs **twice per sign-in** instead of once. The branch did not
create the defect (no sync code writes the `devices` table — verified) but it doubled its
exposure, so shipping the branch without this fix ships a more visible bug.

**Note (2026-08-26):** the same account-switching test session that reported the duplicate cards
also reported "RGB was not correctly saved when I switched accounts" — a separate defect with a
separate root cause (the singleton `DeviceDetailsViewModel`'s in-session caches carried no owner),
fixed alongside this one in [fix-rgb-owner-isolation](../fix-rgb-owner-isolation/fix-rgb-owner-isolation-plan.md).
The two overlap in symptom only: verify them on one transport at a time, or a card that moved
buckets will make either result ambiguous.

## Scope

**In:**
- A `merge_transports` column on `devices`, written from the card and restored into it, so a
  card restored from storage knows the identity rule *before* discovery reports in.
- `AddDevice` dedupe keyed by the same rule the UI matches on: merged → `(owner, name)`;
  non-merged → the existing `(owner, vendor_id, product_id, device_key, name)` tuple.
- `AddDevice` also deletes the card's previous row by `StorageId`, so a re-persist after a
  port move replaces the row instead of leaving an orphan.
- The revive step in `ReloadOwnerScopedState` compares stored records to on-screen cards with
  the same rule (merged → name only).
- A one-time migration that heals existing databases (including the reporter's) by collapsing
  duplicate rows, using a heuristic that provably cannot collapse two genuinely distinct units.
- Re-persisting a revived card **only when its identity actually changed** (today the
  `found != null` branch never persists, so the row drifts from the card forever).
- The **guest** half of the same dedupe: `UserSession.Devices` appends, so a re-persist would
  leave the guest `Flush` writing one card twice. `PersistDeviceCard` upserts the session record
  instead of adding a second one. (Added 2026-08-26 after Round 1, which found this implemented
  but undocumented.) `UserSession` mutations and snapshots are gated by a lock, because a persist
  can arrive on the gRPC discovery thread while the UI thread snapshots for a flush.

**Out:**
- Reworking the merged-transport concept itself, `SettingsProductId`, or the fact that RAMPAGE
  keeps separate *settings buckets* per live vendor id (`HidDeviceMapRegistry.cs:30-35`) —
  that is a documented, deliberate quirk and untouched here.
  > **No longer true as of 2026-08-27.** The split settings bucket was a bug, not a quirk, and is
  > fixed by [`../fix-settings-bucket-identity/fix-settings-bucket-identity-plan.md`](../fix-settings-bucket-identity/fix-settings-bucket-identity-plan.md):
  > the settings **vendor** is now pinned per product in the catalog alongside the product id, and
  > existing split rows are re-keyed. This plan's scope decision was correct at the time — it stayed
  > out of the settings identity and changed only the `devices` row keying, which is still accurate.
- The guest→user device migration's collapse on `(vendor_id, product_id, name)`
  (`LocalStorageService` ~:4264) — pre-existing, orthogonal, left alone.
- Any UI change. Cards, layout and wording stay exactly as they are.
- Deleting rows for devices the user genuinely removed via the card's ⋮ menu.

## Options considered

### A. Persist the settings identity instead of the routing identity
Store `SettingsProductId` + `SettingsDeviceKey` (empty for merged products).
- Pros: reuses an existing concept; no schema change to the key semantics.
- Cons: **does not fix it.** *(True when written; the settings identity gained a pinned vendor on
  2026-08-27 — see the note under "Out" above.)* The settings identity still carries the *live vendor id*
  (`ApplyDeviceIdentity:1174-1179` sets `card.VendorId` from the live transport and only pins
  the product id), and RAMPAGE's dongle enumerates under a different vendor than the board. Two
  rows would remain (`1389/…` and `10473/…`). Rejected on the evidence.

### B. Storage identity mirrors the UI matching rule, carried by a `merge_transports` column (recommended)
Merged product → one row per `(owner, name)`; everything else keeps today's tuple. The column
exists so a restored offline card applies the same rule before any device is discovered.
- Pros: single source of truth for "same device?" across UI and storage; self-heals on the next
  persist; leaves non-merged multi-unit behaviour (two identical mice) untouched.
- Cons: one schema column and a migration; `Name` becomes part of a dedupe key, so a product
  renamed in the catalog would strand its old row (acceptable — the UI already matches on name,
  so a rename already creates a new card today).

### C. Blanket migration collapsing every same-name duplicate, no column
- Pros: smallest diff.
- Cons: destroys the legitimate two-identical-mice case (same VID/PID, different Container IDs
  — the exact scenario PR #112 M2 added `device_key` to protect), and nothing prevents the
  duplicate from coming back on the next transport switch. Rejected.

**Decision: B.** The migration heuristic below is the safe subset of C, used only to heal rows
written before the column existed.

## Recommended approach

### 1. Card identity helper (single definition)

Add one internal static in `HomeViewModel` and use it in every place that asks "is this the
same device?":

```
CardIdentity(name, vendorId, productId, deviceKey, mergeTransports) =>
    mergeTransports ? (name.ToUpperInvariant(), 0, 0, "")
                    : (name.ToUpperInvariant(), vendorId, productId, deviceKey ?? "");
```

Case-folding is deliberate: discovery already upper-cases the client name
(`HomeViewModel:1016`) and matches case-insensitively (`:1091`), while SQLite's `name = $name`
is case-sensitive — the mismatch is a latent second source of duplicates.

### 2. Schema + storage (`LocalStorageService`)

- `devices` gains `merge_transports INTEGER NOT NULL DEFAULT 0`, added by the existing guarded
  `AddColumnIfMissing` path.
- `GetDevices()` (guest), `GetDevices(owner)` and `Flush(...)` read/write the new column.
- `AddDevice(ownerKind, ownerId, device)` deletes, inside its existing transaction:
  - the row whose `id = device.Id` when `device.Id > 0` (replace-in-place on re-persist), **and**
  - when `device.MergeTransports` → every row of this owner with `name = $name COLLATE NOCASE`;
    otherwise → the current exact tuple, with `name` compared `COLLATE NOCASE`.
  Then inserts one row as today and returns its id. Update the stale doc comment at `:3721-3727`
  (it still claims the key is `(owner, vendor, product, name)` and omits `device_key`).
- **Heal migration**, run once in `EnsureSchema` right after the column is added, guarded so it
  cannot run twice (it only touches groups it can prove are duplicates):

  > Within each `(owner_kind, owner_id, name COLLATE NOCASE)` group that contains **two or more
  > distinct `(vendor_id, product_id)` pairs**, keep the row with `MAX(id)` (most recently
  > persisted = the transport last seen live), delete the rest, and set `merge_transports = 1`
  > on the survivor.

  The heuristic is exact for this defect and safe for the case it must not touch: two identical
  physical units always share one `(vendor_id, product_id)` and differ only by `device_key`, so
  such a group has exactly one distinct pair and is never collapsed. A same-product/same-transport
  port move (same pair, different key) is likewise untouched — that stale-row case is handled
  going forward by the `StorageId` delete, not retroactively.

### 3. View model (`HomeViewModel`)

- `PersistDeviceCard` writes `MergeTransports` and passes `card.StorageId` on the record so
  `AddDevice` can replace in place.
- `RecordToViewModel` restores `vm.MergeTransports` from the record (`:1920` area).
- `ReloadOwnerScopedState:844-855`: build the `onScreen` set and compare stored records using
  `CardIdentity`, not the raw tuple. Also fix the stale comment at `:836`.
- Discovery `found != null` branch (`:1121-1135`): capture the identity before
  `ApplyDeviceIdentity` and re-persist **only if it changed**. This must not add a write on the
  steady-state discovery tick (see Performance).
- `DeviceRecord` gains `MergeTransports`.

### 4. What the reporter will observe

On first launch after the fix the migration collapses their two RAMPAGE rows into one, so the
ghost card is gone at startup — no manual cleanup, no database deletion.

> **Note (2026-08-27, `fix-owner-scoped-state-persistence`):** two corrections to the mechanisms
> described above, neither of which changes this plan's acceptance criteria.
>
> 1. **The one-time heal now bails out on two-unit groups.** "Two or more distinct
>    `(vendor_id, product_id)` pairs" is the fingerprint of one device seen over two transports —
>    but a group holding *two identical physical units* **plus** one extra transport row for one of
>    them also has two distinct pairs, and the heal would have collapsed both units into a single
>    card. The flagging `UPDATE` therefore gained a `NOT EXISTS` guard: a same-name group that holds
>    two rows sharing `(vendor_id, product_id)` but differing in `device_key` is left entirely
>    alone — not flagged, and so not deleted. The reported RAMPAGE shape (all pairs distinct) still
>    collapses to the highest id with the flag set, exactly as the criterion at line 194 states.
>    The cost is that such an owner keeps the ghost row until the device next connects, when
>    `AddDevice` collapses it under catalog authority.
> 2. **The revive comparison is taken against a pre-adopt snapshot.** `ReloadOwnerScopedState` now
>    reads the owner's stored rows *before* the adopt loop writes, and the revive pass decides with
>    the [[Swallow rule]] — extracted as `HomeViewModel.IsRepresentedOnScreen` — instead of relying
>    on `AddDevice`'s deletes having already run. Behaviour-preserving: the two predicates coincide,
>    which is why `SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne` stays
>    green unmodified. See `docs/plans/fix-owner-scoped-state-persistence/`.
## File-level change list

Tests land first per the bug-fix rule (items 1–2 must fail on current code).

1. `tests/ProoveGaming.App.Tests/LocalStorageServiceDeviceIdentityTests.cs` — new:
   reproduction + migration + non-merged-preservation coverage at storage level.
2. `tests/ProoveGaming.App.Tests/HomeViewModelPersistenceTests.cs` — edit: reproduction of the
   two-cards-after-sign-in path and the idempotence of a repeated `ReloadOwnerScopedState`.
3. `src/ProoveGaming.App/Models/DeviceRecord.cs` — edit: `MergeTransports` property.
4. `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: `merge_transports` column +
   guarded migration and the one-time heal; column in both `GetDevices` overloads and `Flush`;
   `AddDevice` dedupe by identity rule + delete-by-id; corrected doc comment.
5. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — edit: `CardIdentity` helper;
   `PersistDeviceCard` writes the flag and the storage id, and **upserts** the guest session
   record rather than appending a second one; `RecordToViewModel` restores the flag;
   `ReloadOwnerScopedState` compares by identity; conditional re-persist on the revived
   branch; `LoadDevices` and `FlushSession` read the session list through its snapshot;
   stale comments fixed.
6. `docs/CONTEXT.md` — edit: `merge_transports` / "card identity" one-liners.
7. `docs/plans/persist-devices-and-notifications/persist-devices-and-notifications-plan.md` —
   edit: dated note that the device-row key is now identity-based (that plan describes the
   original `devices` contract).
8. `docs/plans/add-per-device-identity/add-per-device-identity-plan.md` — edit: dated note
   pointing here (it owns the `device_key`/two-identical-units contract this change preserves).
9. `src/ProoveGaming.App/Services/UserSession.cs` — edit: `UpsertDevice` (replace in place,
   append when the id is unknown) replaces `AddDevice` as the only way to add a record, plus
   `SnapshotDevices`; all mutations and snapshots take a private lock. (Added 2026-08-26 after
   Round 1: item 5's guest dedupe needs an upsert rather than a remove-then-add pair, and the
   persist can run on the discovery thread.)

## Acceptance criteria

- [x] Given a merged-transport card persisted once under dongle identity (`1389/49271`, key
  `"C1"`) and again under Bluetooth identity (`10473/12288`, key `""`) for the same owner and
  name, when both persists have run, then `devices` holds **exactly one** row for that owner and
  name. (Fails today: two rows.)
- [x] Given a **non-merged** product with two units of the same model (same vendor/product,
  keys `"A"` and `"B"`), when both are persisted, then `devices` holds **two** rows.
- [x] Given a legacy database containing two rows with the same owner and name but different
  `(vendor_id, product_id)` pairs, when `LocalStorageService` is constructed over it, then one
  row remains (the one with the highest id), its `merge_transports` is 1, and no other owner's
  rows changed.
- [x] Given a legacy database containing two rows with the same owner, name and
  `(vendor_id, product_id)` but different `device_key`, when `LocalStorageService` is
  constructed over it, then **both rows remain**.
- [x] Given one live merged card on screen and one stored row for the same name under a
  different transport identity, when `ReloadOwnerScopedState` runs, then the device list
  contains **one** card for that name and it is the connected one. (Fails today: two.)
- [x] Given `ReloadOwnerScopedState` runs twice in a row (sign-in, then the post-pull refresh),
  when it completes, then the card count and the stored row count are unchanged by the second
  run.
- [x] Given a card is re-persisted after its routing identity changed (port move on a
  non-merged product), when the persist completes, then the previous row is gone and exactly
  one row for that card exists.
- [x] Given a steady-state discovery tick where a connected card's identity is unchanged, when
  the tick completes, then no `devices` write is issued for it.
- [x] Given the migration has already run once, when the app starts again, then it performs no
  further deletes.

## Test strategy

Bug fix (`fix-*`): the reproduction tests are written **first** and must fail on the current
code. All in `tests/ProoveGaming.App.Tests`, using the existing temp-file/in-memory
`LocalStorageService` fixtures and the `HomeViewModelPersistenceTests` card fixtures.

- `LocalStorageServiceDeviceIdentityTests` (new):
  - **Reproduction:** merged card persisted under two transport identities → one row.
  - Non-merged two-units → two rows preserved.
  - Delete-by-`StorageId`: re-persist with a changed key → single row, new id returned.
  - Name case-insensitivity: `"RAMPAGE"` then `"Rampage"` (merged) → one row.
  - **Heal migration:** legacy DB (raw SQL, no `merge_transports` column) with the
    Rampage-shaped pair → collapsed to the max-id row with the flag set; with the
    two-identical-mice shape → both preserved; other owners untouched; re-running the
    constructor performs no further deletes.
  - Guest `Flush`/`GetDevices()` round-trip carries the flag.
- `HomeViewModelPersistenceTests` (edit):
  - **Reproduction:** stored ghost row + live merged card → `ReloadOwnerScopedState` yields one
    card (the connected one).
  - Repeated `ReloadOwnerScopedState` is idempotent in both card count and row count.
  - Non-merged: two stored units + one live → still two cards (no over-collapse).
- `UserSessionDeviceListTests` (new, added 2026-08-26 with file-level item 9): the guest session
  list had no direct tests at all before this change. `UpsertDevice` replaces the record carrying
  an id **in place** — same id, same slot — so a guest card's position and `StorageId` survive a
  transport switch; an unknown id appends under a freshly minted one; the id sequence does not
  churn on re-persist; `SnapshotDevices` is a stable, per-call copy that shares its record
  instances; and concurrent upserts / removals against a looping snapshot neither tear nor lose a
  record. The last one is the automated guard for Round 1's S2 — a verbatim replica of the pre-fix
  `AddDevice`/`RemoveDevice` pair fails all three contracts (id churn, tail-append, torn list),
  which is how the reproduction ordering was confirmed without reverting the fix.

Deliberately **not** covered by automated tests: real HID/Bluetooth transport switching (needs
hardware — QA steps 2–4 own it), the `MainWindow` sign-in/sign-out wiring, and the
`PullCompletedWithChanges` subscription itself (UI plumbing, no headless Avalonia harness in
this repo — the idempotence criterion covers its effect at the view-model level). Added
2026-08-26: also the heal migration's **transactionality** (Round 1 S1) — its failure mode is a
crash between two auto-committed statements, which needs crash injection the repo has no harness
for; the wrapping transaction is itself the guard. And `PersistDeviceCard` on the discovery tick
has no view-model seam (`HandleUsbHidDeviceFoundAsync` is private and driven by the gRPC handler),
so the guest re-persist is pinned at the `UserSession` level rather than end-to-end.

## QA (manual test steps)

1. Launch the app with the Rampage keyboard connected through its 2.4 GHz dongle and sign into
   account A. Expected: exactly one RAMPAGE card, shown as connected.
2. Switch the keyboard to Bluetooth (turn the dongle link off / use the board's BT slot) and
   wait a few seconds. Expected: still exactly **one** RAMPAGE card, now showing the Bluetooth
   connection; no second "NO CONNECTION" card appears.
3. Sign out of account A, sign into account B, then sign back into account A. Expected: one
   RAMPAGE card. Repeat the sign-out/sign-in cycle two more times — the count must stay one.
4. If you previously had the duplicate card: install/run this build once with your existing
   data. Expected: the extra "NO CONNECTION" RAMPAGE card is gone on the very first launch,
   without deleting any settings — your presets, macros and RGB settings are still there.
5. If you have two identical mice of the same model, plug both in. Expected: they still show as
   **two** separate cards (this must not be collapsed).
6. Unplug one mouse and plug it into a different USB port. Expected: one card for it, keeping
   its settings; no duplicate card appears.
7. Delete a device card via its ⋮ menu, then sign out and back in. Expected: it stays deleted.

## Review focus

1. `thread-safety` — the migration runs from `EnsureSchema` on the storage connection and opens
   its own transaction; the new persist path must stay on the storage executor / existing `_lock`
   discipline, the guest session list must only be mutated and snapshotted through `UserSession`'s
   gate (the persist can arrive on the gRPC discovery thread), and the discovery-tick identity
   comparison must not race the UI thread's `Devices` collection.
2. `performance hot-path` — discovery runs on a timer over every device; verify the revived
   branch issues **zero** storage writes when nothing changed.
3. `null-safety` — `DeviceKey`/`Name` nullability in the identity helper and in the SQL
   parameters.
4. `UI-thread affinity` — card list mutations in `ReloadOwnerScopedState` stay on the UI thread.

## Performance & quality

- Discovery is a recurring poll, so the new re-persist must be strictly conditional on an
  identity change; a write per tick per device would be a regression. The comparison is a tuple
  equality on values already in memory — no allocation beyond one `ToUpperInvariant` per card
  per tick, which mirrors what discovery already does at `:1016`.
- The heal migration is O(rows) over a table that holds a handful of cards per owner and runs once
  at construction, before the UI needs data. It opens **its own** transaction (`EnsureSchema` has
  none, so the flag UPDATE and the row DELETE would otherwise auto-commit separately and a crash
  between them would strand the duplicates permanently), mirroring `BackfillMissingUids` and
  `MigrateMacrosToDevices`.
- No change to per-frame RGB, device I/O or startup paths beyond the above.

## Risks

- **The heal heuristic keeps the wrong row** if the last-persisted transport is not the one the
  user considers primary. Impact is cosmetic and self-correcting: the row is replaced the moment
  the device is discovered again. Accepted over the alternative of guessing by transport rank.
- **Two identical merged-transport devices collapse into one card.** This is *already* the
  behaviour of live discovery (merged products match by name alone, `:1089`); this change makes
  storage agree rather than introducing it. Called out explicitly so review does not read it as
  a new regression — a user with two RAMPAGE keyboards sees one card today and after this change.
- **Name-based keys and catalog renames:** renaming a product in the device catalog strands the
  old row (it becomes a card with no live match). Same exposure as today's name-based UI
  matching; no new mechanism.
- **Migration on live user data.** Guarded by the "two distinct vid/pid pairs" proof and covered
  by both preserve-and-collapse tests; the schema step is idempotent and re-runnable.

## Open questions

1. ~~Should the heal migration also run for `owner_kind = 'guest'` rows?~~ **Closed 2026-08-26
   (Round 1): yes, as written.** Guest rows are rewritten wholesale by `Flush` on every change,
   so running the heal over them is harmless; no restriction to user rows.
2. Nothing else blocks `/coder`.

## Review findings

### Round 1 (2026-08-26) — Ship: yes

Scope reviewed: this fix's working-tree delta only (`DeviceRecord.cs`, `LocalStorageService.cs`,
`HomeViewModel.cs`, the two test files, `docs/CONTEXT.md` and the two dated plan notes). The four
API v2.0 phases already committed on `features/newapi` carry their own closed Round 1 journals and
were not re-reviewed. Verification: `dotnet test ./tests/ProoveGaming.App.Tests/…` — **1158 passed,
0 failed** (19 new). The full-solution build is blocked by a running DeviceHost dev process holding
DLLs (MSB3021 file locks) — an environment condition, not a compile error.

#### Plan conformance

- **Implemented as planned:** all 8 File-level change list items. `merge_transports` column with the
  guarded `AddColumnIfMissing` path (`LocalStorageService.cs:561`); the one-time heal
  (`:926`); the column carried in both `GetDevices` overloads and `Flush`; `AddDevice` dedupe by
  identity rule + owner-scoped delete-by-id (`:3780-3830`); the corrected doc comment; the single
  `CardIdentity` definition (`HomeViewModel.cs:1188`) used by the revive pass (`:854`), the
  discovery tick (`:1130`) and — through `DeviceRecord` — the storage dedupe; `RecordToViewModel`
  restoring the flag (`:1955`); `PersistDeviceCard` writing `Id` + `MergeTransports` (`:2018`,
  `:2026`); `DeviceRecord.MergeTransports`.
- **All 9 acceptance criteria met.** Eight are pinned by tests (criteria 1–7 and 9 map one-to-one
  onto `MergedCard_PersistedUnderBothTransportIdentities_LeavesOneRow`,
  `NonMergedTwoUnitsOfOneModel_KeepSeparateRows`,
  `LegacyDb_RampageShapedPair_CollapsesToTheHighestIdWithTheFlagSet`,
  `LegacyDb_TwoIdenticalUnits_AreBothPreserved`,
  `SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne`,
  `RepeatedReloadOwnerScopedState_IsIdempotent_ForMergedCards`,
  `RePersistingByStorageId_AfterTheKeyMoved_ReplacesTheRowInsteadOfOrphaningIt` and
  `LegacyDb_Heal_RunsOnlyOnce_AndDoesNotDeleteOnLaterConstructions`). Criterion 8 — "a steady-state
  discovery tick issues no devices write" — has no automated guard and was **verified by reading**;
  see *Steady-state re-persist* below. Verdict: correct, and it cannot fire spuriously.
- **Deviates from plan (1) — accepted, but record it:** `PersistDeviceCard:2036-2037` calls
  `_session.CurrentSession.RemoveDevice(card.StorageId)` before `AddDevice` for **guest** sessions.
  Not in the plan's Scope or File-level list, but it is necessary and correct: `UserSession.AddDevice`
  appends rather than upserts (`UserSession.cs:29-34`), so without it a re-persist would leave the
  guest `Flush` writing the same card twice — reintroducing the very duplicate this plan removes, on
  the guest path. It also mirrors the existing `ExecuteConfirmDelete:2170-2174` pattern, and the
  id-namespace hazard is already closed upstream: the `IsGuest` transition zeroes every card's
  `StorageId` (`:749-754`), so a user-store id can never be aimed at a session record.
  **Ruling on the ordering side effect: acceptable as shipped.** `RemoveDevice` + `AddDevice` moves
  the record to the tail of `UserSession.Devices` and mints a new id, and guest `GetDevices()` has no
  `ORDER BY` (rowid = `Flush` insertion order), so a re-persisted guest card can appear last in the
  list after a restart. It is cosmetic, in-session invisible (the bound `Devices` collection is
  untouched), and only triggers on a genuine identity move — a transport switch or port move — which
  is rare. It is strictly better than the duplicate card it prevents. See N1 for the cheap
  improvement if it is ever worth doing. Add this to the plan's Scope and File-level item 5.
- **Deviates from plan (2):** the heal does **not** run inside a transaction, though *Performance &
  quality* states it "is inside the existing schema transaction". `EnsureSchema` opens none. See S1;
  the plan bullet needs correcting either way.
- **Accepted limitation (per plan, Risks):** the heal's "two distinct `(vendor_id, product_id)`
  pairs" heuristic would collapse two genuinely different products that happen to share a name.
  Residual risk assessed as negligible — card names come from the device catalog's `ClientName`, and
  the one known same-id/different-name pair ([[Starlight]] / [[Cosmo]], both `0x05D6/0x000A`) has the
  opposite shape and is untouched. No action.
- **Open question 1 answered:** yes, the heal may run for `owner_kind = 'guest'` rows as written.
  Confirmed harmless — guest rows are rewritten wholesale by `Flush` on every change. Close the
  question; no restriction needed.

#### Steady-state re-persist — criterion 8, verified by reading

`HomeViewModel.cs:1130` captures `identityBefore` **before** `FillConnectionCard` and
`ApplyDeviceIdentity`, and compares at `:1140`. This is correct and load-bearing:

- `FillConnectionCard:1690` unconditionally restamps `card.Name = entry.DeviceName ?? entry.Name`,
  and `ApplyDeviceIdentity:1206-1209` unconditionally restamps `VendorId` / `ProductId` /
  `DeviceKey` / `MergeTransports`. Capturing *after* `FillConnectionCard` would compare the new name
  against itself and miss a merged card's rename — the one change a name-keyed identity has left.
- It cannot fire spuriously. Both methods are pure functions of `bestEntry`, `batteryVid`,
  `batteryPid` and `routingKey`, all recomputed identically on a steady-state tick, so the card's
  fields already hold their post-tick values at capture time and the tuple compares equal → **zero
  writes**. The values are catalog constants, not localized strings, so a language switch does not
  perturb them either.
- The only ticks that write are genuine identity moves — transport switch, port move, and the
  one-time backfill of the flag onto a card restored from a legacy `merge_transports = 0` row. Each
  writes once: the new value is what the next tick captures.

#### Code

- [x] **S1. The heal migration's UPDATE and DELETE commit separately** — `src/ProoveGaming.App/Services/LocalStorageService.cs:926-947` (`HealDuplicateDeviceRows`) — Should-fix
  **Problem:** both statements are one `CommandText` batch executed by a single `ExecuteNonQuery`
  (`:946`) with no transaction. `EnsureSchema` opens none — it is a bare `using var cmd` around the
  CREATE batch — so SQLite auto-commits each statement independently. The plan's *Performance &
  quality* section asserts the opposite ("inside the existing schema transaction"); the premise is
  simply not true of this file. Both sibling one-time migrations already do it properly:
  `BackfillMissingUids` (`~:1000`) and `MigrateMacrosToDevices` (`~:703`, `~:747`) each open their
  own `_connection.BeginTransaction()`.
  **Why it matters:** a crash, power loss, or an exception thrown by the DELETE between the two
  commits leaves the flags written and the duplicate rows still present — and the heal is guarded on
  `AddColumnIfMissing` returning true (`:561`), so the ALTER has already committed and it never runs
  again. The user's duplicate RAMPAGE card survives the upgrade the migration exists to fix.
  **Why it is not a Blocker:** the half-done state self-heals. Both surviving rows now carry
  `merge_transports = 1`, so the first `PersistDeviceCard` of that card deletes *every*
  `(owner, name)` row and inserts one — which happens on the next sign-in if the keyboard is live,
  or one session later if it is not. Worst case is a transient second card for a user who both
  crashed mid-migration and has the device disconnected.
  **Fix:** wrap the body in `using var transaction = _connection.BeginTransaction();` … `transaction.Commit();`
  and set `cmd.Transaction = transaction`, exactly as `BackfillMissingUids` does. Two lines. Then
  correct the plan's *Performance & quality* bullet to say the heal opens its own transaction rather
  than claiming EnsureSchema has one.

- [x] **S2. Guest re-persist mutates `UserSession.Devices` from the discovery thread** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:2037` (`PersistDeviceCard`) — Should-fix (pre-existing, widened here)
  **Problem:** the new conditional re-persist at `:1141` runs inside
  `HandleUsbHidDeviceFoundAsync`, reached from the `async void`
  `UsbHidDeviceService_DeviceFound` gRPC handler — a background thread (the method marshals every UI
  mutation through `Dispatcher.UIThread.InvokeAsync`, so it is demonstrably not on the dispatcher).
  In a guest session `PersistDeviceCard` now calls `RemoveDevice` → `List.RemoveAll`
  (`UserSession.cs:36`) on that thread, against a plain `List<DeviceRecord>` that the UI thread also
  mutates via `ExecuteConfirmDelete:2174` and `RegisterExistingCardsInSession:2003`, and that
  `FlushSession:2051` snapshots with `ToArray()`.
  **Why it matters:** `RemoveAll` compacts the list, so an interleaved `ToArray` can now duplicate or
  skip an element rather than merely tear the tail — a guest flush that writes a card twice or drops
  one. The class of race is **pre-existing** (the new-card branch at `:1156` already called
  `PersistDeviceCard` → `AddDevice` → `FlushSession` from this same thread), so this is not a
  regression introduced by the fix; but the change adds an element-shifting mutation and makes the
  path reachable on every transport switch rather than only on first discovery. Note also that
  `FlushSession`'s comment at `:2049` — "Snapshot on the UI thread" — is now demonstrably false for
  this call path, which is how the hazard stays invisible.
  **Fix options:**
  - Option A (correct): marshal the guest branch of `PersistDeviceCard` onto the dispatcher, so every
    `UserSession.Devices` mutation happens on one thread and `FlushSession`'s comment becomes true again.
  - Option B (targeted): give `UserSession` a private lock around `AddDevice` / `RemoveDevice` /
    a new `SnapshotDevices()`, and have `FlushSession` call the snapshot.
  - Option C (minimum, if this is deferred): correct the `:2049` comment to state that the snapshot
    can run on the discovery thread, so the next reader is not misled. Do not ship A/B *and* leave
    the comment.

#### Tests

- Coverage is strong: 19 new tests, every one carrying a "pre-fix this did X" note, and the
  reproduction cases genuinely target the defect rather than restating the new code. The
  preserve-cases (`LegacyDb_TwoIdenticalUnits_AreBothPreserved`,
  `LegacyDb_SameProductDifferentKey_IsNotCollapsed`, `NonMergedTwoUnitsOfOneModel_KeepSeparateRows`,
  `SignIn_WithTwoStoredUnitsOfANonMergedModel_KeepsBothCards`) are the right guard against
  over-collapse and are what makes the heuristic reviewable.
- Criterion 8 has no automated guard, as the orchestrator flagged. Accepted: the plan already
  declares the discovery path out of automated scope (no headless harness), and the reasoning above
  closes it by reading. The `CardIdentity_*` tests pin the comparison primitive itself, which is the
  testable half.
- S1's failure mode (crash between two auto-committed statements) is not reachable from a test.
  Fixing S1 is the guard.

#### Nits

- **N1.** `UserSession.RemoveDevice` + `AddDevice` mints a new id and appends — an `UpsertDevice(record)`
  replacing in place at the same index with the same id would keep guest card order stable across
  restarts and stop `_nextId` churning. Cosmetic; the current form is accepted (see Deviation 1) —
  `src/ProoveGaming.App/Services/UserSession.cs:29-36`.
- **N2.** Transitional edge, no action today: if two on-screen merged cards ever share a name — only
  reachable from a legacy or partially-healed database — the second `PersistDeviceCard` deletes the
  first's freshly inserted row, leaving `card1.StorageId` dangling, so a ⋮ delete on that card is a
  no-op for one session. Converges on the next restart, and the identity dedupe means the revive pass
  can no longer create the situation — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:841-847`.
- **N3.** The `NOT IN` subquery at `:942` is safe from the classic NULL trap (`MAX(id)` over
  `INTEGER PRIMARY KEY` can never be NULL) — worth a one-line comment so a future edit does not
  introduce a nullable column into that select.

#### What's done well

- **One definition of identity, used everywhere.** `CardIdentity` (`:1188`) is genuinely the single
  rule — revive pass, discovery tick, and storage dedupe through `DeviceRecord` — which is exactly
  what makes "storage and UI can never disagree" true rather than aspirational. Do not let a later
  change inline a second copy of the tuple.
- **The owner-scoped delete-by-id** (`LocalStorageService.cs:3789-3796`). The guest/user
  `StorageId` namespace collision is a real hazard and the scoping closes it; the dedicated test
  `RePersistingByStorageId_NeverTouchesAnotherOwnersRow` pins it.
- **The heal heuristic's proof.** Restricting the collapse to groups with two or more distinct
  `(vendor_id, product_id)` pairs — and articulating *why* two identical units can never be such a
  group — turns a scary migration on live user data into a reviewable one.
- **Case-folding the name** in both the helper and the SQL (`COLLATE NOCASE`). This closes a latent
  second duplicate source (discovery matches case-insensitively, SQLite's `=` does not) that was not
  the reported bug but would have become one.
- **Documentation discipline.** Both sibling plans got dated notes that correct their live contract
  without touching their review journals, `docs/CONTEXT.md` gained both new terms, and the stale
  `AddDevice` doc comment was corrected rather than left to mislead.

#### Verdict

Ship: yes — no blockers. The fix is well-targeted, the identity rule is defined once and threaded
through every consumer, the migration is narrower than it needs to be (correctly so), and 1158 tests
pass. Both Should-fixes are small and neither can corrupt data: S1 degrades to a self-healing
partial migration, S2 is a pre-existing race the change widens rather than creates.
First: **1)** wrap `HealDuplicateDeviceRows` in its own transaction and correct the plan's
"inside the existing schema transaction" claim (S1); **2)** decide S2 — Option A or B, or at minimum
correct the false `FlushSession` comment; **3)** record the guest `RemoveDevice` dedupe and its
card-ordering side effect in the plan's Scope and File-level item 5, and close Open question 1.
