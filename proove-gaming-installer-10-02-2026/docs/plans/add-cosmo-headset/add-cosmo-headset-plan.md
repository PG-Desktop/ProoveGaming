# Plan: Add the PG Cosmo headset as a new device type

## Goal

Support the PG Cosmo headset alongside the existing mice and keyboards: it appears as its own card
on the Home screen, connects and disconnects like any other device, and opens a details view with
four sections — Presets, RGB Lighting, Audio and Firmware. Audio is the new capability: two 0–100 %
sliders for output and microphone volume. Cosmo drives the **operating system's** volume for its own
endpoints, but a future headset may hold its own device-side level, so where the volume is applied
must be a per-device capability rather than something baked into the Audio section.

Adding a third device family also forces a piece of groundwork the first two got away with: section
visibility is currently decided by `IsKeyboardDevice` string checks, so anything that is "not a
keyboard" is treated as a mouse. A headset would inherit Buttons, DPI and Settings. Making the nav
capability-driven is therefore part of this change, not optional polish.

## Hardware facts (already established — do not re-derive)

Measured on the connected unit:

| Fact | Value | Consequence |
| --- | --- | --- |
| VID / PID | `0x0D8C` (3468, C-Media) / `0x0334` (820) | Catalog entry uses `3468` / `820` |
| Names | Manufacturer `THI Headphone`, product `PG Cosmo` | — |
| Topology | USB composite: `MI_00` = MEDIA (USB Audio Class), `MI_03` = HID | The Windows endpoints "Headphones (PG Cosmo)" / "Microphone (PG Cosmo)" come from `MI_00` |
| HID interface | `MI_03`, in=32 / out=32 / feature=0, classed "HID-compliant consumer control device" | The device is discoverable by the existing HID pipeline, so the card and connection monitoring come free. The 32-byte **OUT** report is the one hint a vendor channel may exist |
| Volume buttons | HID consumer-control keys | Windows already handles them — that is why they move the OS volume. **We do not need to implement them** |
| **Serial number** | **`00006F64096B22E7` — a real `iSerialNumber`** | Unlike BLAZE and TAIPAN, its Container ID is **stable across USB ports**. The port-move problem does not apply, and `deviceKey` is genuinely per-unit |

**Unknown, and the reason for Phase 0:** whether any vendor-defined collection exists on `MI_03`,
and therefore whether RGB, device-side volume or battery can be controlled at all. Nothing in the
enumeration proves a vendor channel — only the 32-byte OUT report suggests one might be there.

## Scope

**In:**
- Phase 0 hardware probe that settles what is controllable, before any protocol code is written.
- Catalog entry so the DeviceHost discovers Cosmo and the App shows a single card with the existing
  connect / disconnect behaviour.
- Details view with Presets, Audio and Firmware. RGB Lighting **only if Phase 0 proves it**.
- `NavSection.Audio` and an Audio section with two 0–100 % sliders.
- OS-endpoint volume control for the device's own endpoints, applied in the **App**.
- A per-device capability declaring where volume is applied (`OperatingSystem` vs `Device`).
- Making nav-section visibility capability-driven instead of `!IsKeyboardDevice`.

**Added to scope after the plan was written** (all shipped — see the file-level change list):
- **Headset power state.** Byte 19 of report `0xBC`; the card greys out when the headset is switched
  off, even though its dongle stays enumerated.
- **A provisional battery percentage.** Byte 17 — believed to be a constant, shipped deliberately to
  be observed. See the **DECISION** section; it is not an oversight.
- **Audio/mic silence indicators** on the home card and the details bar, from Core Audio.
- **Audio levels as preset state** — a volume change enables Save Preset and is restored on apply.
- **Read-only device probes**, plus two write-capable ones used once under explicit authorisation.

**Out:**
- Any vendor protocol for the headset (RGB, device-side volume, a *confirmed* battery) — `0xBC`
  remains undocumented; see the vendor request.
- Implementing the `Device` volume target. The capability is introduced and Cosmo uses
  `OperatingSystem`; the `Device` branch is a documented not-implemented path until a headset needs it.
- Re-binding the physical +/- buttons. Windows handles them and the user did not ask.
- Changing preset semantics. Presets work per VID/PID exactly as they do for every other device.

## Phase 0 — hardware probe (gate)

Nothing below Phase 1 may be written until this is answered. Add `CosmoProbe` to
`ProoveGaming.Device.Testing` and answer, in order:

1. **Dump the `MI_03` report descriptor** — every collection's usage page, usage, report IDs and
   in/out/feature lengths. A vendor-defined page (`0xFF00`+) alongside the consumer-control
   collection means a vendor channel exists. Only consumer control means it does not.
2. **Listen for input reports** while pressing every physical control on the headset. Records what
   the device volunteers, and whether anything beyond consumer-control keycodes appears.
3. **Only if a vendor collection is found:** attempt a read-style query on it and report the reply.
   Do not attempt writes in Phase 0 — an unknown write to an unknown channel is how the BLAZE flash
   corruption happened.

**Decision the probe produces:**
- No vendor collection → RGB Lighting is **not** shown for Cosmo, device-side volume is impossible,
  and Phase 1 proceeds with OS volume only.
- Vendor collection found → Phase 1 still proceeds unchanged; RGB becomes a separate follow-up plan
  with its own protocol work.

### Phase 0 RESULT (run 2026-08-05, headset connected) — a vendor channel EXISTS

The top-level collection is Consumer (`usagePage 0x000C`), which on its own reads as "nothing to
drive". That reading is **wrong**, and the raw descriptor is what settles it:

```
85 BC        Report ID 0xBC
06 02 FF     Usage Page 0xFF02   <-- vendor-defined
```

Full report table on `MI_03`:

| Report ID | Usage page | Direction | Payload |
| --- | --- | --- | --- |
| `0x03` | `0x000C` Consumer | Input | the volume / media keys Windows already handles |
| `0xBC` | **`0xFF02` vendor** | Input **and** Output | 14 B in / 15 B out |
| `0xFD` | **`0xFF02` vendor** | Input **and** Output | 30 B in / 31 B out |

Two bidirectional vendor report pairs. A consumer-control collection has no need for those, so this
is a genuine host↔device command channel.

**What this changes:** RGB and device-side volume are **not** ruled out — the channel is there, only
the protocol is unknown. What it does *not* change is Phase 1: we still have no protocol, so the
catalog keeps `SupportsRgb: false`, the RGB Lighting section stays hidden, and Audio uses the OS
endpoint volume, which is what Cosmo does today regardless.

It also validates the `AudioVolumeTarget` capability: a device-side level may become reachable on
this very headset once the protocol is known, without reworking the Audio section.

**Follow-up, not part of this plan:** ask the vendor for the `0xFF02` protocol on report IDs `0xBC`
and `0xFD` — the same request that produced the TAIPAN `fabric-info` material. Guessing at writes on
an undocumented channel is precisely how the BLAZE flash corruption happened, so no write experiments
until that arrives.

### `0xFD` is identified — it is Xear DRM, not a control channel

The vendor PDF at `docs/headset/cosmo/CM002X1 Xear Audio Technology Authentication User Guide_V1.0.pdf`
(C-Media, Rev 1.0, 2023-07-28) documents report ID `0xFD` completely, and it is **not** ours to use.

Its section 4 gives the reference report descriptor, which matches our probe byte for byte:

```
05 0C        Usage Page (Consumer)
09 01        Usage (Consumer Control)
A1 01        Collection (Application)
06 02 FF       Usage Page, Vendor Defined (0xFF02)
85 FD          Report ID (0xFD)
95 1F          Report Count (31)      -> 31 data bytes + report ID = the 32 B we measured
```

`0xFD` is a **transparent I2C bridge to a CM002X1 crypto coprocessor**. The headset firmware does not
interpret the payload; it forwards it to the chip over I2C at slave address `0x7A` and returns the
reply:

| Command | Direction | Meaning |
| --- | --- | --- |
| `0xFD 0x11 addr map len data…` | Host → Device | I2C **write** to CM002X1 |
| `0xFD 0x12 addr map len …` | Host → Device | I2C **read** from CM002X1 |
| `0xFD 0x13 addr map len data…` | Device → Host | returned read data |

Its sole purpose is licensing: C-Media's Xear software challenges the coprocessor, and a valid
response unlocks the Xear audio effects. There is no RGB, volume, or battery behind it — driving it
would only talk to a DRM chip.

**So the document narrows the question rather than answering it.** Of the two vendor reports, `0xFD`
is now fully explained and closed. **`0xBC` (14 B in / 15 B out) remains undocumented and is now the
only candidate** for RGB, device-side volume and battery.

That is itself a useful signal about *who to ask*. `0xFD` comes from C-Media's reference design, but
`0xBC` does not appear anywhere in C-Media's document — the headset OEM added it on top. The protocol
request therefore belongs with the **headset OEM (THI / Proove)**, not C-Media, and should name
report `0xBC` specifically.

**Phase 1 is unchanged.** Still no protocol for `0xBC`, so `SupportsRgb` stays false, RGB Lighting
stays hidden, and Audio uses the OS endpoint volume.

### Public sources for `0xBC`: searched, nothing exists (2026-08-05)

Checked before spending hardware time, so this is not repeated:

- **HeadsetControl** (`Sapd/HeadsetControl`), the reference implementation for reverse-engineered
  headset battery/mic protocols, supports Logitech, Corsair, SteelSeries, HyperX, ROCCAT, Audeze,
  Lenovo, Plantronics and Sony. **No C-Media / `0x0D8C` device at all.**
- No public reference to `0D8C:0334` in USB ID databases beyond "C-Media Electronics" — the PID
  itself is not catalogued.
- GitHub code search for the VID/PID pair requires authentication and no `gh` CLI is installed
  here, so that avenue is open if someone with a signed-in session wants to retry.

Conclusion: there is no public protocol to borrow. The state channel has to come from the hardware,
which is what `CosmoStatusProbe` does.

### `0xBC` report shape, decoded from the raw descriptor

```
85 BC        Report ID 0xBC
06 02 FF     Usage Page (Vendor 0xFF02)
09 01  95 01  81 02      Input:  1 byte  (usage 0x01)
09 10  95 0E  81 02      Input: 14 bytes (usage 0x10)   -> 15 data bytes in
85 BC  09 10  95 0F  91 02   Output: 15 data bytes out
```

### Status probe — `CosmoStatusProbe` (read-only)

`dotnet run --project ./src/ProoveGaming.Device.Testing -- cosmo status`

Applies the TAIPAN playbook: that dongle relays device→host pushes but ignores host→device writes,
and its battery was solved by listening rather than querying. The probe walks the user through
timed phases (idle → mic mute → power off → power on → volume → idle) and reports, per report ID,
**which byte offsets ever changed and during which phase** — so a byte that moves only while muting
is the mic flag, and a byte drifting smoothly is the battery.

### Status probe RESULT (run 2026-08-05) — `0xBC` sends NOTHING; passive listening is exhausted

Full guided run: idle → mic mute → power off → power on → volume → idle.

| Expectation | Result |
| --- | --- |
| `0xBC` pushes state like TAIPAN's dongle does | **zero frames.** Nothing, ever, on the vendor channel |
| Mic mute reaches the host | **no HID traffic at all** while muting at the headset |
| Power off/on is visible | **no traffic, and no change in USB interface count** — the dongle stays enumerated with the headset off |
| Report `0x03` | 60 frames, **volume keys only**: byte 1 bit 0 (Volume Increment) and bit 1 (Volume Decrement). The Mute bit (byte 1 bit 2) never fired |

Decoded bit layout of report `0x03` byte 1, from the raw descriptor: `0x01` Volume Increment,
`0x02` Volume Decrement, `0x04` Mute, `0x08` Play/Pause, `0x10` Next, `0x20` Previous,
`0x40` Fast Forward, `0x80` Stop.

**This is the opposite of TAIPAN.** That dongle relayed device→host pushes, which is what made its
battery readable without any host→device write. Cosmo's dongle pushes nothing, so battery, power
state and mic mute must be **polled** — a host→device write on `0xBC`, whose format we do not have.

**Consequences for the App at the time (SUPERSEDED — see "Battery: withdrawn, then re-surfaced"
below, which is the final state):**
- Battery was switched off (`SupportsBattery: false`). Later reversed.
- Power on/off is **not detectable** by any means available to us today.
- The mic-mute indicator reflects the **Windows capture-endpoint mute**, not the headset's physical
  mute switch — the switch never reaches the host. Still useful (Windows-side mute is real and
  common), but it will not follow the button on the headset. Worth stating in QA so it is not
  reported as a bug.

**Not attempted, deliberately: blind writes to `0xBC`.** `0xFD`'s documented format shows a command
byte at offset 1, so `0xBC` very likely has one too — and guessing command bytes on an undocumented
vendor channel can hit a factory-reset or firmware-update command. That is how the BLAZE flash
corruption happened. This is a decision for a human, not something to try speculatively.

**Two ways forward, both external** (see `docs/headset/cosmo/cosmo-vendor-request-2026-08-05.md`):
1. The vendor's `0xBC` command table.
2. **A USB capture of the vendor's own software** while it displays the battery.

### SOLVED 2026-08-05 — `GET_REPORT` reads `0xBC` with no writes at all

The conclusion above was too pessimistic, and the gap was a real one: **"the device pushes nothing"
is not the same as "the device cannot be read."** `HidD_GetInputReport` issues a USB
control-transfer READ of an input report. It is not a host→device command, so none of the risk of
writing undocumented opcodes applies — and the firmware answers it immediately:

```
0xBC  OK  00 00 ... 00  01 57 01 03 B4 0E 01 06  00 00 ...
                        ^16 ^17 ^18 ^19 ^20 ^21
```

Stable across repeated reads. Correlated against a guided power-off / power-on run:

| Offset | Observed | Meaning |
| --- | --- | --- |
| **19** | `0x03` <-> `0x0F` | **headset link / power state.** The ONLY byte that ever moved, and it moved exactly with power off/on |
| 16, 17, 18, 20, 21, 22, 23 | `01 57 01 . B4 0E 01 06` | **constant - not decoded** |

**Byte 19 flickered** `0F -> 03 -> 0F` once during the off phase (the dongle re-attempting a link),
so only `0x03` counts as on and any other value fails safe to off.

#### Battery: withdrawn, then re-surfaced to be observed (final state)

They were first read as battery percent (`0x57` = 87) and millivolts (`0x0EB4` = 3764, a plausible
single-cell Li-ion voltage), and were briefly shipped as such. **That was wrong.**

Re-reading on request produced byte-identical payloads every time: across roughly **50 samples over
more than an hour** - a 76-second poll at 0.7 s intervals plus a dozen one-shot reads, spanning a
full power cycle - those bytes never changed by a single bit. A live battery ADC jitters; a constant
does not. The 87 % / 3.76 V pair also disagrees with a normal Li-ion discharge curve.
*(Corrected 2026-09-03: that mismatch was read at the time as "two unrelated constants". It was
actually one constant and one real reading — 3.76 V is the battery, 87 is not. See the RESOLVED
box below.)*

The tell was there earlier and was rationalised away: the percent/voltage mismatch was noted as
"optimistic but plausible" when it should have been treated as a reason to withhold the claim until
a second data point existed. **A value that never changes is not evidence of a stable reading - it
is evidence that it is not a reading.**

It was therefore withdrawn — and then, on an explicit later decision (2026-08-05), **put back so
the value can be watched over days rather than hours**. A headset sitting on a desk barely
discharges in four hours, so "no change" is not proof of a constant either; the only way to settle
it is to leave it on screen and see whether it ever tracks a real discharge.

### DECISION: the Cosmo battery ships KNOWN-UNRELIABLE, on purpose

> **Read this before changing anything about Cosmo's battery.** The number on screen is believed to
> be wrong. It is shipped anyway, deliberately, as a time-boxed experiment. This is a decision, not
> an oversight, and it is not a bug to be fixed by removing it.

> ## ✅ RESOLVED 2026-09-03 — report 0xBC carries NO battery; the indicator is withdrawn
>
> The experiment below ended, by the second exit criterion. **Byte 17 is disproven and no longer
> published.** The deciding measurement, which four hours on a desk could never produce:
>
> | Same headset, same moment | Reading |
> | --- | --- |
> | Bluetooth (its own GATT battery service) | **40 %** |
> | Dongle report `0xBC`, byte 17 | **0x57 = 87** |
> | Dongle report `0xBC`, bytes 20-21 | **3764 mV** |
>
> A device cannot be 87 % and 40 % at once, so byte 17 is a constant and the App had been showing a
> fabricated level over the dongle — worse than showing nothing, because it looked authoritative.
>
> **The millivolts were tried next, and are also a constant.** For a few hours on 2026-09-03 the
> percentage was derived from bytes 20-21 (3764 mV ≈ 40 % on a Li-ion curve, matching Bluetooth at
> that moment). Then the headset discharged to 30 % by its own Bluetooth report and the window did
> not move a single bit — `01 57 01 03 B4 0E 01 06`, byte-identical before and after. So that
> reading was withdrawn the same day it was added.
>
> **Both candidate fields are constants. There is no battery in report 0xBC.**
>
> **What changed:** `CosmoStateReport.BatteryPercent` is now always null, and Cosmo's `Dongle2_4G`
> transport carries `SupportsBattery: false` so the indicator is hidden rather than filled with a
> guess. This is exactly the first exit criterion the plan wrote down, reached by a cross-transport
> comparison instead of a discharge log. Bluetooth is unaffected and still reports a real level —
> that is where the 40 % and 30 % came from.
>
> **The lesson, twice learned in this file:** a value that is individually plausible, sits in a
> vendor status report, and never changes is NOT evidence of a stable reading. Both wrong answers
> looked convincing and both reached users as a confident, fabricated level. Only a CORRELATED
> change against an independent source settles a field like this.
>
> **Reviving it needs the vendor:** the layout of report `0xBC`, or a battery field added to it.

**The original experiment, kept as the record of what was decided and why:**

| | |
| --- | --- |
| **Status** | ~~Shipped, **unconfirmed**, expected to be a constant~~ — resolved, see above |
| **What is shown** | ~~Byte 17 of report `0xBC` (`0x57` = 87), as a percentage~~ |
| **Confidence** | Low. It has never changed across ~58 reads over ~4 hours, including a full power cycle |
| **Why ship it** | Four hours on a desk is not a discharge. Only leaving it visible over **days** can distinguish "stable reading" from "constant" |
| **Decided** | 2026-08-05, explicitly, after it had been withdrawn once |
| **Exit criteria** | Met 2026-09-03 by the second outcome — a cross-transport comparison, not a discharge |

