# Plan: Re-apply the last-applied custom preset when a device connects

## Goal

When a device connects or reconnects, the hardware should come back holding the configuration the
user last put on it. Today only lighting survives a reconnect — `ReapplyRgbOnConnect` restores
`device_rgb` and nothing else — so button remaps, DPI stages, the active stage, report rate and
lift-off/debounce are whatever the firmware kept, which after a power cycle or a switch between
transports is frequently the factory values. The user then sees the app showing one configuration
and the mouse behaving as another.

This plan restores the whole configuration on connect, using the same trigger point, but **only when
the device's last-applied preset is a custom one**. A device sitting on its auto-created default
preset is deliberately left alone: that preset represents "as it came", so re-pushing it on every
connect would be a hardware write that changes nothing the user asked for.

## Scope

**In:**
- On the connect transition in `HomeViewModel`, re-apply the device's last-applied preset when that
  preset is **not** the default one.
- **Mice only**, via the single coordinated `ApplyDeviceConfiguration` call.
- Respect `Device.SupportsConfiguration`: transports that cannot carry configuration (TAIPAN over
  its 2.4G receiver or Bluetooth) are skipped entirely — no device call, no storage write.
- Reuse the existing per-device live tables as the source, and the existing `_applyGate` for
  serialisation against the details page's own applies.

**Out:**
- **Keyboards.** Their apply is not one coordinated write — it is an RGB push, then a key-override
  push, then a macro resync (`ApplyCurrentStateToDeviceCoreAsync`, keyboard branch) — and the key
  push depends on key-layout state that only exists once the details page has loaded a device.
  Reproducing that off-page is a materially different piece of work. Keyboard lighting already
  survives a reconnect through the lighting-only restore, which stays as-is. Recorded as a follow-up.
  *(2026-08-17: that restore is now the fallback branch of `ReapplyOnConnectAsync` rather than a
  separate `ReapplyRgbOnConnect` call — same behaviour for keyboards.)*
- Changing what `EnsureDefaultPresetAppliedAsync` does on **first** connect (no preset ever applied
  → apply the default). That path is untouched; the new behaviour only fires when an applied preset
  already exists and is custom.
- Any change to preset semantics, the dirty comparison, or the Save Preset button.
- Re-applying on app **start** for a device that is already connected. The trigger is the
  `!wasConnected && found.Connected` transition, which does not fire for a device that was already
  present when discovery first ran. See Open questions.

## Current state (verified — do not re-derive)

| Fact | Where | Why it matters |
| --- | --- | --- |
| Connect transition hook already exists | `HomeViewModel.HandleUsbHidDeviceFoundAsync`, `if (!wasConnected && found.Connected)` → `ReapplyRgbOnConnect(found)` | The natural, already-proven place to hang this |
| A card-parameterised off-page apply already exists | `DeviceDetailsViewModel.ReapplyPersistedRgbAsync(card)` | Proves a public method on the singleton VM can push for a device that is **not** the open one, reading only its `card` argument plus storage |
| Applying a preset copies its snapshot into the LIVE tables | `LocalStorageService.LoadPresetSnapshotToDevice` | **The key simplification**: the live per-device tables already hold the last-applied preset's values, so "re-apply the last preset" == "push the persisted live state". No snapshot reading needed |
| The mouse write is one coordinated call | `ApplyCurrentStateToDeviceCoreAsync`, else-branch → `ApplyDeviceConfigurationAsync(led, bindings, mouseSettings, dpiStages, activeStage, reportRate)` | BLAZE bricks its buttons on separate per-aspect writes; this shape must be preserved exactly |
| The encoders are all `static` | `MapEffectByte`, `ToProtocolBrightness`, `ToProtocolSpeed` (Rgb.cs), `ToProtoMacro` (Buttons.cs) | They can be reused from a card-parameterised method without touching VM state |
| Every needed reader exists | `GetAppliedPreset`, `GetPresets`, `GetRgbState`, `GetButtonBindings`, `GetMacros`; `ISettingsRepository.LoadDpiPresetsAsync` / `LoadActiveDpiStageAsync` / `LoadPollingRateAsync` / `LoadMouseSettingsAsync` | No storage work required |
| `SupportsConfiguration` gating is already in place | `DeviceDetailsViewModel.IsDeviceConfigurable`, and the card property it reads | The new path must apply the same rule, but keyed off the passed card, not `Device` |

## Options considered

**Where the off-page apply lives**

