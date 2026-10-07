# Plan: extended keys through the API — preset push, pull, and materializing a pulled preset

Date: 2026-09-10. Owner: architect. Builds on
[`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md)
(section, tables, `configuration.extended_keys`), the sync plans
[`../add-api-v2-sync/add-api-v2-sync-plan.md`](../add-api-v2-sync/add-api-v2-sync-plan.md) (push),
[`../add-api-v2-pull/add-api-v2-pull-plan.md`](../add-api-v2-pull/add-api-v2-pull-plan.md) (sign-in pull) and
[`../add-api-v2-live-sync/add-api-v2-live-sync-plan.md`](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md)
(background push).


> **2026-09-11.** The pending-apply mark introduced here now covers the **pull case only**. The
> account-switch case it was later extended to (see `../fix-account-switch-device-state/`) is
> handled by the board-vs-owner comparison in `../fix-extended-keys-self-heal/`, which does not
> depend on any in-memory notion of "who was here before".

> **Status note (2026-09-24).** Extended Keys is hidden on every shipping model until the
> implementation is finished — `"SupportsExtendedKeys"` is `false` for Cyrex and Aress in
> `src/ProoveGaming.DeviceHost/appsettings.json`, which also stops the probe and every write to
> the board. Nothing in this plan runs while that flag is off; see
> [`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md) for the full note.

## Goal

A user who configures Rapid Snap / SOCD / Mod-Tap / Toggle on their Cyrex or Aress, saves the preset
and signs in on another machine (or reinstalls) must get the same extended keys back: the preset
carries them to the server, the sign-in pull brings them down, and the device page shows and applies
them for that user. The push and pull halves already exist; what is missing is that a **pulled
applied preset never reaches the board or the live rows** on the new machine, so nothing "shows in
the settings" until the user re-selects the preset by hand. This plan closes that gap (for every
section, not only extended keys), pins the whole chain with tests, and records the server contract
that was verified against the live API on 2026-09-10.

## What was verified before planning (2026-09-10)

- **Server contract.** A temporary preset with `configuration.extended_keys` (an RS pair and an MT
  record, exactly the shape `PresetConfigurationBuilder` emits) was created with
  `api/presets/create` on the live server, read back with `api/presets/get` and `api/presets/list`,
  then deleted. Both reads echoed `extended_keys` (and `key_macro_bindings`) byte-for-byte: the server
  treats `configuration` as an opaque JSON object. This answers open question 2 of the api-v2-sync
  plan for both extensions — no backend change is needed.
- **Client push.** `PresetConfigurationBuilder.Build` already serializes `extended_keys`; Save Preset
  marks the preset dirty; the coordinator sends `presets/create` / `presets/update` with the full
  configuration. A Toggle record saved on this machine this morning reached the server
  (`server_updated_at` set, `is_dirty` cleared).
- **Client pull.** `PresetConfigurationApplier.ApplyExtendedKeys` deserializes the array with the
  firmware-rule validation the phase 2–4 reviews added, and inserts `preset_extended_keys` rows.
- **The gap.** The sign-in pull (`SettingsSyncService`, step 4) writes `device_applied_preset` from
  `devices/get` and nothing else. On the device page, `EnsureDefaultPresetAppliedAsync` returns as
  soon as an applied row exists, `LoadPresetsForDeviceAsync` only *selects* that preset, and the
  connect-time `ReapplyLastCustomPresetAsync` skips keyboards and reads **live** `device_*` rows
  (empty for a fresh owner). `LoadPresetSnapshotToDevice` has exactly one caller — the user-driven
  preset switch. So a pulled preset's extended keys, key bindings, actuation and RGB sit in the
  snapshot tables and never reach the board; the Extended Keys section (which reads the board)
  stays empty and `PresetMatchesLiveState` reads the empty live rows as "unsaved changes".

## Scope

In:

- A **pending-apply mark** written by the sign-in pull whenever it sets a cloud applied preset,
  consumed once by the device page (keyboards and mice) by loading that preset's snapshot into the
  unit's live rows and pushing it to the hardware through the existing preset-apply path — which
  already carries extended keys (`ApplyStoredExtendedKeysToDeviceAsync` after the key/macro pushes).
- The connect-time reapply for mice honours the same mark so a mouse gets its pulled preset without
  opening its page.
- Builder tidy-up: pair modes (RS / SOCD) serialize `hold_key` / `tap_key` as `null` instead of
  zeroed objects — the shape documented to the backend team and what the applier already tolerates.
- Tests for the whole chain (push payload, pull intake, materialization, dirty state) and a QA script
  that runs the two-machine scenario.
- Docs: answer open question 2 in the api-v2-sync plan, add the pending-apply term to the glossary,
  note the verified contract in the API knowledge-base file.

Out:

- Updating a **locally existing** preset from a newer server copy. The pull is additive by design
  (pull plan, option B): a uid already present locally is never overwritten. Two machines editing the
  same preset still resolve last-writer-wins on push. Not changed here.
- Any server change. None is needed.
- A schema change to `preset_extended_keys` / `device_extended_keys`.

## Options considered

### A. Pull loads the snapshot straight into live rows (no page involvement)

`LoadPresetSnapshotToDevice(presetId, owner, vid, pid, "")` right after `SetAppliedPreset`.

- Con: live rows are keyed by the unit's `device_key`; the pull knows no unit and would write under
  `""`, which the page's reads (`Device.SettingsDeviceKey`) never see for a keyed unit. The pull-plan's
  B1 fallback exists only for `GetAppliedPreset` and `GetMacros`; extending it to every live table is
  a wide change. And the board would still not be written until something pushes.

### B. Pending-apply mark, consumed by the page and the connect-time reapply (recommended)

The pull sets a per-owner, per-type `sync_meta` key when it writes the applied row. The first place
that has a unit in hand — the details page's open sequence, or
`ReapplyOnConnectAsync` for a mouse card — checks the mark, runs the existing
`ApplyPresetToDeviceAsync(applied)` (snapshot → live rows under the unit's key → full push, extended
keys included), and clears the mark.

- Pro: reuses the one code path that already does snapshot → live → device correctly, including the
  extended-keys ordering and the actuation "no opinion" rules; unit-keyed rows land where the page
  reads them; `PresetMatchesLiveState` becomes true right after, so Save Preset stays off.
- Pro: one flag, no schema change (`sync_meta` already carries `devicestate.<vid>:<pid>.*`).
- Con: the push happens on first page open / first connect after the pull, not at pull time. That is
  also when a device is guaranteed to be reachable, so it is the right moment.

### C. Always re-apply the applied preset on page open

Drop the early return in `EnsureDefaultPresetAppliedAsync`.

- Con: every page open would rewrite the board (flash wear on T800, a second of HID traffic on
  every keyboard) and would clobber unsaved live edits with the snapshot. Rejected.

## Recommended approach

Option B.

