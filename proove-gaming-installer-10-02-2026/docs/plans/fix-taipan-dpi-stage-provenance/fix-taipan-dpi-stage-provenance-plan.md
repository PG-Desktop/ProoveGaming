# Plan: Fix TAIPAN active-DPI-stage provenance (silent DPI change)

Fixes a defect found in PR review on `feat/PG-410-Taiapan-settings-and-other-fixes`.
Companion to [add-taipan-dpi-polling](../add-taipan-dpi-polling/add-taipan-dpi-polling-plan.md),
whose Review findings journal (Round 1 / Round 2) carries the related open items.

## Goal

A TAIPAN polling-rate or lift-off change can silently move the user's DPI to a different stage.
The MsConfig read-modify-write forces byte `0x11` (DpiSwitch) from `TelinkMouseService._liveDpiStage`,
and that cache is currently poisoned by an **inferred** value: `TryReadActiveDpiStageAsync` applies
the "firmware lags one button press" `+1` correction unconditionally and stores the result. The
correction is right when the *firmware* last wrote `0x11` and wrong when *we* did, so after any UI
stage selection the cache overshoots by one and the next unrelated setting write commits it to the
device. This is a re-occurrence of the "polling change jumps DPI" bug the parent branch set out to
fix, reached by a narrower path. The fix makes the active stage a value the host either *knows* or
*asserts* — never one it guesses.

## Scope

**In:**
- Stop inferred stages from reaching `_liveDpiStage` and therefore the device.
- Give the host a trusted active stage on connect by asserting the App's persisted stage.
- Convert `_liveDpiStage` from `byte?` to a `volatile int` with a `-1` sentinel (open Round 1/2 item
  on this exact field, whose contract this plan rewrites).
- Extract and unit-test `PatchMsConfigAsync`'s source-block decision logic (open Round 1/2 item).
- Regression tests that reproduce the defect before the fix.

**Out:**
- Any change to the `0x49` push path itself — it is authoritative and stays as-is.
- BLAZE / `EasenMouseService`. Its active stage lives in `param_0` byte 1 and is written from a
  cached block; it has no equivalent lag inference.
- Correcting the parent plan's stale text (BLAZE UI switching, lift-off, acceptance criteria 3 and
  7). That is documentation drift, tracked in the parent plan's journal; see **Open questions**.
- Proto / gRPC contract changes. The recommended approach deliberately needs none.

## Protocol facts this fix depends on