- **A. A new card-parameterised method on `DeviceDetailsViewModel` (chosen).** Sits directly beside
  `ReapplyPersistedRgbAsync`, which already does exactly this for one aspect, so the pattern is
  established and reviewable. It can reuse the four `static` encoders as-is, and — importantly — it
  can take the VM's existing `_applyGate`, so a connect-triggered apply and a details-page apply
  cannot interleave two writes at the hardware. Downside: adds a second "operates on a card that may
  not be `Device`" method to an already large class, and every line of it must be disciplined about
  never touching `Device` or any UI collection.
- **B. A new `DevicePresetReapplyService` in `ProoveGaming.App/Services`.** Cleaner separation and
  no risk of accidentally reading VM state. Rejected for now: it would need the four encoders made
  public or duplicated, it would need its own serialisation primitive to avoid racing the VM's
  `_applyGate` (two independent gates guarding the same hardware is worse than one), and it splits
  "how this device is written" across two places. Worth revisiting if keyboards are added later and
  the method grows.

**What to push**

- **A. The persisted live per-device tables (chosen).** Applying a preset copies its snapshot into
  those tables, so they *are* the last-applied preset's values. It is also exactly what the details
  page pushes, so connect-time and page-time applies agree by construction.
- **B. Read the preset's snapshot rows directly.** Equivalent in the normal case, but wrong after a
  Discard: the live tables are the source of truth for "what is on the device now", and the snapshot
  is the target for "what Save Preset would store". Reading the snapshot would silently discard a
  legitimately applied-but-unsaved state.

**How "custom preset" is decided**

- **A. `GetAppliedPreset` → look the row up in `GetPresets` → require `IsDefault == false`
  (chosen).** Uses the existing default-preset concept; no new storage or flags.
- **B. A new column / flag recording "user has customised this device".** More state to keep
  correct, and it would drift from `IsDefault`.

## Recommended approach

Option A throughout.

1. **`DeviceDetailsViewModel.ReapplyLastCustomPresetAsync(DeviceCardViewModel card)`** — new public
   method, sibling to `ReapplyPersistedRgbAsync`. It:
   - returns immediately when `card.SupportsConfiguration == false`, when the device is a keyboard
     (`card.DeviceTypeLabel` contains "keyboard"), or when services/owner are unavailable;
   - reads `GetAppliedPreset` for the card's settings identity; returns if null;
   - looks that id up in `GetPresets` and **returns if the preset is the default or is missing**;
   - assembles the same payload the mouse branch of `ApplyCurrentStateToDeviceCoreAsync` builds, but
     entirely from storage: LED from `GetRgbState` (only when enabled, mirroring
     `ReapplyPersistedRgbAsync`), bindings from `GetButtonBindings` + `GetMacros`, mouse settings
     from `LoadMouseSettingsAsync`, DPI table from `LoadDpiPresetsAsync`, active stage from
     `LoadActiveDpiStageAsync`, report rate from `LoadPollingRateAsync`;
   - sends **one** `ApplyDeviceConfigurationAsync`, inside `_applyGate`;
   - sends `null` for any aspect with nothing stored — the existing contract for "leave this alone" —
     so a partially configured device is not handed fabricated values.
2. **`HomeViewModel.ReapplyOnConnect(card)`** — resolves `DeviceDetailsViewModel` from DI and calls
   `ReapplyOnConnectAsync`; invoked at every connect site.
3. Ordering: the coordinated mouse apply is tried first, and the lighting-only push runs **only** if
   it did not (keyboards, unconfigurable transports, a device on its default preset). One hardware
   transaction per connect, whichever path serves the device.

> **Superseded 2026-08-17** — items 2 and 3 originally read: a `ReapplyLastPresetOnConnect` sibling
> invoked *after* `ReapplyRgbOnConnect`, with RGB first and the coordinated apply second, accepting
> that "for a mouse the coordinated call also carries the LED, so the earlier RGB push is redundant
> but harmless". It was not harmless. Two full transactions landing within milliseconds is exactly
> the burst BLAZE's firmware does not tolerate, and the mouse intermittently came up from a cold
> start with left/right click dead (`docs/mouse/blaze/key-matrix.md`). The two calls are now one
> entry point with the lighting push as a fallback, so nothing is sent twice.

## File-level change list

- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — add
  `ReapplyLastCustomPresetAsync(DeviceCardViewModel card)`: the storage-sourced, card-parameterised
  coordinated apply described above. Must not read `Device` or any UI collection. *(2026-08-17: it
  now returns `bool` — whether it wrote — and `ReapplyOnConnectAsync` was added beside it as the
  single connect entry point, falling back to `ReapplyPersistedRgbAsync` when it did not.)*
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — call the connect restore at every connect
  site. *(2026-08-17: the two callers `ReapplyRgbOnConnect` + `ReapplyLastPresetOnConnect` were
  replaced by one `ReapplyOnConnect`; see the note under Recommended approach.)*
  *(2026-08-18: added `RestoreConnectedDevicesForNewOwner` + the `ShouldRestoreForOwner` rule, which
  re-drives the restore for already-connected cards when the settings owner changes — see the
  owner-transition note below.)*
- `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs` — new tests per Test strategy.
- `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs` — *(2026-08-18)* the
  owner-transition rule: when a resolving owner re-drives the restore, and when it must not.
- `docs/CONTEXT.md` — no new term expected; add one only if the implementation introduces a name
  worth defining.

## Acceptance criteria

- Given a mouse whose last-applied preset is a **custom** preset with stored bindings/DPI/rate →
  disconnect and reconnect it → **exactly one** `ApplyDeviceConfiguration` call is made for that
  device, carrying the stored values.
- Given a mouse whose last-applied preset is the **default** preset → reconnect it → **no**
  `ApplyDeviceConfiguration` call is made.
- Given a mouse that has **never** had a preset applied → reconnect it → no call from this path
  (`EnsureDefaultPresetAppliedAsync` continues to own first-connect, unchanged).
- Given a device whose card has `SupportsConfiguration == false` (TAIPAN on dongle/Bluetooth) →
  reconnect it → **no** device call and **no** storage write, regardless of which preset was applied.
- Given a **keyboard** whose last-applied preset is custom → reconnect it → no coordinated apply from
  this path; its lighting is still restored, by the lighting-only fallback in `ReapplyOnConnectAsync`.
- Given a custom preset where only *some* aspects are stored (e.g. bindings but no report rate) →
  reconnect → the call carries the stored aspects and `null` for the rest; no fabricated values.
- Given the details page for that device is open and applying while it reconnects → the two applies
  do not interleave (both go through `_applyGate`).
- Existing behaviour is unchanged: no new device write occurs on a connect where the last-applied
  preset is the default, and the lighting restore still runs on every connect the coordinated apply
  does not serve.
- Given **any** device → connect it → **at most one** hardware transaction is issued by this path:
  the coordinated apply (which carries the lighting) or the lighting-only push, never both.
  *(Added 2026-08-17 — see the note under Recommended approach.)*
- Given devices that connected while **no settings owner was resolved** (first run, after a
  sign-out, an expired "Remember me" or an expired guest session — the DeviceHost streams devices
  while the user is still on the Welcome screen) → the user starts a guest session or signs in →
  every still-connected card gets the connect restore it could not be given at connect time.
  *(Added 2026-08-18.)*
- Given one sign-in (which raises **both** `UserProfile.CustomerId` and `SessionService.IsGuest`) →
  **exactly one** restore sweep runs, not two — a duplicate sweep is a second hardware write per
  device, which is what BLAZE's firmware does not tolerate. *(Added 2026-08-18.)*
- Given a sign-**out** → no restore sweep and no device write; the next sign-in restores again.
  *(Added 2026-08-18.)*

## Test strategy

New feature → the following coverage is required, not optional. All in
`tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`, which already has the
`FakeUsbHidDeviceService` harness (`ApplyConfigCallCount`, `LastBindings`, `LastActiveStage`,
`LastReportRateHz`) and a real `LocalStorageService` via `NewVmWithRealSettings`.

Required cases:
- **Custom preset → applies once.** Seed a custom preset, mark it applied, seed live bindings/stage/
  rate, call the new method with a card, assert `ApplyConfigCallCount == 1` and that
  `LastActiveStage` / `LastReportRateHz` / `LastBindings` match what was stored.
- **Default preset → does nothing.** Same setup but the applied preset is the default; assert no
  call.
- **No applied preset → does nothing.** Assert no call.
- **`SupportsConfiguration == false` → does nothing.** Assert no call and that no storage row
  changed — the guard must sit before any read/write.
