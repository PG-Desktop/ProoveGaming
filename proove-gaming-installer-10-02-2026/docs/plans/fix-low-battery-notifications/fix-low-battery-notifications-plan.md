# Fix low-battery notifications firing at random moments

> Written 2026-08-20, retroactively for a QA bugfix ("Зараз сповіщення виникають в
> випадковий момент" — the TAIPAN 14 % toast kept reappearing). No earlier plan owned the
> low-battery notification behaviour; this document is now its contract.

## Problem

The low-battery toast (bell notification + system notification) was guarded per **card
instance** and the guard was **cleared by every null reading**. A wireless mouse blanks its
level constantly — it sleeps behind its dongle, a poll misses twice, the card is recreated on
reconnect — and each blank re-armed the guard, so the next reading below 20 % announced the
same percentage again. To the user the toast fired "at random moments".

There was also only one threshold (`< 20`), so nothing distinguished "getting low" from
"about to die".

## Behaviour (the contract)

Implemented in `HomeViewModel` (`LowBatteryDecision` + `CheckLowBattery`), covered by
`tests/ProoveGaming.App.Tests/HomeViewModelLowBatteryTests.cs`.

- **Two bands, one toast each per discharge:** the first reading at **≤ 20 %** announces
  once, the first reading at **≤ 10 %** announces once more. Dropping straight past both
  bands (first reading after reconnect is 8 %) announces only the lower band — one toast,
  not a backlog.
- **State is keyed by the SETTINGS identity** (`VendorId`, `SettingsProductId`,
  `SettingsDeviceKey` — `LowBatteryIdentity`), not by card instance and not by the routing
  identity: `ApplyDeviceIdentity` rewrites `ProductId`/`DeviceKey` on every transport
  switch of a merged-transport product (TAIPAN dongle↔Bluetooth) and on a receiver port
  move, and a key that changes loses the "already announced" entry (PR #117 M1).
  `_batteryMissStreak` deliberately keeps the routing key — a miss streak belongs to the
  specific link. Card recreation does not re-arm the state.
- **A null reading decides nothing.** A missed poll or a sleeping device is not a recharge;
  the announced state survives it. This is the core fix.
- **Re-arm only on a genuine recharge:** a reading **≥ 25 %** (band + 5 hysteresis) clears
  the state. 21–24 % is a hold zone — a reading jittering 19↔21 neither re-arms nor
  re-announces. A partial recovery out of the 10 % band (e.g. back to 15 %) does not re-open
  the 20 % band either.
- **No toast while charging** (the battery is not draining; wired counts as charging),
  **no toast for a disconnected card**, and **no toast without a live reading**
  (`HasBattery` — a restored card carries a stored snapshot that can be hours stale, and on
  the Bluetooth found path the transports flip before the fresh value lands). The state is
  held, not cleared, and the `IsCharging` / `Connected` / `HasBattery` property changes
  re-run the decision — so unplugging the charger at 14 % announces promptly even though
  the level did not change, and a reconnect at an unchanged percentage announces when the
  live reading lands as a `HasBattery` flip.
- **All map writers stay on the UI thread:** the Bluetooth found path raises the trigger
  properties on a gRPC handler thread, so `CheckLowBatteryOnUIThread` marshals the check —
  the same single-writer invariant `_batteryMissStreak` documents.
- State is dropped when the card is deleted or leaves with its owner
  (`ExecuteConfirmDelete`, `ClearOwnerScopedState`) — same lifecycle as `_batteryMissStreak`.

## File-level change list

- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `LowBatteryDecision` (static rule,
  testable without the service graph, like `DeriveHidTransports`), rewritten
  `CheckLowBattery`, `_lowBatteryAnnouncedBand` replacing the per-card
  `_lowBatteryNotified` set, `WireDelete` re-checks on `IsCharging` / `Connected` /
  `HasBattery` too, marshalled through `CheckLowBatteryOnUIThread`.
- `tests/ProoveGaming.App.Tests/HomeViewModelLowBatteryTests.cs` — new; drives the rule
  directly.

## Acceptance criteria

- [ ] Battery drains through 20 % → exactly one toast ("Low battery — NAME (pct %)");
  drains on through 10 % → exactly one more.
- [ ] Mouse sleeps / wakes (level blanks and returns) below 20 % → no additional toast.
- [ ] Charge above 25 %, drain again → the two toasts fire again.
- [ ] On the charger below 20 % → no toast; unplug while still below 20 % → the toast fires.
- [ ] After the 20 % toast, switch a merged-transport device between its transports
  (TAIPAN dongle↔Bluetooth) or move the receiver to another USB port → no repeat toast at
  the same band.

## QA steps

1. With a TAIPAN below 20 %, leave the mouse idle until it sleeps, wake it — no new toast.
2. Plug the cable in below 20 % — no toast; unplug — one toast.
3. Charge past 25 %, then drain — the 20 % toast fires once, later the 10 % toast once.

## Amendment (2026-08-31) — the guard survives restarts; the re-arm requires a live reading

QA re-reported "toasts at random moments" (screenshot: PG TAIPAN at 12 %) after the
2026-08-20 fix. Two remaining holes, both now closed:

1. **The announced state was process-local.** `_lowBatteryAnnouncedBand` died with the
   process, so *every app launch* with a sub-20 % mouse announced the same band again — and
   QA restarts the app constantly. The band is now persisted with the card
   (`devices.low_battery_announced_band`, `DeviceRecord.LowBatteryAnnouncedBand`,
   migration via `AddColumnIfMissing`) through every path: the guest whole-list flush
   (`UserSession.SetDeviceLowBatteryBand` + queued `Flush`), the signed-in write-through
   upsert (`PersistDeviceCard`), and a targeted owner-scoped update the announcement itself
   enqueues (`LocalStorageService.SetDeviceLowBatteryAnnouncedBand`). On restore,
   `RecordToViewModel` seeds the map before `WireDelete`'s initial check, merging by the
   LOWER band when two rows share one settings identity. The card carries a torn-free
   mirror (`DeviceCardViewModel.LowBatteryAnnouncedBand`) because `PersistDeviceCard` runs
   on the discovery thread while the map is UI-thread-only.
2. **The re-arm trusted any high reading.** `level >= 25` forgot the announced state even
   from a stored snapshot (no live reading) or a charge-time value (TAIPAN's cable readings
   derive from charge voltage) — after which the next live sub-20 % reading toasted again.
   `LowBatteryDecision` now re-arms only on a live, off-charger reading
   (`hasLiveReading && !isCharging`); otherwise the state is held, and the genuine re-arm
   lands with the first trustworthy reading after the recharge.

Additional acceptance criteria:

- [ ] Below 20 % with the 20 % toast already shown, restart the app → no new toast at the
  same band; drain to 10 % → exactly one more.
- [ ] A stored snapshot at/above 25 % restored at launch (no live reading yet) does not
  forget an announced band persisted earlier.
- [ ] Charging past 25 % and back below 20 % *without unplugging* does not re-toast;
  unplugging after a genuine recharge re-arms both bands on the first live reading.

Additional QA steps:

4. Drain below 20 % until the toast fires, close the app completely (Exit, not tray),
   relaunch — no new toast; drain to 10 % — exactly one more toast.
5. With the 20 % toast shown, plug the cable in for a few seconds (level may read high while
   charging) and unplug while the real level is still below 20 % — no repeat toast.

File-level change list additions: `LocalStorageService.cs` (migration, devices
SELECT/INSERT ×4, `SetDeviceLowBatteryAnnouncedBand`), `DeviceRecord.cs`, `UserSession.cs`
(`SetDeviceLowBatteryBand`), `DeviceCardViewModel.cs` (persisted mirror),
`HomeViewModel.cs` (seed + `PersistLowBatteryBand` + re-arm gate),
`HomeViewModelLowBatteryTests.cs` (`StaleOrChargingHighReading_DoesNotRearm`),
`LocalStorageServiceDeviceIdentityTests.cs` (band round-trip ×3).

## Review findings

### Round 3 (branch `bugfix/PG-642`, commit 16d8bb5 "PG-615") — Ship: yes

#### Summary
Implements the 2026-08-31 Amendment: the announced-band guard is persisted with the card
(`devices.low_battery_announced_band` + `DeviceRecord.LowBatteryAnnouncedBand` + the
`DeviceCardViewModel` torn-free mirror), and `LowBatteryDecision`'s re-arm now requires a
live, off-charger reading. Rule test + three storage round-trip tests shipped. Full suite
1464/1464 (Debug and Release).

#### Plan conformance
Plan: this document's Amendment (2026-08-31) section.
- Implemented as planned: migration via `AddColumnIfMissing`, both `GetDevices` SELECTs,
  the guest `Flush` INSERT, the `AddDevice` INSERT, the owner-scoped targeted
  `SetDeviceLowBatteryAnnouncedBand`, `UserSession.SetDeviceLowBatteryBand` (in-place under
  `_gate`), seeding in `RecordToViewModel` BEFORE `WireDelete`'s initial check with
  min-nonzero merge, persist-on-announce/persist-on-re-arm via `PersistLowBatteryBand`, and
  the `hasLiveReading && !isCharging` re-arm gate.
- Missing / partial: the Amendment's acceptance criterion 1 (restart → no re-toast) has no
  automated VM-level coverage — see S1 below. Rule and storage layers are covered; the
  seeding path between them is not.

#### Code
- none (no Blockers). Checked specifically:
  - **Column completeness**: every SQL path that touches `devices` rows was audited. The
    guest→user migration (`LocalStorageService.cs:4891`) and the merge/duplicate heals
    (`:980–1014`) are column-preserving raw `UPDATE`/`DELETE` — no path re-inserts rows
    from a partial record, so a persisted band cannot be silently wiped.
  - **Thread affinity**: the band map and card mirror are written only on the UI thread
    (`CheckLowBattery` is marshalled; `RecordToViewModel`'s callers are the startup restore,
    `ReloadOwnerScopedState` — Dispatcher-posted at `MainWindow.axaml.cs:402` and invoked
    from UI-continuation sign-in handlers at `:646`/`:751` — and the UI `OnDeviceAdded`).
    `PersistDeviceCard` (discovery thread) reads only the plain-int card mirror, and the
    user-path write is enqueued on the storage executor — no synchronous SQLite lands on
    the dispatcher.
  - **Re-arm gate edges**: a live off-charger reading ≥25 with `connected=false` (transport
    flag dropped between the reading and the check) still re-arms — correct, a genuine
    recharge reading is trustworthy regardless of the current link. A stale restored level
    and a charge-time value both hold, per the TAIPAN cable-voltage note.
  - **StorageId=0 window**: an announce before the card is persisted skips the targeted
    write; `PersistDeviceCard` carries the band from the mirror when it runs. A card with
    no owner (Welcome screen) persists nothing — and restores nothing, so no loss.
- [x] **S1. No VM-level test for restore-seeding** —
  *(fixed 2026-09-01, PR #125: `HomeViewModelPersistenceTests.SignedInLaunch_RestoresLowBatteryBandOntoTheCard`
  pins the restore-seeding path, and `ReloadOwnerScopedState_SeedsLowBatteryBand_BeforeAdoptRewritesTheRow`
  reproduced the adopt-cycle wipe found in the same review [m1] — fixed by the shared
  `SeedLowBatteryBand` min-merge helper plus a pre-adopt seeding pass in `ReloadOwnerScopedState`.)*
  `tests/ProoveGaming.App.Tests/HomeViewModelLowBatteryTests.cs` — Should-fix
  **Problem / why it matters:** the exact defect class this change fixes (state lost between
  process lifetimes) is pinned only at the two ends: the rule
  (`StaleOrChargingHighReading_DoesNotRearm`) and the storage round-trip. The middle —
  `RecordToViewModel` seeding `_lowBatteryAnnouncedBand` (with the min-nonzero merge)
  before `WireDelete`'s initial check — has no test, and it is ordering-sensitive: moving
  the seeding below `WireDelete(vm)` would silently reintroduce the launch-time toast while
  every existing test stays green.
  **Fix:** `HomeViewModelPersistenceTests` already constructs a real `HomeViewModel`
  (`:92`); add a case that restores a record with `LowBatteryAnnouncedBand = 20` and
  `BatteryLevel = 15`, drives a live reading at 15 (band held → no announce state change),
  then at 9 (announces 10), asserting via the persisted row or the card mirror.

#### Tests
- [x] S1 above (closed 2026-09-01) — the seeding/ordering characterization test — `HomeViewModelPersistenceTests`
  or `HomeViewModelLowBatteryTests`. Everything else that shipped is genuine coverage: the
  re-arm rule test fails against the pre-change `LowBatteryDecision`, and the owner-scoping
  assertion in `SetDeviceLowBatteryAnnouncedBand_UpdatesTheStoredRow` pins the
  `DeleteDevice`-style guard.

#### Nits
- `RecordToViewModel` seeds the map but a deleted-then-readded identity relies on
  `ExecuteConfirmDelete`'s map removal for correctness — a one-line comment tying the two
  would help the next reader; behaviour is correct today.

#### What's done well
- The card-mirror design (`DeviceCardViewModel.LowBatteryAnnouncedBand`) cleanly resolves
  the cross-thread read that a naive map read from `PersistDeviceCard` would have raced,
  and the doc comment says exactly that.
- The re-arm gate ships with the TAIPAN cable-voltage rationale in both code and tests, so
  the "why" survives.
- `SetDeviceLowBatteryAnnouncedBand` copies `DeleteDevice`'s owner-scoping guard instead of
  trusting the id, with the rationale cross-referenced.

#### Verdict
Ship: yes — no blockers; S1 is a contained test-only follow-up. First: (1) add the S1
seeding test, (2) run the Amendment's QA steps 4–5 on a real sub-20 % mouse.

### Round 2 (PR #117, external review) — Major and minor fixed

- [x] **M1. The announcement map was keyed by the ROUTING identity
  (`ProductId`/`DeviceKey`), which `ApplyDeviceIdentity` rewrites on a transport switch
  (TAIPAN dongle↔Bluetooth) or a receiver port move — losing the entry and repeating the
  toast** — fixed 2026-08-20 with the reviewer's Option A: `LowBatteryIdentity` returns
  the stable settings identity (`VendorId`, `SettingsProductId`, `SettingsDeviceKey`),
  used at all four sites (`ClearOwnerScopedState`, `CheckLowBattery`,
  `ExecuteConfirmDelete`); `_batteryMissStreak` untouched (per-link by design). Plan
  contract and acceptance criteria updated above.
- [x] **m1. The trigger wiring was test-invisible — dropping an entry kept all rule tests
  green** — fixed 2026-08-20 as suggested: the list is now
  `internal static readonly string[] LowBatteryTriggerProperties`, `WireDelete` consumes
  it, and `TriggerProperties_CoverEveryGateInput` pins the composition.
- m3 (packaging) — the already-pushed commit is not rewritten; the follow-up work lands as
  two per-plan commits and the PR description names PG-615/616.

### Round 1 (branch `bugfix/PG-366`, working tree) — Ship: yes

#### Summary
Reviews the change this plan documents: `LowBatteryDecision` (static rule) +
`CheckLowBattery` keyed by device identity in `_lowBatteryAnnouncedBand`, with the
`IsCharging` / `Connected` PropertyChanged triggers added in `WireDelete`. 8 new rule
tests. Core defect (null readings re-arming the toast) is fixed and pinned by
`NullReading_DecidesNothing_AndKeepsTheAnnouncedState`.

#### Plan conformance
Plan: this plan (written in the same change — it is the behaviour's first contract).
- Implemented as planned: bands, hysteresis, identity keying, null-survival, charging /
  disconnected gates, state lifecycle on delete and owner sign-off all match.
- Deviates from plan: the plan's "no toast for a disconnected card (its level is a stored
  snapshot)" is not fully delivered — the snapshot CAN toast the instant the card becomes
  connected, before any live reading exists (S1).

#### Code
- [x] **S1. Stored-snapshot level can toast at the moment of (re)connect, before a live
  reading exists** —
  *(fixed 2026-08-20 as recommended: `hasLiveReading` input on `LowBatteryDecision` fed
  from `vm.HasBattery`, `HasBattery` added to the `WireDelete` trigger list; rule test
  `StoredSnapshotWithoutALiveReading_StaysQuiet`.)*
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1612`
  (`CheckLowBattery`) + `HomeViewModel.cs:816-822` (ordering) — Should-fix
  **Problem / why it matters:** a restored card keeps its persisted `BatteryLevel`
  (`RecordToViewModel`, `HomeViewModel.cs:1479`) and `HasBattery` stays false until a live
  reading lands. On the Bluetooth found path, `FillBluetoothCard` flips the transports
  (line 816) BEFORE the fresh battery value is applied (818-822), so the `Connected`
  trigger runs `CheckLowBattery` against the stale snapshot. Concrete failure: mouse was
  stored at 14 %, charged to 80 % while the app was closed, switched on over Bluetooth →
  "Low battery (14 %)" toast fires from the snapshot, then the real 80 % arrives and
  re-arms. A false toast at connect — a narrower instance of the very complaint this fix
  addresses.
  **Fix:** gate the announcement on a live reading — add `vm.HasBattery` to the decision
  inputs (`connected && vm.HasBattery`) AND add `nameof(DeviceCardViewModel.HasBattery)`
  to the `WireDelete` trigger list, so the toast still fires when the live reading lands
  with an unchanged level (14 % stored → 14 % live raises no `BatteryLevel` change).
  Extend `HomeViewModelLowBatteryTests` with the new input.
- [x] **S2. The new `Connected` / `IsCharging` triggers run `CheckLowBattery` — and its
  dictionary writes — on non-UI threads** —
  *(fixed 2026-08-20: `CheckLowBatteryOnUIThread` — `CheckAccess()` fast path, else
  `Dispatcher.UIThread.Post` — used by both the subscription and WireDelete's initial
  call, which the Bluetooth new-card path also reaches off-thread.)*
  `HomeViewModel.cs:1563-1570` (subscription) /
  `HomeViewModel.cs:1615-1625` (dictionary access) — Should-fix
  **Problem / why it matters:** `HandleBluetoothDeviceFoundAsync` sets the transport flags
  outside the dispatcher (`FillBluetoothCard`, line 816 — the method awaits
  `Dispatcher.UIThread.InvokeAsync` for other steps precisely because it is not on the UI
  thread), so the new `Connected` trigger now reads/writes `_lowBatteryAnnouncedBand` on a
  gRPC handler thread while the 60 s poll writes it on the UI thread. Plain `Dictionary`
  under concurrent write is undefined — the sibling `_batteryMissStreak` documents "both
  writers run on the UI thread" as its safety argument (`HomeViewModel.cs:48-51`), and this
  change silently breaks that invariant for the new map. Before the change the trigger was
  effectively dispatcher-only (`BatteryLevel` writes are wrapped in `InvokeAsync`).
  **Fix:** marshal in the subscription — `if (Dispatcher.UIThread.CheckAccess())
  CheckLowBattery(vm); else Dispatcher.UIThread.Post(() => CheckLowBattery(vm));` — or
  post unconditionally; keeps the map single-threaded and matches the documented
  convention.

#### Tests
- [x] With S1: a `LowBatteryDecision` case for "no live reading yet" (snapshot level,
  hasLiveReading=false → no announce) — `HomeViewModelLowBatteryTests.cs`.
  *(Added: `StoredSnapshotWithoutALiveReading_StaysQuiet`.)*
- Accepted (repo pattern): the instance bookkeeping (identity keying, re-arm removal,
  band recording) is untested because `HomeViewModel`'s constructor needs the full service
  graph; the rule carries the logic and is fully covered.

#### Nits
- Bluetooth cards arrive with VID = PID = 0, so two such devices with an empty
  `DeviceKey` would share one announcement entry — same pre-existing keying convention as
  `_batteryMissStreak`; noting so it is a decision, not an accident.
- `_systemNotifications.Show` is called on whatever thread `CheckLowBattery` runs on —
  pre-existing, and moot once S2 marshals the whole method.

#### What's done well
- The static-rule extraction (`LowBatteryDecision`) follows the repo's established
  `DeriveHidTransports` pattern, and the 8 tests read as the behaviour contract.
- Identity keying + null-survival is the correct root fix for the reported randomness,
  and the hysteresis zone (21–24 holds state) is documented in both code and plan.
- State lifecycle parity with `_batteryMissStreak` (delete, owner sign-off) was not
  forgotten.

#### Verdict
Ship: yes — the reported defect is fixed and pinned by tests; S1 is a rarer false toast at
reconnect and S2 is a low-probability race, both contained follow-ups that do not undo the
core fix. First: (1) S2 dispatcher marshal, (2) S1 `HasBattery` gate + trigger, (3) its
rule test.
