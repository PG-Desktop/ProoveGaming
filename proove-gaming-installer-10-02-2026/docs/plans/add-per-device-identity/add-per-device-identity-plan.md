# Plan: Per-device identity for multiple identical devices

Branch: `feat/PG-246-Blaze-macros` (never merge to `main`).

> **Note (2026-08-26, `fix-duplicate-device-cards`):** the two-identical-units contract below is preserved, not changed. The `devices` table now keys a **merged-transport** product's row on `(owner, name)` instead of the routing identity, because that identity changes with the live transport; every other card — including two identical units of one model — keeps the `device_key`-bearing tuple this plan established. The one-time heal migration that collapses legacy duplicates only touches same-name groups holding **two or more distinct `(vendor_id, product_id)` pairs**, which two identical units never are: they share one pair and differ only by `device_key`. See `docs/plans/fix-duplicate-device-cards/`.

> **Note (2026-08-27, `fix-owner-scoped-state-persistence`):** the two-identical-units guarantee is
> now enforced by the heal migration itself, not merely implied by its heuristic. The note above is
> right that two identical units share one `(vendor_id, product_id)` pair — but a same-name group
> holding two such units **plus** one extra transport row for one of them has two distinct pairs, so
> the "two or more distinct pairs" rule would have collapsed both units into one card and flagged it
> merged, deleting a device the user owns. The flagging `UPDATE` now carries a `NOT EXISTS` bail-out:
> a group containing two rows that share `(vendor_id, product_id)` and differ in `device_key` — the
> positive proof of two physical units this plan established — is left entirely alone. See
> `docs/plans/fix-owner-scoped-state-persistence/`.
## Goal

Support two (or more) **physically identical** devices — e.g. two BLAZE mice, both
VID `0x04D9` / PID `0xA245` — so each shows as its own card in the App and is fully
independent: its own RGB, button bindings, DPI/rate and settings, both in the UI and on
the wire. Today everything is keyed on `(VID, PID)`, so two identical units collapse into
one card, share one set of saved settings, and every command reaches only whichever unit
the DeviceHost opens first.

## Scope

**In**
- A stable per-physical-device key (`deviceKey`) derived in the DeviceHost.
- DeviceHost: stop collapsing by `(VID, PID)`; group by `(VID, PID, deviceKey)`; route every
  command to the specific unit that matches `deviceKey`.
- gRPC contract: carry `deviceKey` on `HidDeviceResponse` and on every command request.
- App: cards carry `deviceKey`; dedupe/group by it; every command passes it; DPI/rate events
  map their `DevicePath` to `deviceKey`.
- App SQLite persistence: re-key `device_rgb`, `device_rgb_effect`, `device_settings`,
  `button_bindings` to include `deviceKey`, with a schema migration.

**Out**
- Bluetooth device identity (BT already keyed by `DeviceId`; unchanged here).
- Per-device identity for keyboards is delivered by the same generic mechanism but only
  mouse (`EasenMouseService`) command-routing is exercised/tested on hardware in this change.
- Migrating existing saved settings onto a specific unit (see Open questions — legacy rows
  are treated as orphaned; pre-release data loss is acceptable).
- Renaming/labelling the two cards distinctly in the UI beyond what already differentiates
  them (they will still both read "BLAZE"; disambiguation text is a follow-up).

## Options considered

### Identity source

1. **Windows Container ID** (`DEVPKEY_Device_ContainerId`) — *recommended*.
   A GUID identical for **all** HID interfaces/collections of one physical device and unique
   per device. Robust: correctly groups a single mouse's `MI_00 Col01..03` + `MI_01 Col04`
   into one unit, and cleanly separates a second identical unit. Cost: one `SetupAPI`/`CfgMgr`
   property query per interface path in the DeviceHost (the enumeration code already uses
   `SetupDi*` in `DpiStageMonitor`).
2. **Parse the parent instance from `DevicePath`** — rejected as the primary key.
   The HID interface path (`\\?\hid#vid_04d9&pid_a245&mi_01&col04#8&206abbc6&0&0003#{guid}`)
   has a per-**interface** instance hash; the segment is **not** shared across a device's own
   interfaces, so it can't reliably regroup them. Usable only as a last-resort fallback.
3. **Serial number** — cheapest (`HidDeviceState.SerialNumber` already captured) but
   hardware-dependent: cheap mice frequently report an empty or identical serial, so it can't
   be relied on to be unique. Kept as an *opportunistic upgrade* (see Open questions).

**Recommendation:** Container ID as `deviceKey`, computed once per interface during discovery.
Fall back to the raw `DevicePath` interface-instance string only when Container ID can't be
read (keeps a single device working even if the property query fails).

### How `deviceKey` flows to commands