- **Keyboard → does nothing** from this path.
- **Partial state → nulls, not defaults.** Store bindings only; assert the call carries bindings and
  a null report rate, proving a device with nothing stored for an aspect is not handed a fabricated
  value (this is the `_dpiStageKnown` / `_reportRateKnown` rule expressed off-page).
- **Does not disturb the open device.** Call it for card B while `Device` is card A; assert `Device`
  is still A and no UI collection changed — the regression this design is most likely to introduce.

Deliberately **not** covered by automated tests: that the hardware actually holds the configuration
after a physical reconnect (QA below), and keyboards (out of scope). Note `dotnet-ci.yml` builds only
the App csproj and does not run `tests/`, so this coverage is local-only.

## QA (manual test steps)

1. Connect a BLAZE mouse by cable. In the app, create a new preset, change the DPI to a value you
   will recognise (e.g. 6000) and remap one side button to something obvious (e.g. Mute). Press SAVE
   PRESET. **Expected:** the button greys out again.
2. Unplug the mouse, wait five seconds, plug it back in. **Expected:** within a couple of seconds the
   mouse is back on 6000 DPI and the remapped side button still mutes — without opening the device
   page.
3. Switch the app to the auto-created default preset for that mouse, then unplug and replug it again.
   **Expected:** nothing is re-pushed; the mouse keeps whatever it had, and no settings change on
   their own.
4. Connect a PG TAIPAN using its 2.4G receiver (no cable). Unplug and replug the receiver.
   **Expected:** no settings change, and the app does not report an error — this device cannot be
   configured over the receiver.
5. Connect a keyboard (Cyrex or Rampage) with a custom preset. Unplug and replug it. **Expected:** its
   lighting comes back as before. Key assignments are not expected to be re-pushed by this change.
6. With the mouse's device page open and visible, unplug and replug the mouse. **Expected:** the app
   stays responsive, no error appears, and the page still shows the same preset selected.
7. *(2026-08-18)* Sign out of the app and close it, leaving the BLAZE from step 1 plugged in with a
   recognisable RGB colour and DPI saved. Start the app again and stop on the Welcome screen for
   about ten seconds — the mouse card appears while you are still signed out. Now sign in (or
   continue as guest, matching whoever owns that preset). **Expected:** within a couple of seconds
   of signing in, the mouse's lighting and DPI snap back to the saved preset **without** opening its
   device page. Before this fix the mouse kept its power-on lighting until the card was clicked.
8. *(2026-08-18)* Immediately after step 7, watch the mouse for another ten seconds without touching
   anything. **Expected:** the lighting settles once and stays put — no second flash or re-apply, and
   the mouse's buttons all still work (a duplicated apply is what BLAZE's firmware does not tolerate).

## Review focus

- `async/await` — the new method is fire-and-forget from a discovery handler; it must not block, must
  not deadlock on `_applyGate`, and must not throw into the handler.
- `thread-safety` — it runs off the discovery thread while the UI thread may be applying for the same
  device. `_applyGate` is the coordination point; confirm it is actually taken.
- `null-safety` — every storage read can return nothing; confirm "no stored value" produces `null` in
  the payload rather than a default, and that the method never dereferences `Device`.
- `performance hot-path` — connect events can arrive in bursts (hub enumeration); confirm this cannot
  issue repeated writes for the same device.

## Performance & quality

Not a hot path: this runs once per connect transition, which is a physical user action. It performs a
handful of indexed SQLite reads and one HID write. The reads are synchronous but happen off the UI
thread inside an already-async discovery handler.

Two things to get right rather than fast:
- **No repeated writes.** Connect bursts and the existing `_eventGate` mean the handler can run more
  than once around a single plug-in; the `!wasConnected && found.Connected` edge should make this
  fire once, but the implementation must not assume it and should be idempotent in effect.
- **No UI-thread work.** The method must not touch observable collections; it reads storage and calls
  the device service only.

## Risks

- **A wrong or partial payload reaches the hardware.** This is the serious one: BLAZE bricks its
  buttons when the coordinated write is not complete. Mitigation: build the payload with exactly the
  same shape as the existing mouse branch, send `null` for unknown aspects rather than guesses, and
  cover the partial-state case in tests.
- **Reading VM state by accident.** The method lives on a singleton bound to a different device;
  a stray `Device.` or `DpiPresets` reference would push one device's configuration onto another.
  Mitigation: card-parameterised throughout, plus the "does not disturb the open device" test.
