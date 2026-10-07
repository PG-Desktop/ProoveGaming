# Plan: the board must end every page open wearing the signed-in owner's extended keys

Date: 2026-09-11. Owner: architect. Supersedes the mechanism half of
[`../fix-account-switch-device-state/fix-account-switch-device-state-plan.md`](../fix-account-switch-device-state/fix-account-switch-device-state-plan.md),
whose pending-apply marking does not fire in the case the user actually hits. Related:
[`../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md`](../add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md)
(the mark itself, which stays and is still needed for the pull case).

> **Status note (2026-09-24 — self-heal is dormant).** Extended Keys is switched off for every
> shipping model until the implementation is finished: `"SupportsExtendedKeys"` is `false` for Cyrex
> and Aress in `src/ProoveGaming.DeviceHost/appsettings.json`. `ApplyStoredExtendedKeysToDeviceAsync`
> returns on that flag before it reads storage, and the probe that fills `_extendedKeysOnDevice`
> never runs either, so **the self-heal described below performs no write** — including the
> "overwrites a vendor-app configuration on the first connect" trade-off this plan accepts, which
> is the reason the flag was flipped rather than the nav entry alone (PR #139): with the section
> hidden the user could no longer see, recreate or undo what the heal erased. The acceptance
> criteria and QA steps below are suspended until the flag goes back to `true`.

## Goal

The previous plan tried to keep the board in step with the signed-in account by tracking every
transition that could put someone else's configuration on it. It missed the commonest one, and the
user reproduced the original bug again on 2026-09-11: signing into a second account still shows the
first account's Toggle. This plan stops tracking transitions and makes the invariant self-healing
instead — at the end of a device page open, if the board's managed extended-key records differ from
the signed-in owner's own rows, the owner's rows are written to the board. That is true after an
account switch, after an app restart, after a power cycle, and even after the vendor app changed the
board, without anyone having to enumerate those cases.

## Root cause (evidence, 2026-09-11)

Three facts, gathered from the live database, the DeviceHost log and the process table:

- **No pending-apply mark was ever written.** `sync_meta` holds only `devicestate.*.dirty` rows for
  both accounts — no `.pending_apply`. Both accounts do have an applied preset for the Cyrex, so the
  helper's "no applied preset → mark nothing" branch is not the explanation.
- **The app was restarted between the two accounts.** Account A's write is logged at 10:19:05; the
  running App process started at 10:19:57; account B's page open probed the board at 10:20:09 and
  issued **no write at all** — only `read extended keys: 1 managed`.
- **Therefore the sweep's predicate answered "not a switch".** `_lastAppliedOwner` is an in-memory
  field on the `HomeViewModel` singleton. A restart resets it to null, the restored session is the
  process's *first* owner, and `ShouldReapplyDeviceState(B, null)` is false by the rule copied from
  the lighting sweep.

**Why that rule is wrong here.** The lighting predicate's justification is "the first owner of a
session has no predecessor whose colours are on the board", which holds because lighting is pushed
again on every connect regardless. Extended keys, key remaps, macro bindings and actuation live in
the keyboard's **flash**: they survive a power cycle and an app restart, so the first owner of a
session very much can find the previous owner's configuration on the board. The predicate's shape was
transferred without its justification. No review or test caught it because every test drives the
sweep inside one process, where the field is never null.

Also fixed here, from the same family and found in the branch's final review: **the failure branch of
`WriteExtendedKeysAsync` calls `PersistExtendedKeysState()` regardless of its `persist` parameter.**
The only `persist: false` caller is the preset/self-heal push, whose source of truth is the owner's
rows, not the board. So a failed push re-probes the board and copies **the previous owner's records
into the arriving owner's live rows**, and marks their preset dirty — a Save Preset away from
syncing another account's configuration into this account's cloud.

## Scope

In:

- A self-heal step at the end of the device-open chain: when the owner's live extended-key rows and
  the board's managed records differ, write the owner's rows. Reuses
  `ApplyStoredExtendedKeysToDeviceAsync`, which **already** performs exactly this comparison and
  returns without writing when they match.
- The `persist: false` fix in the failure branch.
- Tests: the reproducing test first (it must fail on today's code), then the rest.
- Keep the pending-apply mark and the owner-change sweep exactly as they are — the mark still carries
  the pull case, where the owner's live rows are legitimately empty and the content is in the preset
  snapshot. The self-heal runs after materialization, so the two never fight.

  > **2026-09-12.** Half of this is superseded: the mark stays and still carries the pull case, but
  > the owner-change sweep no longer WRITES it
  > (`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`, review round 4, M1 — the mark's
  > consumers overwrite the live rows with the preset snapshot, which destroys the incoming
  > account's unsaved state on a switch). The self-heal is now the whole of what carries extended
  > keys across a switch. The limitation named just below — remaps, key→macro bindings and
  > actuation — is correspondingly wider than it was: it is now the state after every account
  > switch, not only after a restart.

Out, and named as the immediate follow-up rather than silently ignored:

- **The same root cause affects key remaps, key→macro bindings and actuation.** They are flash-backed
  too, and after an account switch the board keeps the previous owner's. They are not fixed here
  because each needs its own ordering work and its own semantics: the key push diffs against the
  board matrix, which is read by `RefreshFromDeviceAsync` at the **end** of the open chain (moving it
  is exactly the kind of unexamined re-ordering that caused the last two defects), and actuation's
  empty set means "no opinion", not "reset". Follow-up plan, with the matrix read's position as its
  first question.
- Removing the pending-apply mark or the owner-change sweep.
- Any protocol, preset-schema or API change.

## Options considered

### A. Persist the last-applied owner per device type

Store "whose configuration is on this board" in `sync_meta` so a restart does not lose it, and mark
pending-apply whenever the incoming owner differs.

- Pro: keeps the existing mechanism; small.
- Con: still enumerates transitions, and the stored value can go stale for reasons nothing observes —
  the vendor app writing the board, a firmware reset, a board moved between machines. It answers
  "did we change owner" when the question that matters is "does the board match this owner".

### B. Self-heal by comparing the board against the owner's rows at page open (recommended)

- Pro: answers the question that actually matters, so it is correct for every cause at once, including
  ones nobody enumerated — restart, power cycle, second machine, vendor app.
- Pro: the comparison and the "skip when equal" guard already exist in
  `ApplyStoredExtendedKeysToDeviceAsync`; the change is where it is called from, not new logic.
- Pro: no persisted state to go stale.
- Con: one extra read-compare per page open (the probe already happened, so the compare is in-memory)
  and a write whenever they genuinely differ — including the first open after the vendor app
  configured the board, which will be overwritten. That matches the documented policy for every other
  preset-driven push on this family.

### C. Always push the owner's set at page open

- Con: a flash commit on every open regardless of need. Rejected for wear, and it would clobber a
  board the user is mid-way through configuring elsewhere.

## Recommended approach

Option B, three small parts.

1. **Self-heal call.** In `SetDeviceAsync`'s follow-up chain, after
   `Quietly(EnsureDefaultPresetAppliedAsync())` and before
   `Quietly(RecomputeUnsavedPresetChangesAsync())`, add
   `await Quietly(ApplyStoredExtendedKeysToDeviceAsync(), …)`. That position is load-bearing and the
   call site must say so: it must run **after** the extended-keys probe (which sets
   `_extendedKeysSupported` and `_extendedKeysOnDevice`, the two things the compare needs) and
   **after** both one-shot pushes, so that when materialization or the default-preset push already
   put the owner's set on the board the compare finds them equal and writes nothing; and **before**
   the dirty recompute, so Save Preset is judged against the settled state.
2. **Honour `persist`.** In `WriteExtendedKeysAsync`'s failure branch, only call
   `PersistExtendedKeysState()` when `persist` is true. The re-probe stays unconditional — the UI
   must always show what the board really holds — but a preset-driven push that failed must not
   write the board's records into the owner's rows.
3. **Nothing else changes.** The mark, the sweep, the predicates and the mouse path stay as they are.

*(As built, part 1 also carries a `!Stale() && !ExtendedKeysWriteFailed` guard at the call site, and
the second half of that guard changes what happens to a board that refuses the write. Both are
recorded under Implementation notes → Round 1, and the retry behaviour is in Risks.)*

## File-level change list

- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — edit: the self-heal
  call in the open chain, with a comment recording why its position is load-bearing (same discipline
  the neighbouring `MaterializePulledPresetAsync` call site already carries).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs` — edit:
  `WriteExtendedKeysAsync`'s failure branch honours `persist`; extend
  `ApplyStoredExtendedKeysToDeviceAsync`'s remarks to say it is now also the self-heal entry point
  and what it guarantees at the end of an open.
- `docs/CONTEXT.md` — edit: **Pending device apply** gains a sentence that it no longer carries the
  account-switch case, which the self-heal covers; add **Extended-keys self-heal**.
- `docs/plans/fix-account-switch-device-state/fix-account-switch-device-state-plan.md` — edit: dated
  note that its marking half did not fire on the app-restart path, with the evidence, pointing here.
  Its Review findings journal stays untouched.
- `docs/plans/add-extended-keys-api-sync/add-extended-keys-api-sync-plan.md` — edit: dated note that
  the mark now covers only the pull case.

Tests (bug fix — the reproducing test lands first):

- `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` — edit: **first**, the
  reproducing test — owner A's records are on the board, owner B (with their own applied preset and
  no extended keys, and **no pending-apply mark**, which is the restart case) opens the page → the
  board must be written with B's set and A's stored rows must be untouched. It must fail today.
  Then: the reverse direction (A returns, A's rows go back on the board); a matching board writes
  nothing; a board already matching after materialization writes nothing (the two mechanisms do not
  double-write); an owner with no rows at all clears a board that has records; a read-only transport
  writes nothing.
- `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — edit: the `persist`
  fix — a failed push with `persist: false` leaves the owner's live rows and the preset's dirty state
  untouched while the UI still re-reads the board; a failed user-initiated write (`persist: true`)
  still persists, as today.

## Acceptance criteria

- [ ] Given account A's Toggle is on the board and **the app is restarted** and signed in as account
  B, whose own preset has no extended keys → B opens the keyboard page → the board is written once
  with B's (empty) set, the Extended Keys list is empty, and A's stored rows and preset snapshot are
  unchanged. *(This is the exact case reported on 2026-09-11 and it fails today.)*
- [ ] Given that state → restart again as A → A opens the page → A's Toggle is back on the board and
  in the list, with no manual preset selection.
- [ ] Given the board already matches the signed-in owner's rows → opening the page issues **no**
  extended-keys write (verifiable in the DeviceHost log: a `read extended keys` line with no
  `WriteAsync` after it).
- [ ] Given a pulled preset whose materialization already put the owner's set on the board → the
  self-heal in the same open writes nothing (exactly one write for that open, not two).
- [ ] Given the signed-in owner has no extended-key rows and the board has records → opening the page
  clears the board.
- [ ] Given a device on a read-only transport → opening the page writes nothing.
- [ ] Given a preset-driven push fails on the device → the owner's live rows and their preset's dirty
  state are unchanged, and the section shows what the board actually holds.
- [ ] Given a user-initiated add/remove that fails on the device → the previous behaviour is
  unchanged (board re-read and persisted, failure banner shown).
- [ ] `dotnet test ./src/ProveGaming.slnx --configuration Release` green.

## Test strategy

Bug fix, so the order is fixed: **the reproducing test is written first and must fail on the current
code.** Its shape matters — it must NOT set a pending-apply mark, because the whole point is that the
restart path has none; a test that seeds a mark would pass today and prove nothing.

The touched behaviour already has tests and they must stay green: `DeviceDetailsPulledPresetApplyTests`
(17 cases, including the owner-switch and materialization fences),
`DeviceDetailsExtendedKeysViewModelTests`, `HomeViewModelOwnerRestoreTests`. The sweep and the mark
are unchanged, so their cases must not need editing — if any of them do, that is a signal the change
went wider than the plan and should be reported rather than accommodated.

Cases the change introduces are listed in the change list above. Show non-vacuity for the reproducing
test and for the `persist` fix by reverting each behaviour and confirming the right test fails, as the
previous two rounds did.

Deliberately not automated: the physical keyboard behaviour (QA), the real HID write (faked at the
transport boundary per repo convention), and the follow-up aspects (remaps, macro bindings, actuation)
which are out of scope by design.

## QA (manual test steps)

Needs one keyboard (Cyrex or Aress) and two accounts, A and B. **Restarting the app between accounts
is the point of this test — do not skip it.**

1. Sign in as **A**, connect the keyboard, open its page → Extended Keys. Add a Toggle on **Q**
   (toggled key W, 100 ms). Save Preset. **Expected:** the row is listed; tapping Q latches
   continuous "w".
2. **Close the app completely.** Start it again and sign in as **B** (if it signs in as A
   automatically, sign out and sign in as B). Open the keyboard's page → Extended Keys.
   **Expected:** the list is EMPTY, and tapping Q types a normal "q".
   *(This is the exact step that failed before: A's Toggle was listed here.)*
3. As **B**, add a Rapid Snap pair A ↔ D. Save Preset. **Expected:** listed and working.
4. **Close the app again**, start it, sign in as **A**, open the page. **Expected:** A's Toggle is
   back and works; B's Rapid Snap pair is gone and A ↔ D behave normally.
5. Without closing anything, leave the keyboard page and open it again. **Expected:** the list is
   unchanged and there is no pause or flicker — nothing is rewritten when it already matches.
6. Switch accounts **without** restarting (sign out, sign in as B), open the page. **Expected:** B's
   setup, same as step 3 left it.
7. Unplug the keyboard, plug it back in, open the page. **Expected:** the signed-in account's setup
   is on the board, even though the board was power-cycled.
8. Optional, vendor cross-check: with our app closed, use Keyborder-v3 to add an extended key, close
   it, then open our app and the keyboard page. **Expected:** our app puts the signed-in account's
   setup back — the vendor-added key is gone. This is intended.
9. Regression: as A, add a Toggle and then delete it with the trash icon. **Expected:** both actions
   take effect immediately and the list matches the board after each.

## Review focus

- `UI-thread affinity` — the self-heal runs in the open chain behind the existing generation fences;
  no storage read on the UI thread.
- `async/await` — wrapped in `Quietly` like its neighbours so a device failure cannot crash the open.
- `null-safety` — the compare must be safe when the probe failed (`_extendedKeysOnDevice` null): that
  must mean "do not write", never "write an empty set".

## Performance & quality

No hot paths. The self-heal adds one in-memory comparison per page open (the probe already ran) and a
device write only when the board genuinely differs, so the steady state costs nothing. The failure-
branch change removes a storage write rather than adding one. No allocations of note.

## Risks

- **A board configured by the vendor app is overwritten on the first page open.** This is the
  documented policy for every preset-driven push on this family, and QA step 8 states it explicitly
  so it is a decision rather than a surprise.
- **`_extendedKeysOnDevice` null (probe failed) must not be read as "board is empty"** — that would
  push the owner's set onto a board whose state is unknown, or clear it. The existing guard returns
  early; the review focus calls it out and a test pins it.
- **The follow-up gap is real and visible.** After this lands, extended keys follow the account but
  remaps, macro bindings and actuation still do not. A user who tests remaps across accounts will hit
  the same symptom. That is named in Scope/out rather than left to be discovered.
- **A board that persistently refuses the write is attempted once per page open, and shows the
  failure banner each time.** Before this change a refusal was final for the rest of the session: the
  one-shot pushes had spent themselves and nothing tried again. The self-heal makes "the board
  disagrees with the owner's rows" the trigger, and a refusing board is permanently such a board, so
  each open offers the set once more. The bound is one attempt per open, not one per second: the
  `!ExtendedKeysWriteFailed` skip at the call site stops a second attempt inside the same open (see
  Implementation notes). The alternative — never retrying — was rejected because a board that
  recovers (a reconnect, a firmware that was briefly busy) would then stay on the wrong account's set
  until the user re-applied a preset by hand.

## Open questions

None blocking. The one product question from the previous plan still stands and is unchanged by this
one: whether a guest should be able to overwrite a board a signed-in account configured.

## Implementation notes

### Round 1 — 2026-09-11 — what the implementation added beyond the three parts

"Recommended approach" says three small parts and "nothing else changes". Two things were added that
it does not describe. Both are deliberate; recording them here is the correction review round 1, S3
asked for.

**1. The self-heal is skipped when a push earlier in the same open already failed.** The call site
reads `if (!ExtendedKeysWriteFailed)`. Without it, an open in which the materialization or the
default-preset push was refused by the board would immediately send the very same set again — the
board has just said no to exactly these records, so the retry fails too, and its failure branch
re-probes the board a second time for no new information. The flag is set by `WriteExtendedKeysAsync`
and cleared by the probe at the top of every open, so it scopes the skip to this open only.

**2. It follows from (1) that a refusing board is retried once per page open.** The previous contract,
asserted by `KeyboardPage_DeviceWriteFails_StillClearsTheMarkAndDoesNotRetry`, was stronger: after the
first refusal the App never wrote again, because the pending-apply mark was cleared before the write
and nothing else had an opinion. The self-heal's trigger is not a one-shot mark but the standing
comparison "does the board hold this owner's rows", and a board that refuses keeps failing it — so
each open offers the set once, then stops. That test was renamed to
`KeyboardPage_DeviceWriteFails_ClearsTheMarkAndAttemptsAtMostOncePerOpen` and now pins both halves of
the new contract (one write in the first open, exactly one more in the second), with the supersession
reason written into the test. The clear-before-write ordering it was originally protecting is
unchanged: what it stops is the *materialization* re-running, not the write.

`PresetPush_DoesNotPersistOnSuccess_ButPersistsTheTruthOnFailure` was likewise renamed and inverted
(`…NeitherPersistsOnSuccess_NorImportsTheBoardsRecordsOnFailure`) by part 2 of the plan itself: its
"persist the truth on failure" rationale is sound for a single owner and wrong once the board can be
wearing another account's records.

### Round 2 — 2026-09-11 — review round 1 fix pass

- **S1.** The call site now reads `if (!Stale() && !ExtendedKeysWriteFailed)`. `Stale()` is the
  chain's own local function (generation, device identity, owner identity) and is the same fence the
  device-writing steps above use; nothing narrower was needed.
- **S2.** `ApplyStoredExtendedKeysToDeviceAsync` reads the owner's rows through
  `_localStorage.RunOnStorageAsync(...)` instead of calling `GetDeviceExtendedKeys` inline, so no
  SQLite work and no `SemaphoreSlim.Wait()` happens on the Avalonia dispatcher. Two consequences were
  handled with it: the executor yields `null` once `IsShutDown` is set (that is read as "nothing was
  read", not "no rows"), and the read is now the method's first yield point, so the device, the owner
  and `_extendedKeysOnDevice` are re-taken after it before anything is compared or written — the
  preset push paths reach this method without the chain's `Stale()` fence, and that re-check is what
  covers them.
- **Nit 1 taken along with S2** (it stops being cosmetic once the read is awaited): the unknown-board
  guard (`_extendedKeysOnDevice is null`) now runs *above* the read, so an unprobed board costs no
  executor round-trip.
- Nit 2 (a private `_extendedKeysWriteFailedThisOpen` decoupled from the banner property) was left
  open — out of scope for this pass.

## Test notes

### Round 2 — 2026-09-11 — the device-switch twins (`/test-writer`)

Closes the one open item in Review findings → Round 1 → Tests. **2 tests added to
`tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` (23 → 25 cases), no production
code touched.** App 1743/1743, Infrastructure 1082/1082 in Release.

- `KeyboardPage_DeviceSwitchedBeforeTheSelfHeal_WritesNeitherBoard` — the self-heal twin of
  `KeyboardPage_DeviceSwitchedDuringTheOpen_DoesNotPushThePresetAtTheOtherDevice`, pinning **S1**.
  A Cyrex and an Aress are both already wearing the signed-in owner's own (different) sets, so a
  healthy run writes nothing at all; the switch lands after the Cyrex's chain has settled its board
  baseline and before that chain reaches the self-heal.
- `PresetPush_DeviceSwitchedBetweenTheReadAndTheWrite_DoesNotWriteAtTheOtherBoard` — pins **S2**'s
  post-await re-check, which is what covers the preset-push callers (`…Presets.cs:1055`) that
  `Stale()` does not reach. The push is parked between its storage read and its write, the page is
  switched to the other keyboard in that window, and the Cyrex scope's rows must not reach the
  Aress's flash.

**The seam the review suggested is not sufficient on its own, and that is worth recording.** Review
round 1 proposed covering S1 "with the existing `ProbeEntered`/`ProbeGate` seam: park A's open,
switch to B". Written that way the case is **vacuous in both directions**, because
`LoadExtendedKeysFromDeviceAsync` nulls `_extendedKeysOnDevice` and bumps its load generation
*before* it probes:

- park the **second** device at that probe and it has already erased the baseline the bug reads —
  the self-heal then returns at its `_extendedKeysSupported != true` gate whether or not the fence
  is there;
- park the **first** device there and its own load bails on resume (`Device` has changed), so no
  baseline is ever set and the same gate stops everything.

So the window has to be built out of three co-operating holds, all signal-driven (no sleeps): the
Cyrex's open is parked at its extended-keys probe and **released** so the baseline settles while
`Device` is still the Cyrex; the storage executor is stalled first so the chain parks on its very
next step (`LoadPersistedDeviceStateAsync` → `LoadDpiPresetsAsync`) instead of running on; and the
arriving Aress is held at its **actuation** probe — one step before its own extended-keys load — so
it changes `Device` and nothing else. The same shape drives the S2 case, where the stall is what
keeps the push's queued read from finishing before the switch lands.

Three additive, opt-in harness changes on `FakeHid`, inert for the other 23 cases: `ActuationEntered`
/ `ActuationGate` (the `ProbeEntered`/`ProbeGate` twin one step earlier in the chain, reached only by
a card with `SupportsActuation` — hence `AressCard`), `BoardInfo` (per-board extended-keys state;
with one shared `Info` the fake cannot say which board holds what, so "the write landed on the OTHER
board" is not decidable), and `SeedConfiguredKeyboardPreset` gained defaulted `vendorId`/`productId`
so one owner can have a configured board of each. The two keyboards share a vendor id in real life,
so every "which board" assertion is on the **product** id.

**Non-vacuity, demonstrated by reverting each behaviour and restoring it byte-for-byte:**

- `!Stale() && ` removed from the call site (`DeviceDetailsViewModel.cs:1381`) →
  `KeyboardPage_DeviceSwitchedBeforeTheSelfHeal_WritesNeitherBoard` fails, alone, on 3 of 3 runs. A
  diagnostic assertion on the same revert confirmed the failure is one stray
  `SetExtendedKeys` at the **Aress's** product id carrying the **Aress's** rows — the spurious flash
  commit the review predicted, not an unrelated count change.
- the device/owner clauses removed from the post-await re-check
  (`DeviceDetailsViewModel.ExtendedKeys.cs:1065`) →
  `PresetPush_DeviceSwitchedBetweenTheReadAndTheWrite_DoesNotWriteAtTheOtherBoard` fails, alone, on 3
  of 3 runs, and the same diagnostic confirmed one stray write at the **Aress's** product id carrying
  the **Cyrex** scope's Toggle — another account-scoped set reaching the wrong board's flash.

No product bug was found in this pass, and nothing outside the two new cases changed.

## Review findings

### Round 1 — 2026-09-11 — Ship: yes

Reviewed surface: the uncommitted working-tree delta (7 modified files + this plan), i.e. the whole
of this plan's change. The branch's earlier commits (`dd4b283`..`14be823`) each carry their own
review round in their own plans and are not re-reviewed here.

Note on independence: the plan, the implementation and the tests for this change were produced in
one session by the same author as this review. Every finding below was verified by reading the full
files and, where stated, by running the tests — but a human reviewer should weigh that.

#### Plan conformance

- Implemented as planned: the self-heal call at the stated position in the open chain
  (`DeviceDetailsViewModel.cs:1375`), `persist` honoured in the failure branch
  (`DeviceDetailsViewModel.ExtendedKeys.cs:957`), the reproducing test written first and confirmed
  failing before the fix, and all three doc edits (glossary + dated notes on the two superseded
  plans).
- Deviates from plan: **two behaviours were added that "Recommended approach — three small parts /
  Nothing else changes" does not describe** (see S3): the `!ExtendedKeysWriteFailed` skip at the call
  site, and the contract change that follows from it — a refusing board is now retried once per page
  open, where an existing test asserted "never writes again". Both are defensible and are explained
  in code comments, but the plan text is now stale.
- Missing / partial: none.
- Accepted limitation (per plan): remaps, key→macro bindings and actuation still follow the previous
  owner after a restart. Residual risk: a user who tests remaps across accounts hits the same symptom
  just reported for extended keys, and will reasonably read it as "the fix did not work".

#### Code

- [x] **S1. The self-heal is not fenced against a device switch mid-open** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:1375` — Should-fix
  **Problem / why it matters:** the call carries neither the chain's `Stale()` check nor
  `(device, generation)`, unlike its device-writing neighbour `MaterializePulledPresetAsync(device,
  generation)` on line 1356 — which was given exactly that fence by the previous plan's review round
  1, S1. `ApplyStoredExtendedKeysToDeviceAsync` re-reads `Device` itself, so it can act for a
  different device than the open it belongs to, while the comparison baseline `_extendedKeysOnDevice`
  may still hold the previous device's probe result.
  **Failure scenario:** open keyboard A; before A's chain reaches this step the user opens keyboard B.
  `Device` is now B and B's probe has not yet cleared the baseline, so A's board content is compared
  against B's stored rows. If they differ, B gets an extended-keys write it did not need (a flash
  commit); if they happen to match, B's genuinely needed self-heal is skipped for that open. B's own
  chain corrects either outcome moments later, so the damage is one spurious write or one delayed
  heal — not corruption, which is why this is not a blocker.
  **Fix:** `Stale()` is a local function in scope at the call site —
  `if (!Stale() && !ExtendedKeysWriteFailed)` closes it in one line and matches how the rest of the
  method reasons about staleness.

- [x] **S2. Synchronous storage read on the UI thread, now on every page open** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs:1029` —
  Should-fix
  **Problem / why it matters:** the method reads `_localStorage.GetDeviceExtendedKeys(...)`
  synchronously. The open chain runs on the Avalonia dispatcher, and `LocalStorageService` serialises
  callers behind a `SemaphoreSlim` whose non-async `Wait()` is taken on the calling thread. The
  sibling code in the Presets partial always goes through `RunOnStorageAsync` for this reason, and
  this plan's own **Review focus** says "no storage read on the UI thread". The call is pre-existing
  in the method — but this change is what makes it run on **every** open of an extended-keys keyboard
  rather than only on preset actions.
  **Failure scenario:** a page open landing while the storage executor is busy with a long write (a
  sign-in pull inserting macros, a preset snapshot save) blocks the dispatcher until that write
  finishes — a visible hitch on the exact interaction this feature added.
  **Fix:** `var rows = await _localStorage.RunOnStorageAsync(() => …GetDeviceExtendedKeys(…));`. The
  guards above it already exclude mice and unprobed devices, so the read only happens where needed.

- [x] **S3. The plan no longer describes the code** — this file (Recommended approach, Risks) —
  Should-fix
  **Problem / why it matters:** the implementation added the `!ExtendedKeysWriteFailed` skip and, with
  it, changed a contract an existing test named: a refusing board is retried once per open, where the
  old behaviour was "never again after the first refusal". Risks does not mention repeated attempts
  at all, and Recommended approach says "Nothing else changes". `/reviewer` reviews against the plan,
  so a plan that stops matching sends the next round after the wrong contract — the failure mode this
  repo's documentation rules exist to prevent.
  **Fix:** add an Implementation-notes entry recording both decisions and their reasons, and extend
  Risks with "a board that persistently refuses is written to once per page open and shows the
  failure banner each time".

#### Tests

- [x] **The device-switch-during-open case has no self-heal twin** — the materialization path has
  `KeyboardPage_DeviceSwitchedDuringTheOpen_DoesNotPushThePresetAtTheOtherDevice`, which is what
  caught this class of problem last round; the self-heal added here has no equivalent. Cover it with
  the existing `ProbeEntered` / `ProbeGate` seam: park A's open, switch to B, then assert the write
  that follows targets the device whose rows were read and that nothing lands on the other board.
  File: `tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`. This is the test that
  would fail if S1 turned out worse than analysed.

#### Nits

- The unknown-board guard runs *after* the storage read
  (`DeviceDetailsViewModel.ExtendedKeys.cs:1029-1041`) — moving it above makes the cheap check cheap.
  Cosmetic today; it matters once S2's fix turns the read into an await.
- `ExtendedKeysWriteFailed` drives the failure banner in the view and is now also load-bearing
  control flow at `DeviceDetailsViewModel.cs:1375`. A future change that clears the banner from a UI
  action would silently change when the self-heal runs; a private `_extendedKeysWriteFailedThisOpen`
  would decouple them.

#### What's done well

- The bug-fix ordering was respected for real, not nominally: the reproducing test was written first,
  deliberately **without** a pending-apply mark (a version that seeded one would have passed before
  the fix and proved nothing), and its pre-fix failure was recorded.
- Non-vacuity was demonstrated by reverting each behaviour and naming which tests fell over — and
  when the probe-declined case turned out **not** to pin the guard it appears to test, that was
  written into both the test and the guard's own comment rather than quietly claimed as coverage.
- The two tests whose intent this change superseded were rewritten with the reason stated, not
  deleted and not bent to fit: the single-owner rationale for "persist the truth on failure" is
  recorded alongside why several owners invalidate it.
- The `persist` fix closes a real cross-account data path — a failed preset push importing another
  account's records into this account's rows, one Save Preset away from syncing them to their cloud.

#### Verdict

Ship: yes — no blockers; the reported bug is fixed at the root, proven by a test that failed before
the change, and the suite is green (App 1741, Infrastructure 1082). The three should-fixes are small
and none can corrupt data. First: S1 together with the missing test that goes with it, then S2,
then S3.
