# Plan: Bluetooth as a second connection for the PG Starlight headset (PG-608)

## Goal

Let Starlight be used over classic Bluetooth as well as its 2.4G dongle, on a single device card
that stays live across a dongle↔Bluetooth switch — the same product behaviour Cosmo gained in
PG-564.

Getting there first requires fixing a **live defect** that PG-564 introduced and that only a second
Bluetooth headset could expose: Starlight and Cosmo report the **same Bluetooth VID/PID**, so the
catalog match cannot tell them apart. Today a Starlight connected over Bluetooth is displayed as a
**Cosmo card**. That bug ships in `main` right now, independently of this feature.

## Scope

**In**

- Disambiguating classic-Bluetooth catalog matching so two headsets sharing a VID/PID resolve to
  their own catalog rows.
- A `Bluetooth` transport for Starlight, with battery as a per-transport override.
- `MergeTransports` for Starlight so dongle and Bluetooth are one card.

**Out**

- RGB or any device configuration over Bluetooth. Starlight's LED lives behind its vendor HID
  interface, which classic Bluetooth does not expose — see *Risks*.
- BLE/GATT. Starlight is a classic A2DP/HFP audio device, exactly like Cosmo; the App's BLE path
  cannot see it and is not involved.
- Any change to Cosmo's behaviour beyond the shared matching fix.

## Current state (verified on hardware 2026-08-19 — do not re-derive)

Measured with `dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight bt`
(the Cosmo device-tree discovery probe, parameterised by endpoint name).

| | Starlight | Cosmo |
|---|---|---|
| BT VID/PID | **0x05D6 / 0x000A** | **0x05D6 / 0x000A** |
| BT address | `19D5E987093A` | `9200025FD240` |
| Container ID | `df5cd15f-ae1d-5ede-a87c-7fd07bed7578` | `9ca05de4-f4be-5821-a6d6-5ddd91e2c9c7` |
| Node name | `PG Starlight` | `PG Cosmo` |
| Battery (HFP node) | **70 %** on `PG Starlight Hands-Free AG` | 70 % on `PG Cosmo Hands-Free AG` |
| `IsConnected` | true on the `PG Starlight` node | false (not connected during the run) |

Established facts:

1. **The Bluetooth VID/PID is an ODM constant, not an identity.** `0x05D6 / 0x000A` is byte-identical
   across both headsets. This is the *second* such collision on this product line — CONTEXT.md
   already records that Cosmo and Starlight ship a byte-identical USB `iSerialNumber`
   (`00006F64096B22E7`). Treat any vendor-supplied constant on these headsets as non-identifying
   until proven per-unit.
2. **`ClassicBluetoothDeviceResolver.MatchCatalog` matches on VID/PID alone** and returns the first
   catalog row that matches (`ClassicBluetoothDevice.cs:143-160`). Cosmo declares
   `VendorId 1494 / ProductId 10`; Starlight declares no Bluetooth transport at all. So a connected
   Starlight matches **Cosmo's** row.
3. **Container ID and node name DO discriminate**, and both are already captured by
   `DeviceNodeQuery` / `ClassicBluetoothDevice`. Container ID is per-unit (useless as a catalog key);
   the friendly name is per-model and stable (`PG Starlight` / `PG Cosmo`).
4. **Battery is readable over Bluetooth** as `DEVPKEY_Bluetooth_Battery` on the Hands-Free AG node —
   the same mechanism Cosmo uses. Starlight's catalog entry currently declares
   `SupportsBattery: false`, which is correct for its **dongle** and wrong for Bluetooth.
5. Starlight already declares `SupportedAudioSettings: [OutputVolume, MicrophoneVolume]` and
   `AudioVolumeTarget: OperatingSystem`, so the `AudioContainerId` plumbing added for Cosmo in
   `FillBluetoothCard` applies to it unchanged, with no new code.

## Options considered

### A. Add an optional per-transport name token to the catalog match — *recommended*

A `BluetoothName` string on the Bluetooth transport; a row matches only when the resolved device
name contains it (case-insensitive). VID/PID still has to match.

- **Pro** — uses the one signal that genuinely discriminates, stays declarative in `appsettings.json`,
  and is a strict *narrowing*, so a row that declares no token keeps today's behaviour.
- **Pro** — DeviceHost-side only. Matching happens in `ClassicBluetoothDeviceResolver`, so no
  `.proto` change and no lockstep App/Host deploy.
