# Plan: Add Bluetooth as a second transport for the PG Cosmo headset

## Goal

Cosmo is reachable over Bluetooth as well as its 2.4G dongle. Today the App only knows the dongle,
so a Cosmo connected over Bluetooth appears as nothing at all — while Windows itself shows it,
including a battery level. This plan makes Bluetooth a first-class second transport for the same
headset: one card, the same details view, connection state driven by the Bluetooth stack, and
battery and audio working over the radio.

Extends [add-cosmo-headset](../add-cosmo-headset/add-cosmo-headset-plan.md), which added the device
itself over the dongle. Read its **Hardware facts** and **DECISION: the Cosmo battery ships
KNOWN-UNRELIABLE** sections before touching anything here — this plan resolves that decision.

## Established facts (verified in the codebase, do not re-derive)

> **Two of these were WRONG and were corrected by the Phase 0 probe — see Phase 0 RESULT below.**
> The GATT-battery row and the "discovery comes free" assumption did not survive contact with the
> hardware. They are struck through rather than deleted so the reasoning is not repeated.

| Fact | Evidence | Consequence |
| --- | --- | --- |
| ~~**GATT battery is already generic**~~ | ~~`ReadGattBatteryInternalAsync` reads `0x180F`/`0x2A19` for any paired device~~ | ~~Cosmo's Bluetooth battery costs no new protocol work~~ **FALSE for Cosmo — it is not a GATT/BLE device at all. See Phase 0 RESULT** |
| ~~**The Bluetooth battery is REAL**~~ | ~~Same GATT path Rampage uses~~ | Windows does show a level, but **not** through the path this plan assumed |
| **Audio endpoints resolve by Container ID** | `IAudioEndpointService` takes `string containerId` on every method; `AudioEndpointService.TryResolveEndpointVolume(containerId, dataFlow)` | **This is the real work.** A Bluetooth audio endpoint's container is NOT the USB dongle's container, so the existing resolution cannot work over BT unchanged |
| **Bluetooth cards get identity from the catalog** | `HidDeviceMapRegistry.FindBluetoothCatalogEntry` matches by **name**, preferring a Bluetooth row; `vendor_id` / `product_id` on the entry exist precisely because `BluetoothDeviceResponse` carries no VID/PID | A Bluetooth transport row is what makes the card addressable at all |
| **`0xBC` does not exist over Bluetooth** | It is a USB HID vendor report on `MI_03` of the dongle-attached composite device | Over BT there is no power-state byte 19 and no byte-17 battery. Connection state comes from the BT stack instead, which is strictly better |
| **Cosmo has a stable USB serial** | `iSerialNumber` `00006F64096B22E7` | Its USB container is stable across ports. Its **Bluetooth** identity is `BluetoothDeviceResponse.deviceId`, a different namespace |

## Scope

**In:**
- A Bluetooth transport row on Cosmo's catalog entry so the DeviceHost and App recognise it.
- One merged card across dongle and Bluetooth, with one settings bucket (the TAIPAN pattern).
- Battery over Bluetooth, from the existing GATT path.
- Audio (output + microphone volume, silence indicators) working while connected over Bluetooth.
- Resolving the standing dongle-battery experiment now that a trustworthy reading exists.
- Phase 0 probe to establish how the Bluetooth audio endpoints are identified.

**Out:**
- Any Cosmo vendor protocol over Bluetooth. `0xBC` is USB-only and remains undocumented.
- RGB. Cosmo has none on either transport.
- Simultaneous dongle + Bluetooth as two independent devices — this is one headset, one card.
- Re-binding the physical volume buttons. Windows handles them on both transports.
- Changing how any other Bluetooth device behaves.

## Phase 0 — probe (gate, must run before any code)

The one genuine unknown is **how to obtain the Container ID of Cosmo's Bluetooth audio endpoints**,
without which the Audio section cannot work over BT. Everything else in this plan is mechanical.

Add a read-only probe arm to `ProoveGaming.Device.Testing` that, with Cosmo connected over
Bluetooth, reports:

1. Every render and capture endpoint Windows exposes for it, with **friendly name, endpoint id and
   Container ID** (Core Audio enumeration, the same interfaces `AudioEndpointService` already uses).
2. The `deviceId` the Bluetooth stack reports for the headset (`BluetoothDeviceResponse.deviceId`).
3. Whether the endpoints' Container ID can be reached from that `deviceId` via the CfgMgr32 path the
   repo already has in `DeviceContainerId`.
4. Whether Windows exposes **two** profiles at once — A2DP ("Headphones … Stereo") and Hands-Free
   ("Headset … Hands-Free") — and which one carries the microphone.

**Gate:** if (3) works, the Audio section needs only a different container id and no new abstraction.
If it does not, choose between the options below with real data rather than guessing.

### Phase 0 RESULT (run 2026-08-17, headset connected over Bluetooth)

Probe: `dotnet run --project src/ProoveGaming.Device.Testing -- cosmo-bt` (read-only).

**Finding 1 — audio identity is SOLVED, and it is the good case.** Four endpoints match "Cosmo",
falling into exactly two containers — one per **transport**, not one per direction:

| Container | Covers | State | Endpoints |
| --- | --- | --- | --- |
| `ccd4f06e-aad7-5318-8235-7cef54c645d3` | render + capture | NOTPRESENT | `Headphones (PG Cosmo)`, `Microphone (PG Cosmo)` — the USB/dongle set |
| `9ca05de4-f4be-5821-a6d6-5ddd91e2c9c7` | render + capture | **ACTIVE** | `Headphones (2- PG Cosmo)`, `Headset (2- PG Cosmo)` — the Bluetooth set |

The Bluetooth transport's **render and capture share one Container ID**, so a single identity
addresses both and `IAudioEndpointService` works **unchanged** — it needs that container instead of
the USB one. **Option A/C confirmed; Option B (name matching) is not needed.** The feared A2DP /
Hands-Free split does not appear as two competing render endpoints on this headset.

The two containers being different is the whole point: the card's audio identity **must** follow the
live transport, which is exactly what this plan exists to add.

