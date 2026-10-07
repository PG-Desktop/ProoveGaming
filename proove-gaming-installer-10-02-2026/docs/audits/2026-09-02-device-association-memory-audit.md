# Device Association Service memory audit — 2026-09-02

**Trigger:** QA left a mouse and a keyboard connected overnight. In the morning the Windows
**Device Association Service** (`DeviceAssociationService`, hosted in
`svchost.exe -k LocalSystemNetworkRestricted`) held ~12 GB.

**Scope:** `ProoveGaming.DeviceHost`, `ProoveGaming.Infrastructure`, `ProoveGaming.App` — every code
path that runs unattended while the machine is idle.

This audit follows `2026-08-21-performance-memory-audit.md`. That audit's C7/C11 (RGB service cache
eviction) and C2 (RGB glow timers) are fixed in the current tree; C8 and C12 are not. Neither of
those is the cause of this incident.

> **Status — 2026-09-02, same day:** F1–F3 and the §3 carry-overs (C8, C12, C23) plus the §5
> logging items are implemented; §0's measurements and §4's clear list are unchanged. The findings
> below are left as written — they are the record of what was diagnosed, not a task list. **Not yet
> confirmed on hardware:** the §2 QA reproduction still has to be run against a Bluetooth-paired
> device through a sleep/wake cycle before this incident can be called closed.
>
> **F4 IS WRONG — see the correction appended to it.** It was disproved while writing its
> regression test. This invalidates the audit's claim that the keyboard was not the leaking device;
> **any** Bluetooth-paired PG device, keyboard included, can drive F1–F3.

---

## 0. What the memory is NOT

Device Association Service is a Windows process. Nothing we write allocates inside it directly — it
grows because of what we *ask* it for. The repo's own hypothesis is recorded at
`src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs:88`:

> Running SetupDiGetClassDevs every 2s churns the Windows device stack and grows
> DeviceAssociationService memory unbounded (crash).

**That hypothesis was tested on this machine (Windows 11 26200) and does not hold.** Three
enumeration paths were replicated exactly as the code calls them and run in a tight loop while
sampling the `DeviceAssociationService` PID's private bytes:

| Path replicated | Call site | Iterations | DAS private bytes |
|---|---|---|---|
| `CM_Get_Device_ID_List` + `CM_Locate_DevNode` + 4 property reads, `BTHENUM` **and** `BTHLE` (27 nodes/sweep) | `DeviceNodeQuery` | 3,000 (≈2.5 h of the 3 s loop) | 4,263,936 → 4,268,032 (**+4 KB**) |
| `SetupDiGetClassDevs` + `SetupDiGetDeviceInterfaceDetail` over the HID class (17 interfaces) | `DpiStageMonitor.EnumerateCol04Paths`, `TaipanStateMonitor` | 2,000 | 4,268,032 → 4,214,784 (**−52 KB**) |
| `CM_Get_Device_Interface_PropertyW` for `DEVPKEY_Device_ContainerId` | `DeviceContainerId.TryGet` | 2,000 sweeps × 17 paths | 4,214,784 → 4,214,784 (**0**) |

Raw SetupAPI/CfgMgr32 enumeration is flat. **Chasing enumeration cadence will not fix this
incident.** (Those loops are still worth trimming for CPU/wakeup reasons — see §3 — but they are not
the leak.)

The paths that *do* reach Device Association Service are the **WinRT** ones: `DeviceInformation`
enumeration and GATT session creation. The service's whole job is brokering device
association/enumeration objects for those APIs. Every finding below is on that path — and the
codebase already knows it, at `BluetoothDeviceDiscoveryService.cs:793`:

> `Bluetooth.GetPairedDevicesAsync` is a full device enumeration (`DeviceInformation` under the
> hood — the Device Association Framework path), so it is not free.

This is also the same shape as the previously-hit **23 GB DeviceHost leak**
(`docs/plans/fix-devicehost-ble-memory-leak/`), which was a `BluetoothLEDevice` resolved per
advertisement. That fix removed the *resolution*; it did not remove the *retry loop* around it.

---

## 1. Findings

All line references are in
`src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs` unless stated.

### F1. [HIGH] GATT connect is retried per advertisement with no backoff, no attempt cap, and no give-up

Lines 690-711:

```csharp
if (needConnect && _pendingConnections.TryAdd(deviceId, true))
{
    _ = Task.Run(async () =>
    {
        try { await device.Gatt.ConnectAsync(); RegisterDevice(device); SyncScanState(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "GATT connect attempt for {Id}", deviceId); }
        finally { _pendingConnections.TryRemove(deviceId, out _); }
    });
}
```

