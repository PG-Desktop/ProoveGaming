# fix-devicehost-ble-memory-leak — plan

Date: 2026-08-19. Author: /architect.

## Goal

Stop `ProoveGaming.DeviceHost` from leaking memory without bound while its BLE reconnect scan is
active. A tester machine reached ~23 GB of RAM (95% of system memory) with a paired-but-powered-off
PG Bluetooth device — the state that keeps the scan running 24/7. The fix replaces the InTheHand
advertisement-scan path in `BluetoothDeviceDiscoveryService` with a direct WinRT
`BluetoothLEAdvertisementWatcher` whose Received handler costs O(1) per advertisement, so
advertisements from unrelated devices (passing phones, neighbours' gadgets) cost nothing. Two
smaller resource-churn issues found during the same investigation are fixed alongside:
`DpiStageMonitor` starting readers for foreign (non-catalog) HID devices, and
`DeviceRgbServiceFactory` re-constructing keyboard RGB services (with a full HID enumeration and a
log line) on every call.

> **Note (2026-09-02) — partially superseded by
> [`../fix-das-memory-growth/fix-das-memory-growth-plan.md`](../fix-das-memory-growth/fix-das-memory-growth-plan.md):**
> this plan's watcher survives, but two of its choices are replaced. The scan no longer runs
> continuously until reconnection (a bounded ~10 s window per sweep, rescheduled while a tracked
> device stays disconnected) and no longer uses `Active` scanning mode (`Passive`; the LocalName
> fallback the Active choice fed degrades deliberately). Reason: a continuous scan — kept
> permanently alive on tester machines by stale ghost pairings — made Windows' Device Association
> Service accumulate gigabytes of memory processing advertisements. The in-process leak this plan
> fixed stays fixed; the follow-up bounds the load we push onto Windows.

## Context / problem

Root cause, diagnosed by reading InTheHand.BluetoothLE 4.0.44 source (32feet repo) — treated as
established input to this plan (full chain recorded in the maintainer's diagnosis notes):

- `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs` starts an InTheHand
  `BluetoothLEScan` (`Bluetooth.RequestLEScanAsync`) whenever any tracked paired device is
  disconnected, and the gate fails open (scans when state cannot be read). A paired-but-switched-off
  PG device keeps the scan running permanently.
- On Windows, InTheHand's implementation starts a `BluetoothLEAdvertisementWatcher` with **no
  advertisement filter** and ignores `KeepRepeatedDevices = false` (its
  `PlatformKeepRepeatedDevices => true`).
- For **every** advertisement from **every** BLE device in radio range, the library's
  `BluetoothAdvertisingEvent` constructor runs **before** any subscriber filtering: it calls
  `BluetoothLEDevice.FromBluetoothAddressAsync(...)` and blocks the callback thread with
  `.AsTask().Wait(7000)`. Per Microsoft's docs (quoted in that constructor), requests to unreachable
  devices take 7 s each and queue serially.
- Passing phones use rotating random addresses and are unreachable, so nearly every lookup hits the
  7 s timeout; advertisements arrive much faster than one per 7 s, so the backlog of pending WinRT
  operations, blocked threadpool threads (~1 MB stack each) and undisposed native
  `BluetoothLEDevice` objects grows without bound. That is the multi-GB climb at near-zero CPU.
- Disposing `e.Device` in our handler does **not** fix it — the damage happens inside the library
  before our handler runs.

Secondary churn (same investigation, local DeviceHost logs — not GB-scale but steady waste):

1. `src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs` — `EnumerateCol04Paths()` matches ANY
   HID whose path contains `&mi_01&col04`, any vendor. A foreign device (VID 056D, PID C077 —
   a Logitech unit on the log machine) opens with 0-byte reads, so its reader starts, exits and is
   restarted by the ~30 s safety-net enumeration: ~1,350 cycles/day, each burning a new `Thread`, an
   undisposed `CancellationTokenSource`, two log lines and a `QueryDeviceState` round-trip.
2. `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` — Easen and Telink mouse
   services are cached (`_serviceCache` / `_telinkCache`), but every keyboard RGB service
   (Rampage / Aress / Cyrex / Raster / T-Racer / T800) is constructed fresh on every
   `CreateForDevice` call, each construction re-enumerating HID devices and logging its
   "selected path" line (692/day for one device in local logs). `DpiStageMonitor`'s 2 s poll and
   gRPC calls both hit this path.

## Scope

**In scope**

- Replace the InTheHand advertisement path (`Bluetooth.AdvertisementReceived` subscription,
  `BluetoothAdvertisementReceived` handler, `bluetoothLEScan` field, `Bluetooth.RequestLEScanAsync`)
  with a direct WinRT `Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher`,
  keeping the existing scan-lifecycle semantics (scan only while a tracked paired device is
  disconnected; stop when everything is connected; restart on GATT disconnect; tolerate radio off).
- Pure, unit-testable helpers for Bluetooth-address parsing and advertisement matching.
- Catalog gate in `DpiStageMonitor.RefreshReaders` so readers only start for catalog mice, plus
  disposal of each reader's `CancellationTokenSource` when its loop exits.
- Cache keyboard RGB services in `DeviceRgbServiceFactory`, mirroring the existing Easen/Telink
  cache behaviour exactly.
- `docs/CONTEXT.md` glossary entry for the new term.

> **Note (2026-08-19, PR #115 review m2) — deliberate tradeoff:** the new advertisement path only
> matches devices already in `_trackedDevices`; it never registers a device from an advertisement
> (the old handler ran `DeviceNeedToTrack` + `RegisterDevice` per ad — per-ad device creation is
> exactly the leak path this plan removes). A device not yet tracked — a newly-paired second PG
> device, or a fresh OS-level instance after an adapter toggle — is registered only by the
> periodic sweep: worst case ≤30 s (the active-sweep interval), or ≤5 min (idle sweep) only when
> nothing was ever tracked.

**Out of scope**

- Any new NuGet package (would require human escalation), target-framework change, `.slnx` change,
  `Directory.Build.props` change, or proto change. None is needed: `ProoveGaming.Infrastructure`
  targets `net10.0-windows10.0.26100.0` via `src/Directory.Build.props`
  (`ProoveGaming.Infrastructure.csproj` uses `$(WindowsTargetFramework)`), so the WinRT projections
  for `Windows.Devices.Bluetooth.Advertisement.*` are already available — verified against the
  csproj and props files.
- Everything else in `BluetoothDeviceDiscoveryService` stays on InTheHand: paired-device
  enumeration, the periodic sweep, the classic-Bluetooth poll, GATT battery reads, and the actual
  GATT connect (`device.Gatt.ConnectAsync()` on the already-tracked instance).
- No change to the gRPC contract, `BluetoothDeviceState`, or anything the App sees.
- No invalidation/lifecycle redesign for the factory caches — the new keyboard cache copies the
  existing caches' behaviour (see Recommended approach). (Superseded 2026-08-19 per PR #115
  review M1: the keyboard cache is now cleared on every HID device-list change so a replugged
  keyboard rebinds to its new DevicePath; the Easen/Telink mouse caches keep their
  no-invalidation behaviour — that staleness defect is pre-existing and explicitly scoped out of
  the review fix.)

## Options considered

### Primary fix

1. **Direct WinRT `BluetoothLEAdvertisementWatcher`, handler-side address matching (recommended).**
   Start our own watcher; in the `Received` handler compare `args.BluetoothAddress` (already a
   `ulong` — no allocation, no device resolution) against a snapshot set of the tracked devices'
   addresses. Only a match resolves anything, and even then we do **not** create a device from the
   advertisement — we call `Gatt.ConnectAsync()` on the `BluetoothDevice` instance already held in
   `_trackedDevices`. `BluetoothLEDevice.FromBluetoothAddressAsync` disappears from the
   advertisement path entirely.
   - - Non-matching advertisements cost one ulong hash lookup; nothing blocks; nothing native is
     allocated per ad.
   - - Keeps every other InTheHand seam untouched.
   - − Adds a second Bluetooth API surface (WinRT next to InTheHand) inside one service —
     documented in code comments; acceptable because InTheHand's Windows backend is this same
     watcher underneath.

2. **`BluetoothLEAdvertisementWatcher.AdvertisementFilter` (LocalName filter) instead of
   handler-side matching.** Rejected as the primary mechanism: the LocalName section is only present
   in some advertisement frames (often only in the scan response), so a filter on it can silently
   drop the very advertisement that should wake a reconnect, and it cannot express "this Bluetooth
   address". Handler-side address matching is equally cheap and strictly more reliable. LocalName is
   kept only as a **fallback match** inside our handler for a tracked device whose InTheHand `Id`
   fails to parse as an address (not expected on Windows, where `BluetoothDevice.Id` is the
   12-hex-digit address string — but a fallback beats a dead reconnect path).

3. **Drop advertisement scanning entirely and rely on the 30 s periodic sweep to reconnect.**
   Smallest possible change and removes the leak by construction.
   - − Reconnect latency becomes up to a sweep interval, and worse: the sweep detects reconnection
     via `Gatt.IsConnected`, which for a powered-on-but-not-yet-connected BLE peripheral stays false
     until *something* initiates GATT — the advertisement is the only signal that the device is
     reachable again (the code comments document there is no "GattServerConnected" event). A
     keyboard switched back on could take arbitrarily long to reconnect. Rejected: fixes the leak by
     breaking the feature the scan exists for.

4. **Keep InTheHand's scan but throttle/dispose in our handler.** Rejected outright: the damage
   (the 7 s blocking lookup per advertisement) happens inside the library before our handler runs;
   nothing we do subscriber-side can prevent it.

### DpiStageMonitor gate

1. **Catalog check through the already-injected `IDeviceRgbServiceFactory` (recommended).** Add
   `bool IsCatalogMouse(int vendorId, int productId)` to the interface, implemented on the factory
   via the existing `SelectConfigRow` + a `DeviceType` "Mouse" check — i.e. exactly the set of
   devices `CreateForDevice` would resolve to an `IMouseRgbService`. `DpiStageMonitor` already
   injects the factory, so no new constructor dependency, and the logic lives next to (and is kept
   consistent with) the dispatch it mirrors. Testable in `ProoveGaming.Infrastructure.Tests`
   alongside the existing `SelectConfigRow` tests.
2. **Inject `IDeviceInfoProvider` into `DpiStageMonitor` and filter there.** Works, but duplicates
   the "which rows count as a mouse this factory serves" rule in a second assembly, and
   `SelectConfigRow` is `internal` to Infrastructure so DeviceHost could not reuse it. Rejected.

### Factory keyboard caching

1. **Third cache dictionary mirroring `_serviceCache`/`_telinkCache` (recommended).** One
   `ConcurrentDictionary<(int, int), IDeviceRgbService>` for all keyboard services, keyed
   `(vid, pid)` — not `(vid, pid, deviceKey)` like the mouse caches, because keyboard creation is
   not unit-aware yet (it ignores `deviceKey`), so a key qualified by `deviceKey` would cache
   duplicate instances bound to the same physical interface (corrected 2026-08-19 per review
   finding S3). Behaviour copied from the existing caches, not
   invented: a **null** creation result is never cached (so a keyboard plugged in later gets
   created on the next call), and a cached instance is **never invalidated** — the existing caches
   hold their instance for the DeviceHost process lifetime even across unplug/replug, and the
   keyboard cache does the same. (Verified: neither `_serviceCache` nor `_telinkCache` has any
   removal path.) (Never-invalidated part superseded 2026-08-19 per PR #115 review M1: the
   keyboard cache is cleared on every `IHidDeviceProvider` device-list change — a replug into a
   different USB port previously kept serving a cached service bound to the old dead DevicePath
   until DeviceHost restart. The mouse caches are unchanged.)
2. **Cache inside each `Create*KeyboardRgbService` method.** Six copies of the same
   TryGetValue/store dance. Rejected — one dictionary at the dispatch level is smaller and matches
   how the mouse caches already read.

## Recommended approach

### 1. Primary — WinRT advertisement watcher in `BluetoothDeviceDiscoveryService`

New pure helpers (both in `src/ProoveGaming.Infrastructure/Classes/`, both fully unit-testable —
they touch no WinRT or InTheHand types beyond strings and ulongs):

- **`BluetoothAddressParser`** — `static bool TryParse(string? deviceId, out ulong address)`.
  Parses the InTheHand Windows `BluetoothDevice.Id` format (12 hex digits, e.g. `"E0F1A2B3C4D5"`)
  into the `ulong` form `BluetoothLEAdvertisementReceivedEventArgs.BluetoothAddress` uses. Rejects
  null/empty/wrong-length/non-hex input.
- **`BleAdvertisementMatcher`** — an immutable snapshot built from the currently tracked devices:
  a `Dictionary<ulong, string>` (address → tracked device id) plus, for any tracked id that failed
  to parse, a name-fallback list of `(catalogNameUpper, deviceId)` pairs sourced from the tracked
  device names / catalog rows with `HasBluetooth`. One method:
  `bool TryMatch(ulong address, string? localName, out string deviceId)` — address lookup first
  (cheap ulong compare), LocalName `Contains` (ordinal, upper-cased) only as fallback and only when
  the fallback list is non-empty — plus a `HasNameFallbacks` property so the handler can skip
  reading the advertisement's LocalName entirely when no fallback exists. Constructing a matcher
  allocates; matching does not (beyond the possible `ToUpperInvariant` on the rare fallback path).

Changes in `BluetoothDeviceDiscoveryService`:

- **Remove** the `bluetoothLEScan` field, the `Bluetooth.AdvertisementReceived += / -=`
  subscription (constructor and `Dispose`), the `BluetoothAdvertisementReceived` handler, and the
  `BluetoothLEScanOptions` / `RequestLEScanAsync` call in `SyncScanState`.
- **Add** a `Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher? _watcher`
  field and a `volatile BleAdvertisementMatcher? _matcher` snapshot field.
- **Watcher mutation is serialised** (added 2026-08-19 for review finding B1): a
  `private readonly object _watcherLock` guards `SyncScanState`'s whole
  create/subscribe/`Start()`/`Stop()` block and `TearDownWatcher`'s body, each operating on a
  local variable (never re-reading the field), so two racing callers cannot each start a watcher
  with only one reachable through the field. A `_watcherStopped` flag (set in `TearDownWatcher`,
  cleared on a deliberate discovery restart in `StartDiscoveringCoreAsync`) keeps a late sweep
  pass or connect continuation from resurrecting a watcher after `StopDiscovering`/`Dispose`.
- `SyncScanState` (renamed 2026-08-19 per review m5 — the method is synchronous and `void`, and
  the false Task-returning `Async` name is what caused the Round-1 S2 exception regression) keeps
  its exact decision logic (including failing open) but now:
  - rebuilds `_matcher` from `_trackedDevices` (all tracked devices — a connected device does not
    advertise, and the connect path below re-checks `Gatt.IsConnected`, so including everything is
    safe and simpler than tracking the disconnected subset);
  - when a scan is needed and the watcher is not already `Started`: create the watcher once
    (lazily), set `ScanningMode = Active` (Active retrieves scan responses so the LocalName
    fallback can ever see a name; the per-ad cost of Active vs Passive is negligible next to the
    bug being fixed), subscribe `Received`, and call `Start()` inside the existing try/catch —
    `Start()` throws while the radio is off; keep the current "log debug, retry next sweep"
    semantics. Log the same "[BT] LE scan started/stopped" lines so existing log-based diagnostics
    keep working.
  - when no scan is needed and the watcher is started: `Stop()` it.
  - No `AdvertisementFilter` / `SignalStrengthFilter` is set — filtering is handler-side by design
    (see Options considered).
- **`Received` handler** (the hot path — see Performance & quality):
  1. Read `args.Advertisement?.LocalName` only when `matcher.HasNameFallbacks` (empty in the
     normal case, so the per-ad WinRT projection + string marshal never happens), then
     `TryMatch(args.BluetoothAddress, localName, out var deviceId)` — on no match, return. This is
     the entire cost for every foreign advertisement. (Snippet corrected 2026-08-19 per review
     finding S1 — the original contradicted the Performance & quality section.)
  2. On a match, look up the **already-tracked** `BluetoothDevice` in `_trackedDevices` by
     `deviceId` (nothing is ever created from the advertisement); if absent, return.
  3. If `!device.Gatt.IsConnected && device.IsPaired && _pendingConnections.TryAdd(device.Id, true)`
     — fire-and-forget a `Task.Run` that awaits `device.Gatt.ConnectAsync()`, then
     `RegisterDevice(device)` (raises the connected event), then `SyncScanState()` (stops
     the watcher if that was the last disconnected device), with the existing
     `catch → LogDebug` / `finally → _pendingConnections.TryRemove` shape. `_pendingConnections`
     is the debounce: a device advertises many times per second, and TryAdd guarantees exactly one
     concurrent connect attempt per device. Reading `Gatt.IsConnected` is wrapped in the same
     guarded style the rest of the file uses (it can throw when the radio disappears mid-callback).

     > **Superseded 2026-09-02** — see `docs/audits/2026-09-02-device-association-memory-audit.md`.
     > This condition shipped as written and was itself a leak: `_pendingConnections` bounds only
     > CONCURRENT attempts, so a device that never connects produced one `ConnectAsync` per
     > advertisement forever (F1), and `Gatt.IsConnected` answers "does OUR client hold a link",
     > which stays false for a peripheral Windows owns — so the condition never cleared (F2). The
     > gate is now `!ResolvedConnected(device) && device.IsPaired && ConnectAttemptAllowed(deviceId)
     > && _pendingConnections.TryAdd(...)`: the device-tree verdict decides connectedness, and a
     > per-device backoff (1 s doubling to 60 s) retires the instance via `ReleaseTrackedDevice`
     > after `ConnectAttemptsBeforeRetire` consecutive failures.
  4. The whole handler body sits in the same top-level try/catch-log pattern the old handler had —
     it runs on a WinRT callback thread with no caller to observe an exception.
- `StopDiscovering` and `Dispose` stop the watcher and unsubscribe `Received`
  (`BluetoothLEAdvertisementWatcher` is not `IDisposable`; stopped + unsubscribed is its clean end
  state).
- `DeviceNeedToTrack`, `RegisterDevice`, the periodic sweep, `MarkAllDisconnected`, the classic
  poll and battery logic are untouched.

  > **No longer true as of 2026-09-02** (`docs/audits/2026-09-02-device-association-memory-audit.md`).
  > `DeviceNeedToTrack`'s catalog match now uses `OrdinalIgnoreCase`, as do the three sibling match
  > sites — hardening, not a fix: the case-sensitive comparison worked, but only because
  > `DeviceMapExpander` upper-cases every `DeviceMap.Name` three layers earlier, which nothing at the
  > call site said. It also gained the empty-name guard its two siblings already had. `RegisterDevice`
  > now disposes the instance it replaces (August audit C23) and clears that device's connect backoff.

### 2. `DpiStageMonitor` — catalog gate + CTS disposal

- `src/ProoveGaming.Infrastructure/Interfaces/IDeviceRgbServiceFactory.cs`: add
  `bool IsCatalogMouse(int vendorId, int productId);` with a doc comment ("true when the catalog
  resolves this VID/PID to a row `CreateForDevice` would serve with an `IMouseRgbService`").
  `DeviceRgbServiceFactory` is the only implementation in the repo (verified), so no other
  implementers break; test mocks are Moq-based and unaffected.
- `DeviceRgbServiceFactory`: implement it as `SelectConfigRow(deviceInfoProvider.DevicesMaps, vid,
  pid)` returning a row whose `DeviceType` contains `"Mouse"` (ordinal-ignore-case) — the same
  test `CreateForDevice` itself dispatches on.
- `DpiStageMonitor.RefreshReaders`: after `ParseVidPid`, skip the path when
  `!_rgbFactory.IsCatalogMouse(vid, pid)`. Log the skip **once per path** at Debug (a
  `ConcurrentDictionary<string, bool>`-guarded log, same pattern as the factory's
  `_loggedMissingConfig`) so a foreign device is diagnosable without producing per-sweep noise.
  With the gate in place the foreign VID 056D reader never starts, ending the start/stop churn and
  its `QueryDeviceState` traffic.
- `DpiStageMonitor.ReaderLoop`: in the `finally` block, after `_readers.TryRemove`, dispose
  `state.Cts` (try/catch-swallow, matching the file's guarded-cleanup style). In
  `ExecuteAsync`'s shutdown `finally`, wrap each `reader.Cts.Cancel()` in try/catch
  (`ObjectDisposedException` race with a reader that exited concurrently is now possible and must
  be benign).

### 3. `DeviceRgbServiceFactory` — keyboard service cache

- Add `private readonly ConcurrentDictionary<(int, int), IDeviceRgbService>
  _keyboardCache = new();`.
- In `CreateForDevice`'s keyboard branch: try the cache first with key
  `(vendorId, productId)` — keyboard creation is not unit-aware (it ignores `deviceKey`), so the
  key deliberately omits it to keep mixed call sites (gRPC Container ID vs `""`) from caching
  duplicate instances of the same physical interface; on miss, run the existing name dispatch;
  store the result
  **only when non-null**; return it. Semantics copied from the Easen/Telink caches: null (device
  absent / interface not found) is retried on every call. The per-construction HID enumeration and
  "selected path" log then happen once per device connection instead of on every gRPC call /
  DPI poll.
- **Invalidation (added 2026-08-19, PR #115 review M1):** the factory subscribes to
  `IHidDeviceProvider.SubscribeToDeviceListChanged` in its constructor (converted from a primary
  constructor; the factory is a DI singleton living for the process lifetime, so no unsubscribe
  path is needed) and clears `_keyboardCache` on every device-list change — without this, a
  keyboard replugged into a different USB port kept being served a cached service bound to the
  old dead DevicePath (RGB silently dead until DeviceHost restart). `_serviceCache`/`_telinkCache`
  share the staleness defect but are pre-existing and were explicitly scoped out of the fix.
- **Lifetime note (2026-08-19, review m3):** this cache changed keyboard RGB services from
  per-call to process-cached, which made the "the factories construct a fresh service per gRPC
  call" remarks in `DeviceGate`, `SmartInfiniteTransport.ReadDeviceInfo` and
  `RampageKeyboardKeyMappingService` false. They were reworded to the now-true justification: the
  gate stays static because one DevicePath is shared across service families with differing
  lifetimes (mouse/keyboard RGB services process-cached, key-mapping services per-call).

## File-level change list

| File | Intent |
| --- | --- |
| `src/ProoveGaming.Infrastructure/Classes/BluetoothAddressParser.cs` | **New.** Pure static parser: InTheHand Windows `BluetoothDevice.Id` (12 hex chars) → `ulong` Bluetooth address; strict validation. |
| `src/ProoveGaming.Infrastructure/Classes/BleAdvertisementMatcher.cs` | **New.** Immutable snapshot matcher: address→deviceId map + LocalName fallback; `TryMatch(ulong, string?, out string)` and `HasNameFallbacks` used by the watcher's Received handler. |
| `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs` | Replace the InTheHand advertisement path (static event subscription, `BluetoothAdvertisementReceived`, `bluetoothLEScan`, `RequestLEScanAsync`) with a WinRT `BluetoothLEAdvertisementWatcher` + handler-side matching; connect via the already-tracked `BluetoothDevice`; same scan-lifecycle semantics, stop/unsubscribe in `StopDiscovering`/`Dispose`. |
| `src/ProoveGaming.Infrastructure/Interfaces/IDeviceRgbServiceFactory.cs` | Add `bool IsCatalogMouse(int vendorId, int productId)` so DeviceHost can gate readers on the catalog without duplicating the dispatch rule. |
| `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | Implement `IsCatalogMouse` via `SelectConfigRow`; add `_keyboardCache` (null never cached; keyed `(vid, pid)` only — keyboard creation is not unit-aware; cleared on every HID device-list change via a constructor subscription to `IHidDeviceProvider.SubscribeToDeviceListChanged` — 2026-08-19, review M1). |
| `src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs` | Gate `RefreshReaders` on `IsCatalogMouse` (once-per-path debug log for skips); dispose each reader's `CancellationTokenSource` when its loop exits; guard shutdown `Cancel()` against the disposed race. |
| `tests/ProoveGaming.Infrastructure.Tests/BluetoothAddressParserTests.cs` | **New.** Valid 12-hex ids round-trip to the expected `ulong`; null/empty/short/long/non-hex are rejected. |
| `tests/ProoveGaming.Infrastructure.Tests/BleAdvertisementMatcherTests.cs` | **New.** Address match hits; unknown address misses (the "foreign advertisement costs nothing / resolves nothing" contract); LocalName fallback only for unparseable ids; empty snapshot matches nothing; `HasNameFallbacks` false for address-only/blank-name snapshots, true with a fallback entry. |
| `tests/ProoveGaming.Infrastructure.Tests/DeviceRgbServiceFactoryTests.cs` | Extend: `IsCatalogMouse` true for a catalog mouse row, false for a catalog keyboard, false for a foreign VID/PID (the VID 056D repro); keyboard branch does not cache a null result (two calls with no HID interfaces both return null without throwing); `IsCatalogMouse` true for a button-only wired mouse row (capability-any); keyboard cache-hit returns the same instance for the same `(vid, pid)` regardless of `deviceKey`, with one HID enumeration (mocked Raster-shaped `HidDevice`); M1 invalidation (added 2026-08-19): constructor subscribes to `SubscribeToDeviceListChanged` exactly once, a device-list change clears the keyboard cache (next call re-enumerates and returns a new instance), and the fresh post-clear service is cached again. |
| `docs/CONTEXT.md` | Add glossary entry **LE advertisement watcher** (one line, per the glossary rule). |
| `docs/plans/fix-devicehost-ble-memory-leak/fix-devicehost-ble-memory-leak-plan.md` | This plan. No existing plan under `docs/plans/` documents the LE-scan/advertisement path (grep verified), so this plan becomes its owning document. |

## Test strategy

This is a `fix-*` plan, so tests that reproduce the (testable core of the) bug are written
**first** and must fail on the current code, then the fix makes them pass. The WinRT watcher and
InTheHand statics are not unit-testable; the strategy is to put every decision the hot path makes
into pure seams and test those. All tests go in `tests/ProoveGaming.Infrastructure.Tests` (xUnit,
mirroring the existing Moq + `DeviceMap`-fixture style of `DeviceRgbServiceFactoryTests`).

Write first (fail on current code because the seams do not exist yet / the gate is absent):

1. **`BleAdvertisementMatcherTests.UnknownAddress_DoesNotMatch`** — the direct expression of the
   bug's contract: an advertisement from a device we do not track must resolve to nothing. On
   current code there is no such gate at all (every advertisement resolved a native device inside
   the library), which is exactly what this seam is introduced to forbid.
2. **`DeviceRgbServiceFactoryTests.IsCatalogMouse_ForeignVidPid_IsFalse`** — the VID 056D / PID
   C077 repro from the logs: a catalog containing only PG rows must reject the foreign device, so
   `DpiStageMonitor` never starts its reader.

Then the rest:

- `BluetoothAddressParserTests`: golden path (`"E0F1A2B3C4D5"` → `0xE0F1A2B3C4D5`), lower/upper
  case, and rejects: null, empty, 11/13 chars, non-hex chars, embedded separators.
- `BleAdvertisementMatcherTests`: known address matches and yields the right deviceId; LocalName
  fallback matches only when an id failed to parse and the name contains a catalog BT name
  (case-insensitive); no fallback entries → LocalName ignored entirely; empty snapshot matches
  nothing.
- `DeviceRgbServiceFactoryTests` additions: `IsCatalogMouse` true for a wired mouse row, false for
  a keyboard row, false when only a BT mouse row exists (mirrors `SelectConfigRow`'s existing
  mouse-over-BT exclusion); keyboard `CreateForDevice` with no HID interfaces returns null twice
  without caching or throwing (null-not-cached contract).
- Existing suites (`DeviceRgbServiceFactoryTests` `SelectConfigRow` cases, everything else in the
  project) must stay green — `SelectConfigRow` itself is not modified.

Deliberately **not** covered by automated tests, and why:

- The watcher lifecycle, the `Received` handler, and the actual memory behaviour — they require a
  real radio, real advertisements and WinRT callbacks; covered by QA steps 1–4 instead.
- ~~The keyboard-cache **hit** path (same instance returned twice)~~ — superseded 2026-08-19
  (fix-pass tests): HidSharp's `HidDevice` report-length methods and VID/PID getters are virtual,
  so a Raster-shaped mock (outLen/inLen = 65) can drive a real keyboard creation; the cache-hit
  contract (same instance for the same `(vid, pid)` regardless of `deviceKey`, one HID enumeration
  total) is now unit-tested in `DeviceRgbServiceFactoryTests`. QA step 6 remains the check for the
  real-hardware "selected path" log frequency. The M1 cache-invalidation contract (constructor
  subscription, clear-on-device-list-change, re-cache after clear) is likewise unit-tested there
  via a Moq-captured `SubscribeToDeviceListChanged` handler (added 2026-08-19, post-PR-#115 test
  pass); only the real replug signal path (HidSharp raising the event) stays QA-only.
- `DpiStageMonitor`'s reader threads and CTS disposal — real HID handles and `BackgroundService`
  plumbing; verified via log observation (QA step 5).

## Acceptance criteria

- [ ] Given a PG Bluetooth device paired but powered **off** and ordinary BLE traffic in radio
  range (phones etc.), when DeviceHost runs for 30+ minutes, its Task Manager memory stays flat
  (no monotonic climb; well under 100 MB), instead of growing by hundreds of MB per hour.
- [ ] Given a tracked paired PG device is disconnected, when it is powered on, the App shows it
  connected within ~15 s (advertisement received → `ConnectAsync` on the tracked instance), and the
  DeviceHost log contains the "[BT] LE scan started" line from the watcher path.
- [ ] Given every tracked device is connected, the watcher is stopped ("[BT] LE scan stopped" in
  the log) and no `Received` callbacks are processed until a GATT disconnect restarts it.
- [ ] Given the Bluetooth radio is off, when the sweep tries to start the watcher, the failure is
  logged at Debug, no exception escapes, and the sweep keeps running (same tolerance as today).
- [ ] `BluetoothDeviceDiscoveryService.cs` contains no reference to
  `Bluetooth.AdvertisementReceived`, `RequestLEScanAsync`, `BluetoothLEScan` or
  `BluetoothLEScanOptions` (grep-verifiable), and nothing in the advertisement path calls
  `BluetoothLEDevice.FromBluetoothAddressAsync`.
- [ ] Given a non-catalog HID device exposing an `&mi_01&col04` interface (e.g. VID 056D), when
  DeviceHost runs, no "[DPI] start reader" line is ever logged for its path and no reader
  start/stop churn occurs for it; catalog mice (X43/BLAZE) still get readers and still raise
  stage/rate events.
- [ ] When a DPI reader loop exits (device unplugged), its `CancellationTokenSource` is disposed,
  and DeviceHost shutdown does not throw when it races that disposal.
- [ ] Given a connected RGB keyboard, when `CreateForDevice` is called repeatedly for the same
  `(vid, pid, deviceKey)`, the same service instance is returned after the first successful
  creation, and the per-creation "selected path" / interface log line appears once per device
  connection instead of per call; a null creation result is not cached (next call retries); a
  HID device-list change (unplug/replug) clears the keyboard cache, so the next call constructs
  a fresh service bound to the current DevicePath (added 2026-08-19, review M1).
- [ ] `dotnet build ./src/ProveGaming.slnx` succeeds and
  `dotnet test ./tests/ProoveGaming.Infrastructure.Tests/ProoveGaming.Infrastructure.Tests.csproj`
  is green, including the new test files.

## QA (manual test steps)

1. Install the build. Pair a Proove Bluetooth keyboard (e.g. Rampage over Bluetooth) with Windows,
   confirm it shows connected in the Proove Gaming app, then **switch the keyboard off**. Leave the
   PC running for 30–60 minutes somewhere with normal Bluetooth around (phones, earbuds).
   Expected: in Task Manager, the "ProoveGaming DeviceHost" process memory stays roughly constant
   (tens of MB) the whole time — it must not creep up continuously.
2. Switch the keyboard back on. Expected: within about 15 seconds the app shows it as connected
   again, without restarting anything.
3. Switch the keyboard off again. Expected: the app shows it disconnected within about 30 seconds.
   Switch it on once more — it reconnects again (the cycle is repeatable).
4. Turn Windows Bluetooth off entirely (Settings → Bluetooth), wait a minute, turn it back on.
   Expected: no crash, no error popup; Bluetooth devices show disconnected while the radio is off
   and reconnect after it returns.
5. Plug in a non-Proove USB mouse (e.g. a Logitech mouse) alongside a Proove X43 or BLAZE mouse and
   leave the PC for 10 minutes. Expected: the Proove mouse's DPI button still updates the DPI stage
   in the app immediately; nothing misbehaves with the foreign mouse; the machine does not show the
   DeviceHost process restarting activity every half-minute (for a log-equipped tester: no repeated
   "start reader"/"reader stopped" pairs for the non-Proove device).
6. With a Proove RGB keyboard connected over USB, change the RGB effect a few times and leave the
   app open for 10 minutes. Expected: effects apply instantly every time; (for a log-equipped
   tester: the keyboard's interface-selection log line appears once after plug-in, not every couple
   of seconds).
7. Reboot the PC with all Proove devices connected. Expected: everything shows connected after
   startup; battery levels appear for wireless devices as before.

## Review focus

- `resource disposal` — watcher stop/unsubscribe on every exit path; reader CTS disposal and the
  shutdown race; nothing native created per advertisement.
- `thread-safety` — the `Received` callback runs on WinRT threads against singleton state; the
  matcher snapshot must be immutable-and-swapped (volatile field), `_pendingConnections` is the
  only connect gate, and every watcher create/start/stop/teardown runs under `_watcherLock`.
- `async/await` — no sync-over-async anywhere in the new path; fire-and-forget blocks follow the
  file's guarded pattern (async void / Task.Run bodies fully wrapped in try/catch-log).
- `performance hot-path` — the `Received` handler is the hot path; see below.

## Performance & quality

- **Hot path: the watcher `Received` handler.** With an unfiltered watcher in a busy radio
  environment this fires many times per second. The non-match path must be allocation-free: read
  `args.BluetoothAddress` (ulong), one dictionary/set lookup on the immutable snapshot, return.
  `LocalName` is only touched on the fallback path, which is empty whenever all tracked ids parsed
  (the normal case). No WinRT object is resolved, created or disposed per advertisement.
- **Snapshot swapping, not locking.** `BleAdvertisementMatcher` instances are immutable; the
  service replaces the `volatile` field wholesale inside `SyncScanState`. The handler reads
  whatever snapshot is current — a stale-by-milliseconds snapshot is harmless because the connect
  path re-checks `Gatt.IsConnected` and `_pendingConnections` gates concurrency.
- **Shared singleton state.** `_trackedDevices` / `_trackedStatuses` / `_pendingConnections` are
  already `ConcurrentDictionary`; the new code only reads/TryAdds them, adding no new coordination
  requirements.
- **DpiStageMonitor 2 s poll** is an existing hot-ish loop; the catalog gate runs only inside
  `RefreshReaders` (on device-list changes / 30 s safety net), not in the 2 s poll, so it adds no
  per-poll cost. The keyboard cache *removes* a full HID enumeration + logging from every poll that
  resolves a keyboard VID/PID.
- **No UI thread involved** — everything here is DeviceHost/Infrastructure background work.

## Risks

- **A paired PG device whose advertisements never carry the expected address.** If a device
  advertises with a resolvable-private address different from the Id InTheHand reports, the address
  match would miss. Mitigations: paired devices' identity addresses are what Windows reports for
  `BluetoothDevice.Id`, and the LocalName fallback plus the 30 s periodic sweep both remain. Must
  be verified on real hardware (QA step 2) before ship.
- **Active scanning cost.** `ScanningMode.Active` transmits scan requests; negligible on a desktop
  host, but if it proves problematic the fallback is Passive + address-only matching (drop the
  name fallback).
- **Watcher `Aborted` status.** If the radio disappears while the watcher runs, it stops with an
  error status rather than throwing. (Resolution updated 2026-08-19 per PR #115 review m1:
  restarting an aborted instance via `Start()` is undocumented in WinRT, so `SyncScanState` no
  longer relies on it — an `Aborted` watcher is unsubscribed and discarded under `_watcherLock`,
  and the creation branch starts a fresh instance on the next pass.)
- **New-device registration latency (accepted 2026-08-19, review m2).** Because the advertisement
  path never registers untracked devices (see the Scope note), a newly-paired second PG device or
  a fresh OS-level instance waits for the sweep: ≤30 s active, ≤5 min idle (only when nothing was
  ever tracked). Accepted — restoring per-ad registration would reintroduce the leak path.
- **CTS dispose race in DpiStageMonitor.** A reader exiting concurrently with service shutdown can
  make `Cancel()` throw `ObjectDisposedException`; the plan guards it, reviewer should verify both
  sides.
- **Keyboard cache staleness after replug.** (Resolved 2026-08-19 per PR #115 review M1: the
  keyboard cache is cleared on every HID device-list change, so a replug rebinds on the next
  call. The Easen/Telink mouse caches still carry the pre-existing staleness — scoped out.)
  QA step 6 plus a replug during QA covers the keyboard path.
- **InTheHand static event removal.** `Bluetooth.AdvertisementReceived` unsubscription previously
  had a side effect inside the library (subscribing could auto-start machinery). Removing our
  subscription entirely is the intent; reviewer should confirm no other code depends on it (grep:
  only this service references it today).

## Open questions

None blocking. Two decisions were made in this plan that a human may want to veto before `/coder`
runs:

1. `ScanningMode.Active` (chosen so the LocalName fallback can see names) vs `Passive` (cheapest
   possible). The plan picks Active; flip to Passive if battery/radio noise concerns outweigh the
   fallback.
2. The catalog gate is exposed as `IDeviceRgbServiceFactory.IsCatalogMouse` (no new DI dependency
   in DeviceHost). If a broader "catalog query" surface is preferred long-term, that is a later
   refactor — out of scope here.

## Review findings

*Filled in by /reviewer.*

### Round 1 — 2026-08-19 — Ship: no

#### Summary

The branch does what the plan says: the InTheHand advertisement path in
`BluetoothDeviceDiscoveryService` is gone (grep-verified — no `RequestLEScanAsync`,
`BluetoothLEScan`, `BluetoothLEScanOptions`, `Bluetooth.AdvertisementReceived`, or
`FromBluetoothAddressAsync` anywhere in `src/`), replaced by a lazily-created WinRT
`BluetoothLEAdvertisementWatcher` with handler-side matching against an immutable, volatile-swapped
`BleAdvertisementMatcher` snapshot; connects go through the already-tracked InTheHand instance
gated by `_pendingConnections`. `DpiStageMonitor` gates readers on the new
`IDeviceRgbServiceFactory.IsCatalogMouse` and disposes reader CTSs; `DeviceRgbServiceFactory` gains
a keyboard cache with null-never-cached semantics. Build is clean (0 warnings) and both test
projects are green (739 Infrastructure + 893 App tests). One thread-safety blocker in the watcher
lifecycle and three should-fixes below.

#### Plan conformance

Plan: `docs/plans/fix-devicehost-ble-memory-leak/fix-devicehost-ble-memory-leak-plan.md`

- Implemented as planned: every row of the File-level change list. Both pure helpers exist and are
  strict/immutable as specified; the watcher is created lazily with `ScanningMode.Active`, no
  filters, same log lines; `TearDownWatcher` stops + unsubscribes in `StopDiscovering`/`Dispose`
  and nulls the field for restartability; `Aborted` is treated as not-running (verified against the
  WinRT docs: `Start()` "transitions to Started … or to Aborted if the request failed immediately",
  so re-issuing `Start()` from Aborted on the same instance is the documented retry path — the
  plan's "Watcher Aborted status" risk is resolved, subject to QA step 4 on hardware);
  `IsCatalogMouse` reuses `SelectConfigRow` + the identical DeviceType test; the skip log is
  once-per-path; CTS disposal and the guarded shutdown `Cancel()` are both present; the keyboard
  cache never stores null; `docs/CONTEXT.md` got its glossary entry. All planned tests exist, plus
  extras beyond the plan (whitespace-padded parser rejections, blank-fallback-name dropping,
  address-wins-over-fallback).
- Deviates from plan (1): the `Received` handler reads `args.Advertisement?.LocalName` on **every**
  advertisement (`BluetoothDeviceDiscoveryService.cs:528`), while the plan's *Performance &
  quality* section requires LocalName to be touched only on the fallback path. Note the plan is
  internally inconsistent here — its Recommended-approach step 1 literally shows this call shape —
  so the code followed the snippet and violated the contract. See S1; the plan step should be
  corrected together with the code.
- Deviates from plan (2): `SyncScanStateAsync` was changed from `async Task` to a synchronous
  Task-returning method (`BluetoothDeviceDiscoveryService.cs:225`). Not called for by the plan, and
  it changes exception semantics at the fire-and-forget call site in `DeviceGattDisconnected`. See
  S2.
- Missing / partial: none.
- Accepted limitation (per plan): keyboard cache-hit path, watcher lifecycle/memory behaviour, and
  DPI reader threads are QA-only (steps 1–6) — residual risk: the ~23 GB leak fix itself is only
  provable on hardware; QA step 1 is the release gate.
- Acceptance criteria: the two statically checkable ones (grep criterion; build + Infrastructure
  tests green) are met and were verified in this review. The rest await QA.

#### Code

- [ ] **B1. Watcher lifecycle is mutated from multiple threads with no synchronisation — a race
  can orphan a started, subscribed watcher for the process lifetime** —
  `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:244-291` (`SyncScanStateAsync`), `:343-362` (`TearDownWatcher`) — Blocker
  **Problem:** `SyncScanStateAsync` (create/subscribe/`Start()`/`Stop()` on `_watcher`) runs
  concurrently from four contexts: the periodic sweep, `DeviceGattDisconnected` (InTheHand
  callback thread, `:638`), the connect `Task.Run` continuation (`:565`), and
  `StartDiscoveringCoreAsync`. `TearDownWatcher` (Worker shutdown / Dispose) races all of them.
  None of these takes a lock, and `_watcher` is a plain field.
  **Why it blocks:** concrete interleaving — DeviceHost starts with a connected BLE keyboard;
  `StartDiscoveringCoreAsync` registers it (subscribing `GattServerDisconnected`) and then awaits
  its first `SyncScanStateAsync`; the keyboard drops in that window, so `DeviceGattDisconnected`
  fires `_ = SyncScanStateAsync()` concurrently. Both observe `_watcher == null`; thread A creates
  watcher A, subscribes, and calls `_watcher.Start()` while the field still holds A; thread B then
  overwrites `_watcher = B`, subscribes, and starts B. Watcher A is now **started, subscribed to
  `OnAdvertisementReceived`, and unreachable** — `TearDownWatcher` and the "stop when all
  connected" branch only ever touch B. Result: the radio scans forever even when every device is
  connected (violates the third acceptance criterion), every advertisement is processed twice, and
  the "[BT] LE scan stopped" log lies. A second window: an in-flight sweep pass or connect
  continuation can re-create and start a watcher *after* `TearDownWatcher` ran during Worker
  shutdown / Dispose. The service already fixed exactly this class of race once (`_startGate`,
  `:37-47`) — the watcher mutation path needs the same discipline. Pre-existing in shape
  (the old `bluetoothLEScan` check-then-assign had it too), but this branch rewrote these lines and
  its whole purpose is scan-lifecycle hygiene.
  **Solution options:**
  - Option A (correct, small): everything in the mutation path is now synchronous — add a
    `private readonly object _watcherLock` and take it around the whole
    create/subscribe/Start/Stop block in `SyncScanStateAsync` and around `TearDownWatcher`'s body;
    inside the lock, call `Start()`/`Stop()` on the *local* variable, never re-reading the field.
    Optionally add a `_stopped` flag set in `StopDiscovering` so a late sweep/connect continuation
    cannot resurrect a watcher after shutdown.
  - Option B (minimal): keep lock-free but make the create branch race-safe with
    `Interlocked.CompareExchange(ref _watcher, created, null)` and only subscribe+start the
    instance that won; still leaves the stop-vs-start and teardown-resurrection interleavings
    (self-healing within one sweep, and shutdown-bounded respectively) — acceptable only if
    documented in the comments.

- [ ] **S1. Hot path reads `args.Advertisement?.LocalName` for every advertisement — per-ad WinRT
  RCW + string allocation the plan's performance contract forbids** —
  `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:528` — Should-fix
  **Problem / why it matters:** `args.Advertisement` materialises a `BluetoothLEAdvertisement`
  projection and `LocalName` marshals an HSTRING to a new .NET string on **every** callback,
  including the ~100 %-of-traffic foreign-advertisement case the whole fix exists to make free.
  The plan (*Performance & quality*): "the non-match path must be allocation-free … `LocalName` is
  only touched on the fallback path, which is empty whenever all tracked ids parsed (the normal
  case)." Bounded garbage (GC collects it), so not a leak — but it is a busy-radio-environment
  per-ad allocation in the exact spot the branch is about, and the fix is trivial.
  **Fix:** expose `bool HasNameFallbacks` on `BleAdvertisementMatcher` (or split
  `TryMatchAddress(ulong, out string)` from a rarely-called name overload) and read
  `args.Advertisement?.LocalName` only when a fallback entry exists:
  `matcher.TryMatch(args.BluetoothAddress, matcher.HasNameFallbacks ? args.Advertisement?.LocalName : null, out var deviceId)`.
  Also correct the plan's Recommended-approach step 1 snippet, which contradicts its own
  Performance section and is what the code faithfully followed.

- [ ] **S2. `SyncScanStateAsync` is now synchronous, so an exception escapes `_ =
  SyncScanStateAsync()` in `DeviceGattDisconnected` instead of being captured in the discarded
  Task — new process-crash path on a library callback thread** —
  `src/ProoveGaming.Infrastructure/Services/BluetoothDeviceDiscoveryService.cs:225` (signature), `:638` (unguarded call site) — Should-fix
  **Problem / why it matters:** on main, `SyncScanStateAsync` was `async Task`, so any throw was
  captured in the returned Task and the `_ =` discard swallowed it. The branch made the method
  synchronous ("Returns Task (not async) so existing await call sites stay untouched"), which
  inverts that: a synchronous throw now propagates at the call site. `DeviceGattDisconnected` has
  **no** try/catch and runs on an InTheHand callback thread — the file's own rule ("an escaping
  exception has no caller to catch it") is violated for it. The unguarded region inside
  `SyncScanStateAsync` is `_matcher = BuildMatcher()` plus the two `_watcher is { Status: … }`
  pattern reads: `BuildMatcher`'s catalog lambda (`name.ToUpper().Contains(bt.Name)`, `:325`)
  throws `ArgumentNullException` if a catalog row ever has a null `Name`, and that would now take
  down the DeviceHost service. Low probability, but it is a semantics regression introduced by
  this branch at an unchanged call site.
  **Fix (any one):** wrap `DeviceGattDisconnected`'s body in the file's standard try/catch-log; or
  wrap the unguarded region of `SyncScanStateAsync` (the `BuildMatcher` assignment) in try/catch;
  or keep the method `async Task` so the compiler captures throws in the Task as before. Wrapping
  the handler is the most future-proof — its own `device.Name`/`IsPaired` reads (`:633`) are
  unguarded too.

- [ ] **S3. Keyboard cache is keyed by `deviceKey` but keyboard creation ignores `deviceKey` —
  mixed call sites produce duplicate persistent service instances bound to the same physical
  interface** —
  `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs:62-69` vs `:79-95` — Should-fix
  **Problem / why it matters:** the Easen/Telink caches the plan says to mirror key on
  `(vid, pid, deviceKey)` **and** their creation filters interfaces by that key
  (`:152-162`, `:217-227`), so key and instance agree. The keyboard path copies the key but every
  `Create*KeyboardRgbService(vendorId, productId)` ignores `deviceKey` and picks the first/vendor
  interface. Concrete scenario: `DeviceService` gRPC calls pass `request.DeviceKey` (a Container
  ID) while another path passes `""` for the same keyboard → two distinct cached
  `RampageKeyboardRgbService` instances writing the same DevicePath, each with its own internal
  state. Today keyboard services carried no cross-call state (a fresh instance per call), so
  nothing misbehaves *yet* — but the cache's stated purpose is to let internal state survive
  across calls, and diverging duplicate state is exactly the bug that invites. Also pre-existing
  (not worsened): two physical identical keyboards both resolve to the first unit's interface.
  **Fix:** either thread `deviceKey` into the keyboard creation path with the same Container-ID
  filtering the Easen/Telink paths use (correct, and what "mirroring exactly" implies), or —
  cheap and honest — key the keyboard cache on `(vid, pid)` only and add a comment that keyboard
  creation is not unit-aware yet, so the cache cannot hold duplicates for one physical device.

#### Tests

- [x] Optional, cheap: an `IsCatalogMouse` case for a button-only wired mouse row
  (TAIPAN dongle shape: `SupportsRgb=false`, `SupportsButtonRemap=true`, DeviceType
  "Wireless Mouse") — pins that capability-any rows still count as catalog mice for the DPI gate —
  `tests/ProoveGaming.Infrastructure.Tests/DeviceRgbServiceFactoryTests.cs`.
- [x] If S1 lands as `HasNameFallbacks` / split-method: one matcher test asserting the property is
  false for an address-only snapshot and true with a fallback entry —
  `tests/ProoveGaming.Infrastructure.Tests/BleAdvertisementMatcherTests.cs`.
- Accepted per plan (no action, listed for the record): keyboard cache-hit (same instance twice)
  needs real HidSharp interfaces — QA step 6; watcher lifecycle/`Received`/memory — QA steps 1–4;
  DPI reader threads / CTS disposal — QA step 5. Note `dotnet-ci.yml` does not run `tests/` —
  local runs verified this review (739 Infrastructure + 893 App tests green).

#### Nits

- `BuildMatcher` mixes culture-sensitive `name.ToUpper()` (`:325`) with `ToUpperInvariant()`
  (`:327`) in the same method — mirrored from `DeviceNeedToTrack`, but new code should use the
  invariant form throughout (Turkish-İ pitfall) — `BluetoothDeviceDiscoveryService.cs:325`.
- `_watcher.Start()` can transition the watcher straight to `Aborted` without throwing (per the
  WinRT docs), in which case "[BT] LE scan started" is logged for a scan that never ran; the next
  sweep silently retries, so it is only a misleading log line —
  `BluetoothDeviceDiscoveryService.cs:268-269`.
- The `docs/CONTEXT.md` "LE advertisement watcher" entry is a four-line paragraph in a glossary
  whose rule is "one line each" — trim to one sentence and point at the plan for the rest —
  `docs/CONTEXT.md:77`.

#### What's done well

- The hot path itself is exactly what the plan demanded (S1 aside): one volatile read, one ulong
  dictionary lookup, early return; nothing WinRT is created or resolved per advertisement, and the
  connect path reuses the tracked InTheHand instance with `_pendingConnections` as the sole gate.
- `BleAdvertisementMatcher` is genuinely immutable (defensive copies in the constructor) and the
  blank-fallback-name dropping — with a test explaining *why* a blank name would Contains-match
  everything — is a class of bug most implementations would have shipped.
- `TearDownWatcher` nulling the field before stop/unsubscribe keeps the service restartable after
  `StopDiscovering`, and treating any non-`Started` status (incl. `Aborted`) as not-running is the
  documented-correct retry model — verified against the WinRT `Start()`/Status docs this round.
- The DpiStageMonitor changes follow the plan to the letter: gate placed in `RefreshReaders` (not
  the 2 s poll), once-per-path skip log, CTS disposed by its last owner with both sides of the
  shutdown race guarded.
- Test quality is above the plan's bar: strict-parser whitespace cases, address-wins-over-fallback,
  and the Moq `Times.Exactly(2)` verification that the null keyboard result really re-enumerates.

#### Verdict

Ship: no
Reasoning: one blocker — the unsynchronised watcher create/start/stop/teardown path can orphan a
started, subscribed watcher for the process lifetime, defeating the "watcher stopped when
everything is connected" acceptance criterion in a service whose entire purpose here is scan
hygiene; the fix is a small lock around code that is now fully synchronous. The should-fixes are
each cheap and S1/S2 sit directly on the paths this branch owns.
First three things to tackle:
1. B1 — serialise watcher mutation (lock in `SyncScanStateAsync` + `TearDownWatcher`).
2. S1 — read `LocalName` only when the matcher has fallback entries; fix the plan's step-1 snippet.
3. S2 — guard `DeviceGattDisconnected` (or restore exception-capturing semantics).

### Round 2 — 2026-08-19 — PR #115 review (boikoserhii84)

- **M1 (Major)** — keyboard RGB service cache never invalidated; a keyboard replugged into a
  different USB port kept a cached service bound to the old dead DevicePath. Fixed (Option A):
  `DeviceRgbServiceFactory` converted to a normal constructor, subscribes to
  `IHidDeviceProvider.SubscribeToDeviceListChanged` and clears `_keyboardCache` on every
  device-list change; `_serviceCache`/`_telinkCache` left alone (pre-existing, scoped out by the
  reviewer) with a comment noting the shared defect.
- **m1** — recovery of an `Aborted` watcher via `Start()` on the same instance is undocumented in
  WinRT. Fixed: inside `_watcherLock`, an `Aborted` watcher is unsubscribed from `Received`,
  the field is nulled, and control falls through to the creation branch for a fresh instance.
- **m2** — the advertisement path no longer registers devices absent from `_trackedDevices`.
  Resolved by documentation (restoring per-ad registration would reintroduce the leak path):
  dated tradeoff note in Scope + Risks and a comment at the handler's early-return; worst-case
  registration delay is one sweep (≤30 s active, ≤5 min idle only when nothing was ever tracked).
- **m3** — stale "fresh service per gRPC call" remarks in `DeviceGate`, `SmartInfiniteTransport`
  and `RampageKeyboardKeyMappingService`. Fixed: reworded to the differing-lifetimes
  justification (RGB services process-cached, key-mapping per-call); lifetime note added to the
  Recommended approach.
- **m4** — `RefreshReaders` leaked its just-created `CancellationTokenSource` when losing the
  `_readers.TryAdd` race. Fixed: dispose the CTS and `continue` on a lost race.
- **m5** — `SyncScanStateAsync` was a synchronous Task-returning method with a false `Async`
  suffix. Fixed: renamed to `void SyncScanState()`, `return Task.CompletedTask;` removed, all
  four call sites updated (the `DeviceGattDisconnected` one stays inside its Round-1 S2 guard);
  live plan prose renamed.