1. **Mark.** `LocalStorageService.SyncMetaDeviceStatePendingApply(vid, pid)` →
   `devicestate.<vid>:<pid>.pending_apply`. `SettingsSyncService` step 4 sets it in the
   same storage delegate that calls `SetAppliedPreset`, only when that call actually wrote (the
   existing `wrote` result). Guest-upgrade and every other path leave it alone. The value is the
   **local id of the adopted preset** (`SyncMetaPendingApplyValue(presetId)`), not a bare flag, so a
   mark that outlives the user's next choice is recognised as stale rather than spent against the
   wrong preset (review round 1, S3).
2. **Consume on the page.** In `SetDeviceAsync`'s follow-up chain — `MaterializePulledPresetAsync`,
   sitting **after** `LoadExtendedKeysFromDeviceAsync` (and therefore after `LoadMacroLibraryAsync`,
   `LoadButtonBindingsAsync`, `PopulateKeyLayout` and `LoadActuationFromDeviceAsync`) and immediately
   before `EnsureDefaultPresetAppliedAsync`. If the mark names the preset that is still applied for
   `(owner, SettingsVendorId, SettingsProductId, SettingsDeviceKey)` → clear the mark, then
   `await ApplyPresetToDeviceAsync(preset, restoreUnsetToDefaults: false, target: device)` (the switch
   path: `LoadPresetSnapshotToDevice` under the unit's key + `ApplyCurrentStateToDeviceAsync(pushActuation:
   true)`, which pushes RGB, key overrides, macro bindings, actuation and extended keys). The existing
   `RecomputeUnsavedPresetChangesAsync` that follows in the open sequence then reads matching rows.
   Clear the mark *before* the apply so a failing device write cannot re-trigger a loop; the preset
   stays selected and the user can re-apply by hand.
   The position is load-bearing, not incidental: consuming it inside `LoadPresetsForDeviceAsync`
   (where round 1 put it) runs the apply before the extended-keys probe and before the macro library
   is loaded, so the pulled extended keys are silently skipped and the pulled macro bindings are
   silently deleted — see review round 1, B1 / B2.
3. **Consume on connect (mice).** `ReapplyLastCustomPresetAsync(card)`: when the mark is set and the
   applied preset resolves, `LoadPresetSnapshotToDevice(presetId, owner, card.SettingsVendorId,
   card.SettingsProductId, card.SettingsDeviceKey)` first, clear the mark, then continue with the
   existing live-row reads — the pulled preset now reaches the mouse without opening its page.
   Keyboards keep returning early there (they have no off-page push; the page does it).
   Skipped entirely when `card` is the unit whose details page is open (`IsOpenDeviceCard`): the
   page's own consumption owns the mark then, and a snapshot copy landing mid-open would diverge the
   live rows from the page's state (review round 1, S2).
4. **Builder.** In `PresetConfigurationBuilder`, emit `hold_key = null` / `tap_key = null` when the
   record's code is `None` (pair modes). The applier already treats a null as `None`.
5. **Docs.** api-v2-sync plan: dated note under Open question 2 ("verified 2026-09-10 against the
   live API: `configuration` is opaque; `extended_keys` and `key_macro_bindings` round-trip"); the
   API knowledge-base v2.0 file gets the same note next to §16.1; glossary entry **Pending device
   apply**; this plan is the owner of the materialization behaviour, and the pull plan gets a dated
   note pointing here.

## File-level change list

- `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: `SyncMetaDeviceStatePendingApply`
  key helper next to `SyncMetaDeviceStateDirty`; no schema change (`sync_meta` exists).
- `src/ProoveGaming.App/Services/SettingsSyncService.cs` — edit: step 4 sets the pending-apply mark
  when `SetAppliedPreset` wrote.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — edit:
  `MaterializePulledPresetAsync` consumes the mark (clear, then `ApplyPresetToDeviceAsync`) and the
  shared `ConsumePendingApplyMarkAsync` read-check-clear helper; `ApplyPresetToDeviceAsync` gains an
  optional `target` card so a device switch drops the apply instead of re-aiming it;
  `ReapplyLastCustomPresetAsync` materializes the snapshot for a mouse card when the mark is set and
  the card is not the open one (`IsOpenDeviceCard`).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — edit: `SetDeviceAsync`
  calls `MaterializePulledPresetAsync` in its follow-up chain, after the extended-keys probe and
  before `EnsureDefaultPresetAppliedAsync`.
- `src/ProoveGaming.App/Services/PresetConfigurationBuilder.cs` — edit: null codes for pair modes.
- `docs/plans/add-api-v2-sync/add-api-v2-sync-plan.md` — edit: dated answer to Open question 2.
- `docs/plans/add-api-v2-pull/add-api-v2-pull-plan.md` — edit: dated note that the pulled applied
  preset is now materialized on first page open / connect (this plan).
- `docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md` — edit: note under §16.1 that the
  server stores `configuration` opaquely (verified 2026-09-10) and lists the client extensions
  `key_macro_bindings`, `extended_keys`.
- `docs/CONTEXT.md` — edit: **Pending device apply** one-liner.
- `tests/ProoveGaming.App.Tests/SettingsSyncServicePullTests.cs` — edit: pulled preset with
  `extended_keys` → `preset_extended_keys` rows (all four modes, one malformed item skipped);
  `devices/get` naming a pulled preset → applied row **and** the pending-apply mark; an existing local
  applied row → neither.
- `tests/ProoveGaming.App.Tests/SyncCoordinatorTests.cs` — edit: after a preset snapshot holds an
  extended-key row, the `presets/update` request's configuration contains the `extended_keys` array
  with that row (and `hold_key`/`tap_key` null for a pair mode).
- `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs` (or a new
  `DeviceDetailsPulledPresetApplyTests.cs` if the harness there does not fit) — **new cases**: a
  keyboard opened with the mark set → exactly one `SetExtendedKeysAsync` carrying the snapshot's
  records, live rows equal the snapshot, mark cleared, `HasUnsavedPresetChanges` false; mark absent →
  no write; mark set but the write fails → mark still cleared, no retry loop; a mouse card's
  `ReapplyOnConnectAsync` with the mark → `ApplyDeviceConfigurationAsync` carries the snapshot's RGB.
- `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` — edit: pair-mode record
  serializes `hold_key: null`; round trip build → apply → build unchanged.

## Acceptance criteria

- [ ] Given a Cyrex with an RS pair and an MT record saved into preset "P" while signed in →
  the next sync pass sends `presets/update` (or `create`) whose `configuration.extended_keys` holds
  both records; `api/presets/get` for P's uid returns them unchanged.
- [ ] Given a fresh install (or a signed-out App with cleared data) → sign in as the same user →
  the pull inserts P with its `preset_extended_keys` rows; if P is the cloud's active preset for the
  Cyrex, `device_applied_preset` names it and the pending-apply mark is set.