**Finding 2 — the plan's discovery assumption is WRONG, and this is now the main problem.**
`Bluetooth.GetPairedDevicesAsync()` returns `PG Taipan` and `AJ159 PRO` — **Cosmo is not in the
list**, while its audio endpoints are ACTIVE. Cosmo connects as a **classic Bluetooth audio device**
(A2DP + HFP), not as a BLE/GATT peripheral, so:

- `BluetoothDeviceDiscoveryService` — built on InTheHand's BLE APIs — **will never see it**. Adding a
  Bluetooth transport row to the catalog is therefore *not sufficient*; there is nothing for the row
  to match against.
- The GATT battery path (`0x180F`/`0x2A19`) does not apply. Windows' battery for an HFP headset comes
  from the Hands-Free profile's own battery reporting, surfaced as a Windows **device property**, not
  as a GATT characteristic.

**Consequence:** this plan needs a Phase 0b before any implementation — see Open questions 4 and 5.
The Audio work is unblocked and cheap; **discovery and battery are the real work**, and both are
larger than the original plan assumed.

### Phase 0b RESULT (run 2026-08-17) — both blocking questions answered

Probe: `dotnet run --project src/ProoveGaming.Device.Testing -- cosmo-bt discovery` (read-only).
It inverts the question: instead of asking Bluetooth for the headset, it asks the Windows device
tree what else shares the audio endpoint's container.

**Eleven device nodes share container `9ca05de4-f4be-5821-a6d6-5ddd91e2c9c7`** — 7 classic
`BTHENUM`, **0 `BTHLE`**, which is precisely why the BLE discovery path cannot see this headset.
The ones that matter:

| Node | Instance | Class | Battery |
| --- | --- | --- | --- |
| `PG Cosmo` | `BTHENUM\Dev_9200025FD240\9&8e04a68&0&BluetoothDevice_9200025FD240` | Bluetooth | — |
| **`PG Cosmo Hands-Free AG`** | `BTHENUM\{0000111e-…}_VID&000105d6_PID&000a\…` | System | **80 %** |
| `PG Cosmo Hands-Free` | `BTHHFENUM\BthHFPAudio\a&d4927ef&0&97` | MEDIA | — |
| `Headphones (2- PG Cosmo)` | `SWD\MMDEVAPI\{0.0.0.…}` | AudioEndpoint | — |
| `Headset (2- PG Cosmo)` | `SWD\MMDEVAPI\{0.0.1.…}` | AudioEndpoint | — |

**DISCOVERY — solved.** There is a real, stable device node for the headset
(`BTHENUM\Dev_9200025FD240\…BluetoothDevice_…`). The App can enumerate present device nodes and match
on Container ID using `CM_Get_Device_ID_List` + `CM_Get_DevNode_PropertyW` — the same CfgMgr32 family
`DeviceContainerId` already uses — and treat its presence as "connected over Bluetooth". **No BLE, no
new NuGet dependency, no `Windows.Devices.Enumeration`.** Open question 4 answered: option (b).

**BATTERY — solved, and it is real.** `DEVPKEY_Bluetooth_Battery`
(`{104ea319-6ee2-4701-bd47-8ddbf425bbe5}, 2`) reads **80 %** on the Hands-Free AG node. This is the
value Windows itself displays. It is as cheap to read as the GATT path was assumed to be, and unlike
the dongle's byte 17 it is a genuine level. Open question 5 answered.

**Everything hangs off one identity.** Presence, battery and audio all resolve from the same
Container ID, which is exactly the identity `IAudioEndpointService` already speaks. The card needs to
carry that container while the Bluetooth transport is live, and all three capabilities follow.

**Also captured for the catalog:** the headset's Bluetooth address is `9200025FD240`, and its
Bluetooth VID/PID is `VID&000105d6_PID&000a` (`0x05D6` / `0x000A`) — **different from its USB
identity** (`0x0D8C` / `0x0334`). Matching the Bluetooth transport by BT VID/PID is more robust than
matching by the name `PG Cosmo`, which is user-renameable in Windows.

## Options considered

**How the Audio section addresses the endpoints over Bluetooth:**

| Option | How | Trade-off |
| --- | --- | --- |
| **A. Container ID from the Bluetooth device** *(preferred, pending Phase 0)* | Resolve the BT device's container via CfgMgr32 and hand it to the unchanged `IAudioEndpointService` | No change to the audio layer at all — the interface already speaks Container ID. Depends entirely on Phase 0 (3) |
| **B. Match endpoints by friendly name** | Find endpoints whose name contains the device name | Works without any device-tree plumbing, but fragile: names are localised, user-renameable, and change between A2DP and HFP. Would be the first name-matched identity in a codebase that deliberately uses Container ID everywhere |
| **C. Transport-aware audio identity on the card** | Card carries an `AudioContainerId` set per transport; audio calls use it instead of `DeviceKey` | Honest about the two identities being different things, and survives a transport switch cleanly. Slightly more plumbing than A |

**Card topology:**

| Option | Trade-off |
| --- | --- |
| **Merged card (`MergeTransports: true`)** *(recommended)* | Matches TAIPAN, which is the same situation: one physical device on several transports. Keeps one settings bucket, so preset and volume state survive a transport switch. Requires pinning `SettingsProductId` |
| Two separate cards | Simpler, but the user would see two Cosmos, with volume settings that do not follow the headset. Contradicts the request ("BT as second type of the connection") |

## Recommended approach

Rewritten after Phase 0/0b. **BLE is ruled out by evidence**, not preference: a full sweep of every
`BTHLE` node on the machine found Cosmo on none of them (only PG Taipan and AJ159 PRO are LE, and
Taipan's nodes include `0000180f` Battery Service — which is exactly why the GATT path works for it
and cannot work here). Everything below therefore uses the Windows device tree, which is the
mechanism actually suited to a classic Bluetooth audio device.

1. **A classic-Bluetooth discovery service in the DeviceHost** — a third discovery path alongside HID
   and BLE. It enumerates present device nodes (`CM_Get_Device_ID_List`), reads
   `DEVPKEY_Device_ContainerId` per node (`CM_Get_DevNode_PropertyW`), and groups by container. A
   container holding a `BTHENUM\Dev_*\…BluetoothDevice_*` node **whose Bluetooth VID/PID matches a
   catalog row** is that device, connected. Same CfgMgr32 family `DeviceContainerId` already uses; no
   new dependency.
   **General, not Cosmo-specific** — it is driven entirely by catalog rows, so the next HFP headset
   (Starlight is already a headset in the catalog) needs a config entry and no code.
