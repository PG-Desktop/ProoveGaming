# Plan: Group device config by transport (remove per-transport duplication)

## Goal

A multi-transport device is configured today as **N flat entries** in
`AllowedDevices.AllowedDevicesList` (`src/ProoveGaming.DeviceHost/appsettings.json`). PG TAIPAN has
**three** entries that duplicate 11 shared fields — `Name`, `Hotspots` (a 5-item array), `DpiStages`,
`ImageScale`, `SupportsButtonRemap`, `SupportsMacros`, `SupportedMouseSettings`, `DeviceName`,
`DeviceType`, `CardType`, `ImagePath` — and differ in only four: `VendorId`, `ProductId`,
`HasBluetooth`, `ConnectionType`. Editing a hotspot coordinate means editing it in three places and
keeping them in sync. This restructures the **config shape** to one device definition carrying the
shared settings once, plus a nested list of **transports** (each: connection kind + VID/PID), and
**expands** that back into the existing per-transport `DeviceMap` rows internally so nothing
downstream changes.

> **Revised to approach A** after reading the *full* config, not just BLAZE/TAIPAN. The
> `AllowedDevices` list has ~10 devices including keyboards, and several multi-transport devices vary
> per transport in fields the first draft treated as shared (`DeviceType`, `SupportsRgb`,
> `BatteryCommand`, `ProductName`, `ProductId`). Two consequences, both baked into this revision:
> (1) a transport carries **optional per-field overrides** that fall back to the device default;
> (2) `MergeTransports` stays an **explicit optional flag**, not implied from transport count —
> because **Rampage** has two transports (wired + Bluetooth, same PID) yet is intentionally *not*
> merged today, and implying merge would change its behaviour.

## Scope

**In:**
- New grouped config shape: `AllowedDevices { Devices: [ { …device-level defaults…, MergeTransports?,
  Transports: [ { ConnectionType, VendorId?, ProductId?, ProductName?, DeviceType?, SupportsRgb?,
  BatteryCommand?, BatteryReportId?, BatteryDirectScale? } ] } ] }`. Transport fields are optional and
  fall back to the device-level value; only `ConnectionType` is required per transport.
- A pure expander (Infrastructure) that turns one grouped device × M transports into M `DeviceMap`
  rows — applying `transport-override ?? device-default` for each field, deriving `HasBluetooth` from
  `ConnectionType`, and stamping the device's explicit `MergeTransports` onto every row.
- Rewrite **all** of `src/ProoveGaming.DeviceHost/appsettings.json` `AllowedDevices` to the grouped
  shape (every device: mice + keyboards). Values transcribed verbatim; a same-name multi-entry device
  becomes one device with N transports carrying only what differs.
- Characterization tests for the expander (there are none today for this path), covering **all**
  expanded rows.

**Out:**
- `DeviceMap` shape, `HidDeviceMapRegistry` merge logic, `DeviceInfoProvider`'s *consumers*
  (`DeviceRgbServiceFactory`, `HidDeviceDiscoveryService`, `DeviceKeyMappingServiceFactory`),
  the **gRPC proto**, and the **entire App** — all unchanged. They keep operating on the same
  per-transport `DeviceMap` rows and the same `DeviceCatalogEntry` output.
- Device I/O, any UI.
- Supporting the *old* flat shape (hard-migrate — see Options).

## Protocol/behaviour facts this refactor must preserve

From reading the current code:

- **`DeviceInfoProvider`** maps `AllowedDevice[]` → `List<DeviceMap>` **1:1** today. It becomes
  **1:N** (one grouped device → one row per transport). `DevicesMaps` must contain the *same rows*
  as before.
- **Consumers match `DeviceMap` by (VendorId, ProductId, HasBluetooth).** e.g.
  `DeviceRgbServiceFactory` (`m => !m.HasBluetooth && m.VendorId==… && m.ProductId==…`). So the rows
  must keep exact VID/PID/HasBluetooth.
- **The dongle and Bluetooth transports share `ProductId=33651`** and are disambiguated *only* by
  `HasBluetooth` (`BuildPidVidKey → …:usb` vs `…:bt`). The wired transport is `ProductId=64257`.
  Deriving `HasBluetooth = (ConnectionType == "Bluetooth")` reproduces the three existing rows
  exactly — this is the single most important correctness point.