- [ ] Given that state → connect the Cyrex and open its page → without any click the board receives
  one `SetExtendedKeys` with P's records (host log: one verified write), the Extended Keys section
  lists them, Save Preset is **not** lit, and the mark is cleared.
- [ ] Given the same state → open the page a second time → no further `SetExtendedKeys` write.
- [ ] Given a mouse whose cloud active preset was pulled → plugging the mouse in (page not open)
  applies that preset's RGB/bindings/DPI to it; opening the page afterwards shows it selected and
  clean.
- [ ] Given the device write fails on first open → the banner shows, the mark is still cleared, the
  preset stays selected, and re-selecting it by hand pushes again.
- [ ] Given a preset already present locally with the same uid → the pull leaves its rows untouched
  (existing additive rule; pinned by the existing pull tests).
- [ ] Given a pair-mode record → the pushed JSON has `hold_key: null` and `tap_key: null`; a
  single-key record keeps its objects.
- [x] `dotnet test ./src/ProveGaming.slnx --configuration Release` green. (2026-09-10, after the
  review round 1 fix pass: 1714 + 1082 + 8 passed, 0 failed. Re-run the same day after the
  `/test-writer` round 2 cases: **1718 + 1082 + 8 passed, 0 failed**.)

## Test strategy

Change to existing behaviour with existing tests (`SettingsSyncServicePullTests`,
`SyncCoordinatorTests`, `DeviceDetailsPresetApplyTests`, `PresetConfigurationBuilder/ApplierTests`)
— they stay green and are extended with the cases in the change list. Order: the pull-test and
VM-test cases that describe the materialization are written first against the current code (they
fail: no mark, no write), then the three source edits make them pass.

Not automated: the live server round trip (done by hand on 2026-09-10 and repeated in QA), the
two-machine scenario (QA), HID.

## QA (manual test steps)

Needs one account and two App installations (two PCs, or one PC with the App's local data cleared
between runs). "Machine A" and "Machine B" below.

1. Machine A, signed in, Cyrex connected: on Extended Keys add a Rapid Snap pair A ↔ D and a Mod-Tap
   on Caps Lock (Hold = Left Ctrl, Tap = Esc, 300 ms). Click "Save Preset" on the current preset.
   Wait about half a minute. **Expected:** the App shows no sync error; the preset name has no
   "unsaved" state.
2. Machine A: on the Presets page make sure that preset is the applied one (it is highlighted).
3. Machine B (fresh App, signed out): sign in with the same account. **Expected:** after the sign-in
   spinner, the Cyrex card appears (even if the board is not plugged in yet).
4. Machine B: plug in the Cyrex and open its page. **Expected:** the preset from step 1 is selected;
   "Save Preset" is NOT lit; open Extended Keys → the two rows "A RS D" and "CAPS MT CTRL (L) ESC
   300 ms" are listed with their keys highlighted on the picture — no clicks were needed.
5. Machine B: type — hold A then press D deeper (D wins), tap Caps Lock (Esc), hold Caps Lock + C
   (copy). **Expected:** the board behaves as on machine A.
6. Machine B: close the page and open it again. **Expected:** rows still listed, no "writing" delay,
   Save Preset still off.
7. Machine B: create a second preset without extended keys, apply it, then switch back to the pulled
   one. **Expected:** rows disappear and come back, board follows.
8. Mouse variant: on machine A apply a custom preset with a distinctive lighting colour to a mouse,
   save it; on machine B sign in, then plug the mouse in WITHOUT opening its page. **Expected:** the
   mouse lights up in that colour within a few seconds.
9. Regression: on machine A (the origin machine) restart the App and open the Cyrex page.
   **Expected:** nothing is re-written (no flash of lighting, no delay), rows unchanged.

## Review focus

- `null-safety` — the mark is consumed only when the applied preset resolves to a local row; a
  missing preset must not throw or loop.
- `async/await` — the apply runs inside the page's existing apply gate; the connect-time path uses
  `_applyGate` as `ReapplyLastCustomPresetAsync` already does; no storage call on the UI thread
  (`RunOnStorageAsync`).
- `thread-safety` — the mark is cleared inside the same storage delegate that reads it, so two
  concurrent page opens cannot both push.

## Performance & quality

No hot paths. One extra `sync_meta` read per page open and per mouse connect (a single indexed
row). The materialization costs one full preset push, once, after a pull — the same traffic a manual
preset switch produces. No allocations of note.

## Risks

- The materialization push runs the keyboard's key-override and macro pushes too; on a board that
  holds vendor-app records, they are replaced by the preset's set — already the documented behaviour
  of every preset apply.
- A pulled preset whose `extended_keys` fail the applier's firmware rules loses only those items
  (skipped, logged by the applier's existing warnings); the rest of the preset still applies.
- The mark is per `(owner, vid, pid)`, not per unit: with two identical units, the first one opened
  consumes it; the second gets the preset on its next manual switch. Acceptable and noted.

## Open questions

> **2026-09-10 — the mark gained a second writer.** The pending-apply mark this plan introduced is
> no longer written only by the sign-in pull: `HomeViewModel.RestoreConnectedDevicesForNewOwner`
> now writes it on an owner-to-owner switch, for every connected type the incoming owner has an
> applied preset for, so a shared board stops wearing the previous account's configuration. The
> value contract, the identity check and the consume-and-clear delegate are unchanged; the one
> amendment is that the delegate no longer refuses guest owners (a guest is one side of a switch
> like any other, and a mark it could never consume would never be cleared). See
> `docs/plans/fix-account-switch-device-state/`.

> **2026-09-12 — and lost it again; the mark is the pull's alone once more.** The sweep's write was
> removed (`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`, review round 4, M1): the
> mark's consumers answer it with `LoadPresetSnapshotToDevice`, which overwrites the unit's live rows
> with the preset snapshot — exactly right for a preset pulled from the server and never loaded here,
> and silent data loss on an account switch, where the incoming account's applied-but-unsaved state
> is legitimate. Everything this plan specifies is unchanged. The one amendment that stays is the
> guest one above: the consumer must key on `owner.Kind` rather than assume guests never carry a
> mark.

None. The backend needs no change (verified). The additive pull rule (no refresh of an existing
local preset) is a pre-existing design decision and stays.

## Implementation notes

Dated 2026-09-10, written by `/coder` for the three source edits + the builder tidy-up. Tests and
QA are not part of this round. Two of the bullets below were superseded the same day by the fix pass
for review round 1 — see the subsection at the end of this section; each says so inline.

- **The builder's null rule is gated on the MODE, not on `IsNone`.** "Recommended approach" step 4
  reads "when the record's code is `None`", but the acceptance criterion is explicit that "a
  single-key record keeps its objects" — and a Toggle record's `tap_key` is legitimately all-zero
  while still being part of that record's content (the existing builder test
  `Build_ToggleRecord_CarriesTimeAndTheToggledOutputInHoldKey` pins it).
  So `hold_key` / `tap_key` are null exactly when
  `ExtendedKeyMode.IsPair(mode)`; MT and TGL serialize both objects as before. The applier reads
  either shape.