2. **Catalog** — add a `Bluetooth` transport to Cosmo with its **Bluetooth** VID/PID (`0x05D6` /
   `0x000A`, deliberately different from the USB `0x0D8C` / `0x0334`), `MergeTransports: true`,
   ~~`SupportsConfiguration: false`~~, and the same `SupportedAudioSettings` /
   `AudioVolumeTarget: OperatingSystem`. Match on VID/PID rather than the name `PG Cosmo`, which
   Windows lets the user rename.
   *(2026-09-02: `SupportsConfiguration: false` on headset Bluetooth rows is REVOKED — headsets
   are configurable over Bluetooth (OS-endpoint audio) and the catalog test
   `EveryHeadsetBluetoothRowCarriesConfiguration` now forbids the flag. Do not re-add it. See
   `../remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md`; raised as PR #130
   review m2.)*
3. **Battery** — read `DEVPKEY_Bluetooth_Battery` (`{104ea319-…}, 2`) from the Hands-Free AG node in
   the device's container. Proven to read 80 % on this headset, and it is the same value Windows
   shows. The **dongle battery is unchanged** — decision 2026-08-17, keep it.
4. **Audio** — hand the live transport's Container ID to `IAudioEndpointService`. Nothing in the
   Audio section, its sliders, the preset integration or the silence indicators changes; only the
   identity does. Phase 0 proved render and capture share that container.
5. **Connection state** — presence of the container's device nodes is the signal. The `0xBC` byte-19
   power-state greying stays on the dongle path only, guarded so it cannot run for a BT-live card.

The shape worth keeping in mind: **presence, battery and audio all resolve from one Container ID**,
which is already the identity the audio layer speaks. Get the container onto the card when the
Bluetooth transport goes live and the three capabilities follow from it.

## File-level change list

- `src/ProoveGaming.DeviceHost/appsettings.json` — Cosmo entry: add the `Bluetooth` transport row
  with the **Bluetooth** VID/PID (`0x05D6` / `0x000A`) and `MergeTransports: true`. The dongle row is
  **unchanged** — the battery experiment stays (decision 2026-08-17).
- `src/ProoveGaming.Infrastructure/Services/ClassicBluetoothDeviceDiscoveryService.cs` *(new)* — the
  device-tree discovery described above: enumerate present nodes, group by Container ID, match
  catalog rows by Bluetooth VID/PID, read `DEVPKEY_Bluetooth_Battery`. Raises the same
  connect/disconnect/battery events the existing discovery services do, so the DeviceHost and gRPC
  layers above it need no new concepts.
- `src/ProoveGaming.Infrastructure/Services/DeviceContainerId.cs` — extend, or add a sibling, for the
  device-**node** property reads (the existing helper reads interface properties). Keep the P/Invoke
  in one place rather than a third copy.
- `src/ProoveGaming.DeviceHost/*` — register the new discovery service and surface its devices
  through the existing device/battery push paths.
- `src/ProoveGaming.Device.Testing/AudioEndpointInterop.cs` *(added)* — shared Core Audio
  enumeration (name, Container ID, state, level) used by the probes.
- `src/ProoveGaming.Device.Testing/CosmoBluetoothAudioProbe.cs` *(added)* — Phase 0, read-only.
- `src/ProoveGaming.Device.Testing/CosmoBluetoothDiscoveryProbe.cs` *(added)* — Phase 0b, read-only:
  container grouping, `DEVPKEY_Bluetooth_Battery`, and the full BLE sweep that ruled LE out.