**Exit criteria — whoever next touches this should resolve it, not re-litigate it:**
- **If byte 17 ever moves** in step with real use (charge it, drain it, compare): it is the battery.
  Remove this warning and treat it as confirmed.
- **If it is still 87 after a genuine discharge cycle**: it is a constant. Withdraw it — set
  `SupportsBattery: false` on Cosmo's catalog entry, which hides the indicator without touching any
  code — and update this section.

**Guards already in place, so the worst case is "unknown" rather than "wrong":**
- ~~**Range-guarded to 0-100.**~~ Superseded: the voltage is guarded instead. A reading outside
  2500–5000 mV is not a cell voltage at all and reads as unknown rather than as a flat 0 %, which
  the user would see as a dead headset.
- **Unknown while the headset is off.** The dongle keeps serving the last value, so without this a
  switched-off headset would show a confident, live-looking battery. Unchanged.
- **The voltage is never used to derive the percentage.** `BatteryMillivolts` (bytes 20-21,
  `0x0EB4` = 3764) is exposed for diagnostics only. Deriving a percentage from voltage is exactly
  what made the TAIPAN battery under-report until it was replaced with the vendor's own curve.
- **`RawWindow`** keeps all eight bytes so a future vendor reply or USB capture has the measured data.

**How it reaches the UI:** `CosmoStateMonitor` polls the report every 2 s *while a dongle is
plugged in* (since 2026-09-02 it idles on the HID device-list signal otherwise — see below) and
caches it;
`DeviceService.GetBatteryLevel` answers from that cache (never by reading the device on the request
thread); `SupportsBattery` is left at its default `true` for Cosmo so the indicator shows; and
`HomeViewModel`'s 60 s battery poll keeps the card current.

**Do not revert this to no-battery without discharge evidence.** It was withdrawn once already and
that withdrawal was deliberately reversed.

#### Write-then-read was tried too, and also found nothing (2026-08-05)

The CM002X1 guide's section 3.4 describes a **two-step** exchange for the sibling report `0xFD`:
the host sends an OUT report, the firmware "gets data ready in device buffer", and the host then
issues a **GET_REPORT** to collect the reply. Every probe until this point had only done step 2 on
`0xBC` — asking for a reply without asking a question — which was a genuinely plausible explanation
for the frozen payload (a stale response buffer rather than live state).

It was tested, and the explanation does not hold:

- **Stage 1 (validation) could not run.** `GET_REPORT` on `0xFD` returns nothing at all, so the
  documented Xear I2C bridge is **not implemented on this dongle** — it exists in the report
  descriptor only. `0xBC` is the ONLY report on this device with a working `GET_REPORT` handler,
  which means the OEM implemented that one deliberately.
- **Stage 2 (the real test) came back empty.** Writing `BC <opcode> 00…` for every opcode in
  `0x01..0x30` (minus the denylist) and re-reading the buffer after each: **not one byte changed**,
  across all 47 opcodes. The device stayed fully healthy — all interfaces enumerated, audio endpoint
  OK, `0xBC` still readable.

So the constant window is not a stale buffer waiting for a request. Either the `0xBC` OUT report is
not decoded at all, or the command lives somewhere other than offset 1, or it needs a magic prefix
or checksum we cannot guess. The remaining search space (opcode position × value × payload shape)
is far too large to brute-force, and every extra guess carries the same risk for worse odds.

**Read-only avenues are now genuinely exhausted**, and this was checked rather than assumed:
no HID Power Device (`0x84`) or Battery System (`0x85`) usage page in the descriptor, zero feature
reports, `GET_REPORT` unsupported on `0x03` and `0xFD`, and the USB-Audio-class mute does not track
the headset's physical mic switch.

**Battery therefore remains unavailable.** The vendor request in
`docs/headset/cosmo/cosmo-vendor-request-2026-08-05.md` still stands, and should now also ask what
the constant window at offsets 16-23 means.

**Still not available: microphone mute.** Two independent captures (passive listen and `GET_REPORT`
polling) show no change when the mic is muted at the headset. The App's mic indicator therefore
reflects the **Windows capture-endpoint mute** only, and will not follow the headset's own switch.
That belongs in QA notes, not in a bug report.

**The write sweep was built and NOT run.** `CosmoPokeProbe` Stage B exists (zero payloads,
denylisted DFU/reset ranges, aborts on device disappearance) and stays parked: it was authorised
when read-only looked exhausted, and it no longer is.

Implementation: `CosmoStateReport` (pure parse, `ProoveGaming.Infrastructure/Classes`),
`CosmoStateReader` (GET_REPORT P/Invoke, `ProoveGaming.Infrastructure/Services`), consumed by
`DeviceService.GetBatteryLevel` alongside the existing TAIPAN branch. `SupportsBattery` is back to
its default (true) for Cosmo.

---

Confirmed 2026-08-05: the `MI_03` interface **opens for reading** (Windows does not hold it
exclusively), so passive listening is viable. Strictly no writes — if it turns out the dongle only
answers when polled, that is a finding to report, and trying writes on an undocumented channel is a
decision for a human. That is how the BLAZE flash corruption happened.

> Note on method: the probe's first verdict said "no vendor collection" because it inspected only the
> top-level collection usage. Vendor channels are routinely declared as a usage-page item *inside* a
> standard collection, so the raw-descriptor scan is the check that actually answers the question.

## Options considered

**Where volume is applied**

- **A. Per-device capability in the catalog (chosen).** A new `AudioVolumeTarget` field
  (`OperatingSystem` | `Device`, default `OperatingSystem`) alongside the existing per-device
  capabilities. Cosmo sets `OperatingSystem`. Matches how every other device difference is already
  expressed (`SupportsRgb`, `SupportedMouseSettings`, `SupportsConfiguration`) and means the next
  headset is a config edit plus one implementation, not a rewrite of the Audio section.
- **B. Always drive the OS volume, add device volume later when a device needs it.** Less code now,
  but the Audio section would hard-code an assumption the user explicitly said will not hold, and
  every call site would need revisiting.

**Where Core Audio lives**

- **A. In `ProoveGaming.App` (chosen).** The `DeviceHost` is registered with `AddWindowsService` and
  runs in **session 0**, which cannot control the interactive user's audio endpoints — routing volume
  through it would either fail or address the wrong session. Volume is also a host-OS concern, not
  device I/O, so it does not belong to the device service by responsibility either.
- **B. In `ProoveGaming.DeviceHost` behind gRPC.** Consistent with "all device state comes from the
  host", but broken for the session-0 reason above. Rejected on correctness, not taste.

**How to reach Core Audio**

- **A. Hand-rolled COM interop (chosen).** `IMMDeviceEnumerator` / `IMMDevice` /
  `IAudioEndpointVolume` declared as COM interfaces, matching the existing P/Invoke convention in
  `DeviceContainerId`. No new dependency; roughly 150 lines of well-documented, stable API surface.
- **B. NAudio (NuGet).** Less code and a friendlier API, but a **new dependency** on the App, which
  `CLAUDE.md` requires be raised explicitly rather than assumed, and it pulls in far more than the
  three interfaces needed. See Open questions — the user should confirm before `/coder` runs.

**Which endpoint to control**

- **A. Match the endpoint to the card by Container ID (chosen).** Enumerate render and capture
  endpoints, read each one's device instance / container property, and select the pair whose
  container matches the card's `deviceKey`. Precise, and Cosmo's serial-derived Container ID is
  stable, so the match survives a port change.
- **B. Match on the friendly name containing "PG Cosmo".** Trivial but fragile: it breaks on
  localisation, on a renamed endpoint, and on two identical headsets.

**Modelling the two sliders**

- **A. Reuse `MouseSettingViewModel` with new keys (chosen).** It already provides Min/Max/Value/
  `DefaultValue`/`SliderPct`/`DisplayValue` and the Settings section's slider styling. Add
  `OutputVolume` and `MicrophoneVolume` (0–100, unit `%`) to its factory. Downside: the type name
  becomes inaccurate for a headset.
- **B. Rename it to `DeviceSettingViewModel` first.** Honest naming, but touches several files and
  the XAML that binds it, for no behavioural gain. Recorded as a follow-up if the name starts
  misleading people.

## Recommended approach

Option A throughout, in three steps.

1. **Phase 0 probe** as above. Its answer decides only whether RGB Lighting appears.