- **Firmware lag is real.** On a physical DPI-button press the device moves to stage `S_new` and
  writes `0x11 = S_old`. Hardware-observed: with the mouse on stage 0 the block still read stage 6
  (wrapping on the DPI table's count of 7).
- **Our own write is exact.** `TaipanMsConfig.PatchActiveDpiStage` sets `0x11 = S` and the device
  adopts `S`. There is no lag on this path.
- **The `0xD1` write erases the sector**, so every MsConfig write commits *some* value to `0x11`.
  There is no way to write the polling rate while leaving the stage byte alone — which is why an
  untrusted stage cannot simply be "left out" of the write.
- **No live query exists.** `0x49` and every read-form command in `0x44`–`0x4F` go unanswered, so the
  device cannot be asked what stage it is on.
- **Writes are cable-only.** The dongle relays the mouse's pushes but not host→mouse frames, so
  every write below must no-op cleanly over the dongle.

## Why "compare flash against the persisted stage" cannot work

This deserves recording because it is the natural next idea (it was the review's Option B) and it is
**provably insufficient** — two different device states produce identical observable data:

| Situation | flash `0x11` | App persisted | Real stage |
|---|---|---|---|
| We wrote stage `S` from the UI | `S` | `S` | `S` |
| One button press while the app was closed, from `S` | `S` | `S` | `S + 1` |

In the second row the firmware wrote the *previous* stage, which is exactly the value the app had
persisted before it closed. The two cases are indistinguishable from flash plus persistence alone,
so no comparison rule — and no amount of extra persisted provenance — separates them. Only the
`0x49` push and our own writes carry the truth.

## Options considered

### A. Assert the persisted stage on connect — *recommended*
The App already persists the active stage on every real push (`DeviceDetailsViewModel.cs:163`) and
every UI selection (`DeviceDetailsViewModel.Settings.cs:153`). On device attach it writes that value
to the device via the existing `SetActiveDpiStage` RPC. From that moment `_liveDpiStage` is trusted
(set by our own write), the device matches what the UI shows, and the inference is removed from the
write path entirely.

- **Pro:** no proto change; the host never guesses; UI and device provably agree; reuses the
  existing, already-tested write path.
- **Con:** one MsConfig write per device attach (flash erase + write). A physical button press made
  while the app was closed is overridden at the next attach — see **Risks**.

### B. Host-only: never guess for writes, leave `0x11` as read when untrusted
Drop the `_liveDpiStage` assignment from `TryReadActiveDpiStageAsync`; when the cache is untrusted,
`PatchMsConfigAsync` writes back whatever byte it read.

- **Pro:** smallest possible diff; no extra device writes; fully removes the reported scenario.
- **Con:** incomplete. At cold start after a *button* press the block's byte lags, so writing it back
  moves the DPI one stage — the original bug, in a different path. Trades one silent DPI change for
  another.

### C. Persist provenance / compare flash to the persisted stage
Ruled out above — the states are indistinguishable. Recorded so it is not re-proposed.

## Recommended approach

**Option A**, with the inference demoted to a one-time bootstrap.

1. **`TelinkMouseService`**
   - `_liveDpiStage`: `byte?` → `private volatile int _liveDpiStage = UnknownStage;` with
     `private const int UnknownStage = -1`. Delete the "Byte assignment is atomic" comment, which is
     untrue for `Nullable<byte>` and is the open Round 1/2 thread-safety item.
   - **Trusted writers only:** `SyncStateFromDevice` (the `0x49` push) and `SetActiveDpiStageAsync`
     (our own write). Nothing else may assign it.
   - `TryReadActiveDpiStageAsync`: keeps reading and keeps applying `TaipanMsConfig.LiveDpiStage` for
     its **return value**, but **must not** assign `_liveDpiStage`. Rename to
     `TryInferActiveDpiStageAsync` so the guess is named at every call site, and rewrite the comment
     to state plainly that the result is a bootstrap hint, not a fact.
   - `PatchMsConfigAsync`: force `0x11` only when `_liveDpiStage != UnknownStage`. When untrusted,
     leave the byte as read **and log a warning** naming the risk, so the residual case is visible in
     the DeviceHost log rather than silent.
   - Extract the source-block decision (plausible read → use it / implausible → last committed /
     neither → skip, plus the live-stage override) into an internal pure helper — see
     **Test strategy**.

2. **`TaipanStateMonitor`**
   - `RequestStateRefresh` currently reads the flash and emits the inferred stage as a
     `DpiStageChange` on every gRPC subscribe. That emit is what poisons the App's persisted value
     (constraint 2). Change it to emit **only when the App has no persisted stage** — i.e. it becomes
     the first-run bootstrap — by taking the App's known stage as a parameter.
   - Fix the `state.LastStage` cross-thread write flagged as a Round 2 nit while this method is open.

3. **`DeviceService`**
   - No contract change. `SetActiveDpiStage` already routes to `IMouseRgbService.SetActiveDpiStageAsync`.

4. **App**
   - In `LoadPersistedDeviceStateAsync`, when the device reports `SupportsDpiStageSwitch` and a
     persisted stage exists, assert it: `SetActiveDpiStageAsync(persisted)`. Cable-only; no-ops over
     the dongle. Highlight the persisted value (it is now what the device holds).
   - Guard against redundant asserts: track the last asserted stage per settings identity in the VM
     and skip the write when it is unchanged, so re-opening the device page does not re-write flash.
   - When there is **no** persisted stage (first ever run), take the host's bootstrap value, persist
     it, and assert it — after which the device is trusted for good.

## File-level change list

- `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs` *(edited)* — `_liveDpiStage`
  becomes `volatile int` with an `UnknownStage` sentinel and trusted writers only; the inference
  stops assigning it; `PatchMsConfigAsync` only forces `0x11` when trusted and warns when not;
  source-block decision extracted to a pure helper.
- `src/ProoveGaming.Infrastructure/Classes/TaipanMsConfig.cs` *(edited)* — document on `LiveDpiStage`
  that it is a bootstrap inference valid only when the firmware last wrote the byte, and must never
  feed a write. Fix the header's self-contradiction ("every polling slot is written" vs
  `PollingRateSlots = 1`), the open Round 2 item in this file.
- `src/ProoveGaming.DeviceHost/Services/TaipanStateMonitor.cs` *(edited)* — bootstrap emit only when
  the App has no persisted stage; fix the `LastStage` cross-thread write.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` *(edited)* — assert the
  persisted stage on attach, de-duplicated per settings identity; bootstrap-and-persist on first run.
- `tests/ProoveGaming.Infrastructure.Tests/TelinkMouseStageProvenanceTests.cs` *(new)* — the
  regression tests below.
- `tests/ProoveGaming.Infrastructure.Tests/TaipanMsConfigTests.cs` *(edited)* — assert `LiveDpiStage`
  is documented/behaving as bootstrap-only; keep existing coverage green.

**No proto changes.** `SetActiveDpiStage` already exists and is routed.

## Acceptance criteria

1. Given a TAIPAN on cable and a UI-selected stage 3 → reconnect the gRPC client (or restart the app)
   → change the polling rate → the mouse stays on stage 3 and the UI still shows stage 3. *(This is
   the reported defect; it must fail before the fix.)*
2. Given a UI-selected stage 3 → the DeviceHost log shows the MsConfig write committing `0x11 = 3`,
   not `4`, for the subsequent polling-rate change.
3. Given the physical DPI button is pressed while the app is running → the UI advances to match, and
   a following polling-rate change leaves the DPI on that same stage.
3a. *(Added 2026-09-30.)* Given the physical DPI button is pressed repeatedly with the device details
   page open → the highlight walks the points **in order** (1 → 2 → 3 → … → 1), and the press
   writes **nothing** back to the device — it only marks the preset dirty. `OnDpiStageChanged` used
   to call `StageChangeAndApply`, whose full-configuration write 250 ms later rewrote MsConfig's
   active stage and undid presses landing during it, so the cycle jumped. Pinned by
   `PhysicalDpiButtonPress_DoesNotWriteConfigurationBackToDevice`. The same holds for a physical
   **polling-rate** button press (BLAZE): dirty only, no write-back — pinned by
   `PhysicalPollingRateButtonPress_DoesNotWriteConfigurationBackToDevice`. A press is still recorded as
   the stage/rate the device holds (`NoteAssertedDpiStage` / `NoteAssertedReportRate`), so pressing
   back to the preset's own stage/rate leaves the preset **clean**, and re-opening the page after a
   press issues **no** `SetActiveDpiStage` (criterion 4). Pinned by
   `PhysicalDpiButtonPress_CyclingBackToSavedStage_LeavesPresetClean`,
   `PhysicalPollingRateButtonPress_CyclingBackToSavedRate_LeavesPresetClean` and
   `PhysicalDpiButtonPress_ThenReopen_DoesNotAssertStageAgain`.
4. Given a device attach with a persisted stage → exactly one `SetActiveDpiStage` write is issued;
   re-opening the device details page issues no further write.
5. Given a device attach over the **dongle** → the assert no-ops, no exception is raised, and the UI
   still tracks the physical button.
6. Given a first-ever run with no persisted stage → the app adopts the host's bootstrap stage,
   persists it, and criterion 1 holds from then on.
7. `_liveDpiStage` is never assigned from an inferred value anywhere in the codebase (greppable:
   only `SyncStateFromDevice` and `SetActiveDpiStageAsync` assign it).
8. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` are green.

## Test strategy

`fix-*` — **the reproduction test is written first and must fail on current `HEAD`.**

`TelinkMouseService` is I/O-bound and currently untested, so the decision logic must be extracted to
be testable without hardware. Extract two pure pieces and cover them:

- **`ResolveStageForWrite(int liveDpiStage, byte[] block)`** — the live-stage override.
  - **Reproduction test (write first, must fail before the fix):** given a block whose `0x11 = 3` and
    a cache holding the *inferred* 4, today's code writes 4. The new contract: an inferred value can
    never reach this function, and a trusted 3 writes 3.
  - Trusted stage present → that stage is written.
  - Trusted stage absent (`UnknownStage`) → the block's own byte is left untouched.
- **`ResolveSourceBlock(byte[]? read, byte[]? lastCommitted)`** — the open Round 1/2 gap.
  - plausible read → the read; implausible read + cached block → the cached block; neither → null
    (skip the write). Uses `TaipanMsConfig.IsPlausible`, already tested.
- **`TaipanMsConfig.LiveDpiStage`** — existing tests stay green; add one asserting it is only ever
  consumed as a bootstrap (documented contract), not by the write path.

**Deliberately not automated:** the actual `0x41`/`0x01` HID round-trip, the firmware lag itself, the
dongle no-op, and the App's attach-time assert (UI-thread + gRPC). These are QA below.

## QA (manual test steps)

1. Connect the TAIPAN **by cable**. Open its card → DPI section. Click stage **3600**. Expected: the
   cursor speed changes and 3600 stays highlighted.
2. Without touching the mouse's DPI button, close and re-open the app. Expected: 3600 is still
   highlighted.
3. Now change the **polling rate** to 500 Hz. Expected: the DPI stays on 3600 — the highlight does
   not move and the cursor speed does not change. *(Before this fix it jumped to 4800.)*
4. Press the mouse's physical **DPI button** twice. Expected: the highlight advances by two stages
   and the cursor speed matches.
5. Change the polling rate again. Expected: the DPI stays where the button left it.
6. Change **lift-off distance**. Expected: the DPI stays put here too.
7. Switch the TAIPAN to the **dongle**. Open the device page. Expected: no error, the page loads, and
   pressing the physical DPI button still moves the highlight.
8. Close the app, press the DPI button twice while it is closed, re-open the app. Expected: the app
   shows the stage it had saved and sets the mouse back to it. **This override is intended** — report
   it only if the mouse ends up on a stage that matches *neither* the UI nor where you left it.
9. Regression — repeat 1–6 on a **BLAZE**. Expected: unchanged from before.

## Review focus

`thread-safety` (`_liveDpiStage` crosses the monitor reader thread and the write path under
`_writeLock`), `async/await` (the attach-time assert must not block the UI thread and must survive a
device that vanishes mid-write), `null-safety` (the untrusted-stage path must not fabricate a block),
`gRPC contract` (confirm no proto change was needed and none crept in).

## Performance & quality

No hot paths. A stage assert is one MsConfig RMW per device attach, de-duplicated so re-opening the
page costs nothing. The `0x49` push path is unchanged and still allocation-free in the steady state.
`_liveDpiStage` becoming a `volatile int` removes a torn-read window on a `Nullable<byte>` without
introducing a lock. All device writes stay serialized behind the existing `SemaphoreSlim`.

## Risks

- **The attach assert overrides a button press made while the app was closed.** This is inherent:
  the device cannot be queried and flash cannot distinguish the two states (see the table above). The
  chosen trade-off is that the UI and the device always agree, consistent with how polling rate, DPI
  values and lift-off are already asserted from persistence. Flagged in QA step 8 so a tester does
  not file it as a bug.
- **Extra flash writes.** One MsConfig erase+write per attach. Negligible against typical flash
  endurance at this rate, but the de-duplication guard matters — without it, every device-page open
  would write.
- **First-run bootstrap still uses the inference.** With no persisted value the `+1` rule is the best
  available guess and is correct for the only way the stage could have moved without us (the button).
  It is wrong at most once, and self-corrects on the first real push or UI selection.
- **The untrusted-write path still exists** (no persisted stage yet, and a polling change made before
  any stage is known). It now leaves the byte as read and logs a warning rather than committing a
  guess. Verify the warning appears in DeviceHost logs during QA step 7.

## Open questions

1. **Parent plan staleness.** `add-taipan-dpi-polling-plan.md` still says BLAZE stays display-only
   (Option C, acceptance criterion 7) and lists lift-off as out of scope, both contradicted by
   shipped code. Recommend correcting it as a separate documentation commit rather than inside this
   fix, so this branch's diff stays reviewable — confirm that split.
2. **Is the QA-step-8 override acceptable as product behaviour?** The plan assumes yes (UI and device
   agreeing is the stronger contract). If not, the fallback is Option B plus living with a possible
   one-stage move at cold start; there is no third option that avoids both.

## Review findings

_filled in by /reviewer_

### Round 1 — Ship: yes

_Branch `fix/fixes-on-30.09.2026` (commits 7f3105d, 1a2d87c), reviewed 2026-09-30. Scope for this
plan: acceptance criterion 3a — a physical DPI-button press marks the preset dirty but no longer
writes the configuration back (`OnDpiStageChanged` → `StagePresetDirtyOnly`). The rest of this plan
shipped earlier and is not re-reviewed here._

#### Plan conformance
- Criterion 3a implemented at `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:212`
  and pinned by `PhysicalDpiButtonPress_DoesNotWriteConfigurationBackToDevice`
  (`tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs:1225`), which was confirmed to fail
  against the old handler.
- Criteria 3 and the dirty-marking contract of `refactor-preset-scoped-device-writes` still hold:
  `StageChangeAndApply` was exactly `StagePresetDirtyOnly` + a debounced apply, so the dirty half is
  unchanged and the existing `PhysicalDpiButtonPress_WhilePresetSelected_MarksPresetDirty` passes.
- Not yet verified on hardware: the root cause (a press landing inside the write-back's
  config-mode session being undone) is inferred from the code path and the protocol notes, not
  observed in a log.

#### Code
- [x] **S1. The polling-rate button still writes the whole configuration back** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:239`
  (`OnReportRateChanged`) — Should-fix (left out on purpose, by request)
  **Problem / why it matters:** the same pattern criterion 3a removes: a BLAZE polling-button press
  schedules a full all-blocks `ApplyDeviceConfiguration` (active stage included) 250 ms later, to
  report a change the device has already made. Redundant device traffic on the mouse whose
  write-burst sensitivity is documented (`docs/mouse/blaze/engineering-notes.md`), and a DPI press
  landing in that window can be overwritten the same way.
  **Fix:** the same one-line change (`StagePresetDirtyOnly()`), plus a sibling test to the DPI one.

#### Tests
- [x] Regression test for criterion 3a — added, verified red→green.
- [ ] Hardware QA: press TAIPAN's DPI button repeatedly (fast and slow) with Device Details open —
  the highlight must walk 1 → 2 → … → 1. If it still jumps, the `[Taipan][DPI] stage=N` lines in the
  DeviceHost log show whether the device's own sequence is already out of order.

#### Nits
- none

#### What's done well
- Removes the write instead of guarding it: the device is the source of the change, so there was
  never anything to send.
- The comment at the call site explains the failure mode, which stops the next person from
  "restoring" the apply for symmetry with `SelectDpiPreset`.

#### Verdict
Ship: yes — correct, minimal, and tested. First: (1) hardware QA above; (2) S1 as a follow-up.

### Round 1 — fixes applied

_2026-09-30._ S1 fixed: `OnReportRateChanged` now calls `StagePresetDirtyOnly()` too, pinned by
`PhysicalPollingRateButtonPress_DoesNotWriteConfigurationBackToDevice`. Criterion 3a extended to
cover the polling-rate button.

### Round 2 — external review, 2026-09-30

- [x] **M1. A physical press no longer updated the echo-guard caches** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` (`OnDpiStageChanged`,
  `OnReportRateChanged`) — confirmed regression from the Round 1 change.
  **Problem:** `_assertedDpiStages` / `_assertedReportRates` were refreshed after each press only as a
  side effect of the write-back (`ApplyCurrentStateToDeviceAsync` notes what it wrote). Removing the
  write-back froze them on the preset's value. Consequences: pressing back to the saved stage/rate
  read as an echo, so the preset stayed dirty (false "unsaved changes", and a Discard is a full
  apply); and re-opening the page after a press re-asserted a stage the device already held — a
  flash write on TAIPAN over the cable, against criterion 4.
  **Fixed (option A):** both handlers record the pushed value with `NoteAssertedDpiStage` /
  `NoteAssertedReportRate` after the echo guard and before `StagePresetDirtyOnly`. Three regression
  tests added (see criterion 3a), each verified to fail without the fix; full App suite green in
  Release.