- **Surprising the user.** A device that was deliberately left in a hardware state (e.g. DPI changed
  with the physical button) gets overwritten on reconnect by the stored preset. This is the intended
  behaviour per the request, but it is a real behavioural change and worth calling out in release
  notes.
- **Double-apply with the details page.** Opening the page for a device that just connected can run
  both this and `EnsureDefaultPresetAppliedAsync` / the page's own load. `_applyGate` prevents
  interleaving at the wire, but two sequential identical writes are still possible; harmless, but
  worth watching in the log during QA.

## Open questions

- **Should this also run at app start for an already-connected device?** As designed it does not: the
  trigger is the not-connected → connected edge, and a device present when discovery first runs never
  crosses it. Arguably the user's intent ("apply on device connected") includes launching the app
  with the mouse plugged in. Recommendation: keep the narrow trigger for this change, confirm the
  behaviour in QA step 2, and widen it deliberately if wanted — widening means every app start pushes
  to every connected device, which is a much bigger hardware-write footprint.
- **Keyboards.** Confirm they are genuinely out of scope for now. If they are wanted, that is a
  separate plan: the key-override push needs the layout state the details page builds.

> **2026-09-10 — the keyboard early-return now has a page-side counterpart.**
> `ReapplyLastCustomPresetAsync` still returns early for keyboards, so nothing changed here. But the
> owner-change sweep that calls this restore now also marks each connected type pending-apply for
> the incoming owner, and the details page consumes that mark on its next open — which is how a
> keyboard finally gets the arriving account's configuration instead of keeping the previous one's.
> The behavioural window between the switch and that first page open stays open by design. See
> `docs/plans/fix-account-switch-device-state/`.

## Review findings

### Round 1 — Ship: yes

