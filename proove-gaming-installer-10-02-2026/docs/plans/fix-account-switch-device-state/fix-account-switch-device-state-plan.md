# Plan: a keyboard must wear the signed-in account's own configuration, not the previous account's

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-09-10. Owner: architect. Direct follow-up to
[`../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md`](../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md),
whose pending-apply mark is the mechanism this plan reuses. Related:
[`../add-reapply-preset-on-connect/add-reapply-preset-on-connect-plan.md`](../add-reapply-preset-on-connect/add-reapply-preset-on-connect-plan.md)
(the connect-time restore whose keyboard early-return is the root cause).


> **2026-09-11 — the marking half does not fire on the commonest path.** The user reproduced the
> original symptom again by restarting the app between the two accounts. Evidence: no
> `.pending_apply` row was ever written, the App process started between account A's write and
> account B's page open, and the DeviceHost logged a read with no write. `_lastAppliedOwner` is
> an in-memory field, so a fresh process reads the restored session as the session's first owner
> and `ShouldReapplyDeviceState` answers no — the rule inherited from the lighting sweep, whose
> justification does not transfer to flash-backed state. The sweep and the mark stay (the mark
> still carries the pull case); the account-switch case is covered by
> [`../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md`](../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md)
> instead. This plan's Review findings journal is left as it was written.

> **2026-09-12 — the marking half is removed outright, and the criteria below follow it.** An
> external review of PR #136 (`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`, round 4,
> M1) showed the sweep's mark does not merely under-fire: when it DOES fire it destroys data. The
> mark's consumers answer it with `LoadPresetSnapshotToDevice`, which deletes the unit's live rows
> and replaces them with the preset snapshot — correct for a preset that arrived from the server and
> has never been loaded on this machine, and silent loss for an account switch, where the incoming
> account's applied-but-unsaved state is legitimate and its own. The owner sweep therefore writes no
> mark at all as of 2026-09-12; the mark keeps its pull case and its single writer, the sign-in pull.
> `ShouldReapplyDeviceState`, `_lastAppliedOwner` and `HomeViewModel.MarkDeviceStatePendingApply`
> are deleted with it, and `OwnerSweepState` drops its third element. Criteria 1-5 below are
> rewritten to say what the switch now does; the rest were never about the mark and stand unchanged.

## Goal

Reported on 2026-09-10: a user configured a Toggle extended key under account A, switched to account
B and **saw the same Toggle there**; deleting it under B made it vanish from account A as well. This
plan makes a shared keyboard follow the signed-in account. Nothing leaked between accounts in
storage — that was verified (below) — but the keyboard's flash is shared hardware and, unlike
lighting, a keyboard's configuration is never re-applied when the settings owner changes, so the
board keeps whatever the previous account left and the Extended Keys section, which renders what it
probed off the board, faithfully shows another account's records.

## What was verified before planning (2026-09-10)

Against the live database after the user's reproduction (`device_extended_keys`,
`preset_extended_keys`, `device_applied_preset`):

- **No storage leak and no data loss.** The Toggle (mode `0x82`, slot 16, 100 ms) exists as its own
  row for each owner: the guest owner has one, `user 36` has one in live rows **and** in its
  `Default` preset snapshot. The account where the user deleted it (`user 17`) correctly has none.
  Account A's record was never lost — only the board and the display stopped agreeing with it.
- **Why B saw A's Toggle.** `LoadExtendedKeysFromDeviceAsync` does not read storage at all: it
  probes the board and renders `_extendedKeysOnDevice`. Under account B the board still physically
  held A's record.
- **Why nothing restored B's own state.** The owner-change sweep
  (`HomeViewModel.RestoreConnectedDevicesForNewOwner`, fired from the `CustomerId` and `IsGuest`
  transitions) calls `ReapplyOnConnectAsync`, and `ReapplyLastCustomPresetAsync` returns early for
  keyboards by design ("their apply needs layout state only the details page builds"), leaving only
  the RGB fallback. On the page, `EnsureDefaultPresetAppliedAsync` early-returns because account B
  already had an applied row, and the pending-apply mark is set **only by the sign-in pull**. So no
  path applied B's preset, and none applied A's when the user came back.
- **Scope of the defect.** Extended keys are how the user noticed it, but the same gap leaves key
  remaps, key→macro bindings and actuation from the previous account on the board. The fix is at the
  apply level, so it covers all of them at once.

## Scope

In:

- Set the existing pending-apply mark for **every connected device type the incoming owner has an
  applied preset for** when the settings owner changes (account ↔ account, guest ↔ account), so the
  first page open under the new owner applies that owner's preset to the board.
- ~~Re-drive the materialization for a device page that is **already open** when the owner changes.~~
  Dropped 2026-09-10 (review round 1, S1): no reachable flow leaves a device page open across an
  owner change — the sign-out closes it first. See Recommended approach, step 3.
- Fix the corresponding hole for an incoming owner with **no** applied preset for the device, so a
  never-configured account does not inherit the previous account's records (mirrors the existing
  `resetWhenUnset` lighting rule).
- Characterization tests for today's behaviour where they are missing, then the reproducing test,
  then the fix.

Out:

- An **off-page** keyboard apply (applying the new owner's keyboard configuration without opening
  its details page). Today a keyboard's remaps and macro bindings already depend on the page for
  their apply; widening that is a separate project. Consequence, stated plainly: between the account
  switch and the first page open, the board still *behaves* as the previous account configured it.
  A follow-up is named in Open questions.
- Whether a guest should be allowed to see or overwrite a signed-in user's board at all — a product
  decision, recorded in Open questions.
- Any change to the extended-keys wire format, the preset schema or the API.

## Options considered

### A. Mark the device types pending-apply on owner change (recommended)

`RestoreConnectedDevicesForNewOwner` already runs on exactly the transitions that matter and already
knows the connected cards. Set the pending-apply mark there for the incoming owner; the page's
`MaterializePulledPresetAsync` — which the previous plan positioned after the extended-keys probe and
the macro/layout loads — consumes it.

- Pro: reuses machinery that exists, is tested, and already applies the whole preset (RGB, remaps,
  macro bindings, actuation, extended keys) in the right order.
- Pro: one mechanism for both causes of "the board is not this owner's", pull and owner change.
- Con: the apply happens on page open, not at switch time (see Scope/out).

### B. Re-probe and compare in the Extended Keys section, showing the account's rows when they differ

Render live rows instead of the board when they disagree.

- Con: makes the section lie about the hardware — the user would see a Toggle the board does not
  have. The section's whole contract is "this is what the board holds". Rejected.

### C. Drop the keyboard early-return and build an off-page keyboard apply

- Pro: fixes the behavioural half too.
- Con: the early-return exists because the key push needs layout/catalog state the details page
  builds; reproducing it off-page is a substantial change with its own regression surface, and it
  does not make the *visible* bug any more fixed than A does. Deferred, not rejected.

## Recommended approach

Option A, in four parts.

1. **Mark on owner change.** In `RestoreConnectedDevicesForNewOwner`, when the owner changed and a
   previous owner's state may be on the hardware — the same question `_lastLitOwner` already answers
   for lighting, so introduce the device-state twin `_lastAppliedOwner` rather than overloading the
   lighting field — write the pending-apply mark for each connected card's settings type whose
   incoming owner has an applied preset. Value stays the contract the previous plan set: the applied
   preset's local id. Storage writes go through the storage executor, not the UI thread.