- **`HidDeviceMapRegistry`** groups rows by `Name` for `MergeTransports`, prefers the `"Wired"`
  `ConnectionType` row to pin `SettingsProductId`, and merges same-name rows into one card. TAIPAN's
  three rows (same `Name`, `MergeTransports=true`) must remain so the merge keeps producing one card.
- **`MergeTransports` distinguishes "one device, many transports" (TAIPAN → merge) from "two
  identical units" (two BLAZE → two cards via `DeviceKey`).** BLAZE has one transport, so its rows
  never merge regardless.

## Options considered

### A. Expand grouped config into the existing `DeviceMap` list — *recommended*
Add the grouped shape + a pure expander; `DeviceInfoProvider` calls it and still exposes
`List<DeviceMap>`. `DeviceMap`, the registry, the proto and the App are untouched.

- **Pro:** minimal blast radius; every downstream consumer keeps working unchanged; the expander is
  a pure function, unit-testable in the existing `Infrastructure.Tests` project.
- **Con:** the grouped ↔ flat duality lives in one place (the expander) — acceptable, it is the
  point of the change.

### B. Refactor `DeviceMap` itself into a grouped structure
Make `DevicesMaps` return grouped devices and update every consumer to iterate transports.

- **Pro:** no flat/grouped duality.
- **Con:** touches `DeviceRgbServiceFactory`, `HidDeviceDiscoveryService`,
  `DeviceKeyMappingServiceFactory`, `HidDeviceMapRegistry` and their matching logic — large blast
  radius, high regression risk, for no user-visible gain. Rejected.

### C. Dual-support (accept both flat and grouped shapes)
Bind whichever is present.

- **Pro:** zero-risk migration.
- **Con:** two config codepaths to maintain forever; there is exactly one config file in the repo,
  so the safety is illusory. Rejected in favour of a clean hard-migrate.

## Recommended approach

**Option A**, with these decisions:

1. **Config types live in `ProoveGaming.Infrastructure/Classes`** (so the expander and its tests can
   live in Infrastructure without a new DeviceHost test project — DeviceHost has none, and adding one
   is a `.slnx` change we avoid). `AllowedDevice.cs` already imports `Infrastructure.Classes`.
   - `DeviceGroupConfig` — the device-level defaults (Name, DeviceName, CardType, ConnectionType?,
     ImagePath, RgbImagePath, DeviceType?, ProductName?, VendorId?, ProductId?, SupportsRgb,
     SupportsButtonRemap, SupportsMacros, SupportedMouseSettings, DpiStages, ImageScale, Hotspots,
     BatteryCommand?/ReportId?/DirectScale?) plus **`bool MergeTransports`** and
     **`List<DeviceTransportConfig> Transports`**.
   - `DeviceTransportConfig` — `ConnectionType` (`"Wired"|"Dongle2_4G"|"Bluetooth"`, required) plus
     the **optional overrides** that actually vary per transport in the real config: `VendorId?`,
     `ProductId?`, `ProductName?`, `DeviceType?`, `SupportsRgb?` (nullable bool),
     `BatteryCommand?`/`BatteryReportId?`/`BatteryDirectScale?`. Any null field falls back to the
     device-level value.

2. **`AllowedDevices` (DeviceHost) becomes `{ List<DeviceGroupConfig> Devices }`.** The
   `Configure<AllowedDevices>(GetSection("AllowedDevices"))` binding in `Program.cs` is unchanged;
   only the section's inner shape changes. The old `AllowedDevice` / `AllowedDevicesList` types are
   **removed** (hard-migrate).

3. **`DeviceMapExpander.Expand(IEnumerable<DeviceGroupConfig>) → List<DeviceMap>`** (pure static, in
   Infrastructure). For each device, for each transport, emit a `DeviceMap` with:
   - each field resolved as `transport.Field ?? device.Field` (VendorId, ProductId, ProductName,
     DeviceType, SupportsRgb, battery fields); device-only fields (Hotspots, DpiStages, ImageScale,
     SupportedMouseSettings, RgbImagePath, ImagePath, CardType, DeviceName, SupportsButtonRemap,
     SupportsMacros) copied down; lists copied **by value** so rows don't alias,
   - `ConnectionType` from the transport (falling back to the device-level `ConnectionType` only if
     the transport omits it — in practice every transport sets it),
   - `HasBluetooth = string.Equals(ConnectionType, "Bluetooth", OrdinalIgnoreCase)` — **derived**,
   - `MergeTransports = device.MergeTransports` — **explicit**, stamped on every row,
   - `Name` upper-cased exactly as `DeviceInfoProvider` does today.