`_pendingConnections` prevents **concurrent** attempts. It does not prevent **consecutive** ones:
the moment an attempt completes — success or failure — the next matching advertisement starts
another. A BLE peripheral advertises many times per second, and the watcher runs in
`BluetoothLEScanningMode.Active` (line 321). So a paired device we cannot hold a GATT link to
produces `Gatt.ConnectAsync()` calls as fast as they can fail, indefinitely.

On Windows, InTheHand's `RemoteGattServer.PlatformConnect` resolves the native device
(`BluetoothLEDevice.FromIdAsync` / `FromBluetoothAddressAsync`) and creates a `GattSession` with
`MaintainConnection = true` — verified by inspecting `InTheHand.BluetoothLE 4.0.44`
(`net9.0-windows10.0.19041`), which carries `_session`, `GattSession`, `set_MaintainConnection`,
`NativeDevice`. Those are precisely the Device-Association-brokered objects. Producing them in an
unbounded loop for eight hours is the mechanism that fits a 12 GB service.

**Why it never self-terminates.** The loop is self-sustaining by construction:

1. A tracked device reads as not-connected →
2. `SyncScanState` (line 262) turns the Active watcher **on** →
3. advertisements arrive → `needConnect` (line 680) is true → connect attempt →
4. attempt fails → device still reads as not-connected → back to (1).

**Why a device gets stuck in that state** is documented in this same file, around line 1128: after a
device is removed and re-paired in Windows, *"the tracked `BluetoothDevice` instance is dead and its
`Gatt.IsConnected` never becomes true again"*. An overnight sleep/wake or adapter power-cycle is
exactly the event that produces such an instance — and nothing on this path ever retires it.

**Fix direction:** exponential backoff per device id (e.g. 1 s → 60 s cap), an attempt counter that
retires the instance and forces a re-resolve through the paired-device enumeration rather than
retrying a dead handle, and disposal of the previous `BluetoothDevice`/GATT session before replacing
it (this is the still-open C23 from the August audit).

---

### F2. [HIGH] `SyncScanState` and `needConnect` read `Gatt.IsConnected` — the one signal the file says is not the owner of connection state

Lines 262 and 680.

The August/September work deliberately moved connection state to the Windows device tree.
`ResolveConnected` (line ~1438) and its comment are explicit: *"Connection state has exactly one
owner — the device tree, through `PollLowEnergyPresence` and `ResolveConnected`."* `Gatt.IsConnected`
is documented as wrong **in both directions**: stale `true` after the radio is switched off, and
stuck `false` on a dead instance.

But the two decisions that actually cost radio time and DAS objects were left on `Gatt.IsConnected`:

- `needScan = _trackedDevices.Values.Any(d => d.IsPaired && !d.Gatt.IsConnected)` (line 262)
- `needConnect = !device.Gatt.IsConnected && device.IsPaired` (line 680)

`Gatt.IsConnected` reports whether **our GATT client** holds a link — not whether the device is
connected to the machine. For any BLE peripheral whose link Windows owns (an HID-over-GATT keyboard
or mouse is the normal case), our client may hold no session at all, so both predicates stay true
permanently even while the device is perfectly connected and the tree says so.

Result: Active-mode scanning never stops and F1's connect loop never stops, on a machine where
nothing is wrong.

**Fix direction:** drive `needScan`/`needConnect` from `LowEnergyTreeSaysConnected` /
`_trackedStatuses` — the tree verdict that already owns this state — and fall back to
`Gatt.IsConnected` only where the tree has no opinion, exactly as `ResolveConnected` already does.

---

### F3. [MEDIUM] `TriggerRediscoveryIfUnexplained` can latch on and run a `DeviceInformation` enumeration every 10 s forever

Lines 1340-1380, called from the 3 s poll at line 1240.

The guard is a cooldown, not a state machine:

```csharp
if (!unexplained) return;
if (DateTime.UtcNow - _lastRediscoveryUtc < RediscoveryCooldown) return;   // 10 s
```

`unexplained` is true whenever a device-tree node is connected, `IsCatalogBluetoothDevice` matches
its friendly name, and `IsTrackedAsConnected(address)` is false. Nothing bounds how long that
condition can hold. If rediscovery does not resolve it — the device's InTheHand `Name` does not match
the catalog (see F4), or its id does not parse to an address — the condition is still true on the
next poll, and `Bluetooth.GetPairedDevicesAsync()` runs again 10 s later, forever.

That is up to **8,640 `DeviceInformation.FindAllAsync` enumerations per night** on the API this file
itself annotates as *"the Device Association Framework path"*, plus one `LogInformation` line each.

