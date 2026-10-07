# add-stale-pairing-detection — plan

Date: 2026-09-02. Author: /architect.

## Goal

Teach `BluetoothDeviceDiscoveryService` to recognise a **stale ghost pairing** — a tracked, paired
BLE device that has not connected for weeks and never will (typically an old pairing left behind
when a device's BLE address changed on re-pair, as PG Taipan's does) — and exclude it from the
"does anything need the LE scan" decision. Today one ghost keeps the scan-need condition
permanently true: even after ../fix-das-memory-growth/fix-das-memory-growth-plan.md bounded the
scan to ~10 s windows per 30 s sweep, a machine with a ghost reopens those windows forever, paying
the Device Association Service load and radio time for a device that cannot come back. Observed on
a tester machine (log `20260827`): two tracked PG Taipan entries, `D104…` (ghost, never connects)
and `D112…` (real), scan on one second after startup with no stop.

## Scope

**In scope**

- A hardware probe (`ProoveGaming.Device.Testing`) verifying the staleness signal before any
  behaviour changes: does `DEVPKEY_Device_LastArrivalDate` on a `BTHLE` device node update on
  every reconnect, and does a ghost node carry a weeks-old value? **The probe gates the rest of
  this plan** — if the property does not behave as hypothesised, stop and come back to the user
  with the findings (see Options considered for the fallback).
- `DeviceNode` gains an optional `LastConnectedUtc`; `DeviceNodeQuery` reads the property.
- `BluetoothDeviceDiscoveryService`: a per-address last-connected snapshot from the existing 3 s
  tree poll; the scan-need condition in `SyncScanState` skips devices whose node is present,
  disconnected, and last connected more than `StalePairingThreshold` (14 days) ago. Fail open —
  a device whose staleness cannot be read still counts toward scan need.
- A one-time (transition-only) log line when a device becomes excluded, so QA and future
  diagnostics can see it.
- Pinned pure rule + unit tests in `tests/ProoveGaming.Infrastructure.Tests`.
- Glossary: **Stale ghost pairing** in `docs/CONTEXT.md`; dated cross-note in
  ../fix-das-memory-growth/fix-das-memory-growth-plan.md.

**Out of scope**

- Removing the ghost pairing from Windows (that is the user's Bluetooth settings; we only stop
  paying for it). No un-pairing calls, no writes to the device tree.
- Excluding the ghost from the advertisement **matcher** — deliberately kept: windows opened for
  other devices still listen for the ghost, so if it ever advertises again it reconnects and the
  exclusion self-heals. Exclusion changes the *need* computation only.
- Any change to sweep cadence, classic-Bluetooth polling, GATT battery reads, the gRPC contract,
  or anything the App sees. No new NuGet package, no `.slnx` / `Directory.Build.props` change.
- Host-side persistence of connection history (see option 2 — only reconsidered if the probe
  disproves option 1).

## Options considered

1. **`DEVPKEY_Device_LastArrivalDate` from the device tree (recommended, probe-gated).** Windows
   already keeps a per-node timestamp of when the devnode last arrived (started); for a BTHLE node
   that plausibly means the last connection. + Persistent across host restarts and reboots for
   free; read on the same CfgMgr32 path (`DeviceNodeQuery`) the 3 s poll already walks; a ghost is
   excluded within one poll of startup. − The semantics ("arrival" = reconnect, not
   pairing/boot) are undocumented for BTHLE and must be measured — hence the probe. The repo has
   precedent for exactly this (`BluetoothPresenceWatchProbe` measured `DEVPKEY_Device_IsConnected`
   behaviour on 2026-08-20 before `PollLowEnergyPresence` was built on it).

2. **Host-persisted "last connected" map** (JSON under ProgramData, written on every connect
   transition). + Semantics fully ours. − New persistence surface in the DeviceHost (file
   lifetime, corruption, service-account paths), and blind for the first weeks after install —
   a pre-existing ghost is only excluded `threshold` days after the *host* first sees it, not
   after the *device* last connected. Fallback if option 1's probe fails.

