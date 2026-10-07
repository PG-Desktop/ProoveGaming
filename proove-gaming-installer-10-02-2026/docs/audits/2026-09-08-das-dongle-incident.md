# Device Association Service — second incident, TAIPAN on the dongle (2026-09-08)

**Trigger:** QA left the App running with a **PG TAIPAN connected over its 2.4G dongle**. Windows'
**Device Association Service** (`DeviceAssociationService`, hosted in
`svchost.exe -k LocalSystemNetworkRestricted`) again consumed the machine's memory and the PC
rebooted.

**Relation to the previous audit:** this is a *recurrence* of
[`2026-09-02-device-association-memory-audit.md`](2026-09-02-device-association-memory-audit.md),
not a regression of its fixes. F1 (connect backoff), F2 (tree-owned connection state) and F3
(rediscovery backoff) are all in the tree and all behaving. They bounded three of the four things
we ask DAS for. This audit is about the fourth — the scan itself — and about a leak on the
enumeration path that the previous audit did not look for.

---

## 0. Scope: only one file can cause this

Nothing else in the product touches the WinRT / Device Association Framework path. A repository-wide
search for `Windows.Devices.*` and `InTheHand` returns exactly one production consumer:

```
src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs
```

Every other device path — `DpiStageMonitor`, `TaipanStateMonitor`, `CosmoStateMonitor`,
`SmartInfiniteBatteryMonitor`, `HidDeviceDiscoveryService`, `DeviceNodeQuery`, `DeviceContainerId` —
is SetupAPI/CfgMgr32 or HidSharp, and the previous audit measured those to be flat (§0 there). The
App process itself makes no WinRT Bluetooth call at all; it only speaks gRPC. **The DeviceHost's
Bluetooth service is therefore the only possible source, whatever the connected device happens to
be.** That is what makes "it was on the dongle" a coherent report rather than a contradiction: the
dongle is irrelevant to *how* DAS was loaded — it matters because of what it does to the BLE
service's idea of the mouse.

Four operations in that file reach DAS. Their state before this change:

| Operation | Bounded by | Verdict |
|---|---|---|
| `Gatt.ConnectAsync` | per-device backoff 1 s→60 s, retire after 6 (F1) | bounded |
| `GetPairedDevicesAsync` from the rediscovery trigger | 10 s→30 s→5 min schedule (F3) | bounded rate |
| `GetPairedDevicesAsync` from the periodic sweep | 30 s while anything is tracked, forever | **leaked — G2; unbounded lifetime — G3** |
| `BluetoothLEAdvertisementWatcher` scan | 10 s window per 30 s sweep, **reopened forever** | **unbounded lifetime — G1** |

All four are bounded after this change, and the two idle escalations (G1, G3) share one reset hook
so a machine at rest settles into a single rhythm: one 10 s scan window and one enumeration per
five minutes, both collapsing to immediate the instant anything connects or disconnects.

---

## 1. Findings

### G1. [HIGH] The scan's *duty* was bounded; its *lifetime* was not — and a dongle-mode TAIPAN makes that lifetime permanent

`SyncScanState` opens a scan window whenever any tracked device is `IsPaired && !connected` and not
a 14-day-stale ghost. The sweep re-evaluates that every 30 s, so while the condition holds the
service scans 10 s in every 30 s — **a 33 % duty, 24/7, for as long as the condition lasts.**

The 2026-09-02 plan treated that as a bound because it compared it against a *continuous* scan. It
is not one, because the condition is not transient. It is the normal, indefinite state of ordinary
setups:

- **A TAIPAN paired over Bluetooth but switched to its 2.4G dongle.** The mouse is working
  perfectly, on another transport. Its Bluetooth pairing is fresh (so the stale-ghost veto does not
  apply for 14 days) and its BLE node reports not-connected (so the tree verdict says disconnected).
  Nothing in the BLE service knows the same physical unit is present on the dongle — `MergeTransports`
  is an App/catalog concept that this service never consults. **No amount of scanning can ever
  reconnect it**, and the service scanned for it all night.
- A Bluetooth keyboard switched off for the night — same shape, same permanence.
- Any paired PG device left in a drawer for less than 14 days.

Confirmed on this machine's own DeviceHost log (`%TEMP%/Logs/log20260903.txt`, ~10 h, a tracked
`PG Taipan` marked disconnected): **387 `LE scan started` lines and 385 `LE scan window closed`
lines — the scan was reopened continuously and never once idled.**

This is the mechanism the previous audit's own comment describes: while any scan runs, DAS processes
every advertisement in radio range and accumulates per-rotating-random-address state it does not
return until the service restarts. At 33 % duty it grows roughly three times slower than the
continuous scan that reached ~8.8 GB — which, over a night, is still the whole machine.

### G2. [HIGH] Every paired-device enumeration leaked one native `BluetoothLEDevice` per *non-PG* paired device

`Bluetooth.GetPairedDevicesAsync()` is not a listing. InTheHand's Windows implementation (read from
the 32feet source; the shipped 4.0.44 assembly carries the same `PlatformGetPairedDevices`,
`FromIdAsync`, `NativeDisposeList` members) is:

```csharp
foreach (var device in await DeviceInformation.FindAllAsync(
    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
{
    var bluetoothDevice = await BluetoothLEDevice.FromIdAsync(device.Id);   // native object
    if (bluetoothDevice != null) devices.Add(bluetoothDevice);
}
```

It resolves a **native `BluetoothLEDevice` for every BLE-paired device on the machine** — the
headset, the phone, the watch, not just ours — and disposes none of them. `BluetoothDevice.Dispose`
does release the native object (`DisposeAllNativeObjects` / `NativeDisposeList`), so the wrapper is
a borrow that must be returned.

Our three call sites kept the one or two devices that matched the catalog and **dropped the rest on
the floor**:

- `StartDiscoveringCoreAsync` — once per discovery start.
- `PeriodicReconnectCheckAsync` — **every 30 s, for the life of the service.**
- `TriggerRediscoveryIfUnexplained` — per rediscovery, *and* the whole result of an enumeration
  abandoned on timeout, which still completes with nobody looking at it.

Each dropped wrapper kept its native device alive until the GC happened to collect a tiny managed
object — in a 24/7 service that allocates almost nothing, potentially hours. On a machine with three
unrelated paired BLE devices that is 8,640 leaked native device objects a day, none of it visible in
this process's memory, all of it held in the service that brokers them.

The previous audit fixed the *replacement* case (C23, disposing the tracked instance we overwrite)
and never looked at the far larger set: the ones we never wanted.

### G3. [MEDIUM] The sweep's 30 s `DeviceInformation` enumeration itself

With G2 fixed the sweep no longer *leaks*, but it still performed a full
`DeviceInformation.FindAllAsync` every 30 s — 2,880 a day, for the life of the service, whenever
anything is tracked. That is the last unbounded thing we ask the Device Association Framework for,
and it is unbounded in exactly the way G1 was: not in rate, but in how long the rate is sustained.

Whether that call *alone* moves DAS memory has never been measured (the 2026-09-02 §0 table covered
SetupAPI/CfgMgr32 paths, not this one) — and on reflection it does not need to be, because the
question it answers cannot have changed. Every job the sweep does is covered, faster, by something
cheaper while it waits:

| Sweep's job | What covers it meanwhile | Latency |
|---|---|---|
| notice a device that dropped without an event | BLE presence poll (`PollLowEnergyPresence`) / classic poll | ~3–6 s |
| discover a device paired since startup | `TriggerRediscoveryIfUnexplained` — a newly paired device is a *connected* device | ~10 s |
| rebuild a dead InTheHand instance | same rediscovery trigger | ~10 s |
| notice the radio being switched off | `OnBluetoothAvailabilityChanged` + the tree poll | immediate / ~6 s |

So the sweep got the same treatment as the scan: `SweepIntervalSchedule` — `30 s → 30 s → 1 min →
2 min → 5 min` by sweeps run since the last connection-state change, sharing G1's single reset hook.
2,880 enumerations a day become ~290. The first two steps are both `ActiveSweepInterval` on purpose:
a change landing inside a delay zeroes the counter and the following increment takes it straight
back to 1, and that ordinary race must not punish a real event with a longer interval.

**Fails open on one condition**: while `_liveLowEnergyAddresses` is null — the tree poll has not run
or its enumeration failed — the cheap local cover is not actually there, so the sweep keeps its
original 30 s cadence.

One consequence had to be fixed with it: `MarkDisconnected` (the tree poll's disconnect path) used
to leave the scan window to the next sweep. With the sweep now able to be minutes away, it calls
`SyncScanState` itself — which `DeviceGattDisconnected`, its clean-disconnect twin, has always done.
The two paths now agree, and reconnect latency after a drop is *better* than before this change, not
worse.

---

## 2. What changed

`src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs`:

1. **A scan-window reopen backoff (G1).** `ScanReopenSchedule` — `0 → 30 s → 2 min → 5 min` —
   applied after each window that closes **with no connection-state change**. Every transition
   (`MarkConnected`, `MarkDisconnected`, `DeviceGattDisconnected`, a `RegisterDevice` status change,
   a deliberate discovery restart) calls `ResetScanBackoff`, so the first window after anything real
   happens is immediate, exactly as before. Steady-state duty for a device that is not coming back
   falls from **~33 % to ~3 %** — from ~8 h of scanning a day to ~48 min.

   Waiting is safe because **the window is not the reconnect path** for anything PG ships: Windows
   reconnects HID-over-GATT peripherals itself, and the 3 s `PollLowEnergyPresence` sees that in the
   device tree and calls `MarkConnected` (which refreshes battery) within ~3–6 s. The window only
   accelerates our *own* GATT session; the worst the backoff delays is a battery read, on a device
   the App already shows as connected.

2. **`ReleaseUnretained` (G2).** Every enumeration result we do not retain is disposed, at all three
   call sites plus a continuation for the timed-out enumeration. Guarded by an identity check
   against `_trackedDevices`, so it stays correct even when `DeviceNeedToTrack` threw before
   deciding.

3. **A sweep cadence escalation (G3).** `SweepIntervalSchedule` — `30 s → 30 s → 1 min → 2 min →
   5 min` by quiet sweeps — sharing G1's reset hook (`ResetIdleBackoff`, renamed from
   `ResetScanBackoff` now that it owns both), fail-open while the tree poll has no snapshot, plus
   `MarkDisconnected` opening its own scan window instead of waiting for the sweep.