**Fix direction:** cap consecutive unexplained rediscoveries and back off (10 s → 30 s → 5 min), or
mark the specific address as unresolvable after N attempts so it stops re-arming the trigger.

---

### F4. [HIGH — functional] Bluetooth catalog name matching is case-sensitive, so most devices can never be tracked over BLE

Four sites uppercase the haystack and then compare against the raw catalog `Name`:

| Line | Expression |
|---|---|
| 136 (`DeviceNeedToTrack`) | `bluetoothDevice.Name.ToUpper().Contains(bt.Name)` |
| 389 (`BuildMatcher`) | `name.ToUpperInvariant().Contains(bt.Name)` |
| 1037 | `upper.Contains(m.Name)` |
| 1433 (`IsCatalogBluetoothDevice`) | `upper.Contains(m.Name)` |

`string.Contains(string)` is **ordinal and case-sensitive**. The catalog `Name` values in
`src/ProoveGaming.DeviceHost/appsettings.json` are `BLAZE`, `PG TAIPAN`, `Rampage`, `Raster`,
`T-Racer`, `Aress`, `T800`, `Cyrex`, `Cosmo`, `Starlight`.

Only `BLAZE`, `PG TAIPAN` and `T800` are already uppercase, so only those can ever match. Every
mixed-case entry — **`Rampage`, `Raster`, `T-Racer` (the Bluetooth keyboards) and both headsets** —
fails the match unconditionally. `DeviceNeedToTrack` therefore never tracks them, and
`BuildMatcher`'s name fallback is dead for them.

The classic-Bluetooth path gets this right: `ClassicBluetoothDevice.cs:181` uses
`StringComparison.OrdinalIgnoreCase` against `BluetoothName`. Only the LE path is broken.

**This is why the keyboard is not the leaking device.** Of the catalog, `PG TAIPAN` is the only entry
that is both `HasBluetooth` and uppercase — so a Bluetooth-paired **Taipan mouse** is the one device
that can be tracked, and therefore the one that can drive F1/F2/F3. The Bluetooth keyboards are not
leaking because their Bluetooth support silently does not work at all.

**Fix direction:** `StringComparison.OrdinalIgnoreCase` at all four sites, plus a test pinning a
mixed-case catalog name. Expect this to *increase* the number of tracked BLE devices — so F1–F3 must
land first or at the same time, or fixing F4 will make the leak worse.

### F4 — CORRECTION, 2026-09-02

**The finding above is wrong, and its conclusion about the keyboard is wrong with it.** It was
disproved while writing its regression test: a whole-catalog assertion that should have failed
against the pre-fix code passed instead.

The step F4 missed is `DeviceMapExpander.Expand`, which every `DeviceMap` in
`IDeviceInfoProvider.DevicesMaps` passes through
(`DeviceInfoProvider.cs:21`). It sets:

```csharp
Name = device.Name?.ToUpper() ?? string.Empty,
```

So the catalog names reaching those four comparison sites are **already upper-cased**, and
`upper.Contains(m.Name)` was comparing an upper-cased haystack against an upper-cased needle. It
matched correctly. `Rampage`, `Raster`, `T-Racer`, `Cosmo` and `Starlight` were *not* excluded, and
Bluetooth keyboards were tracked normally.

What remains true, and what changes:

- **The keyboard is not exonerated.** F4 was the sole basis for "the keyboard's Bluetooth support
  silently does not work at all", and therefore for concluding that a Bluetooth Taipan had to be the
  leaking device. With F4 withdrawn, **any** Bluetooth-paired PG device — Rampage or T-Racer
  included — can drive F1, F2 and F3. QA should reproduce against whichever device was actually
  paired, not assume the mouse.
- **F1–F3 are unaffected.** None of them depended on F4; they were diagnosed independently and the
  §0 measurements still stand.
- **The change was still made, as hardening rather than a fix.** Correctness depended on an
  invariant established three layers away and stated nowhere at the call site, whose failure mode is
  silent — a device that is never matched produces no error, no log line and no exception, it just
  never appears. `OrdinalIgnoreCase` removes the coupling; `BluetoothCatalogNameMatchTests` now pins
  the expander's upper-casing directly *and* asserts the matcher no longer needs it.
- **One real defect did come out of it.** `DeviceNeedToTrack` had no empty-name guard, and
  `"anything".Contains("")` is `true` — so a catalog entry with no `Name` (which the expander renders
  as `string.Empty`) would have matched the first device examined and claimed it under that row. The
  two sibling sites already guarded; this one did not. Latent, since every shipped entry is named.