- **Con** — a name is a display string; a firmware revision that renames the device breaks the match.
  Mitigated by matching a substring (`starlight`), not the full name, and by falling back to the
  VID/PID-only behaviour when no token is declared.

### B. Match on Container ID

Rejected. Container ID is *per unit*, not per model — it would have to be discovered and written into
the catalog for every headset every user owns.

### C. Match on the audio endpoint name instead

Rejected as the primary key: it needs a Core Audio round-trip inside what is currently a pure device
tree query, and the endpoint name is the same string as the node name anyway — the same fragility
with more moving parts.

### D. Ask the vendor for distinct Bluetooth PIDs

Worth doing regardless, but it cannot be the plan: it does not fix units already in the field, and
we have no ship date for a firmware change.

## Recommended approach

**Option A**, plus the catalog rows for Starlight.

Matching rule, in `ClassicBluetoothDeviceResolver.MatchCatalog`:

1. Consider only catalog rows with `HasBluetooth` and a matching VID/PID (unchanged).
2. If the row declares `BluetoothName`, it matches only when the device's resolved name contains that
   token, case-insensitively.
3. **A row with a matching name token wins over a row with no token.** Without this the result would
   depend on catalog order — the exact fragility being fixed.
4. If nothing matches by name and some row declares no token, that row matches (today's behaviour, so
   TAIPAN / Rampage / T-Racer are untouched).