Two defects in that work were found by a second reviewer and fixed before merge; both are worth
recording, because both are the *same shape* as the incident itself — a bound that looks like a
bound and is quietly not one:

4. **The battery gate was being evaluated by a clock that had just started escalating.**
   `RefreshConnectedBatteries` gates itself on `BatteryRefreshInterval` (5 min) and was called only
   from the sweep, so once the sweep could be 5 minutes away the gate beat at up to ten — in the
   *normal* case, since a connected mouse generates no transitions and is therefore "quiet". Moved
   onto the 3 s presence poll, where the gate is evaluated accurately. A gate is only as good as
   the clock that checks it, and G3 changed that clock without anyone noticing what else read it.

5. **A backoff reset landing inside an open scan window was erased by that window's close.** The
   reset zeroed the counter and cleared the next-window time; the close then incremented and
   overwrote both unconditionally. Interleaving: window opens, device drops 4 s later (reset fires,
   `SyncScanState` correctly does nothing because a window is already open), window closes at 10 s
   and charges a 30 s delay — so the window after a *fresh disconnect* was not immediate, which is
   the single property that makes the whole backoff safe to have. About one interleaving in four at
   the 30 s step. Fixed with `_idleEpoch`: bumped by every reset, captured at open, re-checked at
   close, and a window whose epoch moved is not charged. Pinned by `ShouldChargeQuietWindow`.

Tests: `BluetoothReconnectBackoffTests` now pins **all four** rate limits — connect backoff,
rediscovery, scan reopen, sweep interval — including the steady-state scan duty, the agreement of
the two idle ceilings, and a blanket "every schedule has a real ceiling" assertion. Flattening any
of them back to a constant fails a test instead of silently restoring the incident, which is the
regression that actually happened here: the scan *was* windowed, and the window was reopened
forever.

---

## 3. What was considered and NOT done

- **Vetoing scan need when the same unit is present on another transport.** Semantically the right
  answer for the dongle case (the mouse is *right there*), and the catalog already carries
  `MergeTransports` plus `IHidDeviceDiscoveryService.IsHidDevicePresent`. Rejected for now because
  the signal is not honest for the case that matters: **a 2.4G dongle stays enumerated whether or
  not the device is switched on or paired to it** (the same fact `CosmoStateMonitor` exists for). A
  veto keyed on "the dongle is plugged in" would suppress BLE reconnection for a mouse the user just
  flipped *to* Bluetooth — the "working device shown as permanently absent" failure this file has
  been burned by before. A veto restricted to the **wired** row would be sound (a cable proves the
  unit is on USB) but does not cover this incident. Revisit only with a per-transport liveness
  signal, not a presence one.
- **Removing the watcher entirely** — still the escalation path recorded as option 3 in
  `docs/plans/fix-das-memory-growth/`; still needs per-model hardware validation. With all four
  paths bounded it should no longer be needed.

---

## 4. How to confirm on the tester's machine

The fix cannot be proven from a unit test — the accumulation lives in another process on an
overnight timescale. From a clean baseline (reboot, or `Restart-Service DeviceAssociationService` as
administrator):

1. Pair the TAIPAN over Bluetooth, then switch it to the **dongle** and leave it there. Leave the
   App running.
2. In `%TEMP%\Logs\log<date>.txt`, count `LE scan started`. Before: one every ~30 s, all night.
   After: they thin out to one every ~5 minutes, and each is followed by
   `LE scan window closed — the next sweep reopens it in 300s…`.
3. Watch **Service Host: Device Association Service** in Task Manager overnight. Before: hundreds of
   MB to GB. After: expected to stay in the tens of MB.
4. Flip the mouse to Bluetooth. Its card must connect within seconds (the tree poll), and the log
   must show the scan cadence collapsing back to immediate on the transition.
5. Switch a Bluetooth device off and on a few times. Each transition must produce a `0s` reopen
   delay on the following close line — the escalations are only allowed to build while genuinely
   nothing is happening.

If step 3 still grows with all four paths bounded, the next move is the escalation already recorded
in `docs/plans/fix-das-memory-growth/` (option 3, removing the watcher), and it should be preceded
by a measurement in the style of the 2026-09-02 §0 table — replicate the suspected call in a loop
and sample the DAS PID's private bytes — rather than another round of reasoning.

> **Note (2026-09-24):** the DeviceHost log no longer lives in `%TEMP%/Logs` (for the LocalSystem service, `C:\Windows\Temp\Logs`). It is now `<install folder>\host\logs\log<date>.txt` (default install: `C:\Program Files\ProoveGaming\host\logs\`). The paths in this audit are left as they were when it was written. See `docs/plans/fix-repository-audit-findings/` (S13).