- **The page consumes the mark only when the device is configurable.** `ApplyPresetToDeviceAsync`
  returns immediately for a read-only transport, so consuming the mark there would burn it without
  a push and leave the pulled preset unmaterialized for good. The consumer therefore
  checks `IsDeviceConfigurable` before touching the mark; a unit on such a transport keeps it until
  it is opened over a transport that can carry configuration. (Superseded in part by the fix pass:
  the consumer is `MaterializePulledPresetAsync` in `SetDeviceAsync`'s follow-up chain, not
  `LoadPresetsForDeviceAsync`. The rule itself is unchanged.)
- **The mark is a plain `SetSyncMeta`, not `MarkSyncDirty`.** It is an inbound (pull → hardware)
  intent, not something to push, and it must not wake the sync coordinator.
  `PushDeviceStateAsync` enumerates the `devicestate.` prefix but acts only on keys ending in
  `.dirty`, so the new suffix is invisible to it — verified by reading that loop, not assumed.
- **`RekeySettingsVendor` deliberately does not re-key `.pending_apply`.** The vendor-alias re-key
  moves the `.dirty` / `.updated_at` keys of a settings bucket; the pull writes the mark under the
  canonical pair already, so a stranded copy under an alias vendor is not reachable in practice,
  and a stranded mark is inert (nothing reads that key). Left out to keep the diff to the plan's
  file list; worth revisiting only if an alias device is ever seen carrying one.
- **Consumption is one shared helper.** `ConsumePendingApplyMarkAsync` reads the mark, runs the
  optional materialization and deletes the mark inside a
  single `RunOnStorageAsync` delegate, so two overlapping opens (or an open racing a connect)
  cannot both act on it. The page passes no `materialize` — its `ApplyPresetToDeviceAsync` does the
  whole snapshot → live → device sequence afterwards; the mouse connect path passes
  `LoadPresetSnapshotToDevice` because its own live-row reads follow immediately. (The fix pass
  changed the signature — it takes the unit's `deviceKey`, returns the adopted preset id and checks
  that id against the applied row — but not this design.)

### Fix pass — 2026-09-10 — review round 1, Code block