1. **Add `device_key` to every command request** — *recommended*. Explicit, self-describing,
   symmetric with `HidDeviceResponse`. The App already holds the key on the card.
2. Keep requests VID/PID-only and have the DeviceHost "guess" the device — rejected: ambiguous
   by construction, which is the whole bug.

### Persistence re-key

1. **Add `device_key` to the primary key of the four device tables + migration** —
   *recommended*. Minimal schema delta, matches the existing `AddColumnIfMissing` pattern used
   for `is_rainbow`.
2. Separate identity table mapping `deviceKey → settings-id` — rejected as premature; no
   requirement yet needs the extra indirection.

## Recommended approach

Introduce a single opaque `deviceKey` string (Container ID) minted in the DeviceHost during
HID discovery and carried everywhere `(VID, PID)` is carried today. `(VID, PID)` stays as the
catalog/type lookup; `(VID, PID, deviceKey)` becomes the *instance* identity used for cards,
command routing and persistence. Empty `deviceKey` means "unknown / legacy" and behaves like
today (single-device fallback), so a device that fails the Container-ID query still works.

Implementation order (each step builds + is coherent on its own):
1. proto: add fields, regenerate.
2. Infrastructure/DeviceHost: mint `deviceKey`, group by it, route commands by it.
3. App: thread `deviceKey` through cards, commands, and DPI/rate mapping.
4. App persistence: re-key tables + migration.

## File-level change list

### 1. gRPC contract (regenerate after editing)

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — add
  `string device_key` to: `HidDeviceResponse`, `GetBatteryLevelRequest`,
  `SetDeviceLedColorRequest`, `SetMouseSettingsRequest`, `SetDpiStagesRequest`,
  `SetActiveDpiStageRequest`, `SetReportRateRequest`, `SetButtonBindingsRequest`,
  `GetButtonBindingsRequest`. Use a new field number after the current max in each message.
  **Regeneration:** the C# is generated on build (`Grpc.Tools`); a normal
  `dotnet build ./src/ProveGaming.slnx` regenerates `Deviceservice.cs`. Call this out in the
  PR because both App and DeviceHost recompile against the new contract.

### 2. Identity + Infrastructure

- `src/ProoveGaming.Infrastructure/Classes/HidDeviceState.cs` — add `string DeviceKey`
  (Container ID; empty when unknown).
- `src/ProoveGaming.Infrastructure/Services/HidDeviceStateFactory.cs` — populate `DeviceKey`
  from the device path via a new Container-ID helper; fall back to the interface instance
  substring of `DevicePath` if the query fails.
- `src/ProoveGaming.Infrastructure/Services/DeviceContainerId.cs` *(new)* — small static
  helper: `string? TryGet(string devicePath)` using `CM_Get_Device_Interface_PropertyW`
  (or `SetupDiGetDeviceProperty`) with `DEVPKEY_Device_ContainerId`. Pure P/Invoke, no state.
- `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` —
  `CreateForDevice(int vendorId, int productId, string deviceKey)`; cache key becomes
  `(vid, pid, deviceKey)`; pass `deviceKey` into the created `EasenMouseService` (and keyboard
  services) so they open the matching physical unit. Keep a `deviceKey == ""` path that
  behaves like the current "first matching device".
- `src/ProoveGaming.Infrastructure/Interfaces/IDeviceRgbServiceFactory.cs` — update signature.
- `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs` — accept the target
  `deviceKey`; when opening the HID stream, select the `HidDevice` whose Container ID equals
  `deviceKey` (fall back to the current interface heuristic when key is empty). This is the
  one hot path that must open the **right** unit for LED/keys/DPI writes.
- `src/ProoveGaming.Infrastructure/Interfaces/IHidDeviceProvider.cs` /
  its implementation — add a way to get the candidate `HidDevice`s filtered by `deviceKey`
  (or expose Container ID per device so `EasenMouseService` can filter). Least-surface option:
  a helper `IEnumerable<HidDevice> GetDevices(int vid, int pid, string deviceKey)`.

### 3. DeviceHost

- `src/ProoveGaming.DeviceHost/Helpers/DevicesHelper.cs` — replace `GroupByPidVid` with
  `GroupByDevice` grouping on `(ProductId, VendorId, DeviceKey)`; `Map(HidDeviceState)` sets
  `HidDeviceResponse.DeviceKey`. Keep merging a single unit's interfaces (same key) but keep
  distinct units separate.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — every command handler reads
  `request.DeviceKey` and calls `CreateForDevice(vid, pid, key)`; the subscribe/`GetDevices`
  paths use `GroupByDevice`; `SetButtonBindings`/`GetButtonBindings` pass the key through to
  the store-less device write (host no longer persists bindings — App owns them).