**Process note:** the audit called this HIGH on a reading of four call sites without tracing where
their inputs came from. The check that would have caught it — following `DevicesMaps` back to its
producer — is the same one the audit applied rigorously to the memory findings and skipped here.

---

## 2. Recommended order

1. **F2** — smallest change, removes the permanent-scan condition that arms everything else.
2. **F1** — backoff + attempt cap + dispose the replaced instance (folds in August C23).
3. **F3** — bound the rediscovery trigger.
4. ~~**F4** — only after 1–3 are in, for the reason stated above.~~ **Withdrawn** — see the F4
   correction. The change was made anyway as hardening, and it does not widen the tracked set, so
   the ordering constraint this step described does not exist.

A useful QA reproduction while fixing: pair the device that was actually connected overnight (**not
necessarily the mouse** — see the F4 correction) over Bluetooth, put the machine through a
sleep/wake cycle, then watch `DeviceAssociationService` private bytes and count
`"[BT LE] A catalogued device is live but not tracked as connected"` lines in `%TEMP%/Logs/log.txt` —
a steady ~6/minute is F3 latched on.

---

## 3. Still open from the 2026-08-21 audit (not the cause of this incident)

- **C8** — `AressKeyboardRgbService.cs:54`, `CyrexKeyboardRgbService.cs:52`,
  `RampageKeyboardRgbService.cs:70`, `T800KeyboardRgbService.cs:128` still call
  `DeviceGate.AcquireAsync(path)` with **no `CancellationToken` and no timeout**, so a wedged gate
  holder produces an un-cancellable pile-up that outlives service shutdown. Every keyboard
  key-mapping/actuation/macro service already passes a token; only the four RGB services do not.
- **C12** — `CosmoStateMonitor.cs:96,103` still polls `DeviceList.Local.GetHidDevices(...)` every 2 s
  unconditionally, for the process lifetime, on machines with no Cosmo. Cost is lower than the August
  audit assumed (HidSharpCore caches its device-key list and invalidates it on `WM_DEVICECHANGE` —
  the assembly carries `_hidDeviceKeysCache` / `_hidDeviceKeysCacheNotifyObject`), and §0 shows the
  enumeration itself is not the DAS leak. It is still 43,200 pointless wakeups/day in a 24/7 service
  and should hook `DeviceList.Local.Changed` like `DpiStageMonitor` does.
- **C23** — `RegisterDevice` (line ~499) still drops the replaced `BluetoothDevice` without disposing
  it. Folded into F1's fix.

## 4. Checked and clear

- **App-side overnight timers.** Battery poll 60 s with a re-entrancy guard
  (`HomeViewModel.cs:1161-1173`), update check 6 h (`UpdateCheckService.cs:25`), notifications capped
  at 100/owner with trim-on-insert. RGB glow timers are now stopped on exit (August C2 fixed).
- **`AudioMuteMonitor`** (2 s COM endpoint resolution) never starts with only a mouse and keyboard
  present — `Track` only adds cards with `HasAudioIndicators`, and stops the timer when the set is
  empty.
- **gRPC `SubscribeOnDevices`** (`DeviceService.cs:664-745`) balances every event subscription in a
  `finally`. No handler leak across stream reconnects.
- **`DeviceRgbServiceFactory`** caches are invalidated on device-list change and the evicted-instance
  cache is bounded by distinct physical units (August C7/C11 fixed).
- **`DeviceGate`** gates are process-lifetime but bounded by enumerated device paths — deliberate and
  documented.

## 5. Not memory, but worth a ticket

`src/ProoveGaming.DeviceHost/appsettings.json` configures the Serilog file sink with
`rollingInterval: Day` but no `retainedFileCountLimit` and no `fileSizeLimitBytes` override. A 24/7
LocalSystem service writes `%TEMP%/Logs/log.txt` forever, one file per day, none ever removed.
`HidDeviceDiscoveryService.WriteLogs` (line 500) emits one `LogInformation` line per HID device on
the machine on **every** device-list change, which makes the daily file grow with unrelated USB
activity. Set `retainedFileCountLimit` (7 is normal) and drop `WriteLogs` to `Debug`.

> **Note (2026-09-24):** the DeviceHost log no longer lives in `%TEMP%/Logs` (for the LocalSystem service, `C:\Windows\Temp\Logs`). It is now `<install folder>\host\logs\log<date>.txt` (default install: `C:\Program Files\ProoveGaming\host\logs\`). The paths in this audit are left as they were when it was written. See `docs/plans/fix-repository-audit-findings/` (S13).