Dated 2026-09-10, written by `/coder` for the two Blockers and the three Should-fix items of review
round 1. Tests were not rewritten in this pass (that is `/test-writer`'s next round); the two harness
edits it did need are listed at the end.

- **B1 + B2 — one move closes both (reviewer's Option A, taken as written).** The mark consumption
  left `LoadPresetsForDeviceAsync` entirely and became `MaterializePulledPresetAsync`, called from
  `SetDeviceAsync`'s follow-up chain **after** `Quietly(LoadExtendedKeysFromDeviceAsync())` and
  **immediately before** `Quietly(EnsureDefaultPresetAppliedAsync())`. Placed there rather than
  directly after the probe because that is the point where *everything* the apply reads is in place —
  the extended-keys probe (`_extendedKeysSupported`, or `ApplyStoredExtendedKeysToDeviceAsync` writes
  nothing), the macro library (`Macros`, or `ResyncKeyMacroBindingsAsync` uploads an empty library and
  deletes every restored `key_macro_bindings` row), the key layout, the button bindings and the
  actuation probe — and because materializing records the preset as applied, which is exactly what
  makes the `EnsureDefaultPresetAppliedAsync` right below it a no-op. The comment at the call site
  says the position is load-bearing, so the next person re-ordering the chain has to read why.
- **B2 Option B (bail out of `ResyncKeyMacroBindingsAsync` when `Macros` is empty and bindings exist)
  was deliberately NOT taken.** Reviewed and rejected as a change that would break a legitimate path:
  "the library is empty and rows still point into it" is exactly the state after the user deletes
  their last macro, and the delete branch is what stops the key from claiming a macro that is gone.
  With the consumption moved, `Macros` is loaded before any apply the open runs, so the accidental
  version of that state no longer occurs. If the ordering is ever disturbed again the regression
  belongs in a test, not in a guard that silences it.
- **S1 — the fence is a parameter, not just a re-check.** `MaterializePulledPresetAsync` re-runs the
  open's `generation` / `ReferenceEquals(Device, device)` guard after the consume await (which yields:
  the storage executor completes its continuations on `TaskScheduler.Default`), and it also passes the
  captured card as the new `target` argument of `ApplyPresetToDeviceAsync`. The re-check alone is not
  enough — `ApplyPresetToDeviceCoreAsync` re-reads `Device` *after* `_presetApplyGate`, another await,
  so the switch can still land in that window; `target` makes the core drop the apply instead of
  re-aiming it at whatever is open. Every existing caller leaves `target` null and behaves exactly as
  before. The mark is spent in the dropped case, which is the accepted trade (the newer open owns the
  page, and re-selecting the preset by hand still pushes it).
- **S2 — the connect-time path declines the materialize half for the open unit.** New
  `IsOpenDeviceCard(card)` compares the **settings** identity (vid / pid / device_key) rather than the
  reference, because `HomeViewModel` hands the connect path the roster's card, which for a reconnect
  need not be the instance the page was opened with. Only the materialize is skipped; the connect-time
  push itself is unchanged, so a device on another page still gets its restore. Skipping is safe by
  construction — the mark survives for the page's own `MaterializePulledPresetAsync`. The method's doc
  comment no longer claims it never reads `Device`: it now says it reads it for exactly one purpose,
  and only to decline work.
- **S3 — the mark's value is the adopted preset's LOCAL id.** `SyncMetaPendingApplyValue` changed from
  a `"1"` constant to `SyncMetaPendingApplyValue(int presetId)`, and `ConsumePendingApplyMarkAsync`
  clears-but-skips when `GetAppliedPreset` for the unit no longer returns that id (`GetAppliedPreset`
  falls back to the pull's unit-agnostic `''` row, so the comparison holds whether or not the unit has
  since recorded a choice of its own). The local id, not the server uid: the pull already has it in
  hand at the `SetAppliedPreset` call, and it is directly comparable to what `GetAppliedPreset`
  returns — a uid would need a second lookup and buys nothing.
- **No TTL was added for the "never expires" half of S3, deliberately.** The identity check invalidates
  the harmful case (a mark declined on a read-only transport, spent much later against a preset the
  user chose in the meantime). A wall-clock expiry would instead silently drop a legitimately pending
  apply from a user who plugs the board in a week after signing in — the exact scenario the feature
  exists for. Residual and accepted: a mark declined on a read-only transport, followed by
  applied-but-unsaved live tweaks to *the same* preset, still loses those tweaks when a configurable
  transport finally consumes it. Multi-condition, and the alternative costs more than it saves.
- **Helper shape.** `ConsumePendingApplyMarkAsync` now returns `int?` (the preset to materialize) and
  takes the unit's `deviceKey` plus an `Action<int>?` materializer. The removal of the mark moved to
  the top of the delegate, before the identity check, so every outcome spends it — a refused write, a
  stale mark and a successful apply all leave the same state, and none of them can produce a per-open
  retry loop. Still one `RunOnStorageAsync` delegate, so the atomicity round 1 was credited for is
  unchanged.
- **Test harness (not test rewriting).** Two mechanical updates were forced by the S3 contract change:
  `DeviceDetailsPulledPresetApplyTests.SetPendingApplyMark` now takes the preset id, and
  `SettingsSyncServicePullTests.Pull_DeviceStateNamesAPulledPreset_…` asserts the mark equals
  `SyncMetaPendingApplyValue(preset.Id)` instead of a bare constant — a strictly stronger assertion.
  No assertion was weakened and no test was skipped; in particular
  `KeyboardPage_OpenedWithThePendingApplyMark_PushesTheSnapshotsExtendedKeysOnce` now passes as
  written, which is the confirmation that B1 is actually closed. The test work review round 1 listed
  (the two vacuous keyboard assertions, and cases for B2 / S1 / S2) is left for `/test-writer`, and
  the stale "KNOWN FAILING" comment on that test belongs to that round too.
- **Not addressed (out of this pass's scope, review round 1 Nits):** the `markDirty: true` echo on the
  materialization's `SetAppliedPreset`, `RekeySettingsVendor` not re-keying `.pending_apply`, and the
  `ownerKind`/`ownerId` destructure in `ReapplyLastCustomPresetAsync`.

## Test notes

Dated 2026-09-10, written by `/test-writer` for the round that followed the three source edits.

- **Tests added.** `PresetConfigurationBuilderTests` (pair modes serialize `hold_key` / `tap_key` as
  null while MT keeps both objects and TGL keeps its all-zero `tap_key`; build → apply → build is
  byte-identical with the nulls in play). `SettingsSyncServicePullTests` (a pulled
  `configuration.extended_keys` lands one `preset_extended_keys` row per mode and skips a malformed
  item; `devices/get` naming a pulled preset writes the applied row **and** the pending-apply mark
  and no `.dirty` key; an existing local applied row writes neither). `SyncCoordinatorTests` (a
  preset snapshot holding extended keys pushes them inside `presets/update`'s configuration, pair
  codes null). `DeviceDetailsPulledPresetApplyTests` — new file, the name this plan reserved: the
  page open materializes the snapshot and clears the mark, a second open writes nothing, no mark
  writes nothing, a refused write still clears the mark, a mark under another owner is not consumed,
  and a mouse connect with the mark carries the **snapshot's** RGB (a deliberately different live
  colour proves it is the snapshot and not the live rows).

- **One acceptance criterion does not hold — product bug, left failing on purpose.** *(Superseded
  2026-09-10 by the fix pass for review round 1 — the bug described below is fixed and the test now
  passes; kept for the record of how it was found. See the round-2 subsection at the end of this
  section.)* Criterion 3
  ("the board receives one `SetExtendedKeys` with P's records" on the first page open) fails.
  `SetDeviceAsync` runs `LoadPresetsForDeviceAsync` — which is where the mark is consumed and
  `ApplyPresetToDeviceAsync` runs — **before** `LoadExtendedKeysFromDeviceAsync`, the only thing
  that sets `_extendedKeysSupported`. So `ApplyStoredExtendedKeysToDeviceAsync` returns at its
  `if (_extendedKeysSupported != true) return false;` guard and writes nothing; the mark is already
  gone, so no later path pushes them either. The Extended Keys section renders
  `_extendedKeysOnDevice` (what the probe read off the board), so it also stays empty — exactly the
  symptom this plan set out to remove. Proof of the ordering rather than of some other cause:
  re-setting the mark and opening the page a second time DOES write, because the first open's probe
  left `_extendedKeysSupported` true. The failing test is
  `DeviceDetailsPulledPresetApplyTests.KeyboardPage_OpenedWithThePendingApplyMark_PushesTheSnapshotsExtendedKeysOnce`,
  and it carries the same explanation in a comment. The fix belongs in the source (consume the mark
  after the extended-keys probe, or await the probe before the materialization), not in the test.

- **Not automated** (unchanged from Test strategy): the live server round trip, the two-machine
  scenario, and HID.

### Round 2 — 2026-09-10 — after the fix pass for review round 1

Dated 2026-09-10, written by `/test-writer` for the round that followed the fix pass. All of it is
in `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`; no other test file changed
and no production code was touched.

- **The round-1 failing test now passes as written.**
  `KeyboardPage_OpenedWithThePendingApplyMark_PushesTheSnapshotsExtendedKeysOnce` kept its
  assertions; only the stale "KNOWN FAILING" comment went, replaced by a line saying this is the
  test that says B1 is closed. It also now asserts the write went to the Cyrex's own vid.

- **The two vacuous assertions were re-pointed** (review round 1, Tests).
  `…_MarkUnderADifferentOwner_IsNotConsumed` additionally asserts that this account's live
  `device_extended_keys` stay empty and that the *other* account's mark is still there;
  `…_OpenedASecondTime_DoesNotWriteAgain` now asserts the first open's single write and its contents
  and then that the same write instance is still the only one, plus the live rows, instead of
  comparing two counts that were both zero. `…_DeviceWriteFails_…` gained `Assert.NotEmpty` on the
  writes, so the refusal it names is now genuinely exercised.

- **Four cases added, one per item the review's Tests block listed.**
  - `KeyboardPage_PulledPresetWithAMacroBinding_UploadsTheLibraryAndKeepsTheBinding` — **B2**. The
    pulled preset's snapshot carries a `preset_key_macro_bindings` row and its macro is in the
    library; after the open the live `key_macro_bindings` row is still there pointing at that macro,
    and every `SetMacrosAsync` payload of the open is non-empty (every one, not just the last: the
    bug produced an empty upload followed by a healthy one from the end-of-open resync).
  - `KeyboardPage_DeviceSwitchedDuringTheOpen_DoesNotPushThePresetAtTheOtherDevice` — **S1**. The
    open's chain is parked inside the extended-keys probe (a `TaskCompletionSource` seam on the fake
    HID) — the step immediately before `MaterializePulledPresetAsync` — and a second
    `SetDeviceAsync` for a mouse lands there; `SetDeviceAsync` sets `Device` and bumps the
    generation in its synchronous prologue, so the page belongs to the mouse when the chain resumes.
    Asserts the mouse's applied preset is not the keyboard's, its live extended keys are empty, no
    extended-keys write went to its vid, and the mark survives for the keyboard's next open. The
    narrower window `target` fences (a switch landing while the apply is queued behind
    `_presetApplyGate`) is not separately reachable from this harness; it is the same guard
    expression, re-checked inside `ApplyPresetToDeviceCoreAsync`.
  - `ReapplyOnConnect_ForTheUnitWhosePageIsOpen_LeavesTheMarkToThePage` — **S2**. The page is opened
    first and the mark is set afterwards (the pull landing while the user sits on the page), then
    `ReapplyOnConnectAsync` is called with a **fresh card instance** carrying the same settings
    identity — which is what pins `IsOpenDeviceCard` comparing identity rather than reference. The
    connect push carries the live colour, the live rows and the mark are untouched, and the next
    page open then materializes the snapshot exactly once.
  - `KeyboardPage_MarkNamingAPresetThatIsNoLongerApplied_IsSpentWithoutApplying` — **S3**. A mark
    naming the pulled preset while the unit's applied preset is a different one: no board write, the
    live rows stay empty, and the mark is spent all the same.

