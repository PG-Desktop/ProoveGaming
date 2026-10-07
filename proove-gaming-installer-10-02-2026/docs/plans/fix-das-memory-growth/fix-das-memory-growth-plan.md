# fix-das-memory-growth — plan

Date: 2026-09-02. Author: /architect.

## Goal

Stop `ProoveGaming.DeviceHost` from driving unbounded memory and CPU growth in Windows'
**Device Association Service** (DAS — the `svchost` instance shown as "Service Host: Device
Association Service" in Task Manager). A tester machine reached ~8.8 GB of DAS memory at ~64% CPU.
The trigger is the same scenario as the earlier in-process leak
(../fix-devicehost-ble-memory-leak/fix-devicehost-ble-memory-leak-plan.md): a paired PG BLE device
left powered off keeps our `BluetoothLEAdvertisementWatcher` scanning 24/7 — local DeviceHost logs
show single scan sessions of 8–14 hours. While any BLE scan runs, DAS processes every advertisement
from every device in radio range; phones use rotating random addresses, so DAS accumulates state
for an endless stream of "new" endpoints and never releases it. Our own handler is already O(1)
per advertisement — the damage now happens inside DAS, before our code runs. The fix bounds how
much scanning we ask Windows to do: **passive scanning** (no scan-request/response cycle per
advertiser) in **duty-cycled windows** (~10 s per 30 s sweep) instead of a continuous active scan.

> **Note (2026-09-02, tester log `log20260827.txt`):** on the affected tester machine the scan
> trigger is not a device left powered off but a **stale ghost pairing** — two tracked PG Taipan
> entries with different BLE addresses (the address increments on every re-pair), one of which can
> never reconnect. The "a tracked paired device is disconnected" condition is therefore
> permanently true: the scan started one second after host startup and never stopped, so DAS
> grows on every run, chronically. This strengthens the duty-cycle choice — the scan being
> needed "forever" is exactly the case a bounded window handles — and is why the QA baseline
> step matters.
>
> **Note (2026-09-08, second incident — this plan's bound was not enough):** QA left the App
> running with a **PG TAIPAN connected over its 2.4G dongle** and the machine again ran out of
> memory to DAS. The duty cycle this plan introduced bounds how *much* of the time we scan; it does
> not bound how *long* we keep doing it, and "a tracked paired device is disconnected" is the
> permanent state of a mouse that is paired over Bluetooth but in use on its dongle — nothing in
> `BluetoothDeviceDiscoveryService` knows the same physical unit is present on another transport,
> and the 14-day stale-pairing veto does not apply to a fresh pairing. A local DeviceHost log over
> ~10 h shows the consequence: 387 `LE scan started` lines, 385 closes, never once idle.
>
> The scan therefore gained a **reopen backoff** — `0 → 30 s → 2 min → 5 min` after each window that
> closes with no connection-state change, reset by every transition — taking the steady-state duty
> from ~33 % to ~3 %. A second, independent defect was found on the enumeration path and fixed with
> it: `Bluetooth.GetPairedDevicesAsync` resolves a native `BluetoothLEDevice` for **every** paired
> BLE device on the machine and disposes none, and every 30 s sweep dropped the non-PG ones without
> disposing them. And the **sweep itself** gained the same escalation (`30 s → 30 s → 1 min → 2 min
> → 5 min`, sharing one reset hook), because a flat 30 s enumeration held forever is unbounded in
> exactly the way the scan was — every job it does is covered faster by the 3 s tree poll, the 3 s
> classic poll or the rediscovery trigger while it waits. **All four DAF paths in the service are
> now bounded**, and the two idle escalations share a ceiling so a machine at rest settles into one
> rhythm: one scan window and one enumeration per five minutes. Full record:
> [`docs/audits/2026-09-08-das-dongle-incident.md`](../../audits/2026-09-08-das-dongle-incident.md).
> The sections below are updated for the backoff; the review journal is left as written.

> **Note (2026-09-02, follow-up):** the ghost case gained a dedicated fix —
> [`../add-stale-pairing-detection/add-stale-pairing-detection-plan.md`](../add-stale-pairing-detection/add-stale-pairing-detection-plan.md)
> vetoes a tracked device that has not connected for 14+ days (per Windows'
> `DEVPKEY_Bluetooth_LastConnectedTime`) out of the scan-need computation, so a machine whose
> only disconnected devices are ghosts opens no windows at all.

## Scope

**In scope**

- `BluetoothDeviceDiscoveryService`: switch the watcher from `Active` to `Passive` scanning mode.
- Duty-cycle the scan: each successful watcher start also schedules a window close (~10 s); the
  periodic sweep (30 s while anything is tracked) reopens the window if a tracked paired device is
  still disconnected. Reconnection during a window still stops the scan immediately, as today.
- A generation token so a stale window-close task can never stop a window opened later.
- Pin the window-close rule with unit tests in `tests/ProoveGaming.Infrastructure.Tests`.
- Documentation: dated superseding note in the previous plan, `docs/CONTEXT.md` glossary
  (add **Device Association Service (DAS)**, adjust the **LE advertisement watcher** entry).

**Out of scope**

- Removing the watcher entirely and relying on the 3 s device-tree poll + Windows auto-reconnect
  (option 3 below). It fully eliminates our DAS load but its safety depends on Windows
  auto-reconnecting every catalog BLE device model — that needs per-model hardware validation
  (Taipan, Rampage, Cosmo at minimum) before it can be planned. Recorded in Open questions.
- Any change to `GetPairedDevicesAsync` sweep cadence, the classic-Bluetooth poll, the low-energy
  tree poll, GATT battery reads, the gRPC contract, or anything the App sees.
- No new NuGet package, no target framework change, no `.slnx` / `Directory.Build.props` change.

## Options considered

1. **Passive scanning only.** One-line mode change; near-zero functional cost (matching is by
   `args.BluetoothAddress`, present in every advertisement; only the rare LocalName fallback —
   a tracked id that failed to parse as an address — degrades, because LocalName usually travels
   in the scan response). − Mitigation only: DAS still processes every advertisement for as long
   as the scan runs, and the scan still runs for hours. Growth rate is reduced by an unknown
   factor, not bounded.

2. **Duty-cycle only (keep Active).** Bounds scan time to ~1/3 duty, so DAS growth slows ~3–4×.
   − Keeps the scan-request/response cycle, the most expensive per-advertiser part of active
   scanning, during every window.

3. **Remove the watcher; rely on the 3 s device-tree poll.** Windows auto-reconnects BLE HID and
   audio devices itself; `PollLowEnergyPresence` already marks reconnection from the tree in
   ~3–6 s and publishes the tree battery. + Eliminates our DAS load entirely. − If any catalog
   device is *not* auto-reconnected by Windows, its card never comes back — nothing else calls
   `Gatt.ConnectAsync`. Needs hardware validation per model; too risky to ship blind.

4. **Passive + duty-cycle (recommended, options 1+2 combined).** Both levers, both low-risk;
   worst-case reconnect latency for a device that only our scan can reconnect rises to ~30 s,
   while devices Windows reconnects itself keep their ~3–6 s tree-poll path unchanged.

## Recommended approach

Option 4. Concretely, in `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs`:

- `ScanningMode = BluetoothLEScanningMode.Passive` (rewrite the comment that currently justifies
  Active: the LocalName fallback degrades deliberately; the address match — the only path PG
  hardware actually uses today — is unaffected).
- New constants next to the existing cadence block: `ScanWindow = TimeSpan.FromSeconds(10)`
  (~1/3 of `ActiveSweepInterval`; tunable, see Open questions).
- New field `int _scanWindowGeneration`, guarded by the existing `_watcherLock`.
- In `SyncScanState`, after a `watcher.Start()` that neither threw nor transitioned straight to
  `Aborted`: increment `_scanWindowGeneration` under the lock (the call is already inside it),
  capture the value, and fire-and-forget a window-close task: `await Task.Delay(ScanWindow)`,
  then under `_watcherLock` — if the captured generation still equals `_scanWindowGeneration`
  **and** `_watcher is { Status: Started }` — `Stop()` the field's watcher and log
  `"[BT] LE scan window closed — the next sweep reopens it if a tracked device is still
  disconnected."`. The close task operates on the **field** under the lock, never on a captured
  watcher instance, matching the file's existing locking discipline.
  *(2026-09-08: the close also counts the window and arms the reopen backoff, and the line now ends
  `"… reopens it in {Delay}s if a tracked device is still disconnected."` — grep for
  `LE scan window closed`, not for the old tail.)*

  > **Correction (2026-09-02, /coder):** the plan originally had the close task also observe the
  > current `_periodicCheckCts` token. That is wrong by ordering: the first `SyncScanState` of a
  > discovery run executes **before** `StartDiscoveringCoreAsync` replaces the CTS, so the
  > captured token is the one about to be cancelled — the first window would never close, and the
  > sweep (seeing a `Started` watcher) would never schedule another, silently restoring the
  > unbounded scan. The delay is therefore untracked and the generation + status guard is the
  > sole authority; the worst a shutdown leaves behind is one `ScanWindow`-long delay that no-ops.
- Extract the guard as a pinned pure rule, in the style of `ShouldPublishBattery`:
  `internal static bool ShouldCloseScanWindow(int scheduledGeneration, int currentGeneration,
  bool watcherStarted)`.
- Everything else about the lifecycle is intact: `DeviceGattDisconnected` still opens a window
  immediately, the connect continuation still stops the scan the moment everything is connected
  (that stop path needs no generation bump — a stale close task is a no-op because the status
  guard sees a non-Started watcher, and a later reopen bumps the generation past it),
  `TearDownWatcher` still wins over any pending close (the field is null afterwards).

Interleavings worked through: sweep firing mid-window early-returns on `Status: Started` (window
closes on schedule, next sweep reopens — duty holds); reconnect-then-redisconnect inside one
window (old close task's generation is stale after the reopen — no-op); teardown before close
fires (`_watcher` null — no-op). A `Start()` landing on a `Stopping` watcher may throw; the
existing catch already logs it at Debug and the next sweep retries — worst case one 30 s gap.

## File-level change list

1. `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs` — passive mode,
   `ScanWindow` constant, `_scanWindowGeneration`, window-close scheduling in `SyncScanState`,
   `ShouldCloseScanWindow` pinned rule, comment updates (Active-mode justification, the "started
   once and left running" history note gains the window story).
   *(2026-09-08)* adds the reopen backoff: `ScanReopenSchedule` / `ScanReopenDelay`,
   `_consecutiveScanWindows` + `_nextScanWindowUtc` (both under `_watcherLock`), `ResetIdleBackoff`
   called from every connection-state transition, the deferral check in `SyncScanState`, the
   increment in `CloseScanWindowAfterDelayAsync`, and `ReleaseUnretained` applied at all three
   `GetPairedDevicesAsync` call sites. Also the sweep cadence: `SweepIntervalSchedule` /
   `SweepInterval`, `_consecutiveQuietSweeps` (Interlocked, incremented at the top of the sweep
   body), `NextSweepInterval` with its fail-open on a null tree snapshot, and `MarkDisconnected`
   calling `SyncScanState` so a tree-detected drop no longer waits for a sweep that may now be
   minutes away.
2. `tests/ProoveGaming.Infrastructure.Tests/ScanWindowTests.cs` — **new**; pins
   `ShouldCloseScanWindow` (see Test strategy). Lands in the same commit as (1); the rule and its
   tests are one unit.
   *(2026-09-08)* the reopen schedule is pinned in `BluetoothReconnectBackoffTests` instead, beside
   the connect and rediscovery schedules it belongs with — the three rate limits are one story.
3. `docs/plans/fix-devicehost-ble-memory-leak/fix-devicehost-ble-memory-leak-plan.md` — dated note
   (do not rewrite history): the Active-scanning choice and continuous-scan lifecycle are
   superseded by this plan; link here.
4. `docs/CONTEXT.md` — add **Device Association Service (DAS)**; update the **LE advertisement
   watcher** entry ("runs while a tracked paired BLE device is disconnected" → runs in bounded
   passive windows while one is).

## Acceptance criteria

- [ ] Given a paired catalog BLE device that is powered off, when the DeviceHost runs, the log
      shows `LE scan started` followed by `LE scan window closed` ~10 s later — never a scan
      session exceeding ~15 s. *(2026-09-08: the pattern no longer repeats every 30 s — the reopen
      backoff spaces the windows out at 30 s → 2 min → 5 min while nothing changes.)*
- [ ] Given the device stays off, when 10 minutes pass, the log contains no scan session without
      a matching close (except one cut short by a reconnect or shutdown).
- [ ] *(2026-09-08)* Given the device stays off (or a TAIPAN stays on its dongle), when an hour
      passes, the log shows **at most ~12** `LE scan started` lines for that hour, each close
      reporting a growing reopen delay up to `300s` — not one every 30 s.
- [ ] *(2026-09-08)* Given any tracked device connects or disconnects, the very next window opens
      immediately: the close line after it reports `0s` again.
- [ ] *(2026-09-08)* Given a machine with unrelated BLE devices paired (a headset, a phone), when
      the DeviceHost runs for hours, no `BluetoothDevice` produced by `GetPairedDevicesAsync` is
      left undisposed — every enumeration result that is not tracked goes through
      `ReleaseUnretained` (code review; the effect is only observable as DAS memory).
- [ ] *(2026-09-08)* Given nothing connects or disconnects for several minutes, the sweep's own
      cadence backs off to 5 min — so the paired-device enumeration runs ~290 times a day, not
      2,880. Reset to 30 s by the first transition.
- [ ] *(2026-09-08)* Given the BLE device-tree poll has produced no snapshot (its enumeration
      fails, or it has not run yet), the sweep stays at the 30 s active cadence — the escalation
      only applies while the cheap poll that covers for the sweep is demonstrably working.
- [ ] *(2026-09-08)* Given a tracked BLE device drops and the tree poll notices it, a scan window
      opens immediately rather than at the next sweep — `MarkDisconnected` now calls
      `SyncScanState`, matching `DeviceGattDisconnected`. Reconnect latency after a drop is no
      worse than before this plan, and better than before this note.
- [ ] Given the device is powered on during an open window, the card shows connected with the
      same latency as before this change (advertisement → connect, seconds).
- [ ] Given the device is powered on between windows, the card shows connected within ~30 s
      (next window) — or ~3–6 s when Windows auto-reconnects it (tree poll), whichever fires first.
- [ ] Given all tracked devices reconnect mid-window, the scan stops immediately
      (`every tracked device is connected` log line), and no later `window closed` line follows
      for that generation.
- [ ] Given the DeviceHost is stopped or discovery is torn down while a window is open, no
      exception is logged from the window-close task.
- [ ] The watcher is created with `BluetoothLEScanningMode.Passive` (visible in code review; no
      runtime observable).
- [ ] `dotnet test ./src/ProveGaming.slnx` is green, including the existing
      `BleAdvertisementMatcherTests` and `LowEnergyPresenceTests` untouched.

## Test strategy

Kind of change: **bug fix**, but the bug lives in an external Windows service (DAS) whose
accumulation cannot be reproduced inside xUnit — a failing-first repro test is not achievable for
an OS-service leak. The closest honest substitute is pinning the new bounded-scan contract so a
regression (someone removing the window, or breaking the generation guard) fails a test, plus
keeping every existing pin green.

In `tests/ProoveGaming.Infrastructure.Tests/ScanWindowTests.cs` (mirroring the comment-forward
style of `BleAdvertisementMatcherTests`):

- `ShouldCloseScanWindow` with matching generation + started watcher → **true** (the golden path:
  a window that ran its course closes).
- Stale generation (scheduled ≠ current) + started watcher → **false** (the exact race the token
  exists for: a close scheduled for an old window must not kill a new one).
- Matching generation + watcher not started → **false** (already stopped by reconnect or
  teardown; nothing to do).

Deliberately not covered by automated tests: the WinRT watcher lifecycle itself (Start/Stop/Status
transitions need a real radio), DAS memory behaviour (external process, hours-long timescale),
and the InTheHand GATT paths — all exercised by the QA steps instead. Existing tests
(`BleAdvertisementMatcherTests`, `LowEnergyPresenceTests`, and the rest of the Infrastructure
suite) must stay green unmodified.

## QA (manual test steps)

Precondition: a Windows PC with Bluetooth, a PG Bluetooth device (e.g. PG Rampage BT keyboard)
paired, the Proove Gaming app installed and running. Note: memory DAS has already leaked is never
returned until the service restarts — start from a clean baseline.

1. Reboot the PC (or run `Restart-Service DeviceAssociationService` as administrator). Open Task
   Manager → Processes, find **Service Host: Device Association Service**, note its memory
   (typically tens of MB).
2. Power the PG Bluetooth device **off**. Leave the Proove Gaming app running.
3. Wait 2–3 hours. Expected: Device Association Service memory stays roughly flat (well under
   ~200 MB) and its CPU sits near 0%. Before this fix it grew continuously into hundreds of MB
   or GB with sustained CPU load.
4. Power the device **on**. Expected: within ~30 seconds its card in the app shows connected,
   with a battery percentage.
5. Power it **off** again. Expected: the card shows disconnected within ~10 seconds.
6. Turn Windows Bluetooth off and back on (Settings → Bluetooth & devices). Expected: the card
   disconnects when the radio goes off and comes back after it returns and the device reconnects.
7. Repeat step 4/5 a few times in a row. Expected: connect/disconnect keeps working; the app and
   the device service do not crash or hang.
8. *(added 2026-09-02, PR #130 review m5)* Power the BLE device **off**, wait at least one full
   minute (so the current scan window closes), then power it **on** and measure the time until
   its card shows connected. Expected: **up to ~30–40 seconds in the worst case** — this is the
   accepted trade-off of this plan (the LE scan now runs in 10-second windows reopened every
   30 seconds instead of continuously). Slower-than-before is EXPECTED here, not a regression;
   investigate only if it regularly exceeds a minute.

   > *(2026-09-08)* The windows are now spaced by the reopen backoff, so a device powered on after
   > a long absence may fall outside every window. **The card must still come back within seconds**,
   > because that path is the 3 s device-tree poll (Windows reconnects the device itself), not the
   > scan. If a device regularly takes minutes to reappear after being switched on, that is the
   > regression to report — it means the tree poll is not covering that model and the backoff has
   > to yield for it.

9. *(added 2026-09-08 — the dongle case, the second incident's actual scenario)* Pair a **PG
   TAIPAN** over Bluetooth, then flip it to its **2.4G dongle** and leave the App running
   overnight from a clean DAS baseline (step 1). Expected: the App shows the mouse connected over
   the dongle; `LE scan started` lines thin out to roughly one per five minutes; **Service Host:
   Device Association Service** stays in the tens of MB by morning. Before this change it grew
   until the machine ran out of memory.

10. *(added 2026-09-08)* From that state, flip the mouse **to Bluetooth**. Expected: the card shows
    connected over Bluetooth within seconds, and the log's next `LE scan window closed` line
    reports a `0s` reopen delay — the transition reset the backoff.

## Review focus

- `thread-safety` — every watcher mutation, including the new window-close, stays under
  `_watcherLock`; the generation field is only read/written inside it.
- `async/await` — the close task is a properly guarded fire-and-forget: cancellation swallowed
  by design, everything else caught and logged (it runs with no caller to observe a throw).
- `resource disposal` — no new subscriptions; teardown still leaves no watcher reachable.
- `performance hot-path` — `OnAdvertisementReceived` must remain untouched: O(1), no allocation
  on non-match.

## Performance & quality

- Hot path: the advertisement `Received` handler — **unchanged by this plan**, and must stay so.
- New steady-state cost: one short-lived `Task` per opened window (at most one per 30 s sweep),
  negligible.
- Shared state: `_scanWindowGeneration` joins the state already guarded by `_watcherLock`; the
  close task takes the lock once after its delay. No new lock, no lock held across an await.
- Passive mode strictly reduces radio and DAS work per advertisement; the duty cycle bounds the
  total. Nothing on the UI/App side is touched.

## Risks

- **Passive + duty-cycle may still not be enough** on some machines — DAS behaviour is
  version-dependent and we control only our contribution. If a tester still sees growth with the
  bounded scan, option 3 (no watcher) is the escalation path; this plan's logging (started /
  window closed) makes the correlation checkable from the DeviceHost log (`<install>\host\logs` since 2026-09-24; `%TEMP%\Logs` before).
- **A device reachable only via the LocalName fallback** (tracked id that isn't a parseable
  address) may no longer be woken by an advertisement under passive scanning; it falls back to
  the sweep/tree paths. No known PG device is in this state today.
- **`Start()` on a `Stopping` watcher** can throw when a reopen races a close; the existing catch
  turns that into a Debug log and a 30 s retry. Acceptable, but the reviewer should confirm the
  close task cannot make this frequent (it fires at most once per window).
- **Tester expectations**: already-leaked DAS memory persists until the service restarts. QA step 1
  exists so a stale baseline is not read as "the fix failed".

## Open questions

None — both resolved with the user on 2026-09-02:

1. **Window length**: **10 s** per 30 s sweep (~1/3 duty), as proposed. Shorten to 5 s only if a
   tester still sees DAS growth with the bounded scan.
2. **Option 3 follow-up**: **deferred**. No hardware validation of watcher removal for now; ship
   passive + duty-cycle and revisit option 3 only if DAS growth is reported again afterwards.

## Review findings

### Round 1 — Ship: yes (2026-09-02)

Reviewed together with `../add-stale-pairing-detection/` — one working-tree delta implements
both plans; findings specific to the stale-pairing veto live in that plan's journal.

#### Plan conformance
- Implemented as planned: Passive mode, `ScanWindow` = 10 s, `_scanWindowGeneration` under
  `_watcherLock`, one close scheduled per successful start, `ShouldCloseScanWindow` pinned rule +
  `ScanWindowTests` (3 cases), comment updates, old-plan superseding note, CONTEXT.md.
- Deviates from plan: the close task drops the `_periodicCheckCts` token the plan originally
  specified — correct, and already recorded as a dated correction in Recommended approach (the
  captured token would have been the one cancelled moments later, so the first window would never
  close). Plan and code agree; no action.
- Accepted limitation (per plan): passive scanning may not reduce DAS growth enough on some
  Windows builds — escalation path (watcher removal) documented; the started/window-closed log
  lines make it checkable from `%TEMP%\Logs`.

#### Code
- [x] **S1. A failed `Stop()` in the window-close task silently restores the unbounded scan** —
  *(closed 2026-09-02 by /coder — via a bounded retry of the same generation (option B shape),
  not option A's discard: unlike the sweep's Aborted branch, this watcher is still `Started`, and
  dropping the field's only reference to a running watcher is the unstoppable-orphan-scan race
  `_watcherLock` documents — plus a second watcher would then scan alongside it. The retry
  re-arms every `ScanWindow` and the generation/status guard ends it on reconnect, teardown, or a
  newer window.)*
  `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:411` — Should-fix
  **Problem:** `CloseScanWindowAfterDelayAsync` calls `watcher.Stop()`; if it throws while the
  watcher's `Status` remains `Started`, the catch at `:420` logs Debug and gives up. The next
  sweep sees `Status: Started`, early-returns, and never schedules another close — the scan then
  runs continuously until a reconnect or teardown, which is exactly the failure mode this plan
  exists to remove.
  **Why it matters:** one transient WinRT failure at the single close attempt undoes the duty
  cycle for the rest of the disconnection (hours/days on a ghost machine). It self-heals only if
  the same failure also flips the watcher to `Aborted` (the sweep then rebuilds it) — not
  guaranteed.
  **Fix options:** A (correct): in the catch, treat the watcher as lost the same way the sweep's
  `Aborted` branch does — under `_watcherLock`, unsubscribe `Received`, null `_watcher`; the next
  sweep builds a fresh watcher and schedules a fresh close. B (minimal): schedule one retry close
  after another `ScanWindow`. Option A mirrors existing discipline and is preferred.

#### Tests
- `ScanWindowTests` pin all three guard directions of `ShouldCloseScanWindow` — adequate for the
  pure rule; the WinRT lifecycle is QA's per plan. No gap beyond S1's path (which is not unit
  -testable without a watcher seam; cover via the S1 fix's code reading clearly instead).

#### Nits
- none

#### What's done well
- The generation-token design is minimal and correct: every interleaving (reconnect-stop → reopen,
  sweep firing mid-window, teardown before close) resolves to a no-op via one pinned rule.
- The dropped-CTS correction caught a genuine ordering bug in the plan before it shipped, and the
  plan was updated instead of silently diverging.
- `OnAdvertisementReceived` is untouched — the hot path constraint held.

#### Verdict
Ship: yes — no blockers; S1 is a rare-edge robustness gap with a cheap, pattern-matching fix.
First: S1.

### Round 2 — Ship: yes (2026-09-02)

#### Plan conformance
- Unchanged from Round 1; the only delta is the S1 fix
  (`src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:398-439`).

#### Code
- **S1 verified closed.** The fix diverges from Round 1's option A (discard) in favour of a
  bounded same-generation retry — and the divergence is *correct*: option A was flawed for a
  watcher still `Started`, where dropping the field's only reference orphans a running scan
  (the exact race the `_watcherLock` comment documents) and the sweep's fresh watcher then
  doubles the radio load. Interleavings re-traced: success returns inside the lock (no retry);
  a reconnect-stop, teardown, or newer window ends the chain at the guard; post-`Dispose` at
  most one `ScanWindow`-long delay no-ops; persistent `Stop()` failure retries at 10 s cadence,
  which is cheap and is the intended pressure toward closing. No new findings.

#### Tests
- Infrastructure suite 836/836 green after the fix (the retry path itself has no watcher seam
  to unit-test, as noted in Round 1).

#### Nits
- none new; Round 1's probe-name nit stands as accepted.

#### What's done well
- The divergence from the reviewer's suggested fix was argued, documented in both the code
  comment and the journal, and is the better design — exactly how a review disagreement should
  be handled.

#### Verdict
Ship: yes — S1 closed correctly; nothing outstanding in this plan.

### Round 3 — Ship: yes (2026-09-08)

Reviews the working-tree delta that responds to the **second DAS incident** (QA left the App running
with a TAIPAN on its 2.4G dongle; the machine ran out of memory and rebooted). Full diagnosis:
[`docs/audits/2026-09-08-das-dongle-incident.md`](../../audits/2026-09-08-das-dongle-incident.md).
The delta adds three things to `BluetoothDeviceDiscoveryService`: a scan-window **reopen backoff**
(G1), **`ReleaseUnretained`** disposal of unretained paired-device enumeration results (G2), and a
**sweep cadence escalation** (G3), plus the pinned schedules in `BluetoothReconnectBackoffTests`.

Verified: `dotnet build ./src/ProveGaming.slnx -c Release` clean; `dotnet test ./src/ProveGaming.slnx
-c Release` → 2,588 passed / 0 failed (995 Infrastructure, 8 Installer, 1,585 App) — the same
command and configuration CI runs.

#### Plan conformance

- **Implemented beyond the plan, and correctly diagnosed.** This round is not an implementation of
  the plan as written — it is a repair of a bound the plan *thought* it had. The plan's own thesis
  ("duty-cycled windows ~10 s per 30 s sweep") is sound about *how much* we scan and silent about
  *how long*, and the incident lived entirely in the second half. The dated note now at the top of
  the plan states that correctly.
- **Deviates from plan — the sweep cadence change is explicitly out of scope.** Scope, line 77:
  *"Out of scope — Any change to `GetPairedDevicesAsync` sweep cadence, the classic-Bluetooth poll,
  the low-energy tree poll, GATT battery reads, the gRPC contract, or anything the App sees."*
  G3 changes exactly that, and (see S1) moves GATT battery read timing with it. The change is
  justified — but the plan currently declares it forbidden while the code does it. See **S2**.
- **Open questions are overtaken by events.** Both answers at lines 324-327 are conditioned on
  "if a tester still sees DAS growth": Q1 says shorten the window to 5 s, Q2 says revisit option 3
  (remove the watcher). A tester *did* see DAS growth again, and neither branch was taken — a third
  option was invented instead. Left as-is the plan reads as a decision procedure that was ignored.
  See **S2**.
- **Acceptance criteria**: the four 2026-09-08 criteria added to this plan are met by the code;
  the three overnight/hardware ones remain unchecked by construction (they need a night and a
  tester, exactly like Round 1's).
- **Accepted limitation (per plan, unchanged)**: reconnection for a BLE device Windows does *not*
  auto-reconnect depends on a scan window. Residual risk is now bounded rather than open-ended —
  worst case ~10 min (see **N4**), not "never" — but it is still a longer worst case than before
  this round, and it rests on the premise recorded in `../add-stale-pairing-detection/` that every
  current PG BLE device is HID-over-GATT and reconnected by Windows itself.
- **Out of scope, correctly left alone**: the cross-transport scan-need veto. The audit's section 3
  argument is right and worth keeping — `IsHidDevicePresent` on a dongle row answers "is the
  receiver plugged in", not "is the device on it", so a veto built on it would suppress BLE
  reconnection for a mouse the user just switched *to* Bluetooth. That is the one failure mode this
  file has repeatedly been burned by, and declining it is the correct call.

#### Code

- [x] **S1. The GATT battery refresh now rides the escalating sweep clock, so its 5-minute interval
      can beat at up to ~10 minutes** — `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:1426`
      (`RefreshConnectedBatteries`, defined at `:757`, gated by `BatteryRefreshInterval` at `:1275`) — Should-fix
      **Problem:** `RefreshConnectedBatteries` is called only from the sweep body and self-gates on
      its own 5 min clock. That gate is only *evaluated* when the sweep runs. With the sweep now
      escalating to 5 min, a refresh that becomes due one second after a sweep waits for the next
      one — so the effective interval for a connected BLE device is up to ~10 minutes.
      **Why it matters:** "quiet" means no connection-state transitions, which is precisely the
      state a working, connected Bluetooth mouse sits in all day. So this is not an edge case — it
      is the normal case. The user-visible effect is bounded (for devices Windows publishes
      `DEVPKEY_Bluetooth_Battery` for, `PublishTreeBattery` on the 3 s poll is the preferred source
      and is unaffected), but for a device without a tree battery value the displayed level can be
      twice as stale as `BatteryRefreshInterval` promises. It is also the one thing in this round
      that changes behaviour the *user* can see, which is what the plan's out-of-scope line was
      protecting.
      **Fix options:**
      - Option A (correct, and cheap): call `RefreshConnectedBatteries()` from
        `PollLowEnergyPresence` instead of from the sweep. It already carries its own time gate, so
        the 3 s poll simply evaluates that gate accurately and the battery cadence stops depending
        on the enumeration cadence entirely. One moved call.
      - Option B (minimal): cap `NextSweepInterval()` at `BatteryRefreshInterval / 2` so the gate is
        always evaluated at least twice per interval. Keeps the coupling, hides it better.
      - Option C: accept, and say so in the `BatteryRefreshInterval` comment — but then the comment
        "five minutes" must be corrected to "up to ten".

- [ ] **S2. The plan now contradicts the code in two places, and one of them is a stated non-goal** —
      `docs/plans/fix-das-memory-growth/fix-das-memory-growth-plan.md:77` (Scope, Out of scope) and
      `:320-327` (Open questions) — Should-fix
      **Problem / why it matters:** the delta changes the sweep cadence, which Scope forbids, and
      resolves the incident by a route neither Open question anticipated. The repo rule is that a
      plan is the standing description of how the feature works and `/reviewer` reviews against it;
      a plan whose Scope section forbids what the code does sends the next round chasing the wrong
      contract. This is the *only* finding in this round that would still be wrong tomorrow if
      nobody touched it — the code is fine, the record is not.
      **Fix:** dated amendments, not rewrites. In Scope, mark the sweep-cadence bullet as amended
      2026-09-08 with a pointer to the audit's G3 (why it moved from non-goal to fix: it is the same
      unbounded-lifetime shape as the scan, and every job the sweep does is covered faster by the
      3 s polls or the rediscovery trigger while it waits). In Open questions, add a dated
      resolution: growth recurred, Q1's 5 s window and Q2's watcher removal were both considered and
      neither was chosen, because the incident was not "the window is too long" but "the window
      never stops reopening".

- [ ] **S3. The abandoned-enumeration cleanup runs native disposals on a thread we do not own** —
      `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:1939-1947` — Should-fix
      **Problem / why it matters:** the continuation that releases a timed-out enumeration's results
      is scheduled with `TaskContinuationOptions.ExecuteSynchronously`, so where the runtime allows
      it the disposal loop runs inline on whichever thread completed the WinRT operation — a system
      callback thread, disposing N native `BluetoothLEDevice` objects. There is no latency
      requirement on this cleanup whatsoever: it is a best-effort release of objects nobody is
      waiting for. The flag buys nothing and adds the only path in this change where our work can
      land on a stack we do not control.
      **Fix:** drop `ExecuteSynchronously` and keep `OnlyOnRanToCompletion` + `TaskScheduler.Default`
      so the release runs on the thread pool. One token.

- [ ] **S4. The assertion that states what the bound is *worth* hardcodes a constant it cannot see,
      so it can silently stop testing anything** —
      `tests/ProoveGaming.Infrastructure.Tests/BluetoothReconnectBackoffTests.cs:228-235`
      (`TheSteadyStateScanDutyIsAboutThreePercent`) — Should-fix
      **Problem / why it matters:** the test computes duty from a local `TimeSpan.FromSeconds(10)`
      because `ScanWindow` is `private` (`BluetoothDeviceDiscoveryService.cs:1189`). Raise
      `ScanWindow` to 60 s — a plausible tuning, and Round 1's Open question explicitly contemplated
      changing it — and the real steady-state duty becomes 17 % while this test still computes 3.2 %
      and passes. A test that keeps passing after the property it names is broken is worse than no
      test, because it is cited as evidence.
      **Fix:** make `ScanWindow` `internal` (the project already has
      `<InternalsVisibleTo Include="ProoveGaming.Infrastructure.Tests" />`) and read it in the test,
      exactly as the four schedules are read.

#### Tests

- [ ] **The invariant the whole fix rests on is unpinned.** All four schedules are pinned as pure
      functions, which is good and is the right level for them — but the property that makes the
      backoff *safe* is not a schedule, it is the wiring: **every connection-state transition resets
      both escalations.** Nothing asserts it. If a future edit adds a fifth transition path (as
      `DeviceGattDisconnected` and `MarkDisconnected` both are today) and forgets
      `ResetIdleBackoff`, every test in this file still passes and a device drop silently inherits a
      5-minute delay. Suggested seam, in the style already used here: extract the decision as a pure
      rule — e.g. `internal static bool ShouldOpenScanWindow(bool needScan, bool watcherStarted,
      DateTime nowUtc, DateTime nextWindowUtc)` — pin its three directions, and have `SyncScanState`
      call it. That converts an untestable field comparison into the same kind of pinned rule as
      `ShouldCloseScanWindow`. Test file: `BluetoothReconnectBackoffTests` (or alongside
      `ScanWindowTests`, which already owns the window-lifecycle rules).
- [x] Four schedules pinned with values, clamps, monotonicity and ceilings; the two idle ceilings
      asserted to agree; a blanket "every schedule has a real ceiling" guard. This last one is the
      test that would have caught the 2026-09-08 recurrence in principle — the scan *was* windowed
      and the window *was* reopened forever — and it is a genuinely good addition.

#### Nits

- **N1.** `ReleaseTrackedDevice` → `MarkDisconnected` now opens a scan window (the new
  `SyncScanState` at `:1076`) for a device that the very next lines untrack, whose own
  `SyncScanState` (`:1864`) then closes it. A brief Start/Stop pair on every device removal —
  harmless, slightly ironic in a change about not asking the radio for things.
- **N2.** `StartDiscoveringCoreAsync:238-246` — a throw from `DeviceNeedToTrack` mid-loop, or the
  `ThrowIfCancellationRequested` immediately after the enumeration, skips `ReleaseUnretained` for
  the remaining devices. One-shot per discovery start, so not worth restructuring, but it is the
  same leak class the round is about; the sweep and rediscovery loops both guard per-device and
  this one does not.
- **N3.** A device discovered by the sweep while already *disconnected* produces no status
  transition (old and new are both `false`), so it does not reset the backoff and its first scan
  window can be up to the ceiling away. Benign — the device is absent, and its power-on is caught by
  the tree poll, which does reset — but it is the one "new thing appeared" case the reset hook does
  not cover.
- **N4.** The audit and this plan both describe the steady state as "one scan window per five
  minutes". Because `SyncScanState` is driven by the sweep, and the sweep's own ceiling is also
  5 min, phase misalignment makes the true worst case ~10 min. The bound holds and the duty
  assertion still passes (reality is better than the claim, not worse), but the wording should say
  "at least five minutes".

#### What's done well

- **The diagnosis is the strongest part.** Establishing that exactly one file in the product touches
  the Device Association Framework — so the dongle could not itself be the load — turned "it
  happened again with a different device" from a contradiction into a scoping proof, and the log
  evidence (387 scan starts, 385 closes, never idle over ten hours) pins the mechanism rather than
  arguing for it.
- **The G2 leak was found by reading the dependency, not by guessing.** `GetPairedDevicesAsync`
  looks like a listing at every call site; nothing short of opening InTheHand's implementation shows
  that it resolves a native device per paired device and disposes none. That is the finding the
  previous audit missed while looking directly at this code.
- **The reasoning for why waiting is safe is explicit and correct**: the scan window is not the
  reconnect path, the 3 s tree poll is, and `MarkDisconnected` gaining its own `SyncScanState` means
  reconnect latency after a drop is *better* than before this round — a change that makes the
  system both cheaper and more responsive, rather than trading one for the other.
- **One reset hook for both escalations**, with the reason written down. The failure mode of two
  hooks drifting apart is exactly how this bound broke the first time.
- **The earlier judgement was revised in the open.** The audit had said "measure G3 before changing
  it", and the reversal is argued rather than quietly made: the measurement would establish whether
  it *matters*, not whether slowing it is *safe*, and a call whose answer cannot have changed does
  not need making.

#### Verdict

Ship: yes — no blockers. The code is correct and well-argued; three of the four should-fixes are
small and one (S2) is documentation that must not be skipped, because it is the only defect that
survives into the next round. S1 is the one with user-visible behaviour behind it.

First: **S2** (amend Scope + Open questions — the plan currently forbids what the code does),
**S1** (move `RefreshConnectedBatteries` off the sweep clock), **S4** (make `ScanWindow` internal so
the duty assertion tests what it names).

Separately reviewed, not part of this work: the Cosmo delta already in the tree
(`CosmoStateReport.cs`, `CosmoStateReportTests.cs`, `CosmoLedProbe.cs`, the `SupportsBattery: false`
catalog row, `add-cosmo-headset-plan.md`). No serious findings — withdrawing both disproven battery
candidates is correct, `DeviceService.cs:116` already answers `Success = false` on the null so no
fabricated level can escape, and the null result is covered by tests. Findings belonging to it, if
any, go in its own plan's journal.

### Round 4 — independent review, both findings closed (2026-09-08)

A second reviewer went over the Round 3 delta and returned two confirmed findings. Both are fixed in
this round; both were verified against the code before fixing, and one of them is a defect Round 3's
own review missed.

#### Code

- [x] **m1 (= Round 3 S1). The GATT battery refresh rode the escalating sweep clock** — closed.
  Independently confirmed with the same interleaving Round 3 recorded: `_consecutiveQuietSweeps`
  reaches 4, `SweepInterval(4)` is 5 min, a refresh falling due one second after a pass waits for
  the next one, and the five-minute interval beats at up to ~10.
  **Fixed by option A**, the smallest of the three: the `RefreshConnectedBatteries()` call moved off
  the end of the sweep body and onto `PollLowEnergyPresence` — the 3 s poll. The method already
  gates itself on `BatteryRefreshInterval`, so nothing about the cadence it promises changed; what
  changed is that the gate is now *evaluated* on a clock that does not escalate. The comment on
  `BatteryRefreshInterval` was rewritten to say why the evaluating clock matters, since that is the
  part the original comment had no reason to think about. The battery cadence no longer depends on
  the enumeration cadence at all, which also removes the coupling that made this a violation of the
  plan's own Out of scope line.

- [x] **m2. A backoff reset landing inside an open scan window was erased by that window's close** —
  closed. **New, and the more valuable of the two** — Round 3 did not catch it.
  **Problem:** `ResetIdleBackoff` zeroed `_consecutiveScanWindows` and set `_nextScanWindowUtc` to
  `MinValue`, but `CloseScanWindowAfterDelayAsync` incremented and overwrote both unconditionally,
  with no record that a reset had happened while the window was open.
  **Verified interleaving** (traced in the code, not hypothesised): a window opens at t0; at t0+4 s
  the tree poll sees a device drop, `MarkDisconnected` calls `ResetIdleBackoff` and then
  `SyncScanState`, which returns at its `Status: Started` check — correctly, a window is already
  open and a second one is neither possible nor wanted; at t0+10 s the close charges the window and
  writes a 30 s delay. So the window after a *fresh disconnect* was 30 s away, not immediate.
  **Why it mattered:** the window is open 10 s per cycle, so this is roughly one interleaving in
  four at the 30 s step — ordinary, not exotic. The damage per occurrence is small (our own GATT
  window is 30 s late; the tree poll still restores the card in ~3–6 s), but it broke the exact
  invariant the whole backoff's safety argument rests on, and it falsified this plan's own
  acceptance criterion — "the close line after it reports `0s` again" — in a case the branch had
  written down as guaranteed.
  **Fixed by option B**, the epoch, rather than option A's duplicated first step. A's trick is what
  `SweepIntervalSchedule` already uses and it would have worked, but it works by coincidence — the
  scan cannot borrow it honestly, because the scan's first step *is* the thing that makes a window
  immediate, so duplicating it would also hand a free extra window to every escalation cycle that
  had no reset at all. The epoch states the intent instead: `_idleEpoch` is bumped by every
  `ResetIdleBackoff`, captured when a window opens, re-checked when it closes, and a window whose
  epoch moved is not charged — the reset stands and the next window is immediate. The retry path
  carries the original epoch, since it is still trying to close the same window.
  **Also fixed, from the same finding's secondary note:** `ScanReopenDelay(0)` is unreachable from
  the production path (the close increments before it looks the delay up), so the Round 3 test
  `TheFirstWindowAfterAChangeIsImmediate` pinned a value the code never reads and was named after an
  invariant it did not check. It is now `ZeroQuietWindowsOwesNoDelay`, documented as the schedule's
  definition rather than the live path, and the real invariant is pinned by the new
  `ShouldChargeQuietWindow` rule — extracted and named for the same reason as `ShouldCloseScanWindow`,
  because this is a race whose failure is invisible in every other test.

#### Tests

- [x] Three new cases pin `ShouldChargeQuietWindow` (spanned no change → charged; spanned a change →
  not charged; several changes → still not charged), and the misnamed schedule test is renamed to
  what it actually asserts. Infrastructure suite: 998 passing.
- [ ] Still open from Round 3: nothing pins the *wiring* — that every transition path calls
  `ResetIdleBackoff`. m2 is a direct instance of why that gap matters: the reset was called
  correctly and was thrown away downstream, which no schedule test could have caught. The epoch rule
  narrows the gap (the discard is now a named, pinned decision) but a future sixth transition path
  that forgets the hook entirely would still pass every test in the file.

#### What's done well

- **The reviewer's refutation attempts are the reason both findings are trustworthy.** m1 checked for
  a second `RefreshConnectedBatteries` caller and for `PublishTreeBattery` covering the gap before
  concluding; m2 tried to prove the interleaving impossible — that `SyncScanState` would open a
  window anyway, or that the early-stop branch would fire — and reported that it could not. A
  finding that survives a genuine attempt to kill it is worth more than three that were never tried.

#### Verdict

Ship: yes — both findings closed, no new blockers. Remaining from Round 3: **S2** (amend the plan's
Scope and Open questions, which still forbid the sweep-cadence change the code makes), **S3**
(`ExecuteSynchronously` on the abandoned-enumeration continuation), **S4** (make `ScanWindow`
internal so the duty assertion tests what it names).