2. **Capability-driven nav (groundwork, must land before or with the Cosmo entry).** Replace the
   `!IsKeyboardDevice` visibility flags with capability reads:

   | Section | Today | After |
   | --- | --- | --- |
   | Buttons | `!IsKeyboardDevice` | `Device.SupportsButtonRemap` |
   | Dpi | `!IsKeyboardDevice` | `Device.SupportsDpiStageSwitch` |
   | Settings | `!IsKeyboardDevice` | `Device.SupportedMouseSettings.Count > 0` |
   | Key assignments | `IsKeyboardDevice` | unchanged for now — it is a keyboard-shaped section with no capability flag yet |
   | Macros | `IsMacrosSupported` | unchanged |
   | Audio | — | `Device.SupportedAudioSettings.Count > 0` |

   `IsSectionAvailable` gains the same treatment so a section cannot be reached by any route it is
   not visible on. This is the change the user asked for previously ("use the capability, not
   `IsKeyboardDevice`, because we will have a lot of device types") and a headset is the case that
   makes it mandatory: without it, Cosmo shows Buttons, DPI and Settings.

3. **Cosmo onboarding.** Catalog entry (single card, `SupportsRgb: false` pending Phase 0,
   `SupportedAudioSettings: ["OutputVolume", "MicrophoneVolume"]`, `AudioVolumeTarget:
   "OperatingSystem"`), the two new proto fields carrying those to the App, an `AudioSectionView`
   modelled on the Settings section, and an `IAudioEndpointService` in the App implementing the OS
   target via COM interop. Volume values persist through the existing `device_settings` path, so
   they join preset snapshots exactly like debounce and lift-off already do.

## File-level change list

Rewritten 2026-08-05 to match what actually shipped. The original list predates the scope growth
(power state, battery, silence indicators, the polling monitors and the write-capable probes were
all added later at the user's direction) and predates the main merge. Everything below is in the
branch.

**Phase 0 — probes (`ProoveGaming.Device.Testing`; read-only unless marked)**
- `CosmoProbe.cs` — **new.** Descriptor dump + input listener for `VID 0x0D8C / PID 0x0334`.
- `CosmoStatusProbe.cs` — **new.** Guided power/battery/mic capture; proved the dongle pushes nothing.
- `CosmoPokeProbe.cs` — **new.** `GET_REPORT` reads (Stage A) plus a guarded opcode sweep
  (`poke write`, **WRITES**).
- `CosmoBridgeProbe.cs` — **new.** Documented `0xFD` I2C read, then `0xBC` request/response
  (`bridge` and `bridge bc` — **both WRITE**).
- `Program.cs` — Cosmo-only dispatch, with a per-arm table naming which arms write.

**Catalog & contract**
- `ProoveGaming.DeviceHost/appsettings.json` — Cosmo entry: VID `3468` / PID `820`,
  `DeviceType: "Wireless Headset"`, `CardType: "single"`, PNG image path, one **`Dongle2_4G`**
  transport, `SupportedAudioSettings`, `AudioVolumeTarget`, `SupportsRgb: false`.
- `Infrastructure/Classes/DeviceGroupConfig.cs` — `SupportedAudioSettings`, `AudioVolumeTarget`,
  `SupportsBattery`.
- `Infrastructure/Classes/DeviceMap.cs` — the same three on the flattened row.
- `Infrastructure/Classes/DeviceMapExpander.cs` — copies all three per transport, cloning lists.
- `GrpcDependencies/Protos/deviceservice.proto` — **contract change.**
  `supported_audio_settings = 32`, `audio_volume_target = 33`, `battery_unsupported = 34`.
  *(31 went to main's `supports_actuation` during the merge; the audio fields moved, because field
  numbers are the wire contract and main's side was already released.)*
- `DeviceHost/Services/HidDeviceMapRegistry.cs` — maps all three into the catalog entry.

**Device state (power + battery)**
- `Infrastructure/Classes/CosmoStateReport.cs` — **new.** Parses report `0xBC`: headset on/off
  (byte 19), battery percent (derived from the millivolts at bytes 20-21 — byte 17 was disproven
  2026-09-03, see the DECISION section), millivolts, raw window.
- `Infrastructure/Services/CosmoStateReader.cs` — **new.** `HidD_GetInputReport` P/Invoke — a
  control-transfer READ; performs no writes.
- `DeviceHost/Services/CosmoStateMonitor.cs` — **new.** Polls power state every 2 s with a two-poll
  confirmation debounce, caches the last report, raises `PowerStateChanged`.
  **Amended 2026-09-02** (`docs/audits/2026-09-02-device-association-memory-audit.md`, C12): the 2 s
  cadence now applies only while a Cosmo dongle is actually present. With none plugged in the loop
  waits on `IHidDeviceProvider.SubscribeToDeviceListChanged` with a 30 s fallback, instead of
  enumerating every 2 s for the life of the service on machines that have never seen a Cosmo. The
  headset's switch is still invisible to USB, so polling remains the only option while a dongle *is*
  present.
  **Amended 2026-09-30:** a Cosmo whose power state has not been read yet is **hidden**, not shown
  (`IsPoweredOff` → `IsPresentable`). Treating "unknown" as on published every freshly plugged
  `0x0D8C:0x0334` device — a stock C-Media id, so also third-party headsets — as a connected Cosmo,
  and the first poll then hid it again: a ~1 s "connected" blink seen in a customer video.
- `DeviceHost/Services/DeviceService.cs` — `PresentableDevices` hides a Cosmo whose headset is not
  confirmed on, on **all four** publication paths; `GetBatteryLevel` answers from the monitor's cache.
- `DeviceHost/Program.cs` — registers `CosmoStateMonitor` as singleton + hosted service.

**Capability-driven nav**
- `App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — nav flags read capabilities instead of
  `!IsKeyboardDevice`; `IsAudioNavVisible` / `IsAudioSection`; `IsStackNavLayout` (layout chosen by
  shape, so a headset gets the single-image frame); `IsSectionAvailable` rewritten as a switch
  covering Audio **and** main's Actuation.
- `App/ViewModels/NavSection.cs` — adds `Audio`, alongside main's `Actuation`.

**Audio section**
- `App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Audio.cs` — **new partial.** Builds both
  sliders, loads live-then-persisted, debounced apply, live endpoint tracking (gated on the Audio
  section being visible), rebuilds serialised by version.
- `App/ViewModels/DeviceDetails/MouseSettingViewModel.cs` — `OutputVolume` / `MicrophoneVolume`
  (0-100, `%`).
- `App/ViewModels/AudioVolumeTarget.cs` — **new.** `OperatingSystem` | `Device` plus a parser.
- `App/Services/IAudioEndpointService.cs` + `AudioEndpointService.cs` — **new.** Hand-rolled Core
  Audio COM interop, matched by Container ID and never falling back to the default endpoint;
  combined state reads; diagnostic logging on every failure path.
- `App/Services/AudioMuteMonitor.cs` — **new.** Polls silence state for every audio-capable card.
- `App/Services/ISettingsRepository.cs` + `SettingsRepository.cs` — `audio.outputVolume` /
  `audio.micVolume`; an absent key stays null rather than reading as 0.
- `App/Models/Settings/MouseSettingsPayload.cs` — adds `AudioSettingsPayload` (nullable fields).
- `App/Views/DeviceDetails/Sections/AudioSectionView.axaml` (+ `.axaml.cs`) — **new**, modelled on
  the lift-off slider.
- `App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — `StagePresetDirtyOnly` (audio
  marks the preset dirty **without** a device write); audio restored on preset apply;
  reapply-on-connect skipped for a device with no configurable state.

**Card, indicators and artwork**
- `App/ViewModels/DeviceCardViewModel.cs` — audio settings, volume target, `SupportsBattery`,
  `IsOutputSilent` / `IsMicrophoneSilent`, `HasAudioIndicators` (requires a live connection),
  `IsRasterImage`.
- `App/ViewModels/HomeViewModel.cs` — populates the new catalog fields in **both** fill methods;
  60 s battery refresh; drives `AudioMuteMonitor` from both discovery paths and stops it on teardown;
  battery indicator gated on `SupportsBattery`.
- `App/Views/SingleCard.axaml` — raster artwork branch; audio/mic indicators with separators.
- `App/Views/DeviceDetails/Sections/ConnectivityBarView.axaml` — the same indicators, battery last.
- `App/Views/DeviceDetails/DeviceDetailsView.axaml` — Audio nav button + section host; layout panels
  gated on `IsStackNavLayout`; RGB glow gated on lighting support; raster image branch.
- `App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — raster/vector image selection; glow
  animation skipped for a device without lighting.
- `App/Assets/Headphones/Cosmo-device-standard.png` — **new** artwork (PNG, not SVG).
- `App/App.axaml.cs` — registers `IAudioEndpointService` and `AudioMuteMonitor`.

**Localization**
- `App/Resources/Strings.en.axaml` + `Strings.uk.axaml` — Audio nav label plus the two slider labels
  and subtitles. *(These live in `ProoveGaming.App/Resources`, not `ProoveGaming.Common` as the
  original plan said — the App has its own `LocalizationManager` and resource files.)*

**Carried in from the main merge**
- `App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs` — culture fix: the four
  `N.NN mm` displays format invariantly. A pre-existing bug on main, not part of Cosmo.
- `App/Views/DeviceDetails/Sections/DeviceImageWithHotspotsView.axaml` — glow gated on lighting.

**Tests**
- `tests/ProoveGaming.Infrastructure.Tests/CosmoStateReportTests.cs` — **new.** Report parsing, power
  state, battery guards, truncated payloads.
- `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs` — audio + battery fields copied
  per transport, lists cloned, sane defaults.
- `tests/ProoveGaming.App.Tests/DeviceDetailsNavVisibilityTests.cs` — **new.** Per-family visible
  sets, layout selection, and a `[Theory]` over all eight `Is*Section` notification flags.
- `tests/ProoveGaming.App.Tests/DeviceDetailsAudioTests.cs` — **new.** Slider construction, volume
  target, persistence, disconnected device.
- `tests/ProoveGaming.App.Tests/DeviceDetailsAudioPresetTests.cs` — **new.** Audio marks the preset
  dirty, does **not** write to the device, and a saved preset restores its levels.
- `tests/ProoveGaming.App.Tests/DeviceCardAudioIndicatorTests.cs` — **new.** Indicator notification,
  connection gating, and the mute × volume × null silence matrix.
- `tests/ProoveGaming.App.Tests/SettingsRepositoryTests.cs` — absent audio keys stay null.
- `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` — culture `[Theory]` from
  the merge fix above.

**Docs**
- `docs/CONTEXT.md` — glossary: Cosmo, Cosmo status report, CM002X1, Xear, Audio volume target,
  SupportedAudioSettings, SupportsBattery.
- `docs/device-identity.md` — Cosmo is the first device tested that reports a real `iSerialNumber`.
- `docs/headset/cosmo/` — the C-Media PDF and the vendor request for report `0xBC`.

## Acceptance criteria

- Given Cosmo plugged in → launch the App → a single Cosmo card appears with its artwork, and it
  shows as connected.
- Given Cosmo unplugged while the App runs → its card becomes disabled/disconnected like a mouse's,
  within the same interval.
- Given Cosmo's details view → exactly four nav items are visible: Presets, RGB Lighting (only if
  Phase 0 proved control; otherwise three), Audio, Firmware. Buttons, DPI, Settings and Macros are
  **not** visible.
- Given a mouse's details view after the nav refactor → Buttons, DPI, Settings, Macros, Presets, RGB
  and Firmware are visible exactly as before.
- Given a keyboard's details view after the nav refactor → its sections are unchanged from today.
- Given the Audio section → two sliders, Output Volume and Microphone Volume, each 0–100 %.
- Given Output Volume moved to 30 % → the Windows volume for the "Headphones (PG Cosmo)" endpoint
  reads 30 %, and no other device's volume changes.
- Given Microphone Volume moved to 80 % → the "Microphone (PG Cosmo)" endpoint reads 80 %.
- Given a volume changed, then the details page closed and reopened → the slider shows the value that
  was set, read back from the endpoint.
- Given Cosmo moved to a different USB port → it is still the same card with the same settings (its
  serial makes the Container ID stable).
- Given a preset saved on Cosmo with specific volumes, then volumes changed, then the preset
  re-selected → the saved volumes are restored.
- Given Cosmo unplugged → moving a volume slider does not throw and does not change any other
  device's volume.

**Added after the original plan (power state, indicators, battery, presets):**
- Given the headset switched **off** while the dongle stays plugged in → its card greys out within
  ~2-4 s, exactly as an unplugged mouse does. Switching it back on restores it.
- Given the dongle plugged in with the headset **off** (or any other `0x0D8C:0x0334` device plugged
  in) → the Cosmo card **never** shows as connected, not even briefly. (Added 2026-09-30.)
- Given the headset switched off → **no battery percentage is shown** (the dongle keeps serving a
  stale one, which must read as unknown rather than as a live level).
- Given a connected Cosmo → the card **and** the details connectivity bar show the same icon row,
  separated by rules, in the order: connection │ audio │ mic │ battery.
- Given a disconnected Cosmo → **no** status icons are shown at all, including the audio/mic pair.
- Given either volume set to 0, or the endpoint muted in Windows → the matching icon switches to its
  "off" form. Muted-at-40 % and unmuted-at-0 % both count as silent.
- Given the headset's own +/- buttons pressed while the Audio section is open → the Output slider
  follows the level Windows reports (measured: they step by 6).
- Given a volume changed → **Save Preset becomes enabled**, and no device configuration frame is
  sent (audio is an OS-level change; nothing is written to the headset).
- Given the headset is on the **dongle** → **no battery indicator is shown at all**. Report `0xBC`
  carries no battery (see the DECISION section), so the `Dongle2_4G` transport sets
  `SupportsBattery: false`. An indicator appearing there is a defect.
- Given the headset is on **Bluetooth** → a real percentage IS shown, from the headset's own GATT
  battery service, and tracks a real discharge.

## Test strategy

New feature → the coverage below is required, not optional.

`tests/ProoveGaming.Infrastructure.Tests`
- `DeviceMapExpanderTests` — `SupportedAudioSettings` and `AudioVolumeTarget` are copied to every
  transport row; the list is cloned, not aliased; both default sanely when the device declares
  neither (mirrors the existing `SupportsConfiguration` tests).

`tests/ProoveGaming.App.Tests`
- **Nav visibility per device type** — the highest-risk part of this change, because it alters
  existing devices. One test per family asserting the exact visible-section set: mouse (Buttons,
  Dpi, Settings, Macros, Presets, Rgb, Firmware), keyboard (unchanged from today), headset
  (Presets, Audio, Firmware, plus Rgb only when `SupportsRgb`). Plus `IsSectionAvailable` refusing a
  section that is not visible, so it cannot be reached by another route.
- **Audio sliders** — built only when `SupportedAudioSettings` is non-empty; correct Min/Max/unit;
  absent for a device that declares none.
- **Volume target** — with `AudioVolumeTarget = OperatingSystem`, moving a slider calls the endpoint
  service; with `Device`, it does **not** (the not-implemented path must be inert, not silently
  wrong). Uses a fake `IAudioEndpointService`.
- **Persistence** — a volume change writes `device_settings`, and the value is restored on reload;
  a saved preset captures and restores both volumes, exercised through the existing preset snapshot
  tests so audio joins the same comparison as debounce and lift-off.
- **Disconnected device** — moving a slider with no endpoint resolved does not throw.

**Added after the original plan** — all implemented, see the Tests block of the file-level list:
- **Section flag notification** — a `[Theory]` over all eight `Is*Section` flags. A section whose
  flag is not raised by the `ActiveSection` setter can never become visible; that is what left the
  Audio section blank, and testing Audio alone would not have prevented the next one.
- **Card indicators** — `HasAudioIndicators` must *notify*, because the catalog fills the list after
  the card is constructed; plus connection gating and the mute × volume × null silence matrix.
- **Audio presets** — a volume change marks the preset dirty, does **not** write to the device
  (guarding the vendor HID channel), and a saved preset restores its levels through the real
  `SelectPresetCommand` path.
- **Report parsing** — `CosmoStateReport` power state, the battery range guard, unknown-while-off,
  and truncated payloads.
- **Settings** — an absent audio key stays null rather than reading as 0.

**Deliberately not automated:** real Core Audio calls against a physical endpoint (the service is
faked at the interface boundary; the real implementation is covered by QA), the Phase 0 probe, and
anything requiring the headset to be plugged in.

**Known gap:** `CosmoStateMonitor` has no tests — its two-poll confirmation debounce, the
"first reading is adopted immediately" rule and the clear-on-unplug path all decide whether a card
shows as connected. There is no `tests/ProoveGaming.DeviceHost.Tests` project, so covering it needs
either the logic extracted or a new project. Note `dotnet-ci.yml` builds only the App csproj and
does not run `tests/`, so this coverage is local-only.

## QA (manual test steps)

1. Plug in the Cosmo headset and launch the app. **Expected:** a Cosmo card appears with its picture
   and shows as connected.
2. Open the Cosmo card. **Expected:** the left menu shows Presets, Audio and Firmware (and RGB
   Lighting only if lighting control was confirmed). There is no Buttons, DPI or Settings entry.
3. Open Audio and drag Output Volume to about 30 %. Open the Windows volume mixer. **Expected:** the
   Cosmo headphones sit at roughly 30 %, and your speakers or other devices are untouched.
4. Drag Microphone Volume to about 80 %, then open Windows Sound settings → Input → PG Cosmo.
   **Expected:** the microphone level reads about 80 %.
5. Leave the Cosmo page and come back. **Expected:** both sliders show the values you set.
6. Press the +/- buttons on the headset itself. **Expected:** the Windows volume changes as it always
   has — this is not something the app controls.
7. Create a preset, set both volumes to distinctive values and save it. Change both volumes, then
   re-select the preset. **Expected:** the saved volumes come back.
8. Unplug the headset. **Expected:** the card greys out like a disconnected mouse does, and the app
   keeps working with no error.
9. Plug the headset into a **different USB port**. **Expected:** the same card comes back with your
   settings — not a second card.
10. Open a mouse and a keyboard and check their menus. **Expected:** unchanged from before this
    release — this is the main regression risk.
11. With the dongle still plugged in, switch the **headset itself off**. **Expected:** within a few
    seconds the Cosmo card greys out as if unplugged, and its battery percentage disappears. Switch
    it back on — the card returns.
12. Look at the Cosmo card on the home screen and at the icon row inside its details page.
    **Expected:** identical icons in both, separated by vertical rules, ordered
    connection │ audio │ mic │ battery.
13. Drag Microphone Volume to 0, then mute the Cosmo microphone in Windows Sound settings.
    **Expected:** the mic icon shows its "off" form in both cases.
14. Press the +/- buttons on the headset while the Audio section is open. **Expected:** the Output
    slider moves to follow, in steps of about 6.
15. Change a volume. **Expected:** Save Preset becomes enabled. Save it, change the volume again,
    then re-select the preset — the saved levels come back.
16. **Battery:** on the **dongle**, expect **no battery indicator at all** — the vendor report
    carries none, and both fields that looked like one were disproven (see the DECISION section).
    An indicator appearing on the dongle is a defect. On **Bluetooth**, expect a real percentage
    that tracks a genuine charge/discharge.

## Review focus

- `gRPC contract` — two new `DeviceCatalogEntry` fields; confirm the numbers are unused, that the
  DeviceHost populates both, and that both `FillConnectionCard` and `FillBluetoothCard` consume them.
- `DI lifetimes` — `IAudioEndpointService` registration, and that it is not captured by a longer-lived
  singleton in a way that outlives the device.
- `resource disposal` — Core Audio interop returns COM objects (`IMMDeviceEnumerator`, `IMMDevice`,
  `IAudioEndpointVolume`); every one must be released, including on the failure paths.
- `UI-thread affinity` — slider changes must not block the UI thread while talking to Core Audio.

## Performance & quality

No hot path. Volume changes are user gestures and should be debounced the same way the existing
sliders are (`ApplyMouseSettingsAsync`'s 300 ms pattern) so dragging a slider does not issue a COM
call per pixel.

Two things to get right rather than fast:
- **COM lifetime.** Every interop object must be released deterministically; a leaked
  `IAudioEndpointVolume` holds an endpoint reference for the process lifetime. Prefer resolving,
  using and releasing inside one method over caching an endpoint across calls.
- **Endpoint resolution cost.** Enumerating endpoints on every slider tick would be wasteful; resolve
  once per device open and re-resolve on a device-change event, not per change.

## Risks

- **The nav refactor changes existing devices.** Getting a capability wrong hides a section a mouse
  or keyboard needs. This is the highest-risk part of the change and why per-family nav tests are
  required rather than suggested.
- **Core Audio in a service context.** If volume is ever moved into the DeviceHost it will silently
  target the wrong session; the plan puts it in the App deliberately and this should not be
  "simplified" later without understanding why.
- **Endpoint matching.** If the Container ID match fails, the code must do nothing rather than fall
  back to the *default* endpoint — falling back would change the user's speakers instead of their
  headset, which is a bad and confusing failure.
- **Phase 0 found a vendor channel** (usage page `0xFF02`), so RGB and device-side volume are live
  possibilities rather than dead ends. `0xFD` has since been identified as the Xear DRM bridge and is
  closed; **`0xBC` is the open candidate**. This does not invalidate Phase 1 — we still have no
  protocol — but the RGB section should be treated as "hidden pending protocol", not "impossible",
  and nobody should quietly delete the capability plumbing.
- **`MouseSettingViewModel` for a headset** is a naming smell that will mildly mislead the next
  reader; accepted deliberately over a rename that touches unrelated files.

## Open questions

- ~~**Core Audio: hand-rolled COM interop or NAudio?**~~ **Decided 2026-08-05: hand-rolled COM
  interop.** No new dependency, and it matches the existing `DeviceContainerId` P/Invoke convention.
  The cost is that we own the COM release discipline — see the `resource disposal` review focus.
- ~~**Is Cosmo's USB device the dongle, or the headset itself?**~~ **Resolved 2026-08-05: it is the
  dongle.** The catalog transport is `Dongle2_4G`, not `Wired`.
- ~~**Should Cosmo show a battery indicator?**~~ **Resolved 2026-08-05: yes, provisionally.** It
  first read as "no" — the `Dongle2_4G` transport switches the indicator on and, with no level to
  fill it, the bar renders a **red "critical" battery**, which reads as flat rather than as unknown.
  That is what the per-device [[SupportsBattery]] flag (default true) was added for. The decision was
  then reversed: byte 17 is surfaced as the percentage so it can be observed over days. Cosmo no
  longer sets `SupportsBattery`; **no device does today**, and the flag remains for the next wireless
  device that genuinely cannot report a level. Cosmo keeps its indicator: the level is derived from
  the cell voltage (resolved 2026-09-03 — see the battery section above).
- **Vendor protocol request — now narrowed to report `0xBC`.** Phase 0 proved a `0xFF02` channel
  exists, and the C-Media PDF has since identified `0xFD` as the Xear DRM I2C bridge (closed, not
  useful to us). **`0xBC` is the only remaining candidate** for RGB, device-side volume and battery.
  Since `0xBC` is absent from C-Media's reference design, ask the **headset OEM (THI / Proove)**, not
  C-Media, and name report `0xBC` explicitly. Worth raising now — long turnaround.

## Review findings

### Round 1 — Ship: no

#### Plan conformance
Plan: `docs/plans/add-cosmo-headset/add-cosmo-headset-plan.md`

- **Implemented as planned:** Phase 0 probe; capability-driven nav + `NavSection.Audio`; catalog
  entry; `SupportedAudioSettings` / `AudioVolumeTarget` threaded through DeviceGroupConfig →
  DeviceMap → expander → proto (31/32) → registry → card → both `Fill*Card` methods;
  `MouseSettingViewModel` factory entries; `DeviceDetailsViewModel.Audio`; `AudioSectionView`;
  hand-rolled Core Audio interop registered in `BuildServices()`; `SettingsRepository` audio keys;
  en/uk strings; `docs/CONTEXT.md` and `docs/device-identity.md` updates. Every File-level change
  list item is present.
- **Deviates from plan** (scope added after the plan was written, at the user's direction): battery
  reporting, `SupportsBattery` + `battery_unsupported` (field 33 at the time, renumbered to 34 by the main merge), `CosmoStateReport` / `CosmoStateReader`,
  `CosmoStateMonitor` power-state polling and device-list filtering, `AudioMuteMonitor`, the silence
  indicators on the home card, live volume tracking, the 60 s battery poll, and the
  `CosmoStatusProbe` / `CosmoPokeProbe` / `CosmoBridgeProbe` probes. All legitimate, none described
  by the plan — see **S4**.
- **Acceptance criteria:** every code-side criterion is met. The endpoint-level ones (30 % / 80 %
  reaching the real Windows endpoints, port-move identity) remain QA-only, as the plan intends.

#### Code

- [x] **B1. The branch does not compile at HEAD** — `tests/ProoveGaming.App.Tests/DeviceDetailsAudioPresetTests.cs:32` — Blocker
  **Problem:** the `CountingHid` fake implements only part of `IUsbHidDeviceService`. The ten missing
  members exist **only as an uncommitted working-tree change**.
  **Why it blocks:** `git checkout feat/PG-533-Cosmo-device && dotnet build ./src/ProveGaming.slnx`
  fails with ten `CS0535` errors (`SetMouseSettingsAsync`, `SetDpiStagesAsync`,
  `SetActiveDpiStageAsync`, `SetReportRateAsync`, `GetButtonBindingsAsync`, `SetKeyMappingAsync`,
  `ProbeKeyMatrixAsync`, `ReadKeyMatrixAsync`, `SetMacrosAsync`, `ReportRateChanged`). CI and any
  fresh clone get a red build.
  **Solution options:** A — commit the working-tree change; it is complete, and with it the suite is
  green (466 Infrastructure + 574 App). B — none; this is simply unfinished work.

- [x] **S1. `SavedPreset_RestoresTheVolumesItWasSavedWith` asserts the opposite of its name** — `tests/ProoveGaming.App.Tests/DeviceDetailsAudioPresetTests.cs` — Should-fix
  **Problem / why it matters:** the test saves 25/70, saves the preset, drifts to 90/90, then asserts
  **90/90**. It passes, but it proves the volumes were *not* restored.
  `RebuildAudioSettingsAsync(forcePersisted: true)` reloads the **live** `device_settings` rows —
  which now hold 90 — it does not read the preset snapshot. A real restore runs
  `ApplyPresetToDeviceAsync` → `LoadPresetSnapshotToDevice` (which overwrites the live rows from the
  snapshot) and only then rebuilds. So the plan's required coverage — *"a saved preset captures and
  restores both volumes"* — is **not met**, and the name creates false confidence that it is.
  **Fix:** drive the real preset-apply path and assert 25/70. Confirm the test fails when
  `RebuildAudioSettingsAsync(forcePersisted: true)` is removed from that path.

- [x] **S2. The probe safety header is factually wrong** — `src/ProoveGaming.Device.Testing/Program.cs:22` — Should-fix
  **Problem / why it matters:** the header states *"Everything except `poke write` is STRICTLY
  READ-ONLY."* That is untrue. `bridge` sends a host→device OUT report at `CosmoBridgeProbe.cs:108`
  (Stage 1) **before** any `bc` argument is considered, and `bridge bc` writes 47 opcodes at
  `CosmoBridgeProbe.cs:159`. On a device whose vendor channel is undocumented, an incorrect
  "this is read-only" claim is the most dangerous comment in the branch: it invites exactly the
  casual run that the rest of this work is careful to prevent.
  **Fix:** name both write-capable arms (`poke write` **and** `bridge` / `bridge bc`) in the header.

- [x] **S3. Endpoint resolution on every call, against the plan's explicit guidance** — `src/ProoveGaming.App/Services/AudioEndpointService.cs:121` — Should-fix
  **Problem / why it matters:** `TryResolveEndpointVolume` performs `CoCreateInstance` + a full
  `EnumAudioEndpoints` + an `OpenPropertyStore`/`GetValue` per enumerated endpoint on **every**
  get/set. With a Cosmo details page open, `TrackEndpointVolumeAsync` (1 s × 2 reads) and
  `AudioMuteMonitor` (2 s × 4 reads) together drive roughly **four full endpoint enumerations per
  second, continuously**. The plan's Performance & quality section says the opposite: *"resolve once
  per device open and re-resolve on a device-change event, not per change."*
  **Fix:** A (correct) — cache the resolved endpoint per container ID and invalidate on a
  device-list change; also fold the two pollers together, since `AudioMuteMonitor` already reads the
  very volumes the details VM re-reads. B (cheap) — lengthen the volume-tracking interval and have
  `AudioMuteMonitor` publish the level it already has.

- [x] **S4. The plan now contradicts the shipped battery behaviour** — plan lines 244-266 and 586-590 — Should-fix
  **Problem / why it matters:** the plan states in three places that `CosmoStateReport` exposes **no
  battery fields**, that the `GetBatteryLevel` branch was removed, and that `SupportsBattery: false`
  is in the catalog. The code does the opposite: `CosmoStateReport.BatteryPercent` (byte 17) is
  exposed, `DeviceService.GetBatteryLevel` answers from it, and `SupportsBattery` is absent from
  Cosmo's entry (so it defaults to true). The **code is intended** — it follows a later decision to
  surface 87 % and observe it over time — so the plan is what is stale. Left as-is, the next reader
  has explicit written instructions to "fix" working code back.
  **Fix:** rewrite the Correction section and the resolved Open question to record the current
  decision, keeping the evidence that the value is still unconfirmed.

- [x] **S5. Reapply-on-connect makes a pointless round-trip for Cosmo** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:472` — Should-fix
  **Problem / why it matters:** `ReapplyLastCustomPresetAsync` passes Cosmo's guards
  (`SupportsConfiguration` defaults true; not a keyboard) and — once a custom preset exists, which
  **this branch newly enables** for Cosmo — calls `ApplyDeviceConfigurationAsync` with LED, bindings,
  mouse settings, DPI stages and rate all null on every headset power-on.
  **Verified not to reach the hardware:** `DeviceService.ApplyDeviceConfiguration:331` resolves
  `CreateForDevice(...) as IMouseRgbService`, gets null for VID `0x0D8C`, and returns
  `Success = false` with no HID write. So this is wasted work and a warning per power cycle, not a
  device risk — but it is the same class of call that `StagePresetDirtyOnly` was introduced to avoid.
  **Fix:** skip the reapply when the card exposes no configurable capability (no RGB, no button
  remap, no mouse settings, no DPI stages).

- [x] **S6. A 2.5 MB unused asset is committed and embedded** — `src/ProoveGaming.App/Assets/Headphones/Cosmo-device.png` — Should-fix
  **Problem / why it matters:** only `Cosmo-device-standard.png` (996 KB) is referenced
  (`appsettings.json:402`). `Cosmo-device.png` is referenced nowhere, but the App csproj embeds
  everything under `Assets` via `<AvaloniaResource Include="Assets\**" />`, so 2.5 MB of dead weight
  ships inside the App binary and therefore inside the single-file installer payload.
  **Fix:** delete it, or reference it if it is the intended details-page artwork.

#### Tests
- [x] Audio preset restore is **not actually covered** — see **S1**. This is the one item in the
  plan's required Test strategy with no working coverage.
- [ ] `CosmoStateMonitor` is untested: the two-poll confirmation debounce, the "first reading is
  adopted immediately" rule, and the clear-on-unplug path all decide whether a card shows as
  connected. There is no `tests/ProoveGaming.DeviceHost.Tests`, so this needs the logic extracted or
  a new test project.
- [x] `AudioMuteMonitor.IsSilent` is `internal` and trivially testable (mute × volume × null matrix)
  but has no test — `tests/ProoveGaming.App.Tests`.

#### Nits
- `MouseSettingViewModel` naming for headset audio — accepted in the plan, still mildly misleading.
- `AudioMuteMonitor` is named for mute but now also reads volume levels; `AudioIndicatorMonitor`
  would describe what it actually does.

#### What's done well
- **COM lifetime is correct.** Every `IMMDeviceEnumerator` / `IMMDeviceCollection` / `IMMDevice` /
  `IPropertyStore` / `IAudioEndpointVolume` is released in a `finally`, including on failure paths,
  and `PropVariantClear` runs even for types the code does not read. This was the plan's top review
  focus and it holds up under inspection.
- **The default-endpoint trap is avoided by construction.** `TryResolveEndpointVolume` returns null
  when no container matches rather than falling back to the default endpoint — exactly the failure
  the plan's Risks section calls out, enforced in code rather than by comment.
- **The nav refactor is properly pinned.** Per-family visible-section sets plus a `[Theory]` across
  all eight `Is*Section` flags, added after the missing `IsAudioSection` notification was found —
  that test would have caught the original bug.
- **The unconfirmed battery is handled honestly:** range-guarded to 0-100, reported as unknown while
  the headset is off (the dongle serves a stale value), and documented with the evidence on both
  sides instead of being presented as fact.
- **Audio staging deliberately avoids the vendor channel.** `StagePresetDirtyOnly` marks the preset
  dirty without emitting a configuration frame, and `ChangingAVolume_DoesNotWriteToTheDevice` is a
  real negative test guarding it.
- **The protocol investigation records its negative results** — the `0xFD`/Xear identification, the
  empty passive capture, and the failed opcode sweep are all written down so nobody repeats them.

#### Verdict
Ship: **no** — B1 alone: the branch does not compile as committed, and the fix is sitting
uncommitted in the working tree. Everything else is a follow-up, but S1 matters because the plan's
required preset coverage is currently a test that asserts the opposite of its name.
First: (1) commit the test-fake completion, (2) fix S1 so preset restore is genuinely exercised,
(3) correct the S2 safety header before anyone runs `bridge`.

Plan ready. Invoke /coder to implement.

### Round 1 — fixes applied

- **B1** closed: the completed `CountingHid` fake is committed, so the branch compiles from a clean
  checkout.
- **S1** closed, and it turned out to be a *test* fault twice over, not a product fault. The rewrite
  drives `SelectPresetCommand` (the real path: `ApplyPresetToDeviceAsync` →
  `LoadPresetSnapshotToDevice`) and asserts 25/70. Getting there exposed that
  `SavePresetCommand` only **opens the save dialog** — `ConfirmSavePresetCommand` is what writes the
  snapshot — so the original test saved nothing at all. Verified the new test genuinely fails
  (`Expected 25, Actual 90`) when `RebuildAudioSettingsAsync(forcePersisted: true)` is removed from
  the preset-apply path.
- **S2** closed: `Program.cs` now carries a per-arm write/read table naming `bridge` and `bridge bc`
  as writers alongside `poke write`.
- **S3** partially closed: volume tracking is now gated on the Audio section being visible, so the
  steady-state cost drops from ~4 endpoint enumerations/second to the mute monitor's alone whenever
  the user is not on that page. **Remaining follow-up:** cache the resolved endpoint per container
  ID and fold `AudioMuteMonitor`'s volume reads together with the details tracker, so an open Audio
  page costs one resolution per direction rather than one per read.
- **S4** closed: the plan's battery section and the resolved Open question now record the shipped
  behaviour (byte 17 surfaced, range-guarded, unknown while off) and explicitly warn against
  reverting it to no-battery without discharge evidence.
- **S5** closed: `ReapplyLastCustomPresetAsync` returns early when the card has no configurable
  state (no RGB, no button remap, no mouse settings, no DPI stages), so Cosmo no longer makes the
  all-null round-trip on power-on.
- **S6** closed: the unused 2.5 MB `Cosmo-device.png` is deleted.
- **Tests** added: `AudioMuteMonitor.IsSilent` now has a mute × volume × null matrix, including the
  volume-0 case that originally left the live Mic icon showing.

**Still open (carried, not fixed):** the S3 caching follow-up above, and `CosmoStateMonitor` has no
tests — its confirmation debounce and clear-on-unplug logic decide whether a card shows as
connected, and there is no `tests/ProoveGaming.DeviceHost.Tests` project to host them.

Suite after fixes: **466 Infrastructure + 581 App, all passing.**

### Round 2 — Ship: yes

Reviewed after `origin/main` was merged in (keyboard actuation + Smart Infinite macro work).
Surface: 5 commits, 0 behind main, clean tree, no untracked files — the three-dot diff is the whole
delta. Verified from a **fresh worktree at HEAD**: `dotnet build` succeeds with no new warnings and
`dotnet test` is **529 Infrastructure + 597 App, all passing**.

#### Round 1 items — all closed, verified individually
- **B1** (branch did not compile) — closed. A clean `git worktree` checkout of HEAD builds and the
  full suite runs green, which is the check that actually matters rather than "it builds here".
- **S1** (preset-restore test asserted the opposite of its name) — closed, and the rewrite is
  genuinely stronger than asked. It drives `SelectPresetCommand` **and** `ConfirmSavePresetCommand`;
  the fix surfaced that `SavePresetCommand` only opens the dialog, so the original test had never
  saved anything at all. Confirmed to fail (`Expected 25, Actual 90`) when the restore is removed.
- **S2** (probe header claimed everything but `poke write` was read-only) — closed; `Program.cs` now
  carries a per-arm table naming `bridge` and `bridge bc` as writers.
- **S3** (endpoint resolution per call) — **partially** closed, as recorded. Volume tracking is now
  gated on `IsAudioSection`, and the `SyncVolumeTrackingTimer()` call survived inside the conflicted
  `ActiveSection` setter. The caching follow-up is still open — carried below.
- **S4** (plan contradicted shipped battery) — closed; the plan records byte 17 as
  surfaced-but-unconfirmed and warns against reverting without discharge evidence.
- **S5** (pointless reapply round-trip) — closed via the `hasConfigurableState` guard.
- **S6** (2.5 MB unused asset) — closed; only `Cosmo-device-standard.png` remains.

#### Merge conformance — nothing lost from either side
This was the main risk and it was checked mechanically, not by eye:
- **No file main had is missing from HEAD** (`git diff --diff-filter=D origin/main HEAD` is empty),
  and all five of main's new actuation / Smart Infinite files are present.
- **No line of main's was removed** in the auto-merged overlap files. Every `-` line in
  `git diff origin/main HEAD` over `DeviceService.cs`, `HidDeviceMapRegistry.cs`, `Program.cs`,
  `appsettings.json`, `DeviceGroupConfig/DeviceMap/DeviceMapExpander.cs`, `DeviceCardViewModel.cs`
  and `HomeViewModel.cs` is an intentional edit from this branch, not lost actuation code.
- **Proto collision resolved correctly.** `supports_actuation = 31` (already on main, so the wire
  contract is fixed) kept its number; the audio fields moved to 32-34. No duplicate field numbers in
  `DeviceCatalogEntry`.
- **Both feature sets survive the whole pipeline**: `DeviceMapExpander` copies `SupportsActuation`
  *and* the audio fields + `SupportsBattery`; `HidDeviceMapRegistry` maps all of them; and
  `HomeViewModel` populates all of them in **both** `FillConnectionCard` and `FillBluetoothCard` —
  the classic spot where a merge drops one side silently.
- **The nav conflict was resolved the right way round.** Main branched before the capability-driven
  nav refactor, so its side of the conflict restores `IsDpiNavVisible => !IsKeyboardDevice`.
  Accepting "theirs" would have handed the Cosmo headset back the Buttons/DPI/Settings sections —
  the exact bug the refactor exists to prevent. The capability reads were kept and
  `NavSection.Actuation => IsActuationNavVisible` folded into the `IsSectionAvailable` switch, which
  is behaviourally identical to main's if/list form for keyboards.
- **Nav backdrop sizing is correct for both.** `KeyboardNavButtonsCount` counts Actuation (keyboard
  panel, `KeyboardActionsMenuHeight`); `VisibleNavButtonsCount` correctly does **not**, because
  Actuation only appears in the keyboard nav panel and never in the stack panel. Counted by hand for
  a headset: 2 + Audio = 3, matching the three buttons the view renders.
- **The constructor-parameter insertion is safe.** `audioEndpoints` was inserted before main's
  `logger`, but every construction site in `src/` and `tests/` uses named arguments and DI resolves
  by type, so nothing binds positionally.

#### Code
- none.

#### Tests
- [ ] `CosmoStateMonitor` remains untested — its two-poll confirmation debounce, "first reading
  adopted immediately" rule and clear-on-unplug path decide whether a card shows as connected. There
  is still no `tests/ProoveGaming.DeviceHost.Tests` project, so this needs the logic extracted or a
  new project. Carried from Round 1.

#### Nits
- **S3 follow-up still open:** `AudioEndpointService.TryResolveEndpointVolume` still does a full
  `CoCreateInstance` + `EnumAudioEndpoints` per read. With the Audio page open, `AudioMuteMonitor`
  (4 reads / 2 s) and the volume tracker (2 reads / 1 s) still duplicate work. The plan's guidance is
  "resolve once per device open"; caching per container ID and folding the two pollers together
  remains the correct fix. Deliberately deferred rather than done — it would refactor code the user
  has just hand-validated.
- `AudioMuteMonitor` also reads volume levels now; `AudioIndicatorMonitor` would describe it better.
- `MouseSettingViewModel` used for headset audio — accepted in the plan.
- Main's `_logger` on `DeviceDetailsViewModel` is assigned but only used from the KeyAssignments
  partial; harmless, and not this branch's code.

#### What's done well
- **The merge was verified, not assumed.** File-level deletions, line-level removals in auto-merged
  files, both features through the full config→proto→registry→card→fill pipeline, and the nav
  arithmetic were each checked mechanically. That is the difference between "it compiled" and "the
  merge is correct".
- **The proto collision was resolved on the right principle** — field numbers are the wire contract,
  so the unreleased side moves. The reasoning is written into the proto comment where the next
  person to add a field will read it.
- **The Round 1 S1 fix went further than the finding.** Chasing *why* the rewritten test failed
  (`Actual: 50`, neither the saved nor the drifted value) exposed a second, independent fault — the
  test had never saved anything, because `SavePresetCommand` only opens a dialog. A test that
  asserted its way to green would have hidden that.
- **The culture fix is complete and pinned.** All four actuation displays are invariant, and the
  `[Theory]` over uk-UA / de-DE / fr-FR / en-US was verified to fail on exactly the three
  comma-decimal locales when the fix is reverted. A sweep found no other culture-dependent decimal
  display in the App.
- **Fixing a pre-existing bug found in someone else's merged code, with a regression test**, rather
  than adjusting the test to match the broken behaviour.

#### Verdict
Ship: **yes** — no blockers. Every Round 1 item is closed and independently verified, the merge
provably lost nothing from either side, and a clean-checkout build plus 1126 passing tests confirm
it. The two carried items (`CosmoStateMonitor` tests, endpoint-resolution caching) are genuine
follow-ups, neither of which affects correctness of what ships.
First: (1) nothing blocking — merge when ready; (2) schedule the endpoint-caching follow-up before
more pollers are added; (3) tell the actuation feature's owner that the culture bug was live on main.

### Round 3 — Ship: yes

_Branch `fix/fixes-on-30.09.2026` (commits 7f3105d, 1a2d87c), reviewed 2026-09-30. Scope for this
plan: `CosmoStateMonitor.IsPoweredOff` → `IsPresentable` (an unread Cosmo is now hidden) and its one
caller, `DeviceService.PresentableDevices`. The same branch also carries a DPI-button fix, a macro
recorder fix and a Home add-card change, reviewed in their own plans / in chat._

#### Plan conformance
- Implemented as the amended file-level list says (2026-09-30 note under `CosmoStateMonitor.cs`):
  unknown power state → not presentable; `PresentableDevices` is the only caller and all four
  publication paths still go through it.
- The new acceptance criterion ("never shows as connected, not even briefly") holds by
  construction: nothing publishes a `0x0D8C:0x0334` interface until `_isOn` holds `true` for its key.
- Accepted limitation (stated in chat, not yet in the plan's Risks): a genuine Cosmo whose
  `CosmoStateReader.TryRead` keeps failing is now hidden instead of shown. Residual risk: a headset
  that used to appear (without power tracking) now does not appear at all.

#### Code
- [x] **S1. Key mismatch now fails closed: the Cosmo can stay hidden for the whole session** —
  `src/ProoveGaming.DeviceHost/Services/CosmoStateMonitor.cs:71-77` (`IsPresentable`) — Should-fix
  **Problem / why it matters:** the filter looks up `_isOn` by the HID interface's `DeviceKey`, which
  `HidDeviceStateFactory` computes ONCE per device path and caches for the session
  (`_identityCache`), while the monitor recomputes the key on every poll. Both call
  `DeviceContainerId.TryGet`, so they normally agree — but if the factory's single read failed
  (cached `""`) and the monitor's later read succeeds (a GUID), the lookup misses forever. Before this
  branch a miss meant "shown"; now it means "hidden", so the result is a plugged-in, switched-on Cosmo
  that never appears and logs nothing about why.
  **Fix:** A (correct) — mirror `TryGetLastReport`'s single-device fallback: when the key is not in
  `_isOn` and exactly one Cosmo is tracked, answer from that entry. B (diagnostic) — log once at
  Information when a Cosmo interface is filtered because its key has no state, so the failure is at
  least visible in the DeviceHost log.

#### Tests
- [ ] `IsPresentable` has no test, and the rule it encodes just flipped. Non-Cosmo → true,
  unparseable ids → true, Cosmo with no state → false are all checkable without hardware (the
  constructor takes a logger and an `IHidDeviceProvider`). There is no DeviceHost test project yet —
  the same gap Round 1 recorded for `CosmoStateMonitor` as a whole.
- [ ] Hardware QA: plug the dongle in with the headset OFF → the card must never light up; switch
  it on → the card appears within ~2 s.

#### Nits
- none

#### What's done well
- Fixes the cause (an unknown state treated as a known "on") instead of adding a settle delay on
  top of it, and names the method for what the filter actually asks.
- The comment records why the old default existed and why it was wrong, so the next reader will not
  "restore" the instant appearance.

#### Verdict
Ship: yes — the change is small and correct in the normal case; S1 is a rare edge that now fails
closed instead of open. First: (1) S1 option A; (2) hardware QA above; (3) add the test when a
DeviceHost test project exists.

### Round 3 — fixes applied

_2026-09-30._ S1 fixed with option A: `IsPresentable` falls back to the only tracked headset's
state when the interface's key has no entry, mirroring `TryGetLastReport`. Trade-off: with two
Cosmos plugged in and one key unread, the unread one borrows the other's state until its first
poll — the same single-device approximation the rest of the host makes. DeviceHost builds; the
`IsPresentable` test gap remains (no DeviceHost test project).