- **Verified against the pre-fix ordering.** With `MaterializePulledPresetAsync` temporarily moved
  back to directly after `LoadPresetsForDeviceAsync` (the round-1 position), 5 of the 12 cases in
  the file fail — including the B2 and S1 regressions and the three extended-keys cases. Restored
  byte-for-byte afterwards. So these are guards on the bug, not on the fix.

- **Harness changes.** The fake HID now records the vid/pid alongside each extended-keys write (a
  cross-device push is invisible otherwise) and records every macro-library upload; it also carries
  the probe seam S1 needs. `SeedPulledMousePreset` / `SeedPulledKeyboardPresetWithMacroBinding`
  replace the copied setup in the mouse cases. Note for anyone extending the mouse cases: the pulled
  preset must sit alongside a default one, because `CreateDefaultPresetIfNotExists` promotes a lone
  row to default on page open and `ReapplyLastCustomPresetAsync` deliberately skips a device on its
  default preset.

- **Still not automated:** the live server round trip, the two-machine scenario, HID, and the UI
  half of acceptance criterion 3 (that the Extended Keys section *renders* the rows). The storage,
  device-write and Save-Preset halves of that criterion are covered.

- **No product bug found in this round.**

## Review findings

### Round 1 — 2026-09-10 — Ship: no

Reviewed: the uncommitted working-tree delta on `features/PG-662-investigation` on top of
`76166f2` (`git diff` + the untracked `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`).
Earlier commits on the branch (the Extended Keys section itself) are out of scope for this round.
Verified with `dotnet build ./src/ProveGaming.slnx -c Release` (0 errors) and
`dotnet test ./src/ProveGaming.slnx -c Release`: **1 failed / 1713 passed** in
`ProoveGaming.App.Tests`; Installer and Infrastructure green.

#### Plan conformance

- **Implemented as planned:** the `SyncMetaDeviceStatePendingApply` key helper and its value
  constant (`LocalStorageService.cs:45-58`); the pull setting the mark inside the same storage
  delegate as `SetAppliedPreset`, gated on `wrote` (`SettingsSyncService.cs:855-863`); the shared
  read-run-clear helper (`DeviceDetailsViewModel.Presets.cs:1873-1888`); the page consuming it
  (`:1862-1866`); the mouse connect path consuming it with `LoadPresetSnapshotToDevice` as the
  materializer (`:807-813`); the builder's pair-mode nulls (`PresetConfigurationBuilder.cs:70-79`,
  `:132`); all five doc edits (`docs/CONTEXT.md` and the api-v2-sync open-question answer landed in
  `76166f2`); all four test files from the change list.
- **Deviates from plan:** the builder's null rule is gated on `ExtendedKeyMode.IsPair` rather than
  on "the record's code is `None`" (Recommended approach step 4). The Implementation notes explain
  why, and the deviation is **correct** — it is the only reading that satisfies acceptance criterion
  8 ("a single-key record keeps its objects") for Toggle's legitimately all-zero `tap_key`. The plan
  prose, not the code, is the stale half; treat the Implementation note as the correction.
- **Missing / partial:** acceptance criteria 3, 4 and 6 are not met — see **B1**. Criterion 9
  (`dotnet test … --configuration Release` green) fails outright.
- **Accepted limitation (per plan):** the mark is per `(owner, vid, pid)`, not per unit — with two
  identical units the first one opened consumes it. Still true; residual risk unchanged and small.

#### Answers to the four questions this round was asked to settle

1. **Is clearing the mark before the apply the right trade-off?** The *ordering* is right — it is the
   only thing that stops a board that refuses the write from re-pushing on every page open — but it
   is only safe if the apply is guaranteed to be genuinely *attempted*. Today it is not (B1, B2), so
   clear-before converts a recoverable state into a permanently lost one: the mark is gone and no
   later path pushes. Keep clear-before; fix the placement so the apply is complete when it runs.
2. **Can the mark leak into the device-state push from the `devicestate.` namespace?** No — verified,
   not assumed. `SyncCoordinator.PushDeviceStateAsync:794-796` filters
   `key.EndsWith(SyncMetaDirtySuffix)` **before** `TryParseVidPidKey`, so `.pending_apply` is skipped
   silently and does not even produce the "unrecognized sync_meta key" warning. The namespace choice
   is safe.
3. **Is the read-and-clear delegate genuinely race-free for two overlapping page opens?** Yes.
   `LocalStorageService.RunOnStorageAsync` is a strict FIFO continuation chain over one connection
   (`LocalStorageService.cs:140-181`), so the whole `Get → materialize → Remove` body is atomic
   against every other storage item — exactly the rule the file already documents at `:3110` for the
   session-expiry latch. A second open (or a connect racing an open) sees the key gone and returns
   `false`. After `Dispose`, `RunOnStorageAsync<bool>` yields `default` = `false`, so shutdown also
   fails safe. This part of the design is sound.
4. **Can the mouse path's materialization fight with the page's own apply?** Not over the mark itself
   (see 3) — but yes over the **rows**: see **S2**. `ReapplyLastCustomPresetAsync` now *writes* live
   storage for a card whose details page may be open, and `HomeViewModel.ReapplyOnConnect`
   (`HomeViewModel.cs:1643-1649`) has no "skip the currently open device" guard.

#### Code