- `src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs` — already per-`DevicePath`; add
  Container-ID resolution so `StageEvent`/`RateEvent` carry `DeviceKey` (add field to the two
  event structs) instead of only `DevicePath`, so the App can map events to the right card.

### 4. App — identity plumbing

- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — add `string DeviceKey`.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — group/dedupe by `(ClientName,
  DeviceKey)` (or `DeviceKey` alone) instead of `ClientName`; a reconnect matches the existing
  card by `DeviceKey`; the connect-time re-apply passes `DeviceKey`.
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` +
  `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — every command method gains a
  `string deviceKey` parameter and sets it on the request; `TryGetLastDpiStage` /
  `TryGetLastReportRate` and the `DpiStageChange`/`ReportRateChange` events key by
  `(vid, pid, deviceKey)`; `GetButtonBindingsAsync`/`SetButtonBindingsAsync` take the key.
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` `DpiStageChange` /
  `ReportRateChange` messages — add `device_key` so the App can route the live event to the
  right card (these are pushed on the subscribe stream).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel*.cs` — the current
  `Device` already exposes `VendorId`/`ProductId`; add `DeviceKey` usage on every
  `_usbHidDeviceService.*Async(Device.VendorId, Device.ProductId, …)` call
  (`Rgb`, `Buttons`, `Settings`, `MacroRecorder` reapply, `ReapplyPersistedRgbAsync`).

### 5. App — persistence re-key

- `src/ProoveGaming.App/Services/LocalStorageService.cs` — add `device_key TEXT NOT NULL
  DEFAULT ''` to the PK of `device_rgb`, `device_rgb_effect`, `device_settings`,
  `button_bindings`; every getter/setter gains `string deviceKey`; migration via the existing
  `AddColumnIfMissing` helper **plus** PK change (SQLite can't alter a PK in place — for these
  tables recreate: `CREATE TABLE …_new` with the new PK, `INSERT … SELECT …, ''`, drop, rename;
  wrapped in a transaction). Legacy rows land with `device_key = ''` (orphaned; see Risks).