2. **Never-configured incoming owner.** When the incoming owner has **no** applied preset for a
   connected device type, the board must not keep the previous owner's records. Reuse the existing
   predicate shape (`ShouldResetUnconfiguredLighting`: a genuine owner-to-owner switch only, never
   the first owner of a session) and let the page's existing
   `EnsureDefaultPresetAppliedAsync` do the work — it already applies the seeded default preset,
   whose empty extended-keys set clears the board. Verify this holds after the previous plan's
   re-ordering; if it does not fire because an applied row exists for a *different* unit key, mark
   that type pending-apply too and let the materialization take it.
3. **No page re-drive is needed — an owner change never finds a device page open.** *(Corrected
   2026-09-10 after review round 1, S1; this step originally asked for a re-drive inside
   `DeviceDetailsViewModel.OnOwnerChanged`, and one was built and then removed. See Implementation
   notes → Round 2 for the evidence.)* Every path that reaches a new non-null owner passes through
   `MainWindow.ClearSignedInState` (both sign-out paths) or `PerformExitDeviceDetails` (leaving the
   page for Home) first, and both begin by calling `ClearDeviceForExit`, which nulls `Device`. So an
   account switch **closes** the device page, and it is the next open — the arriving account's own —
   that consumes the mark and writes the board. `OnOwnerChanged` stays what it already is: the
   synchronous per-owner cache purge, which must also run for a sign-out.
4. **Nothing else changes.** The mark's value contract, its identity check, the consume-and-clear
   delegate and the mouse path all stay as the previous plan left them.

## File-level change list

- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — edit: `_lastAppliedOwner` field and its
  device-state predicate (twin of `_lastLitOwner` / `ShouldResetUnconfiguredLighting`, with the same
  "first owner of a session is not a switch" rule); in `RestoreConnectedDevicesForNewOwner`, mark
  each connected card's settings type pending-apply for the incoming owner when it has an applied
  preset there. Keep the existing lighting sweep untouched. Also (review round 1, S3): the sweep and
  an `OwnerSweepState` accessor over the three "who was the sweep last driven for" fields are
  `internal`, so a test can drive the real method with `_lastLitOwner` and `_lastAppliedOwner` forced
  apart — the Risks section's "reusing the wrong predicate" is otherwise unpinnable.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — edit: no behaviour
  change. `OnOwnerChanged` keeps its cache purge; a comment records why there is no page re-drive
  (step 3 — an owner change always arrives with `Device` already null).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — edit:
  `ConsumePendingApplyMarkAsync` no longer refuses guest owners. Its guard read "guests never pull,
  so they never carry the mark" — true of the pull, false the moment the sweep became the mark's
  second writer, and Scope names `guest ↔ account` in both directions. Nothing else moves.
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit **only if** a bulk
  "applied preset id per device type for this owner" read is needed to avoid N queries on the UI
  thread; otherwise unchanged.
- `docs/CONTEXT.md` — edit: extend the **Pending device apply** entry to say the mark is set by the
  sign-in pull **and** by an owner change.
- `docs/plans/add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md` — edit: dated note that
  the mark gained a second writer (this plan).
- `docs/plans/add-reapply-preset-on-connect/add-reapply-preset-on-connect-plan.md` — edit: dated note
  that the keyboard early-return now has a page-side counterpart for owner changes.

Tests (bug fix — the reproducing test lands first):

- `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` — edit: **the reproducing
  test first** — owner A configures an extended key on a keyboard, the owner switches to B (who has
  an applied preset of their own with no extended keys), B opens the page → the board must be written
  with B's set, and A's live rows and preset snapshot must be untouched. This fails on today's code.
  Then: the same for a remap and a macro binding; an owner switch back to A restores A's records; an
  incoming owner with no applied preset does not inherit the previous owner's records; and (review
  round 1, Tests) an account → **guest** switch, where the guest spends its own mark and cannot
  reach the account's — the case the guest short-circuit's removal from
  `ConsumePendingApplyMarkAsync` needs.
- `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs` — edit: the owner transition marks
  each connected type pending-apply for the incoming owner; the first owner of a session does not;
  an unchanged owner does not; a sign-out marks nothing. Also (review round 1, S3): sweep-level
  cases that drive the real `RestoreConnectedDevicesForNewOwner` over a constructed `HomeViewModel`
  with `_lastLitOwner` and `_lastAppliedOwner` seeded APART through `OwnerSweepState` — the mark
  must follow the device-state field in both directions — plus the `SettingsVendorId <= 0` skip and
  the sign-out guard, which lives in the sweep now that the re-drive is gone.
- `tests/ProoveGaming.App.Tests/HomeViewModelSettingsIdentityTests.cs` — edit only if the settings
  identity used for the mark needs pinning there rather than in the owner-restore file.

**Reversed 2026-09-12 (see the dated note at the top).** The marking half of this list is undone:
`HomeViewModel` loses `MarkDeviceStatePendingApply`, `ShouldReapplyDeviceState` and
`_lastAppliedOwner`, `OwnerSweepState` becomes a two-tuple, and the sweep's marking loop is gone.
`docs/CONTEXT.md`'s **Owner-change sweep** and **Pending device apply** entries are corrected back
to one writer. The guest change to `ConsumePendingApplyMarkAsync` **stays**: a consumer that
special-cases guests is wrong about a bucket `sync_meta` can still be handed. The two test files
keep their cases but assert the ABSENCE of a mark, and the remap / macro-binding cases now pin the
deferred limitation instead of its fix.

## Acceptance criteria

- [x] Given account A saved a preset with a Toggle on Q for a Cyrex and the board holds it → switch
  to account B, whose own preset for that Cyrex has no extended keys → open the Cyrex page → the
  Extended Keys list is empty **and** the board was written once with B's set. *(Carried by
  [[Extended-keys self-heal]] since 2026-09-11, not by the mark.)*
- [x] Given that state → switch back to A → open the Cyrex page → the Toggle is listed again and the
  board holds it, with no manual preset selection. *(Same mechanism.)*
- [x] Given account B never configured that Cyrex at all → switching to B and opening its page → the
  board does not keep A's records. *(Same mechanism: B's empty rows are still an opinion, and the
  self-heal writes them.)*
- [x] Given the account is switched while the Cyrex page is open → the switch itself closes the page
  (signing out leaves the details view), and the first time the new owner opens the Cyrex again the
  page shows that owner's presets and the board is written with that owner's **extended keys**.
  *(Corrected 2026-09-10 after review round 1, S1 — this criterion previously read "without
  navigating away and back", which no reachable flow can satisfy: `ClearSignedInState` nulls `Device`
  before the owner ever changes. Narrowed 2026-09-12 from "that owner's set" to extended keys, per
  the criterion below.)*