- [x] **B1. The pulled preset's extended keys never reach the board — the plan's headline goal** — `DeviceDetailsViewModel.Presets.cs:1862` / `DeviceDetailsViewModel.cs:1295` vs `:1338` — Blocker
  **Problem:** the mark is consumed at the end of `LoadPresetsForDeviceAsync`, which `SetDeviceAsync`
  awaits at line 1295 — **43 lines and five awaits before** `LoadExtendedKeysFromDeviceAsync()` at
  line 1338, the only thing that sets `_extendedKeysSupported`. So the materialization's
  `ApplyStoredExtendedKeysToDeviceAsync` returns at its `if (_extendedKeysSupported != true)` guard
  (`DeviceDetailsViewModel.ExtendedKeys.cs:1018`) and writes nothing. The mark is already cleared, so
  nothing pushes them later either.
  **Why it blocks:** this is the exact scenario the plan exists for. Machine B, fresh sign-in, Cyrex
  with a pulled RS + MT preset: the board keeps its old set, and the Extended Keys section — which
  renders `_extendedKeysOnDevice`, i.e. what the probe read off the board — stays empty. QA steps 4
  and 5 fail. Acceptance criteria 3, 4 and 6 fail, and criterion 9 fails literally: the suite is red
  on
  `DeviceDetailsPulledPresetApplyTests.KeyboardPage_OpenedWithThePendingApplyMark_PushesTheSnapshotsExtendedKeysOnce`
  ("Assert.Single() Failure: The collection was empty"). Correctly diagnosed by `/test-writer` and
  left honestly red — the diagnosis is right and the test should stay as written.
  **Solution options:**
  - Option A (correct, and it also fixes B2): move the consumption out of `LoadPresetsForDeviceAsync`
    into `SetDeviceAsync`'s follow-up chain, immediately **after** `LoadExtendedKeysFromDeviceAsync`
    and `LoadMacroLibraryAsync` and **before** `EnsureDefaultPresetAppliedAsync` — i.e. a
    `await Quietly(MaterializePulledPresetAsync(), …)` sitting between lines 1338 and 1341, acting on
    the preset `LoadPresetsForDeviceAsync` already selected. That is the point where every piece of
    state the apply depends on (extended-keys probe, macro library, key layout, button bindings) is
    in place, and it keeps the existing `Stale()` discipline. It also moves the ~150 ms
    `PopulateKeyLayout` the apply triggers back behind the shell's visibility flip.
  - Option B (narrower): in `LoadPresetsForDeviceAsync`, `await LoadExtendedKeysFromDeviceAsync()`
    before the materialization. Fixes extended keys only — **B2 survives** — and probes the board
    twice per open, because line 1338 re-probes unconditionally and resets `_extendedKeysSupported`
    to null on the way in. Not recommended.
  - Option C (do not do this): make `ApplyStoredExtendedKeysToDeviceAsync` treat a pending probe as
    "supported". That guard is deliberate — it is what stops a write to a board that has not
    confirmed it accepts one.

- [x] **B2. The materialization deletes the pulled preset's key-macro bindings and uploads an empty macro library** — `DeviceDetailsViewModel.Presets.cs:1865` → `DeviceDetailsViewModel.KeyAssignments.cs:1019-1060` — Blocker
  **Problem:** same root cause as B1, different victim. `LoadPresetSnapshotToDevice` restores
  `key_macro_bindings` from `preset_key_macro_bindings` (`LocalStorageService.cs:~3795`), and the
  apply then runs `ResyncKeyMacroBindingsAsync`. That method resolves every binding's `macro_id`
  against the **`Macros` VM collection** — which `SetDeviceAsync` cleared at line 1226 and does not
  refill until `LoadMacroLibraryAsync` at line 1301, *after* `LoadPresetsForDeviceAsync`. So at
  materialization time `Macros` is empty: `BuildMacroLibraryPayload()` (`:1128-1132`) sends an
  **empty library** to the keyboard, then `byMacroId` is empty and every binding takes the "the macro
  is gone from the library" branch and calls `DeleteKeyMacroBinding` (`:1049-1056`).
  **Why it blocks:** a user pulls a Cyrex preset that binds a macro to a key (Cyrex ships
  `SupportsMacros: true` and `HasInteractiveKeyboardOverlay` is true for Smart Infinite 65). First
  page open: the board's macro library is wiped, every pulled `key_macro_bindings` row is deleted
  from live storage, and the keys silently lose their macros. Silent data loss on the very feature
  the pull just delivered, and no test in this round covers it.
  **Solution options:**
  - Option A: B1's Option A fixes this too — after `LoadMacroLibraryAsync` has run, `Macros` is
    populated and the resync behaves exactly as it does on a user-driven preset switch.
  - Option B (defensive, worth having regardless): make `ResyncKeyMacroBindingsAsync` bail out
    instead of deleting when `Macros` is empty *and* `bindings.Count > 0` — an empty VM library with
    non-empty binding rows is never a legitimate "the macros are gone" state, it is a load-ordering
    accident. A storage-backed macro lookup would be the honest test of "gone".

#### Should-fix

- [x] **S1. No staleness re-check between the mark await and the apply — a device switch can push one board's preset onto another** — `DeviceDetailsViewModel.Presets.cs:1822` vs `:1862-1866` — Should-fix
  **Problem / why it matters:** the generation/`Device` check at line 1822 happens *before* the new
  block. `ConsumePendingApplyMarkAsync` always yields (the executor continuation runs on
  `TaskScheduler.Default`), and `ApplyPresetToDeviceCoreAsync` deliberately re-reads
  `var card = Device` after its own gate (`:614`). So: the user clicks device A (mark set), then
  clicks device B while the storage item is queued → the resumed continuation applies **A's**
  `initial` preset to **B's** card, i.e.
  `LoadPresetSnapshotToDevice(A.PresetId, …, B.Vid, B.Pid, B.Key)` overwrites B's live RGB /
  bindings / settings and pushes them to B's hardware. Narrow window, but the payload is another
  device's configuration.
  **Fix:** re-run the guard the method already has, immediately after the consume await —
  `if ((generation is { } g2 && g2 != _setDeviceGeneration) || !ReferenceEquals(Device, device)) return;`
  — accepting that the mark is burnt in that rare case (the newer open owns the page). Cleaner still:
  pass the captured `device` into the apply so it targets the card the preset actually belongs to.
  Moot if B1 Option A moves consumption into `SetDeviceAsync`'s chain, which already re-checks
  `Stale()` between steps.

- [x] **S2. The connect-time path now WRITES live rows for a device whose page may be open** — `DeviceDetailsViewModel.Presets.cs:807-813` (+ `HomeViewModel.cs:1643-1649`) — Should-fix
  **Problem / why it matters:** `ReapplyLastCustomPresetAsync` was read-only against storage; its own
  doc comment still says it "reads only its `card` argument and storage". With the materializer it
  now *mutates* `device_rgb`, `button_bindings`, `device_settings`, `key_macro_bindings`,
  `device_actuation` and `device_extended_keys` under the unit's key. `HomeViewModel.ReapplyOnConnect`
  fires for **every** connecting card with no "skip the currently open device" guard, and
  `RestoreConnectedDevicesForNewOwner` re-drives it on the sign-in owner transition — concurrently
  with the pull that writes the mark. Concrete interleaving: the connect path materializes while the
  details page for the same mouse is mid-open; `SetDeviceAsync`'s awaited
  `PersistRgbStateForCurrentDeviceAsync` (`DeviceDetailsViewModel.cs:1281`) then writes the VM's
  pre-materialization RGB back over `device_rgb`. The board wears the snapshot colour, storage holds
  the other one, `PresetMatchesLiveState` disagrees, and Save Preset lights up on a page the user
  never touched — the exact symptom this plan set out to remove.
  **Fix:** skip the materialize half when the card is the one currently open (compare the settings
  identity against `Device`) and let the page's own consumption handle it — the mark makes that
  hand-off safe by construction. Update the method's doc comment either way: "never writes" is no
  longer true.