4. **`DeviceInfoProvider`** calls `DeviceMapExpander.Expand(config.Devices)` instead of the 1:1
   `Select`. `DevicesMaps` and the two `GetClientName` lookups are otherwise unchanged.

5. **`appsettings.json`** rewritten so every device is one entry with a `Transports` list:
   - **TAIPAN** (`MergeTransports: true`): transports `Wired/-/64257`, `Dongle2_4G/-/33651`,
     `Bluetooth/-/33651` (VendorId 9354 at device level); all rich settings once.
   - **T-Racer** (`MergeTransports: true`): `Dongle2_4G/12290`, `Wired/12370` (+`ProductName`,
     `SupportsRgb`), `Bluetooth/12370`.
   - **Rampage** (no `MergeTransports`): `Wired` (+`BatteryCommand`, `DeviceType "Wired Keyboard"`)
     and `Bluetooth` (`DeviceType "Wireless Keyboard"`), same VID/PID.
   - **BLAZE, Aress, T800, Cyrex, Raster**: one transport each.

Nothing else changes: the expander yields the same `DeviceMap` set the flat config did, so the
registry, proto and App are byte-for-byte equivalent downstream.

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/DeviceGroupConfig.cs` *(new)* — grouped device config
  (shared fields + `Transports`) and `DeviceTransportConfig`.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMapExpander.cs` *(new)* — pure `Expand(groups) →
  List<DeviceMap>`; derives `HasBluetooth`, implies `MergeTransports`, copies shared fields by value.
- `src/ProoveGaming.DeviceHost/AllowedDevice.cs` *(edited)* — `AllowedDevices` now holds
  `List<DeviceGroupConfig> Devices`; delete the old `AllowedDevice` flat type.
- `src/ProoveGaming.DeviceHost/Services/DeviceInfoProvider.cs` *(edited)* — build `_deviceMaps` via
  `DeviceMapExpander.Expand(configurationSettings.Value.Devices)`.
- `src/ProoveGaming.DeviceHost/appsettings.json` *(edited)* — `AllowedDevices` rewritten to the
  grouped shape; shared settings de-duplicated.
- `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs` *(new)* — characterization +
  new-behaviour tests (see Test strategy).
- `docs/CONTEXT.md` *(edited)* — add **Transport (device config)** term; update the AllowedDevices
  description to the grouped shape.

**No proto change. No App change. No `.slnx`/dependency change.**

## Acceptance criteria

