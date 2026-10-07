# Plan: add-keyboard-charge-push

## Goal

When a Smart Infinite keyboard (Rampage today) stays on its 2.4G dongle and the user plugs or
unplugs the USB charging cable, the card's ⚡ indicator currently reacts with up to 60 s of
latency: a charge-only cable enumerates no wired interface, so the only signal is the charge
flag in the 0x1F battery reply, and the App polls that once a minute (`BatteryPollInterval`).
Reproduced live 2026-09-02: cable in ~14:33:30 → flag `true` only at the 14:34:25 poll; cable
out → flag `false` only at 14:43:25. Fix: a DeviceHost-side monitor polls 0x1F on a short
interval for dongle-connected battery keyboards and pushes battery/charge changes through the
**existing** `BatteryChange` gRPC push (the TaipanStateMonitor pattern) — the App side already
applies pushes end-to-end (`ApplyBatteryPush` handles `Known`/`Percent`/`IsCharging`), so the
icon reacts in seconds in both directions.

## Scope

**In:**
- New DeviceHost background monitor for catalog devices that read battery over a HID query on
  the 2.4G dongle (predicate below — Rampage's dongle transport today), polling every few
  seconds while such a device is present and pushing on change.
- `GetBatteryLevel` answering from the monitor's fresh cache for those devices (the Taipan and
  Cosmo handlers already do exactly this), so the App's 60 s poll stops issuing a duplicate
  HID exchange.
- A successful-path cache + steady-state log demotion in
  `HidDeviceDiscoveryService.TryGetBatteryLevel` — at a seconds cadence the current behavior
  (rescan 8 interfaces, two failing opens, ~8 INF lines per read) is unacceptable noise/waste.
- Debounce of charge-flag transitions (two consecutive agreeing reads, the CosmoStateMonitor
  precedent) so a single malformed reply cannot flicker the icon.

**Out:**
- Any proto/gRPC contract change — `BatteryChange` already carries `IsCharging`.
- Any App-side change — `ApplyBatteryPush`, `FindCardForBatteryPush`, the 60 s poll (kept as
  backstop) and the card INPC chain are already correct; they are verified, not modified.
- TAIPAN (`TaipanStateMonitor` pushes `UsbConnected` already) and Cosmo/Starlight
  (`CosmoStateMonitor`; headset charging is a separate known gap — no charge source exists).
- Wired transports — a live cable enumerates HID, `ShouldShowCharging` handles it instantly.
- Bluetooth transports — no host battery protocol on this stack.

## Options considered

1. **App-side: shorten `BatteryPollInterval`** (60 s → ~5 s).
   - + One-line change.
   - − Multiplies gRPC calls and HID exchanges for *every* battery device, not just the ones
     with the latency problem; each tick pays today's full interface rescan; still seconds of
     gRPC/poll overhead for a flag that rarely changes. The code's own comment says the level
     doesn't move faster than 60 s — it's only the charge flag that does. Rejected.
2. **DeviceHost-side monitor + push on change** *(recommended)* — poll 0x1F every ~3 s host-side
   for the affected devices only, push through the existing `BatteryChange` stream only when
   something changed.
   - + Latency drops to ~3–6 s both directions; network traffic ≈ zero at steady state; the
     entire client path already exists and is already tested
     (`HomeViewModelBatterySourceTests` pins `ApplyBatteryPush`); two sibling monitors
     (`TaipanStateMonitor`, `CosmoStateMonitor`) establish every pattern needed — presence
     gating, idle backoff, two-poll debounce, cache-answering `GetBatteryLevel`.
   - − A new host-side poll wakes the keyboard's radio every ~3 s (see Risks); one more
     background service to maintain.
3. **Push from the keyboard (listener, no polling)** — the TaipanStateMonitor model.
   - − Smart Infinite boards don't volunteer battery/charge reports; 0x1F is request/response
     (measured 2026-08-17 and daily since). Nothing to listen to. Rejected.

## Recommended approach

Option 2. New `SmartInfiniteBatteryMonitor` (`src/ProoveGaming.DeviceHost/Services/`),
`BackgroundService`, shaped on `CosmoStateMonitor`:

- **Tracked set (catalog-driven, pure helper `ResolveTrackedDevices`)**: every expanded
  `DeviceMap` row with `ConnectionType == "Dongle2_4G"`, `SupportsBattery`, a set
  `BatteryCommand`, and parseable VID/PID. Today that is exactly Rampage's dongle row
  (VID 1389 / PID 49271, command 31); Taipan (no `BatteryCommand`), Cosmo (ditto), wired and
  Bluetooth rows fall out naturally. No VID constants in code — the catalog decides.
- **Loop**: while a tracked device is enumerated, poll every **3 s** via
  `IHidDeviceDiscoveryService.TryGetBatteryLevel(vid, pid)`; while none is present, wait on
  the device-list-changed signal with a 30 s safety net (same signal-plus-net shape as
  CosmoStateMonitor's `IdleInterval`). The gate inside `TryGetBatteryLevel` already yields to
  key-table/RGB/macro writes (`DeviceGate.TryAcquire` → skip the tick), so no new contention.
  Two gates on the fast cadence (added per review round 1):
  - **Consumer gating (S2)** — the poll runs only while at least one gRPC client is streaming
    device events, via the same `AddConsumer`/`RemoveConsumer` pair
    `DeviceService.SubscribeOnDevices` already drives on `TaipanStateMonitor`; with the App
    closed the loop parks on the signal/30 s net and wakes no radio at all.
  - **Failed-poll backoff (S1, pure helper `ComputeCadence`)** — the receiver enumerates with
    the keyboard switched OFF, so after `FailedTicksBeforeBackoff` (5) consecutive
    no-reading polls on a present device the loop falls back to the 30 s cadence instead of
    running a full failing candidate scan every 3 s; any valid reading or a device-list
    change restores 3 s. The threshold is deliberately above two ticks so a multi-second
    gate-held macro/key-table write never triggers it.
- **Change detection + debounce (pure helper `ComputePush(previous, reading)`)**:
  - `-1` (no reading) is **no evidence**: no push, cache kept, debounce counter reset.
    Clearing on absence stays with the existing App logic (disconnect events, poll-miss
    streak) — this monitor only fast-forwards *good* readings.
  - Battery **level** change with a valid reading → push immediately.
  - **`IsCharging` transition** → push only after two consecutive polls agree (~3–6 s
    total), the Cosmo two-poll rule: byte [9] vanishing for one reply must not flicker ⚡ off.
  - Push carries `Known=true, Percent, IsCharging` with the shape of
    `TaipanStateMonitor.BatteryEvent`; `DeviceKey` may be empty — `FindCardForBatteryPush`
    falls back to VID/PID, and Rampage is a merged-transport card matched by name/ids.
- **`DeviceService`**: subscribe the monitor's `BatteryChanged` exactly like
  `TaipanStateMonitor_BatteryChanged` (reuse `SendBatteryChangedAsync` as-is), and in
  `GetBatteryLevel` answer from the monitor's cache when the cached reading is fresh
  (≤ ~10 s), before falling through to the live `TryGetBatteryLevel` — mirroring the Taipan
  (`TryGetBattery`) and Cosmo (`TryGetLastReport`) branches. This removes the double HID
  exchange the App's 60 s poll would otherwise add on top of the monitor's cadence.
- **`HidDeviceDiscoveryService.TryGetBatteryLevel` hygiene** (required by the new cadence):
  cache the device path that last produced a valid reading per (vid, pid); on the next call
  try that path first and skip the full candidate scan; invalidate the cached path when its
  read fails. Demote the steady-state per-read log lines (`found N interfaces`,
  `candidate[i]`, `trying path`, `response`, `non-zero bytes`) to `Debug` once a cached path
  is being reused — first resolution and every parse *result* line stay as they are, so the
  field-debugging story (this very investigation used those logs) survives at one line per
  read instead of ~10.
- **Registration** (`Program.cs`): singleton + hosted service, exactly how
  `TaipanStateMonitor`/`CosmoStateMonitor` are registered (DeviceService needs the instance
  for the cache and the event).
- **Lightweight-query constraint (user requirement, 2026-09-02)**: the poll loop must be as
  cheap as possible and must involve the **Device Association Service in zero ways** (see
  `docs/audits/2026-09-02-device-association-memory-audit.md` — DAF enumeration is the
  DeviceHost's known heavy path). This holds by construction and must be preserved:
  - Candidate lookup is a filter over the discovery singleton's **in-memory** `_hidDevices`
    list (`GetCandidates` takes `_lockObj` and LINQ-filters — no enumeration per call), and
    the path cache reduces even that to a dictionary hit.
  - The read itself is a direct HID open **by cached path** (CreateFile) + one write + one
    read — no `DeviceInformation`/DAF, no WMI, no device-tree walk, ever.
  - **Presence detection** ("is a tracked device connected?") must come from the same
    in-memory HID list or the device-list-changed signal — never from an enumeration API.
  - The monitor must never call into `BluetoothDeviceDiscoveryService` or any
    `DeviceInformation`-based API; the reviewer should reject any tick-path call that leaves
    process memory except the single HID exchange.

No proto changes, no App changes, no new NuGet, no `.slnx` edits.

## File-level change list

Ordered tests-first for the pure logic (new feature — the helpers exist to be testable).

| # | File | Action | Intent |
| --- | --- | --- | --- |
| 1 | `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteBatteryMonitorTests.cs` | create | Pins the two pure helpers: `ResolveTrackedDevices` against the **real** catalog (Rampage dongle row in; Taipan/Cosmo/wired/Bluetooth rows out — the DeviceHost project is already referenced by this test project); `ComputePush` sequences (level change → push; charge flip → push only on 2nd agreeing read; `-1` → no push, counter reset; flip that reverts after one read → no push). |
| 2 | `src/ProoveGaming.DeviceHost/Services/SmartInfiniteBatteryMonitor.cs` | create | The monitor: catalog-driven tracked set, 3 s poll while present / signal+30 s idle, `TryGetBatteryLevel` reads, per-device cache, two-poll charge debounce, `BatteryChanged` event (Taipan `BatteryEvent` shape), `TryGetBattery(vid, pid, out reading, maxAge)` cache accessor for DeviceService. Pure helpers `internal static` for #1. |
| 3 | `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` | edit | Subscribe `BatteryChanged` → existing `SendBatteryChangedAsync`; in `GetBatteryLevel`, answer from the monitor's ≤10 s-fresh cache for tracked devices before the live read (new branch beside the Taipan/Cosmo ones). |
| 4 | `src/ProoveGaming.DeviceHost/Program.cs` | edit | Register `SmartInfiniteBatteryMonitor` (singleton + hosted service, mirroring the two sibling monitors). |
| 5 | `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs` | edit | Successful-path cache per (vid, pid) with try-first + invalidate-on-failure; demote steady-state per-read discovery/response logs to Debug when reusing a cached path (parse-result line stays INF). |
| 6 | `docs/CONTEXT.md` | edit | Glossary: **Smart Infinite battery monitor** one-liner (third sibling of the Taipan/Cosmo monitors). |

*Implementation note (2026-09-02, /coder):* two support files joined the list. The presence
check the lightweight-query constraint demands ("from the same in-memory HID list") became
`IsHidDevicePresent(vid, pid)` on `IHidDeviceDiscoveryService` (+ implementation in #5) — a
lock-and-scan over `_hidDevices`, no enumeration, no allocation. And
`ProoveGaming.DeviceHost.csproj` gained `InternalsVisibleTo` for
`ProoveGaming.Infrastructure.Tests`, since the plan's `internal static` pure helpers were the
DeviceHost's first internals a test pins. The monitor reuses
`TaipanStateMonitor.BatteryEvent` (as the plan's "shape of" suggests) so
`SendBatteryChangedAsync` is shared verbatim.

*Fix pass (2026-09-02, /coder, round 1):* S1 landed as the pure `ComputeCadence` helper +
`FailedTicksBeforeBackoff = 5` in the monitor (pinned by four new tests); S2 as the
`AddConsumer`/`RemoveConsumer` pair mirrored from `TaipanStateMonitor` and driven from the
same two call sites in `DeviceService.SubscribeOnDevices`. Both nits fixed:
`HidDeviceDiscoveryService.Stop()` now clears its lists under `_lockObj`, and resolved
output-report-ID lists are cached per path beside `_lastGoodBatteryPath` (dropped together
on invalidation).

## Acceptance criteria

- [ ] Given a Rampage on its 2.4G dongle (no wired interface enumerated), when the USB cable
  is plugged in, then the card's battery icon switches to the charging variant within **10 s**
  (3 s poll + two-poll debounce + push), without waiting for the App's 60 s poll.
- [ ] Given the same setup with the cable in and ⚡ showing, when the cable is pulled, then the
  charging icon reverts to the level-colored battery icon within **10 s**.
- [ ] Given a Rampage connected and the DeviceHost running for a minute with no cable change,
  then the DeviceHost log gains at most ~1 battery line per poll at Information level (no
  `candidate[i]` / `trying path` scans per tick — those appear only on first resolution or
  after a path invalidation).
- [ ] Given the App's own 60 s battery poll fires while the monitor is active, then
  `GetBatteryLevel` answers from the monitor's cache (log shows no extra HID exchange for the
  tracked device at the poll timestamp).
- [ ] Given a macro/key-table write in progress over 2.4G (device gate held), when the monitor
  tick lands, then the tick is skipped cleanly (existing gate-yield path) and the next tick
  recovers — no corrupted exchanges, no error spam.
- [ ] Given a TAIPAN on its dongle and a Cosmo on its dongle, their battery/charge behavior is
  byte-for-byte unchanged (their own monitor branches answer first; the new tracked-set
  predicate excludes them — pinned by test).
- [ ] Given one reply missing the charge byte while the cable stays in (transient), then the ⚡
  icon does not flicker off (two-poll debounce — pinned by the `ComputePush` tests).
- [ ] Given the monitor running its steady-state tick, then the tick performs exactly one HID
  open/write/read against a cached device path and touches no enumeration API — no
  `DeviceInformation`/Device Association Framework, no WMI, no device-tree walk (reviewer
  verifies the tick path by reading; the in-memory `GetCandidates` + path cache is the whole
  lookup).
- [ ] `SmartInfiniteBatteryMonitorTests` pass; `HomeViewModelBatterySourceTests` and the full
  Release suite stay green: `dotnet test ./src/ProveGaming.slnx -c Release`.

## Test strategy

New feature (`add-*`) — required coverage, all in
`tests/ProoveGaming.Infrastructure.Tests` (that project references both Infrastructure and
DeviceHost):

- **`SmartInfiniteBatteryMonitorTests.ResolveTrackedDevices…`** (non-negotiable): loads the
  real DeviceHost catalog via the existing `DeviceHostCatalog.Load()` seam and asserts the
  tracked set is exactly the dongle rows with `SupportsBattery` + `BatteryCommand` — today
  `{Rampage dongle}`; explicitly asserts Taipan, Cosmo, every wired row and every Bluetooth
  row are excluded. This is the guard that the new poll can never touch a device another
  monitor owns.
- **`SmartInfiniteBatteryMonitorTests.ComputePush…`** (non-negotiable): the debounce table —
  valid-level change pushes; charge flip pushes only on the second agreeing read; `-1` never
  pushes and resets the flip counter; flip-then-revert produces no push; first-ever reading
  pushes.
- **Existing, must stay green:** `HomeViewModelBatterySourceTests` (`ApplyBatteryPush`
  semantics the push lands on), `HomeViewModelLowBatteryTests`, `StarlightCatalogTests` /
  `HidDeviceMapRegistryBluetoothTests` (catalog readers — untouched).

Deliberately **not** covered by automated tests: the `BackgroundService` loop itself (timing,
device-list signal, real HID I/O — same declared gap as `TaipanStateMonitor` and
`CosmoStateMonitor`, neither of which has loop tests), the gRPC stream write, and the
end-to-end icon latency — all manual QA below. The path-cache inside
`HidDeviceDiscoveryService` is exercised indirectly by every battery read; its logic is a
private lookup, not worth a public seam.

## QA (manual test steps)

1. Put the Rampage keyboard's switch in 2.4G mode with its dongle plugged in, no USB cable.
   Launch the Proove Gaming app and find the Rampage card showing the dongle icon and a
   battery percentage.
2. Plug the USB cable into the keyboard (leave the switch on 2.4G). Watch the battery icon.
   **Expected:** within about 10 seconds the icon changes to the charging (⚡) variant — no
   app restart, no waiting a minute.
3. Wait ~1 minute with the cable in.
   **Expected:** the ⚡ icon stays on steadily (no flicker to a normal battery and back).
4. Unplug the USB cable. Watch the icon.
   **Expected:** within about 10 seconds the ⚡ reverts to the normal battery icon with a
   percentage.
5. Repeat steps 2 and 4 three times in a row.
   **Expected:** the icon follows the cable each time within ~10 seconds.
6. While the cable is in (⚡ showing), open the keyboard's details page and assign a macro to
   a key (a write over 2.4G), then return to the home page.
   **Expected:** the macro saves normally and the ⚡ stays correct.
7. Connect a TAIPAN mouse via its dongle and (if available) a Cosmo headset via its dongle.
   **Expected:** their battery indicators behave exactly as before — TAIPAN shows ⚡ when its
   own cable is plugged, Cosmo shows its percentage with no charging icon.
8. Let the app run 10 minutes with the keyboard on 2.4G and check responsiveness.
   **Expected:** no lag or freezes in the app; keyboard input works normally throughout.
9. Leave the receiver plugged in, switch the keyboard OFF, and let the app run ~10 minutes.
   Then inspect the DeviceHost log (review round 1, S1).
   **Expected:** shortly after the switch-off the log shows one "backing off" line, then at
   most one failed-read burst every ~30 seconds — not a scan every 3 seconds. Switch the
   keyboard back on: the percentage returns within ~30 seconds and the log returns to one
   battery line per 3 s poll.
10. Close the App entirely (quit from the tray, not just minimise) and watch the DeviceHost
    log for ~2 minutes (review round 1, S2).
    **Expected:** battery lines stop (no polling with no client attached) and resume within
    ~30 seconds of reopening the App.

## Review focus

- `thread-safety` — the monitor's cache is read by gRPC handler threads
  (`GetBatteryLevel`) and written by the poll loop; the event fires on the loop thread and
  `SendBatteryChangedAsync` must stay fire-and-forget behind `_streamWriteLock` (existing
  pattern). The path cache in `HidDeviceDiscoveryService` is hit from the monitor loop and
  gRPC threads concurrently.
- `resource disposal` — `BackgroundService` shutdown must stop the loop promptly
  (`stoppingToken` through the waits); event subscription in DeviceService follows the
  existing monitors' lifetime (host singletons, no unsubscribe churn).
- `DI lifetimes` — singleton + hosted-service double registration must resolve to ONE
  instance (copy the sibling monitors' registration shape exactly).
- `performance hot-path` — the 3 s tick must not allocate per-tick beyond the HID buffers the
  read already uses; no polling while no tracked device is present.

## Performance & quality

The new poll is one gated HID write+read every 3 s while a tracked keyboard is present —
the same exchange the App already performs every 60 s, at 20× the cadence but with the
interface-scan removed by the path cache (one open, one write, one read per tick). While no
tracked device is enumerated, the loop parks on the device-list signal (zero work — the
CosmoStateMonitor discipline). Steady-state gRPC traffic is zero: pushes fire only on change.
`ComputePush`/`ResolveTrackedDevices` are pure and allocation-light (the tracked set resolves
once, like `TaipanStateMonitor.TrackedInterfaces`). Nothing on the UI thread changes; the App
applies pushes through the already-marshalled `ApplyBatteryPush` path.

## Risks

- **Radio wake every 3 s costs keyboard battery.** The 0x1F query goes over the air; at 3 s
  cadence the keyboard answers ~1200 queries/hour that it didn't before. Unknown real-world
  drain (the vendor's own driver polls comparable state). Mitigations if QA observes drain:
  raise the interval (5 s still beats 60 s by an order of magnitude) or poll fast only for
  ~60 s after any level/flag change and back off to 15 s when stable. Start simple at 3 s.
  *(Round 1 fixes narrowed this cost: with the App closed the consumer gating stops the poll
  entirely, and with the keyboard switched off behind its receiver the failed-poll backoff
  drops to the 30 s cadence — so the 3 s wake rate only applies while someone is actually
  looking at a live keyboard.)*
- **A second concurrent 0x1F caller.** Until #3 lands, the App's 60 s poll and the monitor can
  interleave on the same interface; `DeviceGate` serializes them (one yields), so worst case
  is a skipped tick — but land #3 in the same change so the steady state has one reader.
- **Log regression for field debugging.** The battery logs solved this exact ticket; the
  demotion must keep one parse-result line per read at Information so tomorrow's
  investigation still has a timeline.
- **Debounce vs responsiveness.** Two-poll agreement adds ~3 s; total ~6–9 s worst case. If
  QA finds that sluggish, the rising edge (false→true) can be made immediate and only the
  falling edge debounced — noted here so it's a one-line policy change, not a redesign.
- **Duplicate pushes vs App poll writes.** `ApplyBatteryPush` and the poll both write the same
  card fields with last-writer-wins semantics on the dispatcher — same as Taipan today; no new
  race class.

## Open questions

1. Poll interval: plan says **3 s** (Cosmo uses 2 s; the two-poll debounce makes effective
   charge latency ~6 s). Say now if you want 2 s (snappier, more radio wakes) or 5 s
   (gentler); otherwise `/coder` ships 3 s as a named constant.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-02: uncommitted working tree vs HEAD (`d167895`) on
`feature/remove-headset-bluetooth-lock`. Delta: new `SmartInfiniteBatteryMonitor.cs` +
`SmartInfiniteBatteryMonitorTests.cs`; edits to `DeviceService.cs`, `Program.cs`,
`HidDeviceDiscoveryService.cs`, `IHidDeviceDiscoveryService.cs`, DeviceHost csproj
(`InternalsVisibleTo`), `docs/CONTEXT.md`, this plan. Commits PG-645/PG-647 are earlier,
separately-reviewed work — out of scope here.

#### Plan conformance

- **Implemented as planned:** all 6 file-list items. Catalog-driven tracked set
  (`ResolveTrackedDevices`, no VID constants — the Rampage dongle row VID 1389 / PID 49271 /
  command 31 confirmed in `appsettings.json:251-257`); 3 s poll / signal + 30 s idle
  (byte-for-byte the CosmoStateMonitor loop shape, incl. the `SemaphoreFullException` guard
  and `OperationCanceledException → return` through the unsubscribing `finally`); two-poll
  charge debounce with `-1` as no-evidence (`ComputePush`); `TaipanStateMonitor.BatteryEvent`
  reuse so `SendBatteryChangedAsync` is shared verbatim; DeviceService cache branch correctly
  ordered **after** Taipan (`DeviceService.cs:93`) and Cosmo (`:111`) and **before** the live
  fallback (`:138`); path cache + log demotion in `TryGetBatteryLevel`; CONTEXT.md glossary
  entry. Open question 1 resolved as the plan default (3 s named constant).
- **Recorded deviations — both assessed sound:**
  - `IsHidDevicePresent` on `IHidDeviceDiscoveryService`: single implementer in the whole
    solution (no fakes broken), a lock-and-scan over the in-memory `_hidDevices` list
    (`HidDeviceDiscoveryService.cs:152-163`) — no enumeration, no allocation. Exactly the
    presence seam the lightweight constraint demanded.
  - `InternalsVisibleTo` in the DeviceHost csproj, scoped to
    `ProoveGaming.Infrastructure.Tests` (which already references DeviceHost): matches the
    repo's stated convention (expose to tests via `InternalsVisibleTo`, not `public`).
- **Lightweight-query constraint — VERIFIED by reading the full tick path.** Steady-state
  chain: `Poll` → `IsHidDevicePresent` (lock + scan, in-memory) → `TryGetBatteryLevel` →
  `GetBatteryConfig` (LINQ over in-memory `DevicesMaps`, built once in the
  `DeviceInfoProvider` ctor) → `_lastGoodBatteryPath` dictionary hit → `FindCandidateByPath`
  (lock + scan, in-memory) → `TryReadDevice` → `DeviceGate.TryAcquire` (in-process
  semaphore) → `ResolveReportIds` (descriptor of the already-held `HidDevice` instance) →
  `device.Open()` (CreateFile by path) + one write + one read, with a Win32
  CreateFile-by-path fallback. **Zero** `DeviceInformation` / Device Association Framework /
  WMI / device enumeration / `BluetoothDeviceDiscoveryService` calls anywhere in the chain;
  `_hidDevices` maintenance is event-driven (`RecognizeDevices` on device-list-changed), not
  per-tick.
- **Log-demotion contract — verified.** The parse-result line stays Information
  (`HidDeviceDiscoveryService.cs:282`); every per-step discovery/response line is Debug under
  `quiet`; warnings keep their level; `LogResponse` gates on `IsEnabled` before formatting.
  Steady state = one INF line per successful read, as criterion 3 demands.
- **Accepted limitation (per plan):** ~1 INF line per 3 s tick ≈ 28,800 battery lines/day in
  the DeviceHost log while a Rampage dongle is present — explicitly chosen for field
  debugging (Risks: "log regression"). Residual risk: daily log-file growth; revisit if the
  Serilog file size becomes a complaint.
- **Thread-safety / disposal / DI (Review focus):** clean. `_state` is a
  `ConcurrentDictionary` with atomic tuple replace (single writer: the loop; readers: gRPC
  via `TryGetBattery`); `_lastGoodBatteryPath` likewise; the event handler only reads
  `_serverStreamWriter` and fire-and-forgets `SendBatteryChangedAsync`, which takes
  `_streamWriteLock` asynchronously and catches everything (`DeviceService.cs:938-942`) — the
  poll thread never blocks and never sees a subscriber exception. `stoppingToken` flows
  through the only wait; subscribe/unsubscribe are the same delegate through a `finally`.
  Singleton + `AddHostedService(sp => sp.GetRequiredService<…>())` resolves to one instance
  (`Program.cs:38-39`), all dependencies singletons.

#### Code

- [x] **S1. Dongle-present-but-keyboard-off turns every tick into a full failing rescan** —
  `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs:114-146` +
  `src/ProoveGaming.DeviceHost/Services/SmartInfiniteBatteryMonitor.cs:236-280` — Should-fix
  **Problem / why it matters:** the 2.4G receiver (VID 1389 / PID 49271) enumerates whether
  or not the keyboard behind it is powered on, so `IsHidDevicePresent` keeps the poll at the
  3 s cadence while the keyboard is switched off. If in that state the dongle does not serve
  a valid stale reading (it may time out, or answer zeros → "out of range"), no reading ever
  validates, so `_lastGoodBatteryPath` never (re)populates and **every** tick runs the full
  candidate scan: N interfaces × report IDs × up to 2 s of blocking read timeouts, plus
  ~10-20 log lines per tick at INF/Warning (`LogDiscoveryHeader`, per-candidate warnings,
  "no valid battery reading obtained"). Concrete scenario: user leaves the dongle in and
  powers the keyboard off overnight → hours of continuous failed HID exchanges and a log
  growing by tens of thousands of lines. Whether this fires depends on the dongle's firmware
  (Cosmo's dongle serves stale data; Rampage's is unmeasured) — it must be checked on
  hardware.
  **Fix:** in the monitor, back off after a failed pass — e.g. after K consecutive ticks
  with no valid reading for a present device, wait `IdleInterval` instead of `PollInterval`
  (reset on any valid reading or device-list change). One counter, no new seams. Also add a
  QA step: dongle in, keyboard off for ~10 min → inspect the DeviceHost log.
- [x] **S2. Polling continues with no gRPC consumer — radio wakes with nobody watching** —
  `src/ProoveGaming.DeviceHost/Services/SmartInfiniteBatteryMonitor.cs:236` — Should-fix
  **Problem / why it matters:** the DeviceHost is a Windows Service, so with the App closed
  the monitor still wakes the keyboard's radio ~28,800 times/day for readings nobody
  consumes (pushes go nowhere — `_serverStreamWriter` is null — and `GetBatteryLevel` is
  never called). The plan's Risks weighs radio drain only for the in-use case; the
  no-App case is all cost, zero benefit. Plan-conformant (the plan gates on presence only;
  Cosmo polls unconditionally too, but its poll is a local USB control read, not a radio
  wake), so this is a flagged residual product risk, not a deviation.
  **Fix:** the consumer hooks already exist — `DeviceService.SubscribeOnDevices` calls
  `_taipanStateMonitor.AddConsumer()`/`RemoveConsumer()`; mirror that pair on this monitor
  and treat zero consumers like "no device present" (idle on the signal). Acceptable as a
  follow-up; if taken, update this plan's Scope/Risks in the same change.

#### Tests

- [ ] none required beyond the shipped coverage — both non-negotiables are present and pin
  the real catalog (`TrackedSetIsExactlyTheRampageDongleRow`,
  `TaipanAndTheHeadsetsAreNeverTracked`, `WiredAndBluetoothRowsAreNeverTracked`) and the
  full debounce table including the `< 1 or > 100` boundary (`0`/`101` vs `1`/`100`) and the
  two flip×level interleavings. Orchestrator-verified: Release build clean, full Release
  suite 2488/2488. Informational gaps, consistent with the plan's declared-gap list:
  `TryGetBattery` freshness (would need a clock seam) and the DeviceService branch ordering
  (manual QA) are untested; if S1's backoff lands, its counter logic should get a pure-helper
  test in the same style.

#### Nits

- `Stop()` clears `_hidDevices`/`_dtos` outside `_lockObj` while `IsHidDevicePresent` /
  `GetCandidates` / `FindCandidateByPath` enumerate under it — pre-existing and
  shutdown-only (`Worker.cs:61`, monitors stop first in reverse registration order), but the
  new 3 s reader widens the window; moving the two `Clear()` calls inside the lock is free —
  `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs:81-82`.
- `ResolveReportIds` re-parses the report descriptor and rebuilds its LINQ list on every
  tick, including the quiet cached-path one; the IDs are as stable as the path, so they
  could be cached beside `_lastGoodBatteryPath`. In-memory only (constraint intact), just
  gen0 churn at a 3 s cadence — `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs:314-343`.

#### What's done well

- The two pure helpers carry all the decision logic, leaving the loop nearly logic-free —
  exactly what made the test table this complete; `LevelChangeWhileAFlipRevertsPushesTheConfirmedChargeState`
  pins a genuinely subtle interleaving.
- The gate-busy vs read-failed distinction in `TryReadDevice` (keep the cached path on a
  held gate, invalidate only on a real failure) is the detail that keeps a multi-second 2.4G
  key-table write from evicting a perfectly good path.
- Comment discipline: every non-obvious choice (why `-1` is no evidence, why `quiet` spares
  the parse-result line, why the tracked set resolves lazily) is explained at the site with
  its measurement date.

#### Verdict

Ship: yes — no blockers; the code matches the plan including the lightweight-tick
constraint, verified by reading the whole call chain. S1 and S2 are acceptable follow-ups
but S1 should be QA-probed on hardware before release. First: 1) QA the keyboard-off/dongle-in
log behavior and add the S1 backoff if it spams; 2) decide on S2 consumer gating (one
mirrored AddConsumer/RemoveConsumer pair); 3) fold the `Stop()` clears under the lock while
someone is in the file.