3. **In-memory "disconnected since" only.** + Trivial. − Resets on every service restart; at a
   14-day threshold it would effectively never fire on real machines (reboots, updates).
   Rejected.

4. **Heuristic "two tracked ids, same catalog model, one connected"** — treat the older address
   as ghost. − Indistinguishable from a user who owns two units of one model (a case the code
   already defends elsewhere: see `DeviceNeedToTrack`'s per-id catalog map). Rejected.

## Recommended approach

> **Probe outcome (2026-09-02, /coder — see [`notes.md`](notes.md)):** the gate PASSED, but on a
> different key. `DEVPKEY_Device_LastArrivalDate` is falsified (identical adapter-event value on
> every node, unmoved by reconnects). The verified signal is
> **`DEVPKEY_Bluetooth_LastConnectedTime`** (`{2bd67d8b-8beb-48d5-87e0-6cda3428040a}`, pid 11):
> per-device, persistent, stamped on both connect and disconnect, months-old on abandoned
> pairings. The steps below are updated to that key; the mechanism is otherwise unchanged. One
> addition from the probe: the property is stored as local-time FILETIME, so elapsed time may be
> off by hours or slightly negative — irrelevant at a 14-day threshold, and a "future" stamp
> reads as fresh, which is the fail-open direction.

Option 1, in three steps — the probe first, and the probe's findings recorded before the
behaviour change lands.

**Step 1 — probe.** `LastArrivalDateProbe` in `src/ProoveGaming.Device.Testing` (own class, wired
into `Program.cs` per that project's convention): enumerate `BTHLE\Dev_*` nodes via the same
CfgMgr32 calls `DeviceNodeQuery` uses, print instance id, friendly name, `IsConnected`, and
`DEVPKEY_Device_LastArrivalDate` (+ `LastRemovalDate` for context). Guided phases: baseline →
power the device off → on again → re-read. READ-ONLY. Success criterion: the reconnect moved
LastArrivalDate to "now", and any known ghost node shows a weeks-old value. Findings go into
[`notes.md`](notes.md) next to this plan (created by the probe run).

**Step 2 — plumbing.** `DeviceNode` gains `DateTime? LastConnectedUtc = null` (positional record —
appended with a default so existing constructions, including tests, compile unchanged).
`DeviceNodeQuery.EnumerateLowEnergyNodes` fills it via a new `GetFileTimeProperty` helper
(`DEVPROP_TYPE_FILETIME`, key `{2bd67d8b-8beb-48d5-87e0-6cda3428040a}, pid 11` —
`DEVPKEY_Bluetooth_LastConnectedTime`, per the probe), null on any failure. Classic nodes don't
need it — leave `EnumerateClassicBluetoothNodes` filling null.

**Step 3 — the exclusion.** In `BluetoothDeviceDiscoveryService`:

- `private static readonly TimeSpan StalePairingThreshold = TimeSpan.FromDays(14);` next to the
  other cadence constants, with the why-comment.
- `PollLowEnergyPresence` builds `Dictionary<string, DateTime> lastConnectedByAddress` from the
  nodes (address parsed exactly as `PresentLowEnergyAddresses` does) and swaps it into a
  `private volatile Dictionary<string, DateTime>? _lastConnectedByAddress` — replaced wholesale,
  never mutated, same discipline as `_liveLowEnergyAddresses`.
- `SyncScanState`'s need computation changes from
  `Any(d => d.IsPaired && !d.Gatt.IsConnected)` to the same loop with a staleness veto: a device
  does **not** create scan need when the pinned rule says it is stale. Everything else about the
  method (fail-open catch, matcher build, watcher lifecycle) is untouched — the matcher still
  includes stale devices.
  *(Amended 2026-09-02, PR #130 review m4: the veto is no longer purely per-device. The
  aggregation moved into the pinned `ResolveScanNeed` helper, and a vetoed ghost yields — i.e.
  still creates scan need — when it is the ONLY paired entry, because with no paired non-stale
  sibling there is no other window to reconnect it and the veto would silently kill
  reconnection for a real device back from a long holiday. The "ghost-only machine" residual
  risk noted in this plan's review journal is thereby closed; the perpetual-scan cost returns
  only on such machines. Pinned by the `ResolveScanNeed` cases in `StalePairingTests`.)*
- Pinned rule, in the file's established style:
  `internal static bool IsStalePairing(DateTime? lastConnectedUtc, bool? treeSaysConnected, DateTime nowUtc, TimeSpan threshold)`
  — true only when the tree explicitly says disconnected (`treeSaysConnected == false`; null =
  no opinion = **not** stale, fail open) **and** `lastConnectedUtc` is non-null **and**
  `nowUtc - lastConnectedUtc > threshold`.
- Transition-only exclusion log: a `private readonly ConcurrentDictionary<string, bool> _scanNeedExcluded`
  (or equivalent) so `"[BT] {Name} excluded from scan need — paired but last connected {Days} days
  ago (stale pairing)."` is emitted once when a device first becomes stale, and cleared when the
  device connects again (the same transition `MarkConnected` already owns).

Self-healing worked through: ghost re-paired by the user → new address, fresh node, tracked
normally. Ghost actually powers on → any window opened for another device still matches it (it
never left the matcher) → connect → LastConnectedTime updates → no longer stale. All real devices
connected + only ghosts disconnected → `needScan` false → **no windows open at all** — the
tester's steady state finally costs zero.

## File-level change list

1. `src/ProoveGaming.Device.Testing/LastArrivalDateProbe.cs` — **new**; the read-only probe
   (step 1), guided off/on phases, prints LastConnected/LastArrival/BattSeen per BTHLE node.
2. `src/ProoveGaming.Device.Testing/Program.cs` — wire the probe in under a `last-arrival`
   argument, documented in the header comment like its siblings.
3. `docs/plans/add-stale-pairing-detection/notes.md` — **new**; probe output + verdict, recorded
   before step 3 lands.
4. `src/ProoveGaming.Infrastructure/Classes/ClassicBluetoothDevice.cs` — `DeviceNode` gains
   `DateTime? LastConnectedUtc = null` (appended positional with default).
5. `src/ProoveGaming.Infrastructure/Services/DeviceNodeQuery.cs` — `GetFileTimeProperty` helper +
   fill `LastConnectedUtc` for low-energy nodes.
6. `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs` — threshold
   constant, `_lastArrivalByAddress` snapshot, staleness veto in the scan-need loop,
   `IsStalePairing` pinned rule, transition-only exclusion log.
7. `tests/ProoveGaming.Infrastructure.Tests/StalePairingTests.cs` — **new**; pins `IsStalePairing`
   (see Test strategy).
8. `docs/CONTEXT.md` — add **Stale ghost pairing**. *(Done at plan time, per the glossary rule —
   the term is introduced by this plan.)*
9. `docs/plans/fix-das-memory-growth/fix-das-memory-growth-plan.md` — dated note: the
   "ghost keeps the scan needed forever" case gains a dedicated fix; link here.

## Acceptance criteria

- [ ] Probe run recorded in `notes.md`: powering a PG BLE device off and on moves a per-connection
      timestamp on its node to the reconnect time. **Done 2026-09-02** — holds on
      `DEVPKEY_Bluetooth_LastConnectedTime` (the original `LastArrivalDate` candidate is
      falsified; see the correction note in Recommended approach).
- [ ] Given a machine with a ghost pairing (tracked, paired, tree-disconnected, last connected
      > 14 days ago) and all real devices connected, when the host runs, the log shows the
      exclusion line once for the ghost and **no** `LE scan started` lines repeating afterwards.
- [ ] Given a real device powered off yesterday (last connected < 14 days), scan windows keep
      opening — this plan's veto does not apply to it. *(2026-09-08: no longer "exactly as before"
      — the windows still open, but the reopen backoff added in
      [`../fix-das-memory-growth/`](../fix-das-memory-growth/fix-das-memory-growth-plan.md) spaces
      them out to one per 5 min while nothing changes. Scan NEED is unaffected by that change; only
      the cadence of acting on it is.)*
- [ ] Given a stale-excluded device that advertises while a window is open for another device,
      it still reconnects (matcher unchanged), and after that reconnect it counts toward scan
      need again on a later disconnect.
- [ ] Given `LastConnectedTime` cannot be read for a device (property missing, parse failure),
      that device still creates scan need (fail open) and no exclusion line is logged for it.
- [ ] The exclusion log line appears at most once per staleness transition, not once per sweep.
- [ ] `dotnet test ./src/ProveGaming.slnx` is green; existing `LowEnergyPresenceTests` compile
      and pass unmodified.

## Test strategy

Kind of change: **new feature** — the following tests are non-negotiable coverage, in
`tests/ProoveGaming.Infrastructure.Tests/StalePairingTests.cs` (comment-forward style of
`LowEnergyPresenceTests`, which documents the measured hardware behaviour the rule encodes):

- Disconnected + last connected older than threshold → **stale** (the golden path).
- Disconnected + last connected inside the threshold → not stale (a device on holiday is not a
  ghost).
- Exactly at the threshold boundary → not stale (`>` not `>=` — pin the comparison).
- `treeSaysConnected == true` → never stale regardless of timestamp (a connected device with an
  odd timestamp must not be vetoed).
- `treeSaysConnected == null` (tree has no opinion) → not stale — **fail open**, the same
  tri-state trap `ResolveConnected` documents.
- `lastArrivalUtc == null` → not stale — fail open.

Deliberately not covered by automated tests: the CfgMgr32 property read (needs a real device
tree — verified by the probe and QA), the end-to-end scan-need loop (WinRT watcher), and
LastConnectedTime semantics themselves (that is the probe's job, recorded in `notes.md`).

## QA (manual test steps)

Precondition: a Windows PC with a PG Bluetooth device paired and working, and — for step 5 — a
machine that also carries an old duplicate entry of a PG device in Windows Bluetooth settings
(one that never connects; the tester machine from the August report qualifies).

1. Launch the Proove Gaming app with the PG Bluetooth device connected. Expected: its card is
   shown connected, as usual.
2. Power the device off and on. Expected: the card goes disconnected and comes back within
   ~30 seconds — same as before this change.
3. Power the device off and leave it off for ten minutes. Expected: the card stays disconnected;
   nothing else changes (a recently-seen device is never treated as a ghost).
4. Open the log folder: `host\logs` inside the Proove Gaming install folder (default `C:\Program Files\ProoveGaming\host\logs`; moved there from `%TEMP%\Logs` on 2026-09-24), open today's `log….txt`. Expected for
   steps 1–3: no line containing "excluded from scan need".
5. On the machine with the old duplicate pairing: launch the app with the real devices connected,
   wait two minutes, open today's log the same way. Expected: one line saying the duplicate
   device was "excluded from scan need … (stale pairing)", and after it no repeating
   "LE scan started" lines while the real devices stay connected.
6. On that same machine, open Task Manager and watch "Service Host: Device Association Service"
   for an hour of normal use with all real devices connected. Expected: memory stays flat and
   CPU near 0%.
7. Remove the duplicate entry in Windows Settings → Bluetooth & devices, then re-pair the real
   device. Expected: the card works normally; no "excluded" lines for it in the log afterwards.

## Review focus

- `thread-safety` — the last-arrival snapshot must follow the replace-wholesale/`volatile`
  discipline of `_liveLowEnergyAddresses`; no mutation of a published dictionary.
- `null-safety` — the tri-state veto: null tree opinion and null timestamp must both fail open;
  a collapsed null would silently stop scanning for every device on startup.
- `performance hot-path` — the 3 s poll gains one dictionary build per pass (matches existing
  cost); `OnAdvertisementReceived` must remain untouched.
- `resource disposal` — the probe opens nothing that outlives it; no new subscriptions in the
  service.

## Performance & quality

- Hot path: the advertisement `Received` handler — untouched. The 3 s tree poll gains one
  `Dictionary<string, DateTime>` per pass, in line with the sets it already builds; the sweep's
  need computation gains one dictionary lookup per tracked device (a handful).
- Shared state: one new volatile snapshot field (replace-wholesale) and one transition map —
  both follow patterns already in the file; no new locks.
- No allocations added to any per-advertisement or per-frame path.

## Risks

- **Timestamp semantics** were undocumented for BTHLE nodes — the whole reason the probe gated
  the plan. *Resolved 2026-09-02*: `LastArrivalDate` is indeed unusable (adapter clock), but
  `DEVPKEY_Bluetooth_LastConnectedTime` was measured stamping every connect and disconnect
  (`notes.md`). Residual risk: the key is absent from SDK headers (like `DEVPKEY_Bluetooth_Battery`
  above it in `DeviceNodeQuery`, stable for years) — a future Windows change would degrade to
  fail-open, i.e. back to today's always-scanning behaviour, never to a wrongly silent scan.
- **Wrongful exclusion**: a genuinely-away-for-a-month device whose model Windows does *not*
  auto-reconnect would, on return, only reconnect via a window opened for some *other* device.
  Mitigated by the 14-day threshold, by keeping the device in the matcher, and by the fact that
  every current PG BLE device is HID (Windows reconnects those itself; the tree poll then marks
  the card within ~6 s). Worth re-stating in the exclusion log line so a support case is
  diagnosable.
- **Clock skew**: LastConnectedTime is stored as local-time FILETIME (measured — reads ~3 h off
  through a UTC interpretation), and a user's clock can jump. Accepted: at a 14-day threshold an
  hours-scale error is noise, a "future" stamp reads as fresh (fail open), and the self-healing
  paths above bound the damage of a pathological jump.

## Open questions

None — both resolved with the user on 2026-09-02:

1. **Threshold**: **14 days**, as proposed.
2. **Probe scheduling**: run **locally** on the developer machine against the local PG Rampage BT
   (off/on cycle, verify the timestamp moves on reconnect). The tester machine's ghost is not
   required to unblock implementation; its logs already establish the ghost's existence.

## Review findings

### Round 1 — Ship: yes (2026-09-02)

Reviewed together with `../fix-das-memory-growth/` — one working-tree delta implements both
plans; the scan-window finding (S1, failed `Stop()` in the close task) lives in that plan's
journal.

#### Plan conformance
- Implemented as planned: probe (gate passed, findings in `notes.md`), `DeviceNode.LastConnectedUtc`
  (appended positional with default — `LowEnergyPresenceTests` compile unmodified),
  `GetFileTimeProperty` + `DevpkeyBluetoothLastConnectedTime` in `DeviceNodeQuery`,
  `_lastConnectedByAddress` volatile replace-wholesale snapshot, veto in `SyncScanState`'s
  need loop, `IsStalePairing` pinned rule + 6 `StalePairingTests`, transition-only exclusion log,
  clearing on `MarkConnected` / `RegisterDevice` connect / `ReleaseTrackedDevice`, both glossary
  terms, cross-note in the fix-das plan.
- Deviates from plan: the signal key swapped from `DEVPKEY_Device_LastArrivalDate` to
  `DEVPKEY_Bluetooth_LastConnectedTime` after the probe falsified the former — recorded as a
  dated correction in Recommended approach with measurements in `notes.md`. Plan and code agree.
- Accepted limitation (per plan): the key is absent from SDK headers — a future Windows change
  degrades to fail-open (perpetual scan need, today's behaviour), never to a silent non-scan.
  Verified: every unreadable input path in `IsExcludedAsStalePairing` and `IsStalePairing`
  returns "not stale".
- Residual (out of scope per plan, worth remembering): on a ghost-only machine the veto stops the
  scan *windows*, but the 30 s sweep still calls `GetPairedDevicesAsync` (a DAS enumeration)
  because the ghost stays tracked — the idle 5-minute cadence never engages. Small next to
  scanning; a follow-up could let stale devices count toward "idle".

#### Code
- none — no blockers, no should-fixes. Interleavings checked with concrete scenarios: exclusion
  cannot fire before the first successful tree poll (null snapshot → fail open, one startup
  window at most); the 30 s re-registration of a ghost never clears the exclusion (clear is
  connect-transition-only); tree flicker cannot spam the log (each re-exclusion requires a real
  connect in between); the veto runs outside `_watcherLock` on thread-safe state only.

#### Tests
- [ ] Optional (next `/test-writer` round): pin `LowEnergyLastConnectedByAddress` — a node with
  `LastConnectedUtc = null` is skipped, an unparseable instance id is skipped — mirroring how the
  battery map is exercised. The plan's non-negotiable list (the 6 `IsStalePairing` directions) is
  fully met; this is defence for the builder only. — `tests/ProoveGaming.Infrastructure.Tests`

#### Nits
- `lastConnected!.Value` in the exclusion log — `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:1620`.
  The bang is provably safe (stale ⇒ timestamp non-null via the rule) but a `is { } last` capture
  before the rule call would drop it.
- `DateTime.FromFileTimeUtc` can throw above year ~2554 — `src/ProoveGaming.Infrastructure/Services/DeviceNodeQuery.cs:243`.
  A corrupt property value would abort that enumeration pass (caught → null → callers skip the
  pass, the safe direction). An upper-bound guard next to the `<= 0` one would keep one bad node
  from costing a whole pass.
- `LastArrivalDateProbe` now verifies `LastConnectedTime`; the class name records the original
  hypothesis. The header comment explains the history, so acceptable — rename only if the probe
  is ever touched again.

#### What's done well
- The probe-gate worked exactly as designed: the original key was falsified by measurement, the
  better key was found and verified with a live power cycle, and the plan was corrected with the
  evidence recorded — instead of shipping code on a wrong assumption.
- Fail-open discipline is consistent across every input (id read, tree opinion, timestamp,
  future-skewed clock) and each direction is pinned by a dedicated test.
- Keeping the ghost in the matcher makes wrongful exclusion self-healing rather than terminal.

#### Verdict
Ship: yes — no blockers; the one should-fix in this delta belongs to the fix-das plan (S1
there). First: fix-das S1, then the optional builder test.

### Round 2 — Ship: yes (2026-09-02)

#### Plan conformance
- Unchanged from Round 1; the delta closes both code nits.

#### Code
- Nit 1 (null-forgiving bang) closed: `IsExcludedAsStalePairing` now captures `lastConnected`
  via `is not { } last` before the rule call — the null path short-circuits to the identical
  "not stale" outcome the rule itself returns, so semantics are unchanged and the `!` is gone.
- Nit 2 (`FromFileTimeUtc` range) closed: `DeviceNodeQuery.GetFileTimeProperty` catches
  `ArgumentOutOfRangeException` and returns null, so one corrupt property no longer aborts an
  enumeration pass.
- Probe-name nit intentionally left, per Round 1's own "rename only if touched again". No new
  findings.

#### Tests
- 836/836 green. The optional `LowEnergyLastConnectedByAddress` pin from Round 1 remains open
  for a `/test-writer` round — still optional, not a ship gate.

#### Nits
- none new

#### Verdict
Ship: yes — nothing outstanding; the optional builder test is a follow-up, not a gate.