- [x] **S3. The mark says "something was adopted", not WHICH preset — and it never expires** — `LocalStorageService.cs:52` / `SettingsSyncService.cs:860-862` — Should-fix
  **Problem / why it matters:** the value is a hardcoded `"1"`, and nothing ever bulk-clears
  `sync_meta` for an owner. A mark the page declines to consume (`IsDeviceConfigurable` false — a
  read-only transport, per the Implementation notes) survives indefinitely. If the user then applies
  a different preset by hand and later opens the page over a configurable transport, the mark is
  consumed against **whatever is applied then**: `ApplyPresetToDeviceAsync` reloads that preset's
  snapshot over the live rows, silently discarding applied-but-unsaved live state (an RGB or DPI
  tweak that was pushed to the device but never saved into the preset). The consumer cannot tell the
  difference because the mark carries no identity.
  **Fix:** store the adopted preset's id (or uid) as the mark's value instead of `"1"` — the column
  already exists — and have `ConsumePendingApplyMarkAsync` clear-but-skip when the currently applied
  preset is not the one the pull adopted. Same one-delegate atomicity, one extra comparison.

#### Tests

- [ ] **Two of the new keyboard cases pass vacuously while B1 stands** —
  `DeviceDetailsPulledPresetApplyTests.KeyboardPage_MarkUnderADifferentOwner_IsNotConsumed` and
  `…_OpenedASecondTime_DoesNotWriteAgain` assert only on `hid.ExtendedKeysWrites`, which is empty for
  **every** keyboard case today. They would keep passing if the owner-kind guard or the once-only
  rule broke. Assert on the observable that actually moves: the live rows
  (`LiveExtendedKeys(store, owner)` staying empty / unchanged) and the mark, as
  `…_OpenedWithoutTheMark_WritesNothingToTheBoard` already correctly does.
- [ ] **No coverage for B2** — add a keyboard case where the pulled preset's snapshot carries a
  `preset_key_macro_bindings` row and a macro in the library, and assert that after the open the live
  `key_macro_bindings` row still exists and `SetMacrosAsync` was called with a non-empty library.
  This is the gap that let a silent data-loss path through a full round.
- [ ] **No coverage for S1** — a second `SetDeviceAsync` for a different card issued while the first
  open's mark consumption is queued, asserting the second card's live rows are untouched.
- [ ] **No coverage for the mouse-connect / page-open overlap (S2)** — `ReapplyOnConnectAsync` for the
  card currently in `Device`, asserting the page's state and the live rows do not diverge.
- Everything else in the round is well covered: the builder's pair nulls and the
  build → apply → build round trip, the pull's `extended_keys` intake including a malformed item, the
  applied-row-and-mark pairing plus the "no `.dirty` key" assertion, and the mouse snapshot-vs-live
  colour proof.

#### Nits

- The materialization ends in `SetAppliedPreset(...)` with the default `markDirty: true`
  (`DeviceDetailsViewModel.Presets.cs:706-708`), so the first open after a pull immediately
  re-dirties the device state and pushes back to the server the exact `active_preset_uid` the pull
  just read. Harmless echo, one extra `devices/update` per pulled device type.
- `RekeySettingsVendor` (`LocalStorageService.cs:1300-1312`) does not re-key `.pending_apply`.
  Accepted in the Implementation notes and the reasoning holds (the pull writes under the canonical
  pair); worth noting that a stranded mark is also never *deleted*, so it would sit in `sync_meta`
  forever.
- `ReapplyLastCustomPresetAsync:807-808` destructures `ownerKind`/`ownerId` and separately passes
  `owner.Value`, and discards the helper's `bool` result. Both fine; the destructure exists only for
  the closure, which could just capture `owner.Value`.
- On the one open that materializes, `LoadKeyLayoutForDevice()` inside the apply pulls the ~150 ms
  `PopulateKeyLayout` build ahead of `coreLoaded.TrySetResult()`. Capped in practice by
  `MainWindow.axaml.cs:894`'s `Task.WhenAny(…, Task.Delay(300))`, so the page still flips — but it
  flips onto a busy UI thread. B1 Option A removes this as a side effect.

#### What's done well

- **The race-free consumption is genuinely race-free.** Read → materialize → delete inside one
  `RunOnStorageAsync` delegate is exactly the rule `LocalStorageService.cs:3110` already documents
  for read-then-write latches, and it is the right primitive here. One helper for both call sites, so
  the two cannot drift.
- **The namespace question was answered by reading the code, not by assuming.** The
  `EndsWith(".dirty")` filter in `PushDeviceStateAsync` really does make the mark invisible to the
  push, warning included. The Implementation note says so and it checks out.
- **The mark is written in the same delegate as the applied row and gated on `wrote`**, so the two
  can never disagree, and a guest upgrade or an existing local applied row leaves both alone — pinned
  by `Pull_LocalAppliedRowExists_LeavesAppliedPresetAloneAndSetsNoPendingApplyMark`.
- **The builder deviation is the right call.** Gating on `IsPair` rather than `IsNone` is the only
  reading that keeps Toggle's all-zero `tap_key`, and
  `BuildApplyBuild_WithNullPairCodes_RoundTripsToIdenticalJson` is the right shape of test for a
  serialization tidy-up — it proves the change is not lossy.
- **`/test-writer` reported the failing criterion instead of weakening the test.** The diagnosis in
  Test notes is accurate down to the mechanism, and the "re-set the mark and open twice" proof is a
  good piece of evidence. Do not delete or `Skip` that test to get the suite green.

#### Verdict

Ship: **no**. The change's headline goal does not happen — a pulled preset's extended keys still
never reach the board (B1, with the suite red on exactly that) — and the same load-ordering mistake
silently deletes pulled macro bindings and wipes the board's macro library (B2). Everything *around*
the materialization is well built: the mark, its atomic consumption, its isolation from the push, and
the builder tidy-up are all correct and should survive the fix unchanged.

First: **(1)** move the mark consumption out of `LoadPresetsForDeviceAsync` into `SetDeviceAsync`'s
follow-up chain, after `LoadExtendedKeysFromDeviceAsync` and `LoadMacroLibraryAsync` (B1 Option A —
closes B1, B2, S1 and the last nit at once); **(2)** add the macro-binding regression test B2 should
have been caught by, and re-point the two vacuous keyboard assertions at the live rows; **(3)** guard
the connect-time materializer against the currently open device (S2) and decide on carrying the
adopted preset id in the mark's value (S3).