Reviewed the full delta of `feat/PG-477-Check-Taipan` against **`origin/main`**, immediately after
merging `origin/main` in (merge `726cafe`, bringing T800 RGB #80 and Rampage key assignments #67).
Working tree clean, so `git status` adds nothing beyond the branch delta: 50 files, +4999/−88.
Build clean; 990 tests pass (450 Infrastructure + 540 App).

#### Plan conformance

Plan: this document — it covers **only the last feature** on this branch. The other eight changes
(TAIPAN battery curve, Bluetooth PID, `SupportsConfiguration` gating, preset dirty-state backfill,
port-move card reuse, `AppResilience`, and two `Device`-after-`await` crash fixes) have no plan and
were reviewed code-only. That is a lot of unplanned work for one branch, but each piece was
user-directed and individually tested, so it is noted rather than graded as scope drift.

- **Implemented as planned:** `ReapplyLastCustomPresetAsync` is card-parameterised and reads only its
  argument plus storage; source is the live per-device tables, not the snapshot; ONE coordinated
  `ApplyDeviceConfigurationAsync`; `_applyGate` shared with the page; unstored aspects sent as `null`;
  keyboards and unconfigurable transports return before any read or write; the `HomeViewModel` hook
  is added at both connect sites, after `ApplyDeviceIdentity` so the settings identity is already set.
- **Acceptance criteria:** all met except one partial — see S1, which lets a *preset-bookkeeping*
  write through on a transport the branch declares read-only.
- **Accepted limitation (per plan):** the feature does not fire for a device already connected when
  the app starts, since the trigger is the not-connected → connected edge. Residual risk: a user who
  launches the app with the mouse plugged in does not get their custom preset re-applied, which is
  plausibly what "on device connected" meant. Still listed under Open questions.
- **Verified surviving both merges** (the specific concern raised): battery curve + tests, Bluetooth
  PID `33382`, `SupportsConfiguration` end-to-end (config → `DeviceMap` → expander → registry → proto
  field 30 → `HomeViewModel` → card → all gates), `BackfillPresetSnapshotFromLive`, port-move card
  reuse (`!x.Connected`) and the settings-key freeze, `AppResilience` install, and both
  captured-card crash fixes. TAIPAN catalog intact (`Dongle2_4G` / `Bluetooth` =
  `SupportsConfiguration: false`). No duplicate field numbers in `DeviceCatalogEntry` after main
  added its own.

#### Code

- [ ] **S1. A read-only transport still writes preset bookkeeping to the database** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs`
  (`LoadPresetsForDevice`, the `if (defaultPreset.Seeded)` branch) — Should-fix

  **Problem / why it matters:** the branch states, and an acceptance criterion requires, that a
  transport with `SupportsConfiguration == false` performs **no** device call and **no** storage
  write. `SeedCatalogDefaultDpiAndRate()` inside that branch *is* gated, but the
  `_localStorage.SavePresetSnapshot(...)` beside it is not. Opening the details page for a device
  with no presets yet — a TAIPAN first seen on its dongle — therefore creates the default preset row
  and snapshots the live rows into it.

  Harm is low in practice: on such a transport no live device rows were ever written, so the snapshot
  is near-empty, and the row itself is needed for the UI to have a preset at all. But it contradicts
  a rule this branch introduced, and the next person hardening this area will expect the rule to hold.

  **Fix:** keep creating the preset row (the UI needs it) and gate only the snapshot —
  `if (defaultPreset.Seeded && IsDeviceConfigurable)`. A later cable session fills it through
  `BackfillPresetSnapshotFromLive`, which is exactly what that method exists for.

#### Tests

- [ ] **`ReapplyOnConnect_DoesNotDisturbTheOpenDevice` cannot catch the bug it was written for** —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`

  The plan names this the regression the design is most likely to introduce: the method reading
  `Device` (the open device) instead of its `card` argument, pushing one device's configuration onto
  another. But `open` and `other` are both `FakeMouse()`, so they share `Vid`, `Pid` and `DeviceKey`
  and differ only in `Name`, which the method never reads. Substituting `Device` for `card`
  throughout would leave every assertion passing — the test checks "the VM was not mutated", not
  "the right device was targeted".

  **Fix:** give the second card a distinct `VendorId` / `ProductId` / `SettingsProductId`, seed the
  applied preset under *those* ids, and assert the write targeted them. Then a stray `Device.` read
  fails the test.

- [ ] **No test covers the `HomeViewModel` wiring.** Every new test drives
  `ReapplyLastCustomPresetAsync` directly, so nothing pins that it is called on the connect
  transition, after `ApplyDeviceIdentity`, at **both** call sites. Reading the code confirms the
  ordering is correct today; a test would keep it correct. Lower priority — the hook is three lines.

#### Nits

- The LED is pushed twice per connect for a mouse: `ReapplyRgbOnConnect` sends a single-block LED
  write and the coordinated apply then carries the LED again — `HomeViewModel`, both connect
  branches. Verified harmless: `EasenMouseService` serialises every write path on its own
  `_writeLock`, and the second write is a full-content all-blocks write, the documented-safe shape
  (the 2026-07-02 flash-corruption incident was caused by *partial-content* all-blocks writes, not by
  two writes in succession). Left as-is per the plan.
- `PersistRgbStateForCurrentDevice` is now gated on `IsDeviceConfigurable`, so on a read-only
  transport `device_rgb` is never populated and `ReapplyPersistedRgbAsync` would have nothing to
  restore. No current device is affected — TAIPAN, the only unconfigurable transport, has no RGB —
  but it becomes live the day an RGB-capable device gains such a transport.
- `.claude/settings.json` gains `dotnet build` / `dotnet test` permissions — tooling convenience,
  unrelated to any feature on this branch.

#### What's done well

- **The read-only gating is applied at the flow's entry points, not at the ~35 individual write
  calls.** Enumerating those is exactly how two earlier attempts in this area missed cases.
  `StageChangeAndApply` returning early also means the dirty flag cannot latch, so the disabled Save
  Preset button and the "nothing to save" state are produced by two independent mechanisms that
  agree — valuable for something that regressed repeatedly.
- **`configuration_unsupported` is negative on the wire deliberately.** proto3 defaults a bool to
  false, so an older DeviceHost that never sets it reads as "configuration works" rather than
  disabling saving for every device — the lockstep trap the device-defaults work already hit once.
- **Both crash fixes capture the card before the `await`** rather than re-null-checking, and the
  second converted *every* post-await read in the method, not just the line that threw.
- **The battery curve was confirmed against hardware**, not only against the vendor's formula: raw
  986/982 → 100 % / 98 %, matching the vendor's worked example, against 71 % / 69 % under the old
  curve.

#### Verdict

Ship: yes — no blockers. The feature matches its plan, and every earlier fix on the branch survived
both merges intact, verified individually since that was the specific concern. S1 is a small
consistency gap in a rule this branch itself introduced; the test gap matters because that test is
the designated guard for this design's most likely regression. First: (1) the isolation test, so the
guard is real; (2) S1; (3) decide the app-start open question — it is the difference between the
feature firing for most users and only on replugs.

Plan ready. Invoke /coder to implement.