- `src/ProoveGaming.Device.Testing/Program.cs` *(added)* — arms `cosmo-bt` and `cosmo-bt discovery`.
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — if Option C: an audio-identity property
  distinct from `DeviceKey`, defaulting to `DeviceKey` so every existing device is unaffected.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `FillBluetoothCard`: stamp the audio identity
  for a Bluetooth headset; make sure the merged card keeps its settings bucket across a transport
  switch (the TAIPAN pinning already exists — verify it covers a headset).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Audio.cs` — use the audio
  identity rather than `DeviceKey` when calling `IAudioEndpointService`.
- `src/ProoveGaming.App/Services/AudioMuteMonitor.cs` — same substitution for the silence indicators.
- `docs/CONTEXT.md` — one-line entries for any new term introduced (e.g. the audio-identity concept).
- `docs/plans/add-cosmo-headset/add-cosmo-headset-plan.md` — dated note under the battery DECISION
  recording how it was resolved. **Do not rewrite its review journal.**
- `tests/ProoveGaming.App.Tests/*` — see Test strategy.

## Acceptance criteria

- Given Cosmo paired and connected over Bluetooth only → the App shows **one** Cosmo card, connected,
  with no second card for the dongle transport.
- Given Cosmo on Bluetooth → its card shows a battery percentage that comes from GATT, and it
  **changes** across a real discharge (unlike the dongle's byte 17).
- Given Cosmo on Bluetooth → the details view's Audio section shows the current output and microphone
  levels, and moving either slider changes the Windows level for **that headset's** endpoints and no
  other device's.
- Given Cosmo on Bluetooth → the output/microphone silence indicators reflect the OS state on the
  home card and the details connectivity bar, as they already do over the dongle.
- Given the user switches Cosmo from the dongle to Bluetooth mid-session → the same card stays, its
  saved audio levels are still associated with it, and no duplicate card appears.
- Given Cosmo connected over the **dongle** → no battery indicator is shown (the withdrawn
  experiment), and everything else behaves exactly as before this change.
- Given Cosmo switched off or out of range on Bluetooth → the card shows disconnected, driven by the
  BT stack rather than by `0xBC`.
- Given any **other** Bluetooth device (Rampage, TAIPAN) → behaviour is byte-for-byte unchanged.
- `dotnet build ./src/ProveGaming.slnx` succeeds; existing App + Infrastructure tests stay green.

## Test strategy

New feature → the following is required coverage, not optional. All in
`tests/ProoveGaming.App.Tests`, which already has the `FakeUsbHidDeviceService` harness and the
`HomeViewModelBatterySourceTests` static-helper pattern for rules that cannot construct a full
`HomeViewModel`.

- **Transport merging** — a Cosmo catalog entry with dongle + Bluetooth rows produces ONE card;
  switching the live transport does not create a second. Mirror the existing TAIPAN merge tests.
- **Settings bucket stability** — audio levels saved on the dongle transport are read back when the
  card is on Bluetooth (`SettingsProductId` / `SettingsDeviceKey` pinning).
- **Audio identity selection** — the identity handed to `IAudioEndpointService` is the Bluetooth
  endpoint container when the card is BT-live, and the USB container otherwise. This is the test that
  would catch the whole feature silently addressing the wrong device's volume.
- **Battery source** — a BT-live Cosmo takes the GATT level; a dongle-live Cosmo shows none.
- **Regression** — Rampage over Bluetooth is unaffected by the `FillBluetoothCard` change.

**Deliberately not automated:** the Core Audio interop itself (real endpoints, no seam), the GATT
read (no abstraction over `InTheHand.Bluetooth` statics — a known, repeatedly-flagged gap), and
anything requiring a real radio. Those are covered by the QA steps below.

## QA (manual test steps)

1. Pair the PG Cosmo headset with Windows over Bluetooth and connect it. Launch the app.
   **Expected:** one Cosmo card, shown as connected, with a battery percentage.
2. Compare that percentage with the one Windows shows in Settings → Bluetooth & devices.
   **Expected:** they agree.
3. Open the Cosmo card → Audio section. Move the Output Volume slider.
   **Expected:** the Windows volume for the Cosmo playback device moves with it; no other device's
   volume changes.
4. Move the Microphone Volume slider. **Expected:** the Windows level for the Cosmo microphone moves.
5. Mute the headset output in Windows. **Expected:** the silence indicator appears on the home card
   and in the details connectivity bar.
6. With the app running, turn the headset off, wait ~30 seconds, turn it back on.
   **Expected:** the card goes disconnected and comes back connected, with the battery level again.
7. Switch the headset from Bluetooth to its 2.4G dongle while the app is running.
   **Expected:** still exactly one Cosmo card; the audio levels you set in step 3 are still shown.
8. On the dongle, check the card. **Expected:** no battery percentage (the withdrawn experiment).
9. Connect a Rampage keyboard over Bluetooth. **Expected:** it behaves exactly as it did before.
10. Leave the headset on Bluetooth for a working day, checking the battery occasionally.
    **Expected:** the percentage falls. This is the observation that retires the old dongle reading.

## Review focus

`gRPC contract` · `DI lifetimes` · `thread-safety` · `null-safety`

The Bluetooth discovery path runs on the BT stack's threads and pushes into UI-bound collections;
the previous round of Bluetooth work in this repo produced five defects found only by reading logs,
none of them unit-testable. Give the card-identity and connection-transition code the most scrutiny.

## Performance & quality

No new hot paths. The GATT battery read is already throttled to a 5-minute refresh and is not
touched. Core Audio calls stay where they are — off the UI thread, marshalled back — and the Audio
section's sliders already debounce. The one thing to watch is that resolving a Container ID from the
device tree must not happen per slider movement: resolve it once when the card's transport changes
and cache it on the card.

## Risks

- **Phase 0 may find no path from the BT device to the endpoint container.** Then Option A is out and
  the choice is C (more plumbing) or B (name matching, which this codebase has deliberately avoided).
  This is why Phase 0 gates the work.
- **Two Bluetooth profiles at once.** Windows commonly exposes A2DP (stereo, no mic) and Hands-Free
  (mic, degraded audio) as separate endpoints that switch depending on what has the microphone open.
  Which endpoint the sliders should address — and whether the answer changes while a call is active —
  is a real product question, not just an implementation detail.
- **Both transports live simultaneously.** The dongle may be plugged in while the headset is on
  Bluetooth, exactly as with TAIPAN, where the receiver enumerates whether or not anything is talking
  through it. The card must pick the live transport, and "a dongle is present" is not evidence.
- **Ending the dongle battery experiment is a product decision**, not a technical one. It is
  recommended here because a trustworthy reading now exists to compare against, but the existing plan
  asked for it to be resolved deliberately.

## Open questions

1. ~~**End the dongle battery experiment?**~~ **DECIDED 2026-08-17 — keep it.** The dongle battery
   stays as it is; the expectation is that the correct value will be found rather than withdrawn.
   No change to Cosmo's dongle row.
2. ~~**Which endpoint do the sliders drive when both A2DP and Hands-Free are present?**~~ **ANSWERED
   by Phase 0** — the live transport exposes one render and one capture sharing a container, so one
   identity drives both. No per-direction handling needed.
3. ~~**GATT battery while paired vs connected?**~~ **Moot** — Cosmo is not a GATT device.
4. **How should the App discover Cosmo over Bluetooth at all?** (New, blocking.) It is invisible to
   the BLE discovery the App uses. Candidates, cheapest first:
   a. **Treat the ACTIVE audio endpoint as the presence signal** — the App already enumerates
      endpoints by container, and the endpoint going ACTIVE is precisely "the headset is connected
      over this transport". No new dependency; but it makes an audio endpoint the source of truth for
      device presence, which no other device does.
   b. **Enumerate classic Bluetooth devices from the Windows device tree** (`BTHENUM`, via CfgMgr32 —
      the repo already has `DeviceContainerId` doing this kind of query) and match by container.
   c. **`Windows.Devices.Enumeration`** with an association-endpoint filter — the most "correct"
      Windows answer, but a new API surface in the DeviceHost.
5. **Where does the Bluetooth battery actually come from, and can we read it?** (New, blocking for
   the battery half.) Expected to be the Windows device property
   `{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2` on the Bluetooth device, populated from HFP battery
   reporting. **Needs a Phase 0b probe to confirm before any battery work is scoped.** If it is
   readable there, it is roughly as cheap as the GATT path was assumed to be; if not, battery over
   Bluetooth may not be available to us at all even though Windows shows it.

## Follow-up: the same latency existed for BLE (2026-08-20)

The 3 s classic poll this plan introduced fixed disconnect latency for **classic** Bluetooth devices
only. Low-energy devices — a PG Taipan over Bluetooth, say — stayed on the 30 s `ActiveSweepInterval`
(five minutes when nothing else was tracked), because that sweep is the only thing that clears a
card's Bluetooth state. The App showed a switched-off Bluetooth mouse as connected for well over
twenty seconds, and `HomeViewModel`'s disconnect branch could not help: it keys on
`card.BluetoothLinkUp`, and the HID path deliberately never clears it.

Measured on a PG Taipan, polling the device tree every 2 s:

| action | what Windows did |
|---|---|
| switched the mouse OFF | the `BTHLE` node's `IsConnected` flipped **True → False** |
| removed the pairing in Windows settings | the node **disappeared outright** |

Both showed up immediately, so Windows was never the delay — the cadence was, and only on our side.

**Change.** `DeviceNodeQuery.EnumerateLowEnergyNodes()` (enumerator `BTHLE`) plus
`BluetoothDeviceDiscoveryService.PollLowEnergyPresence()`, riding the existing 3 s classic loop
rather than opening a third one. Absence and `IsConnected=false` are treated as the same signal.

Three deliberate constraints, each guarding a failure mode this file already warns about:

- **One-directional.** It only ever marks a device *disconnected*. Reconnection stays with the
  advertisement watcher, the GATT events and the sweep, so a cheap local heuristic can never assert a
  device is present that the App cannot actually talk to.
- **A failed enumeration is not evidence.** `EnumerateLowEnergyNodes` returns `null` — not an empty
  list — when the tree cannot be read, and the poll then leaves every tracked device alone. An empty
  list on failure would disconnect everything on one bad call.
- **Two consecutive misses (~6 s) before acting.** A BLE link is a radio link and one sweep can catch
  it mid-blip; showing a working mouse as absent is the worst failure mode here. Still five times
  faster than the sweep it replaces. The classic path needs no such confirmation.

**Correction, same day — the poll had to become bidirectional.** Shipping it one-directional fixed the
disconnect and exposed a pre-existing reconnect gap that had been invisible: because the card never
went offline before, nobody could see that it never came back either. Removing a device in Windows
and pairing it again left the card offline until the App was restarted.

The cause is the same signal, failing the other way. `RegisterDevice` decides connectedness from
`device.Gatt.IsConnected`, which reflects OUR GATT client rather than the OS link. After a re-pair the
tracked `BluetoothDevice` instance is dead, so that flag never becomes true again and no sweep can
recover it. This file already documented the flag being stale `true` once the radio is off; it is
equally wrong stuck at `false` here.

So the device tree became the source of truth in **both** directions: the poll now calls
`MarkConnected` as well as `MarkDisconnected`, and `RegisterDevice` defers to the tree through
`ResolveConnected(treeOpinion, gattConnected, isPaired)`. The tri-state matters — a `null` opinion
means the tree has none (no successful enumeration yet, or an unresolvable address) and must NOT read
as disconnected, or every tracked device would drop at startup before the first poll. Reconnect is
now on the same 3 s clock as disconnect; there is no confirmation delay in that direction, because
positive evidence of a live node is not something a blip fabricates.

Covered by `tests/ProoveGaming.Infrastructure.Tests/LowEnergyPresenceTests.cs` (21 cases), including
the base-UUID trap — a service node's id ends in `00805f9b34fb`, so a naive "last twelve hex digits"
would return that for every device, match no live node, and disconnect a healthy mouse every poll —
and the tri-state precedence above.

**Second correction — reconnect needed two more fixes, found by instrumenting rather than reasoning.**

Making the poll bidirectional was necessary but not sufficient. Two further defects only became
visible once the connect side was logged at all (until then `RaiseDeviceConnectionChanged` was silent
while disconnects logged, so an oscillating status appeared in the log as a lone "marked
disconnected" repeating every 30 s with no visible cause):

1. **We held a GATT link to a device the user had unpaired.** Every event after removal still read
   `paired=true`, battery reads kept succeeding, and the advertisement watcher kept calling
   `ConnectAsync`. Each of those re-asserted "connected" within seconds, so a removed device's card
   resurrected itself. The cause was collapsing two signals that had already been measured as
   different: switching a device OFF leaves its node with `IsConnected=false`, while REMOVING it
   deletes the node. `PresentLowEnergyAddresses` now separates them, and an absent node triggers
   `ReleaseTrackedDevice` — drop the subscription, disconnect the GATT link, dispose, untrack.

2. **A re-paired device arrived with an empty `ClientName`, and the App discards those.** The device
   was rediscovered and did report connected; the identity was lost in transit. Root cause is
   hardware: a TAIPAN's BLE address **increments on every re-pair** (`D10B…` → `D10C…` → `D10D…`,
   measured across three cycles). `DeviceMap.DeviceId` cached the first pairing's address and
   `DeviceNeedToTrack` only filled it when empty, so `GetClientName(deviceId)` matched nothing
   afterwards. Fixed by refreshing that cache and by adding a name-based fallback in
   `ResolveClientName`, so a moved address can never blank out a device's identity.

See [[BLE address instability]] in `docs/CONTEXT.md` — anything keying a BLE device by address across
pairings (settings buckets, `MergeTransports`, saved presets) is suspect until checked.

**Method note.** Four rounds of this were spent on hypotheses that measured out false — address
parsing, a stale binary (twice; `strings` silently fails on these DLLs, so verify assembly contents
by reading the bytes in .NET, not with `strings`). What actually resolved it was logging both
directions of the connection event, including `ClientName`. The asymmetric logging was the real
defect underneath the defect.

**Build-order note.** `ProoveGaming.Infrastructure.Tests` now references `ProoveGaming.DeviceHost`
(added for the registry tests in PG-608), so a **running DeviceHost locks the test build**. Stop the
service before `dotnet test`.

## Review findings

### Round 2 (2026-08-26) — the BLE presence work — Ship: no

Surface: the whole uncommitted working tree on `main` (no commits ahead of it). 551 insertions, of
which 387 are in `BluetoothDeviceDiscoveryService`. Verified on hardware by the user: disconnect and
re-add both behave, with an acceptable delay.

#### Code

- [x] **B1. A battery read still asserts "connected", so a released device can resurrect** —
  `BluetoothDeviceDiscoveryService.cs:544-548` (`UpdateBatteryAsync`) — Blocker

  **Problem:** the method raises `isConnected: true` unconditionally whenever a GATT read returns a
  value, without consulting `_trackedStatuses` or checking the device is still tracked:

  ```csharp
  int battery = await ReadGattBatteryAsync(device);
  if (battery > 0)
      RaiseDeviceConnectionChanged(device.Id, device.Name, true, device.IsPaired, battery);
  ```

  `ReadGattBatteryAsync` allows up to 5 s. A read in flight when `ReleaseTrackedDevice` or
  `MarkDisconnected` runs therefore completes *afterwards* and re-asserts connected for a device that
  is gone.

  **Why it blocks:** this is the exact failure mode the whole round was spent removing, still
  reachable — and it is visible in the captured log:
  `23:15:09 marked disconnected` → `23:15:12 GATT battery 46% → connected=true`, three seconds later.
  Releasing the GATT link closed the steady-state case, which is why hardware now looks correct, but
  the race survives with a window of up to five seconds. It will reappear as an intermittent
  "removed device goes active again" that is far harder to diagnose than the original.

  **Solution options:**
  - *Option A (correct):* make the battery path report a LEVEL, not a connection. Raise only when the
    device is still in `_trackedDevices` and `_trackedStatuses` already says connected; otherwise drop
    the reading. Connection state should have exactly one owner — the tree — and this is a second one.
  - *Option B (narrower):* have `ReleaseTrackedDevice` stamp a released-generation per id and discard
    late completions for it. Fixes the release path but leaves `MarkDisconnected` racing.

- [x] **S1. A hung paired-device enumeration disables rediscovery permanently** —
  `BluetoothDeviceDiscoveryService.cs` (`TriggerRediscoveryIfUnexplained`) — Should-fix

  **Problem / why it matters:** `_rediscoveryInFlight` is cleared in a `finally`, but
  `Bluetooth.GetPairedDevicesAsync()` is awaited with no timeout. If it never returns — the call the
  file already documents as the one that misbehaves when the adapter goes away — the flag stays set
  and reconnect-after-re-pair silently stops working for the life of the process, with no log.

  **Fix:** bound it the way `ReadGattBatteryAsync` bounds its read (`Task.WhenAny` + a timeout), and
  log when the bound is hit.

- [x] **S2. The rule that broke twice has no test** — `ResolveClientName` — Should-fix

  **Problem / why it matters:** an empty `ClientName` makes the App discard the event and the device
  silently never appears — the final bug of this round, and the hardest to see. `ResolveClientName` is
  private and untested; `LowEnergyPresenceTests` covers only the pure address/liveness statics. The
  same gap as PG-608 round 1: the seam that actually failed is the one with no coverage.

  **Fix:** extract as `internal static string ResolveClientName(IReadOnlyList<DeviceMap> maps, string?
  byId, string? deviceName)` and cover: id hit wins; id miss falls back to name; name miss yields
  empty; a device whose address changed still resolves. It must fail against the set-once cache.

- [x] **S3. `DeviceNeedToTrack` now overwrites the shared row's `DeviceId` on every call** —
  `BluetoothDeviceDiscoveryService.cs:135-145` — Should-fix

  **Problem / why it matters:** the change from set-once to always-write is right for a changing
  address, but `DeviceMap` is shared catalog state and two units of the same model would now fight
  over the row, last-writer-wins, churning on every sweep. Previously it was merely stale; now it is
  unstable. `ResolveClientName`'s name fallback masks the symptom, which is what makes it worth
  naming rather than leaving.

  **Fix:** key the id cache per device rather than per catalog row, or accept it explicitly with a
  comment saying single-unit-per-model is assumed.

#### Nits

- `InternalsVisibleTo("ProoveGaming.Device.Testing")` was added to the production
  `ProoveGaming.Infrastructure.csproj` to let a probe drive the shipping parsers. Justified, but it is
  production metadata serving investigation code — worth a second opinion before it ships.
- The two new probes and the docs are unrelated to the fix and should land as separate commits.

#### What's done well

- The two device-tree signals (node absent vs `IsConnected=false`) are separated on measured evidence
  rather than assumption, and the distinction is load-bearing.
- `ResolveConnected` makes the tri-state explicit so "no opinion" cannot silently collapse to
  "disconnected" — the failure that would drop every device at startup.
- Rediscovery is bounded three ways (catalogued devices only, one in flight, cooldown) rather than
  simply lowering the sweep interval and paying the enumeration cost forever.
- Logging both directions of the connection event was the actual fix for the *debugging* problem, and
  it was made permanent rather than used and removed.

#### Verdict

Ship: **no** — B1 only. Everything the user tested works, and B1 is precisely why that is not
sufficient evidence: it is a ≤5 s race that steady-state testing will usually miss, in the exact
behaviour this round was meant to fix.

First: **B1**, then **S2** (which would have caught the client-name bug), then **S1** and **S3**.

#### Round 2 — B1 resolved (2026-08-26)

Option A, the correct one: the battery path now publishes a **level** and never asserts a
**connection**. `UpdateBatteryAsync` drops a completed reading unless the device is still tracked and
the tree already calls it connected, so a read in flight across a disconnect or a release can no
longer revive the card. Connection state keeps exactly one owner.

The decision is extracted as `ShouldPublishBattery(battery, stillTracked, currentlyConnected)` so the
race is pinned by a test rather than by reading the call site — the same treatment `ResolveConnected`
got, and for the same reason: both are one-line rules whose failure mode is silent.

Verified regressive, not merely passing: restoring the old `battery > 0` rule fails exactly the two
new tests (`ABatteryReadingIsDroppedForAReleasedDevice`,
`ABatteryReadingIsDroppedForADeviceTheTreeCallsDisconnected`) and passes the other 28. Suite: **797
Infrastructure + 906 App**.

Ordering was checked at both call sites — `RegisterDevice` and `MarkConnected` each set
`_trackedStatuses` *before* firing the battery read, so the first reading after a genuine connect is
still published.

#### Round 2 — S1, S2 and S3 resolved (2026-08-26)

- **S1** — the paired-device enumeration is now bounded (`RediscoveryEnumerationTimeout`, 10 s, the
  same bound `ReadGattBatteryAsync` puts on its own radio call) and logs a warning when it is hit.
  `_rediscoveryInFlight` is cleared in a `finally`, but an await that never returns never reaches it;
  one hang would otherwise have disabled reconnect-after-re-pair for the life of the process with
  nothing in the log to say so.

- **S2** — the name match is extracted as
  `MatchCatalogNameByDeviceName(maps, deviceName)` and covered by 10 cases: a moved address still
  names its device, matching is case-insensitive, a WIRED row never names a Bluetooth device, an
  unknown device yields empty rather than borrowing the first row's identity, a nameless device does
  not throw, and Cosmo/Starlight do not collapse into one another. This is the only identification
  path that survives an address change, and it had no test while it broke twice.

- **S3** — the shared catalog mutation is **gone** (`config.DeviceId` writes remaining: 0). A device
  id maps to a catalog name, so that association now lives in a per-id
  `_catalogNameByDeviceId` inside the service and `DeviceMap` is immutable again. This removes both
  the set-once staleness and the last-writer-wins churn between two units of one model, rather than
  trading one for the other. `Map(BluetoothDevice)` was routed through `ResolveClientName` too — it
  had the same stale-id lookup and would have kept the bug alive on that path.

`ResolveClientName` now resolves in three steps: the per-id memory, then the legacy
`DeviceMap.DeviceId` lookup (kept in case another catalog path populates it), then the device's own
name. Suite: **807 Infrastructure + 906 App**.

**All Round 2 findings are closed.**



_Filled in by /reviewer._

### Round 1 — Ship: no

Branch `feat/PG-564-Cosmo-BT-connection`. Committed: this plan and the three probes
(`AudioEndpointInterop`, `CosmoBluetoothAudioProbe`, `CosmoBluetoothDiscoveryProbe`, the `Program.cs`
arms). **The entire implementation is uncommitted working-tree change** — 8 modified files plus 3
untracked — so a reviewer working from the commit list alone sees only the probes. Commit before
merge review.

#### Plan conformance

- **Implemented as planned:** classic-Bluetooth discovery via the device tree; catalog Bluetooth
  transport row matched on **Bluetooth** VID/PID (`0x05D6`/`0x000A`, correctly distinct from the USB
  identity); `MergeTransports: true`; battery from `DEVPKEY_Bluetooth_Battery`; audio addressed by the
  live transport's Container ID; dongle battery left untouched per the 2026-08-17 decision.
- **Deviates from plan:** the plan specified one `ClassicBluetoothDeviceDiscoveryService.cs`. The code
  instead splits into a pure `ClassicBluetoothDeviceResolver` + a P/Invoke `DeviceNodeQuery`, wired
  into the **existing** `BluetoothDeviceDiscoveryService`. The split is an improvement — it is the
  `X43KeyMatrix`/`EasenMouseService` idiom and is what made the parsing testable — and reusing the
  existing event pipeline means `DeviceService`, the gRPC contract and the App's Bluetooth card path
  needed no changes at all. **The plan's File-level change list is now stale and should be corrected.**
- **Missing / partial:** `SupportsConfiguration: false` was specified for the Bluetooth row and was
  then removed (correctly — it gates preset dirty-tracking, not just device writes). The plan still
  says to set it; correct the plan.
- **Acceptance criteria:** 4 of 8 met. Card appears and is active over BT ✔; battery real and from
  GATT-free path ✔; audio sliders drive the right endpoints ✔ (user-confirmed); other Bluetooth
  devices unaffected ✔. **Not met:** transport switch (B1), preset save over BT (B2), and the
  merged-bucket criterion is untested. The "at most one card" criterion is untested.

#### Code

- [ ] **B1. Bluetooth zeroes the other transports and never restores them** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1084-1089` (`FillBluetoothCard`) — Blocker
  **Problem:** `card.HasBluetooth = isConnected;` then `if (isConnected) { card.HasWire = false;
  card.HasDongle = false; }`. When Bluetooth later reports disconnected, `HasBluetooth` goes false but
  `HasDongle` is left at the stale `false` Bluetooth wrote. `Connected => HasWire || HasDongle ||
  HasBluetooth` is then false.
  **Why it blocks:** user-reproduced. Headset connected over Bluetooth with its 2.4G dongle also
  plugged in; switch the headset to the dongle. Within 3 s the classic poll reports Bluetooth
  disconnected, the card goes **inactive** — while audio plays through the dongle perfectly. It only
  recovers on a headset power-cycle, which produces a fresh HID event that recomputes `HasDongle`.
  Turning the Bluetooth radio off entirely takes the `MarkAllDisconnected` path and does recover,
  which is why that route appears to work.
  Cosmo is the first device where both transports are genuinely live at once (the USB receiver stays
  enumerated throughout), so this exclusivity was safe until this branch.
  **Solution options:**
  - Option A (correct): on the Bluetooth-disconnected transition, re-derive `HasWire`/`HasDongle`
    from current HID state rather than leaving what Bluetooth zeroed — e.g. re-run the HID fill for
    that card, or have the disconnect branch query the DeviceHost for present transports.
  - Option B (do NOT take): stop zeroing them when Bluetooth connects. The exclusivity is deliberate
    and guards a real TAIPAN bug — an idle receiver enumerates whether or not anything is talking
    through it, so "a dongle is present" is not evidence of a live link (`HomeViewModel.cs:1010-1016`).

- [ ] **B2. Save Preset is lit on page open over Bluetooth, before any edit** —
  `DeviceDetailsViewModel.Presets.cs:130` / `DeviceDetailsViewModel.Audio.cs:265` — Blocker
  **Problem:** opening Cosmo's details over Bluetooth shows unsaved changes immediately. Ruled out by
  reading: the volume-tracking poll already guards its writes with `_suppressAudioSave`
  (`Audio.cs:160-161`), and slider subscriptions attach only *after* the load (`Audio.cs:80-81`), so
  neither stages dirty.
  **Why it blocks:** the user cannot tell a real edit from this, so Save Preset is meaningless on the
  transport the feature exists for. Most likely the dirty flag is **honest**: the Bluetooth and dongle
  audio endpoints are different Windows devices with independent volumes, so the live level genuinely
  differs from the preset's snapshot. If so this is a product decision, not a plumbing bug.
  **Solution options:**
  - Diagnostic first (cheap, settles it): open the page over BT with Save lit, note both slider
    values, then compare the `device_settings` rows against the preset snapshot for that bucket.
    Numbers differ → honest dirty. Numbers identical → the comparison is keyed on the wrong settings
    bucket over Bluetooth, i.e. a real bug in `ApplyBluetoothIdentity`'s merged-transport pinning
    (`HomeViewModel.cs:1104-1112`).
  - Option A (if honest): make audio levels per-transport preset state, or seed the preset's level to
    the endpoint on first open so they agree.
  - Option B (if bucket mis-keying): fix the pinning so BT and dongle share one settings bucket.

- [ ] **S1. `GetBluetoothDevicesAsync` hands out objects the poll then mutates in place** —
  `BluetoothDeviceDiscoveryService.cs:279` and `:849-850` — Should-fix
  **Problem / why it matters:** the method returns references to the `BluetoothDeviceState` instances
  stored in `_classicDevices`, and the 3 s poll loop mutates those same instances
  (`state.IsConnected = false; state.BatteryLevel = -1;`) from a background task. A caller enumerating
  the returned list can watch entries change underneath it: `DeviceService` mapping them into a gRPC
  response while the poll flips the flag yields a device reported connected with battery `-1`, or
  disconnected carrying a stale level, purely on interleaving. Every other value in this service is
  produced fresh per call; these are the only shared mutable ones.
  **Fix:** return copies (`results.AddRange(_classicDevices.Values.Where(d => d.IsConnected).Select(Clone))`),
  or store immutable snapshots and replace rather than mutate.

- [ ] **S2. A query method raises connect/disconnect events** —
  `BluetoothDeviceDiscoveryService.cs:278` — Should-fix
  **Problem / why it matters:** `GetBluetoothDevicesAsync` calls `PollClassicBluetoothDevices()`, which
  raises `DeviceConnectionChanged`. The App calls this at startup, so a *read* synchronously pushes
  connect/disconnect events back into the caller — re-entrancy that nothing here is written to expect,
  and a surprise for any future caller who just wants the list. The stated reason (a headset already
  connected at launch should not wait for the first sweep) is sound; the side effect is not.
  **Fix:** split the poll into "refresh state" and "raise transitions", and have the query call only
  the former; or seed `_classicDevices` once at start-up instead.

- [ ] **S3. The classic poll runs every 3 s for the life of the host regardless of catalog** —
  `BluetoothDeviceDiscoveryService.cs:~520` (`ClassicPollInterval`) — Should-fix
  **Problem / why it matters:** the loop enumerates `BTHENUM` and does four property reads per node
  every 3 s forever, on every machine, even where no catalogued classic device exists. Cheap per
  iteration, but it is unconditional background work that no other discovery path in this service
  does. The 3 s figure exists only to make the transport switch feel immediate.
  **Fix:** back off when no catalogued classic device has ever been seen (the BLE sweep already has
  exactly this idle/active distinction and the reasoning for it), or drive off a device-change
  notification instead of polling.

#### Tests

- [ ] **The App-side audio identity is untested.** `AudioContainerId`'s fallback-to-`DeviceKey`, and
  its **clearing** when Bluetooth is not live, are the whole correctness story for volume following
  the transport — and the clearing is what stops the sliders driving `NOTPRESENT` endpoints after a
  switch to the dongle. No test covers either. `tests/ProoveGaming.App.Tests`.
- [ ] **No test for the B1 transition.** A card whose Bluetooth goes disconnected while a dongle is
  present must remain `Connected`. This is exactly the kind of transport-flag rule the existing
  `HomeViewModelBatterySourceTests` static-helper pattern covers well.
- [ ] **Merged-transport behaviour untested for a headset** — one card across dongle+BT, and one
  settings bucket across a switch. The plan's Test strategy asks for both; neither exists.
- [ ] `ClassicBluetoothDeviceResolverTests` (24 tests) is genuinely good and needs no additions — see
  below.

#### Nits

- `CosmoBluetoothDiscoveryProbe` carries its own CfgMgr32 declarations now duplicated by
  `DeviceNodeQuery`; fold the probe onto the production helper —
  `src/ProoveGaming.Device.Testing/CosmoBluetoothDiscoveryProbe.cs`
- `StarlightAudioProbe` still holds a private copy of the Core Audio interop that
  `AudioEndpointInterop` now provides; the file itself says so and defers it.

#### What's done well

- **The pure/P-Invoke split.** `ClassicBluetoothDeviceResolver` being testable without hardware is
  what caught two real bugs before they shipped: the address parser returning the Bluetooth base
  UUID's final group (`…00805f9b34fb`) for every device, and the all-zero-address "Standard Serial
  over Bluetooth link" becoming a card.
- **Tests built on real captured instance IDs**, not invented shapes. That is the only reason the
  UUID collision was caught at all.
- **Reusing the existing event pipeline** rather than adding a parallel one: `DeviceService`, the gRPC
  contract and the App's Bluetooth card path are untouched, and nothing downstream can tell a classic
  device from a BLE one.
- **Evidence-first throughout.** BLE was ruled out by sweeping every LE node on the machine, not
  assumed; the battery property was measured before being relied on; the connection property was
  measured in both states after the first cut got it wrong.
- **The plan records its own wrong assumptions struck through** rather than deleting them — the GATT
  battery and "discovery comes free" rows are both preserved as corrections.

#### Verdict

Ship: no — two Blockers, both user-reproduced on hardware, both in the seam between the new discovery
path and the existing transport/preset logic rather than in the new code itself. The discovery layer
underneath is solid and well-tested; what is missing is how it composes with a device that has two
live transports at once, which Cosmo is the first to have.
First: 1) B1 transport-flag restoration, 2) the B2 diagnostic to tell an honest dirty flag from a
bucket-keying bug, 3) S1 shared mutable state.