Both headsets get a token, because giving one to Starlight alone would leave Cosmo's untokened row
still matching a Starlight.

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/DeviceGroupConfig.cs` — add `BluetoothName` to the
  transport config record so it binds from `appsettings.json`.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` — add `BluetoothName` to the flattened
  per-transport row.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMapExpander.cs` — carry `BluetoothName` through the
  device × transports flattening.
- `src/ProoveGaming.Infrastructure/Classes/ClassicBluetoothDevice.cs` — implement the four-step match
  above in `MatchCatalog`, replacing first-match-wins.
- `src/ProoveGaming.DeviceHost/appsettings.json` —
  - Cosmo's Bluetooth transport gains `"BluetoothName": "cosmo"`.
  - Starlight gains `"MergeTransports": true` and a Bluetooth transport:
    `{"ConnectionType": "Bluetooth", "VendorId": "1494", "ProductId": "10", "BluetoothName": "starlight", "SupportsBattery": true, "SupportsConfiguration": false}`.
    *(Note 2026-09-02: `"SupportsConfiguration": false` was removed from this row — headset
    presets/audio work over Bluetooth because they target the OS endpoint, not the vendor HID
    channel; see `docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md`.)*
- `tests/ProoveGaming.Infrastructure.Tests/ClassicBluetoothDeviceResolverTests.cs` — extend with the
  collision cases (see Test strategy).
- `docs/CONTEXT.md` — define **BluetoothName**; extend the existing ODM-constant note to cover the
  shared Bluetooth VID/PID.

No `.proto` change, no new NuGet dependency, no `.slnx` change.

## Acceptance criteria

- Given a Starlight and a Cosmo both paired, and **Starlight** connected over Bluetooth → the App
  shows a **Starlight** card, not a Cosmo card. *(This fails on `main` today.)*
- Given the same, with **Cosmo** connected → a Cosmo card, unchanged from PG-564.
- Given both connected at once → two cards, each with its own name, battery and audio identity.
- Given Starlight connected over Bluetooth → its card is **active**, shows a battery percentage, and
  the Output/Microphone Volume sliders move the `PG Starlight` Windows endpoints (not Cosmo's).
- Given Starlight switched from Bluetooth to its 2.4G dongle → the **same** card stays active and its
  saved settings persist across the switch (`MergeTransports`).
- Given Starlight disconnected from Bluetooth → the card goes inactive and its battery indicator
  clears, rather than holding a stale 70 %.
- Given a catalog row that declares **no** `BluetoothName` (TAIPAN, Rampage, T-Racer) → matching is
  byte-for-byte what it is today.
- ~~Given Starlight over Bluetooth → Save Preset is disabled and no configuration write is
  attempted (`SupportsConfiguration: false`).~~ *Superseded 2026-09-02: the flag was removed from
  the row — Save Preset now works over Bluetooth (audio levels apply to the OS endpoint); see
  `docs/plans/remove-headset-bluetooth-lock/`.*

## Test strategy

Behaviour change to existing code, and the touched code **already has tests**
(`ClassicBluetoothDeviceResolverTests`, 24 cases built on real captured instance IDs) — so those stay
green and are extended. All in `tests/ProoveGaming.Infrastructure.Tests`:

- **The collision, both directions.** Two catalog rows sharing `0x05D6/0x000A`, one tokened
  `cosmo`, one tokened `starlight`; a device named `PG Starlight` resolves to the Starlight row and a
  device named `PG Cosmo` to the Cosmo row. This is the regression test for the shipped bug and must
  be written first, against the *current* resolver, where it fails.
- **Catalog order does not decide.** The same assertion with the two rows in the opposite order.
- **Untokened rows are unaffected.** A row with no `BluetoothName` still matches on VID/PID alone.
- **Tokened row beats untokened row** when both match VID/PID.
- **No match on a foreign name** — VID/PID matches but the name matches no token and no untokened row
  exists → `null` rather than an arbitrary row.
- **Case and substring** — `PG Starlight Hands-Free AG` matches the token `starlight`.

Deliberately **not** covered automatically: the Core Audio endpoint routing and the real Windows
Bluetooth stack (QA below). Note CI builds only the App csproj and does not run `tests/`, so this
coverage is local-only.

## QA (manual test steps)

1. Pair both a PG Cosmo and a PG Starlight with the PC. Switch both headsets off. Launch the app.
2. Switch the **Starlight** on and connect it over Bluetooth. **Expected:** a card named
   **Starlight** appears and becomes active, showing a battery percentage. It must NOT say Cosmo.
3. Open the Starlight card → Audio. Move Output Volume. **Expected:** the Windows volume for
   "PG Starlight" changes, and the Cosmo endpoints are untouched.
4. Move Microphone Volume. **Expected:** the "PG Starlight" microphone level changes.
5. Switch the **Cosmo** on over Bluetooth as well. **Expected:** a second, separate card appears named
   Cosmo, with its own battery. Two cards, correctly named.
6. Switch the Starlight from Bluetooth to its 2.4G dongle. **Expected:** the same Starlight card stays
   active — no second card appears, and the volume levels you set are still there.
7. Switch the Starlight off entirely. **Expected:** within a few seconds its card goes inactive and
   the battery indicator clears (no stale percentage).
8. Open the Starlight card over Bluetooth and look at Presets. **Expected:** Save Preset is disabled,
   and no error appears.
9. Connect a PG TAIPAN over Bluetooth. **Expected:** unchanged from before this work — it still
   appears correctly.

## Review focus

`thread-safety` (the classic-Bluetooth poll writes shared discovery state), `null-safety` (a device
node with no name must not throw or match everything), `performance hot-path` (the match runs inside
the 3 s classic poll, per device, per tick).

## Performance & quality

The match runs on every classic-Bluetooth poll tick (3 s) for every enumerated device. It is a
string `Contains` over a handful of catalog rows — negligible — but it must not allocate per tick:
use `StringComparison.OrdinalIgnoreCase` on the existing strings, no `ToLower()` copies.

`MatchCatalog` stays a **pure static function** over `(maps, device)`, which is what makes the
collision testable without a device tree. Do not let it reach for Core Audio or the registry.

## Risks

- **Name-based matching is fragile to a firmware rename.** Accepted, and mitigated by substring
  matching plus the untokened fallback. Flagged to the vendor as part of option D.
- **RGB over Bluetooth is out of scope and may be assumed present.** Starlight declares
  `SupportsRgb: false` today, so nothing regresses — but if RGB is ever enabled for its dongle, the
  Bluetooth transport must keep it off, since the vendor HID interface is not exposed over classic
  Bluetooth. `SupportsConfiguration: false` on the transport is the guard. *(Note 2026-09-02: that
  guard was removed with the flag — if dongle RGB ever ships, the Bluetooth row needs an explicit
  `"SupportsRgb": false` override instead; see `docs/plans/remove-headset-bluetooth-lock/`.)*
- **Battery is per-transport.** Starlight's device-level `SupportsBattery: false` is right for the
  dongle; only the Bluetooth transport overrides it to true. Setting it device-wide would put a
  battery indicator on the dongle connection that can never be filled.
- **Two headsets connected at once is now a real state**, and the audio identity is what keeps their
  sliders apart. `AudioContainerId` already carries this per card, but it has never been exercised
  with two live BT headsets — hence QA step 5.

## Open questions

1. Should we ask the vendor for distinct Bluetooth PIDs (option D) in the same message as the
   outstanding TAIPAN dongle question, or separately?
2. Does Starlight expose a mic-mute state over Bluetooth? Cosmo does not over any transport; not
   required for this plan, but it would change the Audio section if it does.

## Review findings

### Round 1 — Ship: no

#### Plan conformance

Plan: this document. Reviewed surface: the whole working tree (no commits on the branch — `git log
main..HEAD` is empty, so every change below is uncommitted).

- **Implemented as planned:** the `BluetoothName` token through `DeviceGroupConfig` → `DeviceMap` →
  `DeviceMapExpander`; the four-step match in `MatchCatalog`; both catalog rows; the regression tests.
  Option A was followed exactly, and the rename trade-off was documented in the code rather than
  quietly dropped.
- **Verified genuinely regressive:** reverting only `ClassicBluetoothDevice.cs` fails 5 of 33 resolver
  tests, so the new tests do catch the shipped bug rather than merely passing alongside it.
- **Missing:** the plan assumed `MatchCatalog` was the only thing standing between a classic-Bluetooth
  device and its capabilities. It is not — see **B1**. Two acceptance criteria do not hold.
- **Out of scope in the same tree:** the TAIPAN response probe, its evidence document and the
  `Program.cs` dispatch belong to the PG-608-unrelated vendor investigation. Not a defect; they should
  land as their own commit.

#### Code

- [x] **B1. Starlight over Bluetooth is served the DONGLE catalog row** —
  `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs:55-56, 85-93` — Blocker

  **Problem:** the registry de-duplicates rows into `_mapsByPidVid` on the key
  `"{vid}:{pid}:{bt|usb}"`, first-wins. Cosmo's and Starlight's Bluetooth rows now share the key
  `1494:10:bt`, so **Starlight's Bluetooth row is dropped at construction**. `FindBluetoothCatalogEntry`
  then searches `_mapsByPidVid.Values` for a Bluetooth row named "Starlight", does not find one, and
  falls back to the name lookup in `_maps` — which holds the **dongle** row (both Starlight rows have a
  null `ProductName`, so they collide on key `STARLIGHT`, and the Bluetooth row does not displace a
  non-Bluetooth incumbent).

  `MatchCatalog` is therefore doing its job — the device is correctly identified as Starlight and
  `ClientName` is right, which is why the card shows the correct name and the change *looks* like it
  works — but the **capabilities** the App receives come from the wrong transport.

  **Why it blocks:** simulated against the real catalog, `FindBluetoothCatalogEntry("Starlight")`
  yields `BatteryUnsupported=True` and `ConfigurationUnsupported=False`. So over Bluetooth:
  - the battery indicator is **absent**, though Windows reports 70 % on the Hands-Free AG node;
  - **Save Preset is enabled** on a link that cannot carry configuration.

  That is precisely the failure the comment above `FindBluetoothCatalogEntry` was written to fix for
  TAIPAN, reintroduced by a different route. It also makes the `SupportsBattery: true` /
  `SupportsConfiguration: false` overrides on the new Bluetooth row **dead configuration** — written,
  never read. Two acceptance criteria fail: *"its card is active, shows a battery percentage"* and
  *"Save Preset is disabled"*.

  **Solution options:**
  - *Option A (correct):* stop routing a name lookup through an id-keyed dictionary.
    `FindBluetoothCatalogEntry` should select from **all** maps (`_deviceInfoProvider.DevicesMaps`, or
    a `_bluetoothMapsByName` dictionary built alongside the others) on `HasBluetooth && Name ==
    clientName`. The id de-duplication is meaningful for `FindCatalogEntryByPidVid`; it is simply the
    wrong index for a by-name question.
  - *Option B (narrower):* include `Name` in `BuildPidVidKey` so the two Bluetooth rows stop colliding.
    Fixes this symptom, but leaves a by-name lookup reading an id-keyed index, so the next product to
    share an id pair re-opens it.
  - *Rejected:* inventing a distinct Bluetooth PID for Starlight in the catalog. The shared pair is a
    measured hardware fact; the catalog must not claim otherwise.

- [x] **S1. A merged product with no wired transport pins its settings bucket by catalog ORDER** —
  `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs:31-32` — Should-fix

  **Problem / why it matters:** the pin is `first row with ConnectionType "Wired"` ?? `product.First()`.
  Starlight has no wired transport, so it falls to `product.First()` — today the dongle row, giving the
  correct `877`. But that is only because the dongle transport happens to be listed first in JSON.
  Moving the Bluetooth transport above it would pin Starlight's settings to product id **10** — the
  same id Cosmo would pin to under the same edit, so the two headsets would then **share one settings
  bucket**, and every existing Starlight preset would be orphaned under the old id. A JSON reordering
  is not something a reviewer would flag as behavioural today.

  **Fix:** prefer a non-Bluetooth row explicitly — `Wired` ?? first non-Bluetooth ?? first — so the
  pinned id can never come from a transport whose ids are an ODM constant shared across products.

- [x] **S2. The layer the bug lives in has no test at all** — `tests/` — Should-fix

  **Problem / why it matters:** `ClassicBluetoothDeviceResolverTests` and `StarlightCatalogTests` both
  gained good coverage, and both pass while the feature is broken, because no test exercises
  `HidDeviceMapRegistry` — no test project references it. The catalog→registry→gRPC seam is where a
  shared id pair actually does damage, and it is unobserved.

  **Fix:** add coverage for `FindBluetoothCatalogEntry` over the real catalog, asserting that Cosmo and
  Starlight each receive **their own** Bluetooth row (`BatteryUnsupported` / `ConfigurationUnsupported`
  per transport). This is the test that would have caught B1 and must fail before the fix.

#### Tests

- [x] `FindBluetoothCatalogEntry` per-transport capability resolution for both headsets — the B1
  regression test — new coverage for `HidDeviceMapRegistry` (needs a test project that can reference
  `ProoveGaming.DeviceHost`, or the registry's construction logic exercised through its public API).
- [x] Settings-bucket pinning for a merged product with no wired transport (S1), asserting the pinned
  id is the dongle's and not order-dependent.

#### Nits

- `CosmoBluetoothDiscoveryProbe` is no longer Cosmo-specific now that it takes a match name; the class
  name misleads at the call site `CosmoBluetoothDiscoveryProbe.Run("starlight")` —
  `src/ProoveGaming.Device.Testing/CosmoBluetoothDiscoveryProbe.cs:33`.
- `Headset(null!, …)` forces a null through a non-nullable parameter when the underlying record already
  declares `string? Name`; the helper should take `string?` —
  `tests/ProoveGaming.Infrastructure.Tests/ClassicBluetoothDeviceResolverTests.cs`.

#### What's done well

- The collision was found by measurement before it was designed around, and the shipped
  Starlight-as-Cosmo bug was surfaced rather than silently absorbed into the feature.
- `MatchCatalog` tracks the untokened candidate instead of returning early, so the result cannot depend
  on catalog order — the actual defect, not just its symptom.
- The stale doc comment that argued *against* name matching was rewritten with the measurement and the
  accepted trade-off, rather than deleted.
- The regression tests were verified to fail against the old resolver (5 of 33), not assumed to.
- An existing test that broke (`StarlightExpandsToOneTransportRow`) was split into per-transport
  assertions rather than loosened.

#### Verdict

Ship: **no**. The identification half is correct and well tested; the capability half is broken by a
dictionary-key collision one layer below the fix, so the feature ships without its battery indicator
and with Save Preset wrongly enabled over Bluetooth. The fix is small and local.

First: **B1** (route the by-name lookup off the id-keyed index), then **S2** (the test that proves it),
then **S1**.

#### Round 1 — resolution (2026-08-19)

All three closed, plus both nits.

- **B1** — `HidDeviceMapRegistry` gained `_bluetoothMapsByName`, and `FindBluetoothCatalogEntry` now
  reads that instead of `_mapsByPidVid`. A by-name question no longer goes through an id-keyed index,
  so the next product to share an id pair cannot reopen this.
- **S1** — the settings pin is now `Wired` ?? first **non-Bluetooth** ?? first, so a product with no
  cable can never pin its bucket to ids that are an ODM constant.
- **S2** — new `tests/ProoveGaming.Infrastructure.Tests/HidDeviceMapRegistryBluetoothTests.cs`
  (9 cases) covering the seam. `ProoveGaming.Infrastructure.Tests` now references
  `ProoveGaming.DeviceHost`, which nothing did before — that absence is why B1 shipped past a green
  suite.
- **Nits** — `CosmoBluetoothDiscoveryProbe` renamed to `ClassicBluetoothDiscoveryProbe` (it is
  parameterised by name and no longer Cosmo-specific); the resolver test helper takes `string?`
  instead of forcing `null!`.

Verified regressive, not merely passing: reverting only `HidDeviceMapRegistry.cs` fails the B1 and S1
tests (2 of 9) and passes the rest. Suite: **731 Infrastructure + 866 App**.

Note for the next round: casing is an implementation detail at this seam — `DeviceMapExpander`
upper-cases `Name` and the registry indexes and looks up through `ToUpper()`. The capability
assertions compare names case-insensitively rather than pinning it.