- `src/ProoveGaming.App/Models/RgbStateRecord.cs` — unchanged shape, but the caller passes the
  key (record itself needn't hold it).
- Callers of the persistence methods (RGB save/load, settings save/load, button-binding
  save/load, `button_bindings` macro links) — all in `DeviceDetailsViewModel.*` — pass
  `Device.DeviceKey`.

## Acceptance criteria

1. Given two identical BLAZE mice plugged into two ports → the App Home screen shows **two**
   device cards, not one.
2. Given two BLAZE cards → setting RGB effect A on card 1 and effect B on card 2 → each
   physical mouse shows its own effect (the write reaches the intended unit, verified by the
   LED and by the `[LED]` log line for the matching device).
3. Given a per-device saved effect → unplug/replug one mouse → that mouse's saved effect is
   re-applied to **that** unit only (`ReapplyOnConnect` routes by `deviceKey`).
4. Given a button binding saved on card 1 → it does not appear on card 2 (persistence keyed by
   `deviceKey`; `GetButtonBindings` for card 2 returns card 2's own set).
5. Given one BLAZE mouse only (the common case) → behavior is unchanged from today: one card,
   settings persist and reload across reconnect.
6. Given a device whose Container ID cannot be read → `deviceKey` is empty and the device still
   appears and is controllable (single-device fallback), i.e. no regression to today's path.
7. `dotnet build ./src/ProveGaming.slnx` succeeds; `dotnet test ./src/ProveGaming.slnx` is
   green.

## Test strategy

This is an `add-*` feature; the following automated coverage is required. It splits along the
"pure logic vs hardware" line — Container-ID resolution and real HID opens are **not**
unit-testable (need Windows device tree + hardware) and are covered by QA instead.

- `tests/ProoveGaming.Infrastructure.Tests`
  - `DevicesHelper.GroupByDevice`: given HID states for two units (same VID/PID, different
    `DeviceKey`, each with several interfaces) → two grouped results, each merging only its own
    interfaces. Given empty `DeviceKey` on all → falls back to one group (today's behavior).
  - `DeviceRgbServiceFactory` cache: `CreateForDevice(vid,pid,keyA)` and `(vid,pid,keyB)` return
    **distinct** cached service instances; same key returns the same instance.
- `tests/ProoveGaming.App.Tests`
  - `LocalStorageService` re-keyed CRUD: save RGB/settings/button-binding under `(vid,pid,keyA)`
    and `(vid,pid,keyB)` → each reads back independently; reading `keyA` never returns `keyB`'s
    rows.
  - Migration: open a DB created with the **old** schema (no `device_key`) → `EnsureSchema`
    upgrades the four tables without throwing and preserves/So-orphans legacy rows per the
    documented rule (assert the new columns exist and old data didn't crash startup).
  - `HomeViewModel` grouping (if reachable without a live gRPC channel — the discovery handler
    takes a `List<HidDeviceResponse>`): feeding two responses with different `DeviceKey`
    produces two cards; same `DeviceKey` de-dupes to one.
- **Not automated:** Container-ID P/Invoke correctness, `EasenMouseService` opening the correct
  physical unit, live DPI/rate event routing — all require real hardware; covered in QA.

## QA (manual test steps)

1. Plug in **two** BLAZE mice (two USB ports). Launch the App. **Expected:** two separate
   device cards appear on the Home screen.
2. Open the first card → RGB → pick **Breathing** with a red colour, Apply. Open the second
   card → RGB → pick **Neon**, Apply. **Expected:** mouse 1 shows red breathing, mouse 2 shows
   neon — each independently, no cross-talk.
3. On the first card, remap the **Forward** button to "My PC". Open the second card → Buttons.
   **Expected:** the second card's Forward is still its default, not "My PC".
4. Unplug the first mouse, wait 3 s, plug it back into the **same** port. **Expected:** its card
   reconnects and its red-breathing effect is restored automatically; the second mouse is
   unaffected.
5. Close and relaunch the App with both mice connected. **Expected:** both cards return, each
   with the settings you gave it (RGB + button remap persisted per unit).
6. Unplug the second mouse entirely, leaving one. **Expected:** exactly one card remains, fully
   functional (single-device path unchanged).
7. Regression: with a single BLAZE mouse only, repeat a normal RGB + button-remap + reconnect
   cycle. **Expected:** identical to previous builds — no new prompts, no lost settings.

## Review focus

- `gRPC contract` — new fields on many messages; both sides must agree and regenerate.
- `thread-safety` — the singleton discovery services and `DeviceRgbServiceFactory` cache now
  key by `(vid,pid,deviceKey)` and are hit from device-I/O threads and gRPC handlers.
- `resource disposal` — one `EasenMouseService`/HID stream **per physical unit** now; ensure no
  handle leak when a unit disconnects and its cached service is abandoned.
- `null-safety` — `deviceKey` may be empty/absent (legacy, or Container-ID query failure);
  every path must treat empty as "fallback", never crash.

## Performance & quality

- Container-ID resolution runs **during discovery** (not per command, not per frame). Resolve
  once per interface and cache on `HidDeviceState`; do **not** query it inside `SetLedColor` /
  the DPI read loop.
- `EasenMouseService` selecting the right `HidDevice` by key must not re-enumerate on every
  write — resolve the target device path once per service instance (the factory cache already
  gives one instance per unit).
- No new allocations on the DPI/rate read loop; the added `DeviceKey` on the event structs is a
  string reference already computed at discovery.
- Persistence calls stay off the UI hot path (already sync SQLite on the UI thread as today; no
  change to that contract).

## Risks

- **Container-ID stability.** For devices with no unique hardware serial, Windows may derive the
  Container ID from the USB port, so moving a mouse to a different port yields a new
  `deviceKey` → its saved settings won't follow it to the new port (it looks like a new
  device). Acceptable for v1; documented. Devices with a real serial are stable across ports.
- **Legacy settings loss.** Existing `(vid,pid)` rows migrate with `device_key = ''` and are
  effectively orphaned (no live device has an empty key), so pre-existing single-device users
  lose saved RGB/buttons on upgrade. Acceptable because distribution is pre-release/testing
  only. Mitigation option in Open questions.
- **Contract churn.** Many request messages change at once; a stale generated `Deviceservice.cs`
  (e.g. partial rebuild) will produce confusing mismatches — do a clean rebuild of the slnx.
- **Two identical units, one port hub reordering.** If Windows reorders enumeration, cards must
  stay bound to their `deviceKey`, not to list order — the App must match reconnects by key
  (covered by criteria 3/4).

## Open questions

1. **Serial check** — do the actual BLAZE units report a **unique** `SerialNumber`? If yes,
   prefer serial as `deviceKey` (stable across ports, no P/Invoke). Cheap to verify first
   (log `HidDeviceState.SerialNumber` for both units); decide before coding step 2.
2. **Single-device migration** — should we stamp legacy `(vid,pid)` rows onto the first-seen
   unit's `deviceKey` on first connect (preserving one existing user's settings), or accept the
   orphaning? Recommendation: accept orphaning for v1 (pre-release), revisit if we ship.
3. **UI disambiguation** — both cards read "BLAZE". Do we need a suffix ("BLAZE #2") or a
   per-card rename now, or is that a separate follow-up? Recommended: follow-up.

## Review findings

_Filled in by /reviewer._