1. Given `appsettings.json`, every physical product is **one** device entry with a `Transports`
   list; no field that is genuinely shared (e.g. TAIPAN's Hotspots/DpiStages/ImageScale) appears more
   than once within a device.
2. Given the grouped config → `DeviceInfoProvider.DevicesMaps` yields **exactly** the pre-refactor
   set of rows (same count and same per-row VendorId/ProductId/HasBluetooth/ConnectionType/Name/
   DeviceType/SupportsRgb/ProductName/battery/MergeTransports). Specifically the TAIPAN rows
   (`64257/Wired/bt=false`, `33651/Dongle2_4G/bt=false`, `33651/Bluetooth/bt=true`), Rampage's two
   rows with per-transport `DeviceType` and wired-only `BatteryCommand`, and T-Racer's three rows
   with wired-only `ProductName`/`SupportsRgb`.
3. Given a Bluetooth transport → its row has `HasBluetooth=true`; every non-Bluetooth transport row
   has `HasBluetooth=false` (derived, not declared).
4. Given a device with `MergeTransports: true` (TAIPAN, T-Racer) → all its rows carry
   `MergeTransports=true`; a device without it (Rampage, mice/keyboards) → `false`, preserving
   Rampage's current non-merged behaviour.
5. Given a transport override (Rampage wired `BatteryCommand`, T-Racer wired `ProductName`/
   `SupportsRgb`, per-transport `DeviceType`) → the row uses the override; where a transport omits a
   field it falls back to the device default.
6. Given the expander copies list fields → each row owns its own `Hotspots`/`DpiStages` instances
   (mutating one row's list does not affect another).
7. Given the App running: TAIPAN shows **one** card across cable/dongle/BT; two BLAZE units show
   **two** cards; Rampage shows one card — all unchanged from today.
8. Given a QA edit of one TAIPAN hotspot coordinate in the single device block → all transports
   reflect it (one place to edit).
9. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` green, incl. the
   new expander tests.

## Test strategy

`refactor-*` — the touched path (**`DeviceInfoProvider` config→`DeviceMap` expansion**) has **no
existing tests** (confirmed: only `HidDeviceDiscoveryTests` exists, and it mocks `IDeviceInfoProvider`
rather than exercising the mapping). So characterization tests land **with** the expander and lock in
the equivalence.

New `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs`:

- **Golden equivalence (characterization):** a grouped config mirroring the real one expands to the
  known set of `DeviceMap` rows with the exact per-row VendorId/ProductId/HasBluetooth/ConnectionType/
  Name/DeviceType/SupportsRgb/ProductName/BatteryCommand/MergeTransports the flat config produced.
  This is the safety net guaranteeing the registry/proto/App see identical input. Includes the
  multi-transport cases (TAIPAN, T-Racer, Rampage).
- `HasBluetooth` derivation: `Bluetooth` → true; `Wired`/`Dongle2_4G` → false; case-insensitive.
- `MergeTransports` explicit: device flag stamped on every row; a two-transport device *without* the
  flag (Rampage) yields `MergeTransports=false` on both rows.
- Per-transport override vs fallback: a transport overriding `DeviceType`/`SupportsRgb`/
  `BatteryCommand`/`ProductName`/`ProductId` uses the override; a transport omitting it falls back to
  the device default.
- Shared-field copy: Hotspots/DpiStages/SupportedMouseSettings present and equal on every row of a
  device; list instances are **not shared** between rows (criterion 6).
- Empty/edge: a device with zero transports yields zero rows (and does not throw).

**Deliberately not automated:** `HidDeviceMapRegistry` and `DeviceInfoProvider` themselves live in
`ProoveGaming.DeviceHost`, which has **no test project** (adding one is a `.slnx` change we avoid).
They are unchanged by this refactor and consume the expander's output; the golden-equivalence test is
what protects them. The end-to-end config→catalog→App path stays a QA concern.

## QA (manual test steps)

1. Build and start the DeviceHost, then the App.
2. Connect the TAIPAN **by cable**. Expected: one TAIPAN card appears. Open it → the 5 button
   hotspots (markers + lines) sit correctly on the image; DPI stages and lift-off are present.
3. Switch the TAIPAN to the **2.4G dongle**, then to **Bluetooth**. Expected: still exactly **one**
   TAIPAN card each time (not a new card per transport); its settings look the same.
4. Connect a **BLAZE**. Expected: a separate BLAZE card with its own hotspots. If you have two BLAZE
   units, connect both. Expected: **two** BLAZE cards.
5. In `appsettings.json`, change one TAIPAN hotspot coordinate (e.g. `side-back` `ButtonY`) in the
   single TAIPAN device block, restart the DeviceHost and the App, reopen the TAIPAN. Expected: that
   marker moved — and you only edited it in **one** place.
6. Regression: every device still shows the right image, name, DPI section, RGB (BLAZE), macros and
   button-remap exactly as before.

## Review focus

`gRPC contract` (confirm the `DeviceCatalogEntry` output is unchanged — the App must see identical
catalog entries), `null-safety` (transport lists, optional `ProductName`, derived fields, a device
with no transports), `DI lifetimes` (`DeviceInfoProvider` stays the same singleton; the expander is
pure/stateless).

## Performance & quality

No hot paths — this runs once at DeviceHost startup while binding config. The expander allocates one
`DeviceMap` per transport (a handful total) and copies small lists by value to avoid aliasing between
rows. No threading concerns (startup, single-threaded). `DeviceInfoProvider` remains a singleton
holding immutable-after-construction state.

## Risks

- **Row equivalence is load-bearing.** If the expander produces even slightly different
  VID/PID/HasBluetooth rows, `DeviceRgbServiceFactory` / `HidDeviceDiscoveryService` /
  `HidDeviceMapRegistry` matching silently breaks (wrong or missing device services). Mitigated by
  the golden-equivalence test (criterion 2).
- **Dongle vs Bluetooth share PID 33651.** The only thing separating them is `HasBluetooth`, now
  derived from `ConnectionType`. A wrong `ConnectionType` string in config (typo) would collapse them
  — validated by the derivation test and QA step 3.
- **`MergeTransports` stays explicit** (not implied). Rampage — two transports, deliberately not
  merged today — proves implication would silently change behaviour. Every device's flag is
  transcribed from the current config verbatim; the golden test (criterion 4) locks this.
- **Per-transport override fallback must match the flat rows exactly.** A field that varies (Rampage
  `BatteryCommand` wired-only, T-Racer `ProductName`/`SupportsRgb` wired-only, per-transport
  `DeviceType`) must resolve to the same value the flat entry had. Getting a fallback wrong silently
  changes a keyboard's card/RGB/battery. Mitigated by the golden test asserting these exact fields.
- **Hard-migrate:** an out-of-tree/older `appsettings.json` still using the flat shape will bind to
  an empty `Devices` list (no devices recognised) rather than erroring. Acceptable because the repo
  has one config; call it out in the PR.
- **Installer/config propagation:** the DeviceHost must be **restarted** to pick up config, and the
  installer embeds `appsettings.json` — the tracked source is
  `src/ProoveGaming.DeviceHost/appsettings.json`; the `src/artifacts/…/appsettings.json` copies are
  gitignored build outputs that regenerate, so they need no manual edit.

## Open questions

_Both resolved (approach A, confirmed by the user):_

1. **`MergeTransports`** — kept as an **explicit** optional device flag, not implied. Rampage (two
   transports, unmerged today) makes implication unsafe.
2. **Hard-migrate** — yes, drop the flat shape entirely; the repo has a single config file.

## Review findings

_filled in by /reviewer_

### Round 1 — Ship: yes (with should-fixes)

Reviewed the full branch delta: 6 commits (main..HEAD, 35 files) + uncommitted config-plumbing
(12 modified, 5 new). Build clean, 314 tests pass (272 Infrastructure incl. 10 new expander tests,
42 App). The transport refactor matches this plan; equivalence to the flat rows is validated by the
golden tests and a field-by-field round-trip. The provenance fix's earlier Round 1/2 items are
closed on this branch (`_liveDpiStage` is now `volatile int` + `UnknownStage`; `ResolveStageForWrite`
/ `ResolveSourceBlock` extracted and tested).

Scope correction to the review request: the heavy installer logic (app-kill, cancel rollback, 1073,
pending-reboot deletions) is **already on main** — this branch's only installer delta is
`MainWindow.Modal.cs` (an empty-accessor warning fix) plus the dependency bump. So a dedicated
`/security-review` is low-value for this branch; the one installer-adjacent risk is the Avalonia bump.

#### Code
- [ ] Dependency bump untested beyond build — `*.csproj` (Avalonia 12.0.4→12.1.0, Svg.Skia
  5.0.0→5.1.1, committed) — Should-fix. Spans every Avalonia project incl. the installer
  (release-process), and the entire backdrop-blur/layout series was built against it. A minor
  Avalonia bump can shift rendering; needs a hardware smoke test and a PR call-out (repo constraint:
  flag dependency changes).
- [ ] `DeviceMapExpanderTests.cs` is still untracked (`??`) — process — `git add` it before commit so
  the safety-net tests actually ship.
- [ ] Nit — `BackdropBlurControl._wallpaper` static `Bitmap` is never disposed. Intentional
  process-lifetime cache (like an asset); fine, worth a one-line note only.
- [ ] Nit — `PascalizeHotspotId` (DeviceDetailsViewModel.cs:623) yields a degenerate resource key for
  an empty/all-dash id. Config ids are controlled (the 5 standard ids), so not a real risk.
- [ ] Nit — `BackdropBlurControl.Render` can compute a `sourceRect` partly outside the bitmap during
  layout transitions (negative/oversized origin); `DrawImage` tolerates it and it's transient.

#### Tests
- [ ] No test binds the real `appsettings.json` — Should-fix. The expander tests use hand-built
  configs mirroring the catalog, but nothing loads the actual config and asserts the expanded rows,
  so a future config typo (e.g. a wrong `ConnectionType` collapsing the dongle/BT PID-33651 split)
  wouldn't be caught. A bind-and-expand test over the shipped `appsettings.json` closes the plan's
  own top risk.
- [ ] App-side config plumbing untested — Should-fix. `PascalizeHotspotId` (pure), the `ImageScale`
  `0 → 1.0` mapping (HomeViewModel.cs:523), and the hotspot builder have no `App.Tests` coverage,
  though the branch adds a whole config→App path. At least the two pure pieces deserve unit tests.
- [x] Expander behaviour covered — `DeviceMapExpanderTests` (TAIPAN shared-PID split, Rampage
  non-merge + overrides, T-Racer wired-only overrides, HasBluetooth derivation, list-clone isolation,
  empty/null) green.
- [x] Provenance encoder coverage — `TaipanStageProvenanceTests` green.