- [ ] **Not met, and deliberately deferred.** Given a key remap and a key→macro binding saved under A
  → the same switch → B's page shows B's remaps/bindings, but the BOARD does not get them: the open
  runs no preset push for an account that already has an applied preset, so A's remap stays on the
  key and A's macro record stays on its slot until B's next preset action. B's own macro binding does
  reach the board, because `ResyncKeyMacroBindingsAsync` runs unconditionally from
  `RefreshFromDeviceAsync`. This is the limitation
  [`../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md`](../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md)
  names and defers by name ("the same root cause affects key remaps, key→macro bindings and
  actuation … follow-up plan, with the matrix read's position as its first question"). It was met
  between 2026-09-05 and 2026-09-12 by the sweep's mark, at the cost of M1's data loss. Pinned as it
  now stands by `KeyboardPage_OpenedAfterAnAccountSwitch_DoesNotYetCarryTheNewOwnersRemap` and
  `..._WritesTheNewOwnersMacroBinding_ButLeavesTheOthersOnTheBoard`, so the follow-up has two tests
  to invert.
- [ ] Given the same account is signed out and back in → nothing is written to the board (it already
  wears that account's configuration).
- [ ] Given a device page is opened twice under one owner with no owner change between → exactly one
  board write for the first open, none for the second.
- [ ] Given a mouse → the existing connect-time restore behaviour is unchanged (its tests stay green).
- [ ] `dotnet test ./src/ProveGaming.slnx --configuration Release` green.

## Test strategy

Bug fix, so the order is fixed: **the reproducing test is written first and must fail on the current
code** — `DeviceDetailsPulledPresetApplyTests`, owner A configures an extended key, owner switches to
B, B opens the page, assert the board carries B's set (today it carries A's, and nothing is written).
Only then the fix.

The touched features already have tests, and they must stay green and be extended:
`DeviceDetailsPulledPresetApplyTests` (12 cases from the previous round, including the S1/S2/S3
fences), `HomeViewModelOwnerRestoreTests` (the lighting sweep's predicates — the new device-state
predicate must not change any of them), `SettingsSyncServicePullTests` (the pull remains the mark's
other writer), `DeviceDetailsPresetApplyTests`.

Cases the change introduces: mark written per connected type on an owner change; not written for the
first owner of a session, an unchanged owner, or a sign-out; consumed once per switch; an incoming
owner with no applied preset; and a regression case that would fail if the new predicate accidentally
reused `_lastLitOwner` (a lighting-only transition must not spend the device-state mark) — which,
after review round 1 S3, means driving the real `RestoreConnectedDevicesForNewOwner` with the two
fields forced apart, not only the predicates in isolation.

Deliberately not automated: the physical keyboard behaviour after a switch (QA), the real HID write
(faked at the transport boundary per repo convention), and the off-page behavioural leak, which is
out of scope by design.

## QA (manual test steps)

Needs one keyboard (Cyrex or Aress) and two accounts, A and B.

1. Sign in as **A**, connect the keyboard, open its page → Extended Keys. Add a Toggle on **Q**
   (toggled key W, 100 ms). Click "Save Preset". **Expected:** the row is listed; tapping Q latches
   continuous "w".
2. Sign out and sign in as **B**. Open the same keyboard's page → Extended Keys. **Expected:** the
   list is EMPTY — B has never configured this board — and tapping Q types a normal "q".
   *(Before this fix, A's Toggle appeared here.)*
3. As **B**, add a different extended key, for example a Rapid Snap pair A ↔ D. Save Preset.
   **Expected:** listed and working.
4. Sign back in as **A**, open the keyboard page. **Expected:** the Toggle on Q is listed again and
   works; B's Rapid Snap pair is NOT listed and A ↔ D behave normally.
   *(Before this fix, A's Toggle was gone.)*
5. Sign in as **B** again and check. **Expected:** B sees the Rapid Snap pair, not the Toggle. The
   two accounts keep their own setups indefinitely as you switch back and forth.
6. As **A**, remap a key (say F1 → F5) and bind a macro to another key. Save Preset. Switch to **B**
   and open the page. **Expected:** B's own remaps and macro bindings, not A's; typing F1 gives F1.
7. Sign out and sign back in as the **same** account, then open the page. **Expected:** everything is
   as you left it, and there is no visible re-writing pause.
8. With the keyboard page **open**, sign out. **Expected:** the app leaves the device page and lands
   on the Welcome screen — the page does not stay open behind the sign-out. Sign in as the other
   account, open the keyboard from the device list. **Expected:** the page and the board are that
   account's, first time, with no manual preset selection. *(Rewritten 2026-09-10 after review round
   1, S1: the step used to say the open page re-draws itself "without needing to go back to the
   device list", which the app never does — signing out closes the page, so re-opening it is the
   flow, and the previous wording would have been ticked off for the wrong reason.)*
9. Guest check: use the app as a guest, configure an extended key, then sign in as **A**.
   **Expected:** A sees A's own setup, not the guest's.
10. Mouse regression: with a mouse connected and a custom preset applied under A, switch to B.
    **Expected:** the mouse's lighting follows B, exactly as it did before this change.

## Review focus

- `thread-safety` — the mark is written from the owner-transition handler, which runs on the UI
  thread from a `PropertyChanged`; the write must go through the storage executor and must not race
  the page's consume-and-clear delegate.
- `UI-thread affinity` — no storage query on the UI thread in the sweep. (The open page needs no
  fence of its own: an owner change never finds one open — review round 1, S1.)
- `async/await` — the sweep is `void`-returning today; any awaited work added there must not become
  fire-and-forget without a `Quietly`-style guard.
- `null-safety` — a sign-out (owner → null) must write nothing and must not throw.

## Performance & quality

No hot paths. The sweep runs on owner transitions only (a handful per session). It adds one applied-
preset lookup per connected card; if that turns out to be more than a couple of queries, use a single
bulk read rather than a loop of point queries, and keep all of it off the UI thread. The apply itself
is one preset push per device, once per switch — the same traffic a manual preset switch produces,
and the existing "board already matches" check suppresses it when the incoming owner's set is already
on the board (so signing out and back into the same account writes nothing).

## Risks

- **Double apply.** The owner-change mark and `EnsureDefaultPresetAppliedAsync` could both push on
  the same open. The materialization records the preset as applied precisely so the default push
  below it becomes a no-op — that ordering must be preserved, and a test should pin "exactly one
  write per open".
- **Reusing the wrong predicate.** `_lastLitOwner` answers "whose *lighting* is on the board" and is
  updated only when the lighting sweep runs. Overloading it for device state would skip cases the
  lighting sweep skips. Hence a separate field; a test pins that they can diverge.
- **The behavioural window stays open.** Until the page is opened, the board still behaves as the
  previous account configured it. QA step 2 checks the list, not the typing, for that reason; the
  follow-up in Open questions is where that gets closed.
- **Flash wear.** Every added apply is a flash commit on some boards. The "already matches" check and
  the one-write-per-switch rule are what keep this bounded; a regression here would be silent.

## Open questions

1. **Should a guest be able to see and overwrite a signed-in user's board at all?** A product
   decision, not a technical one. This plan treats guest and account symmetrically (each owner gets
   its own configuration on the board). The alternative — a guest never writes a board an account has
   configured — needs a rule from the product side.
2. **Off-page keyboard apply** (option C): worth scheduling? It closes the behavioural window in
   Scope/out. Recommendation: schedule it separately once this lands, since it carries the whole
   key-push-off-page regression surface.
3. **Two identical units.** The mark is per `(owner, vid, pid)`, as the previous plan established. With
   two identical keyboards the first one opened spends it. Accept, or key the mark per unit? Accepting
   is consistent with the existing behaviour; changing it is a small edit to the key helper.

## Implementation notes

### Round 1 — 2026-09-10 — the implementation

*(Left as written. Its "Deviation: the re-drive is its own entry point" paragraph was superseded by
Round 2 below, which deletes the re-drive; the reasoning it records is why the branch existed.)*

Dated 2026-09-10, written by `/coder`. Source edits plus the ONE reproducing test the Test strategy
mandates; the rest of the test list is left to `/test-writer`.

**The reproducing test came first and failed.**
`DeviceDetailsPulledPresetApplyTests.KeyboardPage_OpenedAfterAnAccountSwitch_WritesTheNewOwnersSet`
`AndLeavesTheOthersRecords` — A saves a Toggle on Q, the board physically holds it, B (whose own
applied preset has no extended keys) becomes the owner and opens the page. Run against the code as
it stood, with the sweep's marking step omitted, it failed on

```
Assert.Single() Failure: The collection was empty
```

i.e. B's open wrote nothing at all to the board, which is precisely the report: B kept looking at
A's Toggle. It passes with the fix, and asserts A's live rows, A's preset snapshot and A's applied
row are all untouched, so a "fix" that worked by overwriting A's records would not pass it.

**Where the mark is written.** `HomeViewModel.MarkDeviceStatePendingApply` is an `internal static`
taking the store and the owner, so the sweep's per-card work is drivable from a test without the
device/session service graph — the same convention `ShouldRestoreForOwner` and
`ShouldResetUnconfiguredLighting` already follow in this class. Its read (`GetAppliedPreset`) and
write (`SetSyncMeta`) are one delegate, and the sweep hands the whole thing to the storage executor
through the existing `EnqueueStorageWrite`, so nothing queries storage on the UI thread.

**`_lastAppliedOwner` is a separate field, as the Risks section requires.** `ShouldReapplyDeviceState`
is `ShouldResetUnconfiguredLighting`'s twin in shape and in rule (a genuine owner-to-owner switch
only), and both fields are assigned after the `if (!restore) return;`, so a sign-out clears neither.
They can diverge, and a `/test-writer` case should pin that.

**Deviation: the re-drive is its own entry point, not part of `OnOwnerChanged`.** The file-level
change list put the already-open-page re-drive inside `DeviceDetailsViewModel.OnOwnerChanged`. It is
instead `RedriveOpenDeviceForOwnerChange`, called from the same sweep immediately after the marking
loop, for two reasons that only appear once the ordering is written down:

1. **Ordering.** The re-drive consumes the marks the sweep writes. `OnOwnerChanged` is called
   *before* the sweep knows whether there are marks (it must also run for a sign-out, which marks
   nothing), so a re-drive started there would race them. Called after the loop, the storage
   executor's strict FIFO is what guarantees the re-drive sees the marks — it is not a timing
   assumption.
2. **`OnOwnerChanged` is synchronous cache housekeeping** with three existing tests that drive it
   directly and assert on view-model state (`DeviceDetailsRgbOwnerIsolationTests`); a fire-and-forget
   page re-open landing inside it would clear `Presets` under them.

The re-drive is `SetDeviceAsync(Device)` — re-opening the card is exactly "the new owner's presets
and the new owner's board", it bumps the generation so an in-flight open under the previous owner
stops at its own `Stale()` check, and `MaterializePulledPresetAsync` rides along. It is wrapped in
`Quietly` (never unobserved) and exposed as `OwnerChangeRedriveTask`, the same test seam shape as
`TransportSwitchRefreshTask`.

**One amendment outside the plan's "nothing else changes":** `ConsumePendingApplyMarkAsync` no
longer refuses guest owners. Its guard read "guests never pull, so they never carry the mark" — true
of the pull, false the moment the sweep became the second writer. Scope names `guest ↔ account` in
both directions, and a guest that could never consume its own mark would both keep wearing the
account's records and accumulate a mark nothing ever clears. Nothing else about the delegate moved.

**The never-configured incoming owner needed no code.** Verified by reading the path and left as the
plan's step 2 predicted: `MarkDeviceStatePendingApply` writes nothing for an owner with no applied
row, `EnsureDefaultPresetAppliedAsync` fires precisely on `applied is null`, and the default preset
it applies has an empty extended-keys set, which `ApplyStoredExtendedKeysToDeviceAsync` pushes as a
genuine "none" and so clears the board. A `/test-writer` case should pin it rather than trusting the
reading.

**Open questions — the recommended defaults were taken, none blocked.**

1. *Guest vs. signed-in board ownership.* Treated symmetrically, as the plan proposed: each owner
   gets its own configuration on the board, guest included. Still a product decision.
2. *Off-page keyboard apply.* Not attempted; out of scope by design. The behavioural window between
   the switch and the first page open stays open, and the follow-up stays unscheduled.
3. *Two identical units.* Accepted as-is: the mark stays per `(owner, vid, pid)`, and with two
   identical keyboards the first one opened spends it. Consistent with the existing behaviour, and
   changing it remains a small edit to the key helper.

**Verification.** `dotnet build ./src/ProveGaming.slnx` clean (0 errors, no new warnings);
`dotnet test ./src/ProveGaming.slnx --configuration Release` green — 1719 App + 1082 Infrastructure
+ 8 Installer. `DeviceDetailsPulledPresetApplyTests` is 13 cases now (the 12 from the previous round
plus the reproducing one) and `HomeViewModelOwnerRestoreTests`' lighting predicates are unchanged.

### Round 2 — 2026-09-10 — `/coder` fix pass over review round 1's three Should-fixes

**S1 — the re-drive is deleted (reviewer's option A).** The claim was checked in the code before
deciding, not taken on the review's word:

- `MainWindow.ClearSignedInState` opens with `_deviceDetailsViewModel.ClearDeviceForExit()`
  (`MainWindow.axaml.cs:468`), which sets `Device = null`. **Both** sign-out paths call it —
  `SignOutToFreshSignIn` (:431, reached from `OnSessionTokenInvalid` and `OnTokenRejected`) and
  `OnLogoutRequested` (:454).
- `PerformExitDeviceDetails` (:936) calls the same thing on the way from the details page to Home.
- Every remaining route to a NEW non-null owner starts from Welcome or Home, so it has already passed
  through one of those two: `OnSignInSucceeded`, `OnGuestContinueRequested`, and `OnNicknameChosen`
  (whose entry point `OnGuestRequested` is raised by the **HomeView** guest menu). Walking every
  `_currentStep = AppStep.Welcome` / `AppStep.Home` assignment in `MainWindow.axaml.cs` turns up no
  other one.
- The single exception is `OnSessionExpired` (:620) — the only owner-losing path that does not clear
  `Device`. It sends the user to Welcome with `Device` still set, and the next "Continue as guest"
  was therefore the one flow that reached the re-drive. Its effect there was not the plan's feature:
  the fresh guest has no applied preset (`ClearData` wiped it), so the re-drive re-opened a page the
  user is not even looking at (they are on Home) purely to let `EnsureDefaultPresetAppliedAsync` push
  the default preset — **a board write on guest expiry that did not happen before this change**.

So the branch was not merely dead; where it did run it did the wrong thing. Deleted:
`RedriveOpenDeviceForOwnerChange`, the `OwnerChangeRedriveTask` seam, and the sweep's call to it. The
`DeviceDetailsViewModel? details` local that existed only to serve the call reverts to the
`if (ownerChanged) _services.GetRequiredService<DeviceDetailsViewModel>().OnOwnerChanged(owner);`
one-liner it was at `d3bcc24`. A comment where the method stood now records *why* there is no
re-drive, so the next reader does not re-derive the same wrong model. Corrected in the live sections
of this plan: Scope, Recommended approach step 3, the file-level change list, Test strategy, Review
focus, **acceptance criterion 4** and **QA step 8** — the last two matter most, because QA copies the
step verbatim into the ticket and the old wording ("without needing to go back to the device list")
would have been ticked off by a tester whose sign-out had closed the page for him.

**S2 — closed by removing its cause, not by adding a fence.** The review's own sequencing makes S2
conditional on S1 keeping the re-drive ("then **S2** if S1 keeps the re-drive"), and its failure
scenario needs two overlapping `SetDeviceAsync` chains over the *same* `DeviceCardViewModel` with an
owner change between them. With the re-drive gone nothing starts a second chain over the same card
from inside the app: the only caller is `MainWindow.OpenDeviceDetails` (`:907`), which is reached by
clicking a card, and getting back to the card list runs `PerformExitDeviceDetails` → `Device = null`
first. `SetDeviceAsync`'s tail is therefore back to the invariant it was written under — "stale"
means a different device — which `LoadPersistedDeviceStateAsync`'s `ReferenceEquals(Device, device)`
and `EnsureDefaultPresetAppliedAsync`'s `Device is null` both already catch. No fence added: the tail
is untouched pre-existing code, and threading `generation` through two more steps to guard a caller
that no longer exists is exactly the speculative hardening this plan says not to write. **If the
off-page follow-up (Open question 2) ever makes an owner change land on a live page, S2 comes back
and must be fixed with it** — that is what the fence would have been for.

**S3 — the sweep is now drivable.** `RestoreConnectedDevicesForNewOwner` is `internal` instead of
`private`, and a new `internal OwnerSweepState` accessor reads and seeds `_lastRestoredOwner`,
`_lastLitOwner` and `_lastAppliedOwner` as a tuple. That is the whole of what the review asked for:
those three are written only by the sweep itself, and `_lastLitOwner`/`_lastAppliedOwner` are
assigned on adjacent lines, so nothing could tell whether the sweep feeds the right field to the
right predicate until a test can force the two apart first. `HomeViewModel` is already constructible
in tests (`HomeViewModelSettingsPersistTriggerTests` and three others do it), so a sweep-level case
with `_lastLitOwner` and `_lastAppliedOwner` set to different owners, asserting the mark after
`store.DrainStorageQueueAsync()`, is now possible — the review's (a), (b'), (c) and (d). Writing it is
`/test-writer`'s job.

**Tests removed, and why (forced by the S1 deletion).** Two cases in
`DeviceDetailsPulledPresetApplyTests.cs` called the deleted `internal` members and no longer compile:
`OpenKeyboardPage_WhenTheOwnerChangesUnderIt_IsRedrivenWithTheNewOwnersPresetAndBoard` and
`RedriveOpenDevice_OnSignOut_DoesNothing`. Both are deleted rather than rewritten — the behaviour
they pinned no longer exists. This also disposes of the round-1 Nit about the first one's comment
repeating S1's factual error. The sign-out null-guard the second one asserted is still worth a case
in some form (a sign-out must write nothing and must not throw through the *sweep*, which is where
the guard now lives); that belongs with S3's sweep-level test, so it is left to `/test-writer`. No
other test file was touched.

**Not done, deliberately.** The four round-1 Nits are out of scope for this pass by instruction. Two
of them (`ReapplyLastCustomPresetAsync`'s synchronous UI-thread reads, and
`SyncMetaDeviceStatePendingApply` missing from `MergeDeviceVendorAsync`'s re-key list) are
pre-existing and want items of their own.

**Verification.** `dotnet build ./src/ProoveGaming.App/ProoveGaming.App.csproj -c Release` — 0
errors, 8 warnings, all pre-existing. `dotnet test
./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj --configuration Release` — **1732
passed, 0 failed** (1734 before this pass, minus the two deleted cases). Run in place rather than
through `--artifacts-path`, which is why the two environmental `DatePickerResourceKeyTests` failures
the round-1 review saw do not appear here.

## Test notes

> **2026-09-10 — two of the cases below are gone.** The `/coder` fix pass for review round 1 S1
> deleted `RedriveOpenDeviceForOwnerChange`, so
> `OpenKeyboardPage_WhenTheOwnerChangesUnderIt_IsRedrivenWithTheNewOwnersPresetAndBoard` and
> `RedriveOpenDevice_OnSignOut_DoesNothing` went with it — the behaviour they pinned is not
> reachable in the app. `DeviceDetailsPulledPresetApplyTests` is 18 cases, not 20 (Round 2 below
> takes it to 19). Everything else below still stands. See Implementation notes → Round 2.

### Round 1 — 2026-09-10 — the Test strategy's list

Dated 2026-09-10, written by `/test-writer`. The rest of the Test strategy's list, on top of the ONE
reproducing case the coder wrote. **15 tests added, no production code touched.**

`tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs` (+8) — the new device-state
predicate: a genuine owner-to-owner switch re-applies, the first owner of a session does not, an
unchanged owner does not, a sign-out marks nothing. Plus the Risks section's "reusing the wrong
predicate", pinned by driving `ShouldResetUnconfiguredLighting` and `ShouldReapplyDeviceState` from
DIFFERENT field values so the two answer differently in both directions — lit-for-A / applied-for-
nobody, and its mirror. And what the sweep writes: `MarkDeviceStatePendingApply` marks each connected
type with the applied preset's local id, marks nothing for an owner with no applied preset, and marks
the incoming owner only.

`tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` (+7) — a key remap and a
key→macro binding survive the switch as the NEW owner's; switching back to A restores A's records;
an incoming owner with no applied preset does not inherit the previous owner's (the case that needed
no code — pinned, not trusted to the reading); exactly one board write per open with the second open
silent; the already-open-page re-drive through the `OwnerChangeRedriveTask` seam; and the sign-out
null guard (no task, no write, no throw).

Two harness additions, both opt-in and inert for the existing cases: `FakeHid.KeyMappingWrites`
records the key-record writes, and `FakeHid.KeyMatrix` supplies (and then holds) the board's live key
matrix. The matrix matters — the slot-keyed push is a DIFF against it, so without a baseline the
remap and macro cases could only observe what the new owner writes, never that the previous owner's
record came OFF the board.

**Confirmed non-vacuous.** With the marking step removed from the tests' `SwitchOwnerTo` helper —
the pre-fix behaviour, where nothing set the mark on an owner change — 5 of the 7 new device cases
fail. The two that do not are the ones that should not: the never-configured-owner case is cleared by
`EnsureDefaultPresetAppliedAsync` and never needed the mark, and the sign-out case asserts a guard.

**Verification.** `dotnet test ./src/ProveGaming.slnx --configuration Release`: App 1732 passed,
Infrastructure 1082, Installer 8. The two `DatePickerResourceKeyTests` failures are environmental —
this machine builds with `--artifacts-path` (the user is running the App and the DeviceHost out of
the repo's own output folders), and those two walk up from `AppContext.BaseDirectory` looking for
`src/ProoveGaming.App/Resources/`. They fail on path resolution alone, unrelated to this change.

**No product bug found.** Every case the plan named behaves as the plan says it should.

### Round 2 — 2026-09-10 — closing review round 1's Tests block

Dated 2026-09-10, written by `/test-writer` after the `/coder` fix pass. **5 tests added, no
production code touched.** Both open items in review round 1's Tests block are closed; the third
("if S1 is resolved by option B or C…") is moot — S1 was resolved by option A, so the re-drive and
the S2 scenario it describes no longer exist.

`tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs` (+4) — the first cases in this file
that drive **`RestoreConnectedDevicesForNewOwner` itself** rather than its parts, which is what
review round 1's S3 asked for and what the `internal` sweep plus the `OwnerSweepState` accessor were
made for. A real `HomeViewModel` over in-memory storage with HID faked at the transport boundary
(the `HomeViewModelSettingsPersistTriggerTests` construction), a connected keyboard card, and the
three "who was the sweep last driven for" fields seeded through the accessor. The connected card is
deliberately `SupportsRgb = false`: `ReapplyLastCustomPresetAsync` skips keyboards and
`ReapplyPersistedRgbAsync` needs RGB, so the sweep's trailing per-card restore is inert and the
pending-apply mark is the only thing these cases can be reading. The marks are read after
`store.DrainStorageQueueAsync()` — they go through `EnqueueStorageWrite`, so a synchronous read
would be reading a queue that has not run.

- `Sweep_WhenOnlyTheDeviceStateFieldSaysSwitch_WritesTheMark` — **the important one, S3 (a).**
  `_lastLitOwner` and `_lastAppliedOwner` forced APART: lit for the incoming owner already, applied
  for the previous one still. The lighting question answers "not a switch", the device-state question
  answers "a switch", and the mark must be written — so the sweep must have asked
  `ShouldReapplyDeviceState` about `_lastAppliedOwner`. This is the Risks section's "reusing the
  wrong predicate", and it is the first test that can catch the sweep feeding the right predicate the
  WRONG field.
- `Sweep_WhenOnlyTheLightingFieldSaysSwitch_WritesNoMark` — the mirror, where reusing `_lastLitOwner`
  would SPEND a mark nothing justifies. An applied preset is seeded, so "no mark" cannot pass for the
  trivial reason that there was nothing to name.
- `Sweep_SkipsACardWhoseSettingsIdentityNeverResolved` — S3 (c). An applied row is seeded in the
  `(0, 0, "")` fallback bucket (a real state — `docs/plans/fix-settings-bucket-identity`), and the
  card whose settings identity never resolved must not be marked, while its resolved sibling is.
- `Sweep_OnSignOut_WritesNoMarkAndKeepsTheDepartingOwnerInTheDeviceStateField` — S3 (d), and the
  sign-out guard the deleted `RedriveOpenDevice_OnSignOut_DoesNothing` used to assert. The guard now
  lives in the sweep, so it is asserted there: owner → null writes no mark and does not throw
  (`OnOwnerChanged` still runs for a sign-out), `_lastRestoredOwner` goes null so the next sign-in
  reads as a change, and `_lastAppliedOwner` keeps naming the DEPARTING owner — signing out does not
  take that owner's configuration off the board, and clearing it would make the next arrival read as
  the first owner of the session and skip the re-apply the switch exists for.

`tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` (+1) —
`KeyboardPage_OpenedByAGuestAfterAnAccountSwitch_SpendsOnlyTheGuestsMark`, the second open item in
the Tests block. Account A → guest, with the guest holding an applied preset of its own (a Rapid Snap
pair, not an empty set, so "the board wears the guest's set" cannot pass by the board simply having
been cleared). It asserts the guest's open writes the guest's set, spends the GUEST's mark, and
leaves A's mark standing — A carries one across the switch purely so the reviewer's reason the
guard removal is safe ("`sync_meta` is keyed by `(owner_kind, owner_id, key)`, so a guest can only
ever see a guest-keyed row") is an assertion rather than an argument. A's live rows, preset snapshot
and applied row are asserted untouched as in the sibling account-switch cases.

Harness: three existing helpers in that file gained an optional `kind` parameter defaulting to
`User` (`SeedConfiguredKeyboardPreset`, `SetPendingApplyMark`/`PendingApplyMark`, `SwitchOwnerTo`),
so the guest case seeds and reads under `OwnerKindGuest` through the same one construction site.
Every existing case is unchanged by construction — the default is what they were passing implicitly.

**Confirmed non-vacuous — each new case was run against a reverted production behaviour:**

| Reverted behaviour | Failing case(s) |
| --- | --- |
| `ShouldReapplyDeviceState(owner, _lastAppliedOwner)` → `_lastLitOwner` | both `Sweep_WhenOnly…` cases (2 failed / 26 passed — every predicate-only case in the file still passed, which is exactly S3's point) |
| `if (card.SettingsVendorId <= 0) continue;` deleted | `Sweep_SkipsACardWhoseSettingsIdentityNeverResolved` |
| `_lastLitOwner` / `_lastAppliedOwner` assigned ABOVE `if (!restore) return;` | `Sweep_OnSignOut_…` |
| the guest short-circuit put back into `ConsumePendingApplyMarkAsync` | `KeyboardPage_OpenedByAGuest…` (the only failure in its file — the one line of production code round 1 changed in `Presets.cs` now has a test) |

Every revert was undone and both production files verified byte-for-byte identical to their
pre-experiment state.

**Verification.** Run in place, not through `--artifacts-path` (the user is running the App and the
DeviceHost out of the repo's own output folders, so a full in-place solution build fails on file
locks and a redirected artifacts path breaks `DatePickerResourceKeyTests` on path resolution):
`dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj --configuration Release` —
**1737 passed, 0 failed** (1732 + 5). `dotnet test
./tests/ProoveGaming.Infrastructure.Tests/ProoveGaming.Infrastructure.Tests.csproj --configuration
Release` — **1082 passed, 0 failed**. No `DatePickerResourceKeyTests` failure in either.

**No product bug found in this round either.** The sweep feeds each decision its own field, the
sign-out guard holds, the `SettingsVendorId <= 0` skip holds, and a guest consumes its own mark and
only its own.

## Review findings

### Round 1 — 2026-09-10 — Ship: yes

Reviewed: the uncommitted working-tree delta on `features/PG-662-investigation` over `d3bcc24`
(`git diff`, no staged and no untracked files) — `HomeViewModel.cs`, `DeviceDetailsViewModel.cs`,
`DeviceDetailsViewModel.Presets.cs`, the two test files and the four doc files. The earlier commits
on the branch are already-reviewed Extended Keys work and were out of scope by instruction.

Verified independently: `dotnet test ./src/ProveGaming.slnx --configuration Release`
(`--artifacts-path` into a scratch folder, because the App and the DeviceHost are running out of the
repo's own output folders) — App **1732 passed / 2 failed**, Infrastructure **1082**, Installer
**8**. The two failures are `DatePickerResourceKeyTests.EveryKey_IsDefinedInTheResourceDictionary`
(en/uk), which walk up from `AppContext.BaseDirectory` looking for `src/ProoveGaming.App/Resources/`
and cannot find it under a redirected artifacts path. Environmental, exactly as the Test notes say,
and unrelated to this change.

#### Plan conformance

- **Implemented as planned:** step 1 (`_lastAppliedOwner` + `ShouldReapplyDeviceState` as
  `ShouldResetUnconfiguredLighting`'s twin; the per-card marking loop in
  `RestoreConnectedDevicesForNewOwner`); step 2 (no code needed — verified by reading and pinned by
  `KeyboardPage_OpenedByAnOwnerWithNoAppliedPreset_ClearsThePreviousOwnersRecords`); step 4 (the
  mark's value contract, its identity check, the consume-and-clear delegate and the mouse path are
  otherwise untouched). `LocalStorageService.cs` is correctly left unchanged — the loop is one point
  read per connected card on a pool thread, so the bulk read the plan held in reserve is not needed.
- **Deviates from plan (recorded in Implementation notes):** step 3 — the already-open-page re-drive
  is `DeviceDetailsViewModel.RedriveOpenDeviceForOwnerChange` (`DeviceDetailsViewModel.cs:803`)
  called from the sweep, not code inside `OnOwnerChanged`. The *ordering* argument for the move is
  sound (see "What's done well"); the *reachability* premise it rests on is not — see **S1**.
- **Amendment outside the plan's "nothing else changes" (recorded):** the guest short-circuit is
  gone from `ConsumePendingApplyMarkAsync` (`DeviceDetailsViewModel.Presets.cs:1959`). Checked and
  safe — see "What's done well".
- **Acceptance criteria:** 1, 2, 3, 5, 6, 7, 8 met (1/2/3/5/7 pinned by new tests; 6 by the
  predicate; 8 by the untouched mouse suite). **Criterion 4 is not delivered in the shipping app**
  — see **S1**. Criterion 9 verified above modulo the two environmental failures.
- **Accepted limitation (per plan):** the off-page keyboard apply stays out of scope, so between the
  switch and the first page open the board still *behaves* as the previous account configured it.
  Residual risk: a user who switches accounts and starts typing before opening the page gets the
  other account's remaps and extended keys. Unchanged by this round; the follow-up is Open question 2.

#### Code

- [x] **S1. The already-open-page re-drive never runs in the shipping app, and the comment that
  justifies it states the opposite of what the code does** — `DeviceDetailsViewModel.cs:788-813` —
  Should-fix
  **Problem:** `RedriveOpenDeviceForOwnerChange` opens with `if (Device is not { } open) return;`,
  and its comment asserts *"A sign-out does NOT leave the details page (SignOutToFreshSignIn goes
  straight to Welcome; only PerformExitDeviceDetails clears Device)"*. That is no longer true.
  `MainWindow.axaml.cs:460-475` — `ClearSignedInState()`, called by **both** sign-out paths
  (`SignOutToFreshSignIn:431` and `OnLogoutRequested:454`) — has `_deviceDetailsViewModel
  .ClearDeviceForExit()` as its **first** statement (line 468), which sets `Device = null`
  (`DeviceDetailsViewModel.cs:1427`), and its own comment says it was added for precisely this
  reason. Leaving the details page for Home also calls `ClearDeviceForExit()`
  (`MainWindow.axaml.cs:936`), and the guest→sign-up entry point (`OnGuestRequested:828`) is raised
  from the **HomeView** guest menu (`HomeView.axaml.cs:169`), so the user is on Home — `Device` is
  null there too. Net: at every owner transition to a non-null owner, `Device` is already null and
  the re-drive returns at its first line.
  **Why it matters:** plan step 3 and **acceptance criterion 4** ("switched while the Cyrex page is
  open → … without navigating away and back") are not delivered — the page is closed by the
  sign-out, so the user *does* navigate back, and the mark alone carries the fix. **QA step 8 will
  appear to pass for the wrong reason**: the tester signs out (page closes), signs in, re-opens the
  page, and the ordinary mark consumption writes the board — which proves nothing about the
  re-drive. The reproducing test passes because it calls `vm.RedriveOpenDeviceForOwnerChange()`
  directly, a path the app never takes. The one flow that *does* reach it is guest-session expiry
  (`OnSessionExpired:620` is the only owner-losing path that does **not** call `ClearDeviceForExit`)
  followed by "Continue as guest" — and there the new guest has no applied preset (`ClearData` wiped
  it), so the re-drive's only effect is an `EnsureDefaultPresetAppliedAsync` default push, i.e. a
  board write on guest-expiry that did not happen before this change.
  **Solution options:**
  - Option A (smallest, honest): delete `RedriveOpenDeviceForOwnerChange` and its call site, correct
    acceptance criterion 4 and QA step 8 to say a sign-out closes the details page so the mark is
    what carries the switch, and drop the re-drive test. Nothing is lost — the marking half already
    covers every reachable flow.
  - Option B (keep the mechanism): leave the code as defence-in-depth for a future flow that keeps
    the page open, but fix the comment to say it is currently unreachable and why, and correct
    criterion 4 / QA step 8 the same way. Cheapest if the off-page follow-up (Open question 2) is
    expected to change the sign-out flow. If this option is taken, **S2 must be fixed with it.**
  - Option C (make it reachable): stop clearing `Device` on sign-out. **Not recommended** — that
    reopens the exact bug `ClearSignedInState`'s comment records (the next account's first open
    flushed the departing card's still-displayed RGB into the arriving account's rows).

- [x] **S2. `SetDeviceAsync`'s tail has no generation fence, and the re-drive is the first caller
  for which "stale" means the SAME device object** — `DeviceDetailsViewModel.cs:1349-1381` —
  Should-fix
  **Problem:** the last `if (Stale()) return;` sits after `PopulateKeyLayout()` (line 1351).
  Everything below it — `LoadActuationFromDeviceAsync`, `LoadExtendedKeysFromDeviceAsync`,
  `LoadPersistedDeviceStateAsync(device)`, `EnsureAppPreferencesLoadedAsync`,
  `EnsureDefaultPresetAppliedAsync`, `RecomputeUnsavedPresetChangesAsync`, `RefreshFromDeviceAsync`
  — runs with no `Stale()` check between the steps. Only `MaterializePulledPresetAsync(device,
  generation)` fences itself (line 1917). That was safe until now because "stale" always meant *a
  different device was opened*, which `LoadPersistedDeviceStateAsync`'s `ReferenceEquals(Device,
  device)` (line 543) and `EnsureDefaultPresetAppliedAsync`'s `Device is null` (line 1435) both
  catch. The re-drive re-opens the **same** `DeviceCardViewModel` instance, so both of those checks
  pass on a chain that the generation bump has already invalidated.
  **Failure scenario:** an open is still in its tail when the owner changes and the re-drive starts a
  second chain over the same card. The stale chain carries on into `LoadPersistedDeviceStateAsync` →
  `AssertActiveDpiStageAsync` (line 841) and `EnsureDefaultPresetAppliedAsync` under the **new**
  owner, concurrently with the fresh chain: two `SetActiveDpiStageAsync` writes (on TAIPAN a rate/
  stage MsConfig patch is a flash erase+write — the very thing `AssertActiveDpiStageAsync` exists to
  deduplicate), and on a keyboard two overlapping `RefreshFromDeviceAsync` probes of up to 128
  gRPC→HID round-trips each against one board. `EnsureDefaultPresetAppliedAsync` also reads the
  shared `Presets` collection, which the fresh chain is concurrently clearing and repopulating.
  Because of **S1** the practical exposure today is confined to the guest-expiry path — which is why
  this is a Should-fix and not a Blocker — but it becomes a live correctness risk the moment the
  re-drive is made reachable (S1 option B or C).
  **Fix:** add `if (Stale()) return;` between the tail's `Quietly` steps — at minimum immediately
  before `LoadPersistedDeviceStateAsync` and before `EnsureDefaultPresetAppliedAsync` — or thread
  `generation` into those two the way `MaterializePulledPresetAsync` already takes it, and check it
  there as it does.

- [x] **S3. Nothing tests the sweep itself, so the two things the deviation turns on are unpinned**
  — `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs:148-292`,
  `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs:390-397` — Should-fix
  **Problem / why it matters:** every new case exercises a *part* in isolation.
  `ShouldReapplyDeviceState` and `MarkDeviceStatePendingApply` are called as free functions;
  `SwitchOwnerTo` calls `MarkDeviceStatePendingApply` **synchronously**, not through
  `EnqueueStorageWrite`; and the re-drive case calls `vm.RedriveOpenDeviceForOwnerChange()` directly.
  `RestoreConnectedDevicesForNewOwner` — the method the change actually edits — is never driven. So
  four things are untested: (a) that the sweep passes `_lastAppliedOwner` and not `_lastLitOwner`,
  which is the plan's own named Risk and is invisible today because the two fields are assigned on
  adjacent lines (`HomeViewModel.cs:1765-1766`) — `TheDeviceStateDecisionIsReadOffItsOwnFieldNot
  TheLightingOne` proves the two *predicates* differ, which cannot catch the sweep feeding the wrong
  *field* to the right predicate; (b) that the marks are enqueued on the storage executor **before**
  the re-drive is called, which is the entire ordering argument of the deviation; (c) the
  `card.SettingsVendorId <= 0` skip; (d) that a sign-out through the real sweep leaves
  `_lastAppliedOwner` intact while nulling `_lastRestoredOwner`.
  **Fix:** one sweep-level case in `HomeViewModelOwnerRestoreTests` — the class already has a "What
  the sweep actually SENDS" section that drives the real method — with `_lastLitOwner` and
  `_lastAppliedOwner` forced to *different* owners, asserting the mark after
  `store.DrainStorageQueueAsync()`. That single test closes (a), (b) and (d) at once.

#### Tests

- [ ] Sweep-level coverage of `RestoreConnectedDevicesForNewOwner` — see **S3** for the four gaps
  and the one test that closes most of them — `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs`
- [ ] The guest side of the guest-guard removal is asserted only as a predicate
  (`ReappliesDeviceStateWhenOneAccountReplacesAnother`). No device-level case runs
  `ConsumePendingApplyMarkAsync` with a **guest** owner, so the one line of production code this
  round changed in `Presets.cs` has no test that fails if it is reverted. Add a guest→account or
  account→guest variant of `KeyboardPage_OpenedAfterAnAccountSwitch_WritesTheNewOwnersSetAnd
  LeavesTheOthersRecords` — `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`
- [ ] If **S1** is resolved by option B or C, the re-drive needs a case where a *second* owner change
  lands while the first re-drive's chain is still in its tail, asserting no duplicate
  `SetActiveDpiStageAsync` / preset push — the scenario **S2** describes —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`

#### Nits

- The factual error in **S1** is repeated verbatim in the test comment on
  `OpenKeyboardPage_WhenTheOwnerChangesUnderIt_IsRedrivenWithTheNewOwnersPresetAndBoard` ("a
  sign-out goes straight to Welcome without clearing Device") — fix both together, or the next
  reader inherits the same wrong model twice —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs:1029`.
- A mark written for an owner who never opens that device's page is never cleared: it stays in
  `sync_meta` until that owner next opens the page. Harmless (the identity check makes a stale mark
  read as stale, and a guest's rows die with `ClearData`) and bounded at one row per owner per
  device type, but worth knowing it is by design rather than by accident.
- Checked and **pre-existing, not caused by this change**, but it qualifies the Review focus item
  "no storage query on the UI thread in the sweep": the new marking loop is clean, yet the sweep's
  older half is not. `ReapplyOnConnect` → `ReapplyOnConnectAsync` → `ReapplyLastCustomPresetAsync`
  (`DeviceDetailsViewModel.Presets.cs:806-812`) runs `GetAppliedPreset` and `GetPresets`
  synchronously, before its first `await`, i.e. on the UI thread the sweep was called from. Out of
  scope here; worth its own item.
- `SyncMetaDeviceStatePendingApply` is absent from `MergeDeviceVendorAsync`'s `deviceStateKeys`
  re-key list (`LocalStorageService.cs:1308-1315`), so a mark written under a pre-merge vendor id is
  stranded. Pre-existing, and this change does not widen it — the sweep writes under
  `SettingsVendorId`, which is already the canonical settings identity the consumer reads.

#### What's done well

- **The FIFO ordering argument holds, and is in fact stronger than claimed.** `EnqueueStorageWrite`
  → `RunOnStorageAsync` appends to `_executorTail` synchronously under `_executorGate`
  (`LocalStorageService.cs:146-156`), so the marks are on the queue before
  `RedriveOpenDeviceForOwnerChange` is even called; the consume
  (`ConsumePendingApplyMarkAsync` → `RunOnStorageAsync<int?>`) is enqueued many awaits later. The
  guarantee needed is not "FIFO among concurrent enqueues" but simply "enqueued first", which is
  unconditional here. The second reason for the move — that `OnOwnerChanged` must also run for a
  sign-out, which marks nothing — is correct and matches the sweep's structure.
- **Removing the guest short-circuit widens nothing.** The mark lives in `sync_meta` keyed by
  `(owner_kind, owner_id, key)` (`LocalStorageService.cs:2952`, `3068`), and both the read and the
  clear pass `owner.Kind`/`owner.Id`, so a guest can only ever see a guest-keyed row and a user only
  a user-keyed one. `MigrateGuestToUserCoreAsync` (`LocalStorageService.cs:5410+`) deliberately does
  not move `sync_meta` rows, so even the register-from-guest path cannot carry a guest mark into an
  account. The identity check against `GetAppliedPreset` is a second, independent gate. The
  amendment was necessary and is correctly scoped.
- **Null-safety on sign-out is clean, and belt-and-braces.** Owner → null gives `restore == false`,
  so the sweep returns (`HomeViewModel.cs:1763`) before both the marking loop and the re-drive;
  `_lastAppliedOwner` is deliberately left holding the departing owner, which is what makes the next
  arrival read as a switch; `owner is { } incoming` keeps the non-nullable `SettingsOwner` parameter
  safe; and `RedriveOpenDeviceForOwnerChange` carries its own null-owner guard anyway. No mark is
  written, nothing throws, and no stale mark is created — the Review focus item is met.
- **Flash wear on a same-account cycle is genuinely zero.** Sign-out leaves `_lastAppliedOwner = A`,
  so signing back in as A gives `ShouldReapplyDeviceState(A, A) == false` → no mark → no
  materialization, and `EnsureDefaultPresetAppliedAsync` early-returns on A's existing applied row.
  Acceptance criterion 6 holds by construction, not by luck.
- The queued delegate captures `incoming` and `(vid, pid, key)` into locals rather than closing over
  the card or re-reading the provider, so a mark still lands under the right owner if the owner moves
  again before the item runs. And it keys off `SettingsVendorId`, not `VendorId` — matching what the
  consumer reads, which is exactly the merged-vendor trap this repo has been bitten by before.
- **Test harness design.** `FakeHid.KeyMatrix` as a *mutable* baseline is the right call: the
  slot-keyed push is a diff, so without a board that starts holding the previous owner's record,
  "A's record came OFF the board" would be unobservable and the remap/macro cases would have passed
  for the wrong reason. `Assert.Equal(FactoryRecord(SlotQ), hid.KeyMatrix[SlotQ])` in the
  macro-binding case is a real assertion about the hardware, not about the tables.
- **Documentation discipline.** `docs/CONTEXT.md` gained **Owner-change sweep** as a new term and
  **Pending device apply** was corrected to name both writers; both neighbouring plans got dated
  notes appended outside their review journals; no journal was rewritten. This is what the repo's
  documentation rules ask for.

#### Verdict

Ship: yes — the reported bug is fixed, the mechanism is the right one, and the thread-safety,
null-safety and flash-wear questions the Review focus raised all check out. The three Should-fixes
are about a branch that never executes, a fence that only matters once it does, and tests that pin
the parts but not the whole; none of them makes the shipped behaviour wrong.
First: **S1** (decide whether the re-drive stays — and either way correct the comment, criterion 4
and QA step 8 so the manual test stops proving something it does not test), then **S2** if S1 keeps
the re-drive, then **S3**'s single sweep-level test.
