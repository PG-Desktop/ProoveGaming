# Plan: Add the PG Starlight headset as a new device type

## Goal

Add the **PG Starlight** headset to the App as a supported device: a single card on the home page
with the same connection tracking and disabled-when-disconnected behaviour every device has, and a
details view with **Presets**, **Audio** and **Firmware**.

**Outcome (2026-08-07): the whole feature is the catalog entry.** The Cosmo work had already
generalised the headset infrastructure, so Presets, Audio and Firmware arrive with no code at all.
Every capability beyond them was investigated on hardware and found unreachable — RGB, battery,
power state, microphone-mute state and the volume wheel. Those results are recorded in full below,
because the value of this plan now is stopping the next person from re-running the same probes.

## Hardware facts (already established — do not re-derive)

Measured 2026-08-07 with the device connected, via
`dotnet run --project ./src/ProoveGaming.Device.Testing -- starlight` (read-only).

- **VID `0x0D8C`** (C-Media, 3468) / **PID `0x036D`** (877).
- Manufacturer **"Jieli Technology"**, ProductName and FriendlyName **"PG Starlight"**.
  The manufacturer string differs from Cosmo's "THI Headphone" and names a *different SoC vendor*.
- USB composite. Only **`MI_03`** exposes HID, with **two collections**, both top-level usage page
  `0x000C` (Consumer), usage `0x01`:

  | Collection | Reports | Size | Purpose |
  | --- | --- | --- | --- |
  | `Col01` | IN `0x01` | 3 B | Media / volume consumer keys — Windows already handles these |
  | `Col02` | IN **and** OUT `0xFD` | 32 B (31 data + report ID) | The only vendor channel |

  `Col02` raw descriptor:
  `05-0C 09-01 A1-01 85-FD 09-10 15-00 25-FF 35-00 45-00 65-00 55-00 75-08 95-1F 81-02 09-10 91-02 C1-00`

- **No feature reports anywhere** (feature length 0 on both collections).
- **No report `0xBC`.** Cosmo's vendor channel does not exist on this device.
- **No report `0xFF`.** This is decisive for lighting — see below.
- USB `iSerialNumber` **`00006F64096B22E7`**, ContainerId `99e3b024-79e0-5516-ace7-150b21eb9dd0`.

### The serial number is not a per-unit identity — this corrects an existing claim

Starlight's `iSerialNumber` is **byte-identical to Cosmo's**. Two different products from two
different SoC vendors report the same string, so it is an **ODM firmware constant**, not a unit
identifier.

`docs/device-identity.md` presented the Cosmo serial as proof that a real `iSerialNumber` solves
per-unit identity. **Corrected 2026-08-07** — a serial only helps if it is *unique*, and this one
demonstrably is not. Container ID still separates *models* (it incorporates VID/PID),
but **two units of the same model would very likely collide** — which is worse than the
port-derived case, because the collision is invisible and permanent rather than fixed by replugging.

Untested: we have one Starlight and one Cosmo. The two-same-model collision is strongly implied but
not measured. Recorded as a risk, not asserted as fact.

### Lighting: the hardware HAS RGB — the open question is the channel, not the capability

**Confirmed by the user 2026-08-07: Starlight has RGB.** So lighting is in scope, and the probe's
job is to find *how* to reach it, not *whether* to bother.

`docs/headset/starlight/CM002X1 LED Control Specification_v0.3.pdf` defines a complete LED protocol:

- A **16-byte HID OUTPUT report** — byte 0 report ID, byte 1 command, bytes 2-15 parameters.
  Little-endian (changed in v0.2).
- Commands:

  | Cmd | Name | Parameters |
  | --- | --- | --- |
  | `0x01` | LED Start | none — begins display using the stored configuration and colour array |
  | `0x02` | LED Stop | none — device returns the LED to its default state |
  | `0x03` | Send LED Configuration | byte 2 operation mode; bytes 3-4 LE colour-array size; bytes 5-6 LE update interval (ms) |
  | `0x04` | Send Color Array | bytes 2-3 LE offset, then **four** RGB triples at bytes 4-6, 7-9, 10-12, 13-15 |

- Operation modes: `0x00` **Static** (array size always 1, interval 0) · `0x01` **Repeat Forward**
  (cycles the array in order) · `0x02` **Back And Forth** (sweeps the array and reverses) ·
  `0x03` **Lookup Table** (array indexed by *audio output amplitude* — index 0 = quietest,
  n-1 = loudest; size should be a power of two).
- Required order: **Stop → Send Configuration → Send Color Array… → Start**. The device keeps the
  previous configuration and array if either is omitted between a Stop and a Start.
- **The table is written to flash on Start and erased on Stop**, so lighting survives unplug.
- A malformed set-output-report is **stalled** by the device — a clean, unambiguous failure signal.

#### The channel problem, and what the interface survey settles

The spec says a driver detects LED support by finding **report ID `0xFF`** in the HID report
descriptor. **Starlight does not declare `0xFF`** — its only vendor report is `0xFD`. The spec is
also written for a **"CM002X1 demo board"** while this device reports **Jieli** silicon.

An earlier reading of this concluded lighting might be unreachable. That conclusion was drawn from
**HID interfaces only**, which was too narrow — an LED could plausibly hang off a vendor USB
interface that never appears in a HID enumeration. The full device tree closes that gap:

```
USB\VID_0D8C&PID_036D\00006F64096B22E7   USB Composite Device
  +-- MI_00  MEDIA     "PG Starlight"                 (USB Audio Class endpoints)
  +-- MI_03  HIDClass  "USB Input Device"
        +-- COL01  HID-compliant consumer control device   report 0x01, IN, 3 B
        +-- COL02  HID-compliant consumer control device   report 0xFD, IN+OUT, 32 B
```

There is **no vendor/WinUSB interface**. `MI_00` is the audio function; `MI_03` is the only control
surface. Therefore, if the LED is host-controllable at all, **report `0xFD` is the only possible
route** — there is nowhere else for the commands to go.

That makes the hypothesis concrete and cheap to test: send the documented 16-byte LED command
sequence **on report `0xFD`** instead of `0xFF`. Report `0xFD` carries 31 data bytes, comfortably
more than the 15 the LED payload needs, so the layout fits with room to spare (which may itself mean
the payload is wrapped in an envelope — see Phase 0b).

### The physical volume buttons: already handled, nothing to build

The headset's own **volume +/-** are standard consumer-control keys, and they are on `COL01`
(report `0x01`), the collection Windows claims as a "HID-compliant consumer control device".

- **Windows consumes them.** The App never sees the keypress — that path is not observable to us,
  and no amount of probing changes it.
- **We control the same target they do.** With `AudioVolumeTarget: "OperatingSystem"`, our Output
  Volume slider drives that device's Windows endpoint, which is exactly what the buttons move.
- **The slider follows them anyway.** `DeviceDetailsViewModel.Audio.cs` already polls the endpoint
  once per second while the Audio section is open, precisely because the buttons are invisible to us
  (measured on Cosmo: they step by 6 — 52 → 46 → 58). The poll is gated on the Audio section being
  visible and suppressed for 1.2 s after a local drag so it cannot fight the user mid-drag.

So for Starlight this behaviour arrives free with the catalog entry. **No new code, and no research
needed** — the question "can we track the buttons?" is already answered: not the presses, but the
resulting level, which is what the user actually sees and hears.

### The factory SDK bundle: reviewed, and it does not cover Starlight

`docs/headset/cosmo/SDK/` (105 files) was reviewed in full on 2026-08-07. It contains two things,
neither of which answers a Starlight question:

**1. `CmXearSDK v1.0.28.0` — C-Media's Xear *audio* SDK.** A 235-page API reference plus a WPF demo
app with source. Its surface is audio DSP and plumbing: EQ, audio enhancements, device/format/session
management, volume control, foreground-window awareness, playback events, and licensing
(`CmLicenseCategory` — Xear features are licensed, so the SDK is not free to use even for audio).

A word-boundary scan of the whole 235-page guide returns **zero** hits for LED, RGB, battery, HID,
`0xFD`, `0xBC`, `0xFF`, colour array, Starlight or Jieli. The only source hit anywhere in the bundle
is a `"Mute"` toggle label in the demo's volume-control page.

**2. An `AUSDOM_A3_Pro` driver + APO package** — a `.sys` driver, an APO (Audio Processing Object)
DLL, INF/CAT files and install/uninstall executables.

#### The INF settles who this bundle is for

```
[ManufacturerID.NTamd64.10.0]
%AUSDOM_A3_PRO.DeviceDesc% = AUSDOM_A3_PRO, USB\VID_0D8C&PID_0367&MI_00
```

It binds **PID `0x0367`** — a *third* product, neither Cosmo (`0x0334`) nor Starlight (`0x036D`) —
and it binds **`MI_00`, the audio interface**, not `MI_03` where the vendor HID channel lives. It is
an audio-class driver that installs Xear processing onto the audio function. It cannot reach an LED
or a battery, by construction.

The three PIDs sitting adjacent (`0x0334`, `0x036D`, `0x0367`) confirm these are siblings from one
ODM, which is also the simplest explanation for the shared `iSerialNumber` constant.

#### One finding worth carrying forward: `SystemDynamicLighting`

The SDK's only lighting-related surface is a property and callback named `SystemDynamicLighting`,
described as *"whether system-wide dynamic lighting function is enabled"*. That is **Windows 11
Dynamic Lighting** (Settings → Personalization → Dynamic Lighting), not a device LED API — it exists
so a vendor app can tell whether Windows has taken over RGB control.

Windows drives Dynamic Lighting over **HID LampArray** (usage page `0x59`). Starlight declares only
usage page `0x0C`, so it is **not** a LampArray device and Windows will not claim its LED. Good news
for us: there is no contention to design around. It also means Windows itself offers no route to the
LED, so the vendor channel remains the only option.

#### What this changes in the plan

Nothing structural — Phase 0b remains the way to settle the LED channel. What it does change is the
expected value of asking the vendor again: they have now supplied an audio SDK for a sibling product
and an LED specification for a *demo board*, but nothing for this device's actual firmware. The
targeted request is narrower than "documentation": **the LED command framing for PID `0x036D` on
report `0xFD`**, or their tool that drives it.

## What Cosmo already gave us (why this plan is small)

The Cosmo change (`../add-cosmo-headset/add-cosmo-headset-plan.md`) built this generically, and it
is all catalog-driven. Starlight needs **none** of it rebuilt:

- `SupportedAudioSettings`, `AudioVolumeTarget`, `SupportsBattery` through
  `DeviceGroupConfig` → `DeviceMap` → `DeviceMapExpander` → proto → `HidDeviceMapRegistry`.
- The whole Audio section: `DeviceDetailsViewModel.Audio.cs`, `AudioSectionView.axaml`,
  `IAudioEndpointService` (Core Audio matched on Container ID), `AudioMuteMonitor`,
  `SettingsRepository` audio keys, preset integration via `StagePresetDirtyOnly`.
- Capability-driven nav (`IsAudioNavVisible`, `IsStackNavLayout`, `IsSectionAvailable`).
- Card artwork raster branch, audio/mic indicators, connectivity bar, battery gating.
- Localization for the Audio nav label and both sliders.

What is genuinely Cosmo-*specific* and therefore **not** reusable: `CosmoStateReport`,
`CosmoStateReader` (scoped by `CosmoStateReader.Matches(vid, pid)`), `CosmoStateMonitor`, and the
`PresentableDevices` power-state hiding. Starlight has a different PID, so none of it will
accidentally pick this device up — a clean separation that needs no defensive work.

## Scope

**In**

- Phase 0 hardware probe: read-only survey, then an authorised LED write test.
- Catalog entry for Starlight — delivers Presets, Audio and Firmware with no code.
- RGB Lighting: a `StarlightLedService` implementing the documented command set over the channel
  Phase 0 confirms, wired through the existing RGB plumbing.
- `docs/CONTEXT.md` glossary terms and a correction to `docs/device-identity.md`.
- Tests for catalog expansion, nav visibility, and the LED frame encoder.

**Out**

- Battery. Not readable today and not shipped on a guess — see the decision below.
- Microphone-mute state. Not exposed by this device any more than by Cosmo.
- Host-driven LED animation. The firmware owns effect playback by design, and every Start writes to
  flash — see Performance & quality.
- Refactoring the Cosmo state pipeline to be "generic". Two devices is not evidence of a shared
  shape, and Starlight has no `0xBC` at all.

## Phase 0 — hardware probe (gate for the lighting work only)

The catalog entry does not depend on this and can land first. **The LED service does.**

### 0a. Survey `0xFD` (read-only)

`Col02` declares `0xFD` as both IN and OUT, 31 data bytes each way. Cosmo's `0xFD` was the Xear I2C
bridge and `GET_REPORT` on it failed; Starlight's may be entirely different.

1. **Passive listen.** Read input reports through a guided sequence: idle → volume +/- → mic
   mute/unmute → power off → power on → every button. Record which offsets move in which phase.
   This is the method that decoded Cosmo's power state, and it writes nothing.
2. **Control-transfer read.** `HidD_GetInputReport` on `0xFD`, reusing the P/Invoke already in
   `CosmoStateReader`. On Cosmo this was the breakthrough: the dongle pushed nothing but answered a
   control read.

**Answers:** whether the device reports anything at all unprompted, and whether battery or power
state are readable as a side benefit.

### 0a RESULT (run 2026-08-07) — `0xFD` carries no readable state at all

Both read-only arms were run against the connected headset. The answer is unambiguous:

- **`starlight watch`** — the full guided sequence (idle, volume +/-, mic mute, power off, power on,
  every button). **No frames arrived.** The device pushes nothing on `0xFD`, ever, under any of the
  physical actions we can perform.
- **`starlight read 30`** — 30 control-transfer reads, one per second. Every one succeeded, and
  every one returned **32 zero bytes**:
  `00-00-…-00`.

So `HidD_GetInputReport` on `0xFD` *works* — the read is accepted, unlike Cosmo's `0xFD` which failed
outright — but the buffer is empty. There is no battery, no power state and no microphone-mute state
readable from this device by any passive means.

**What this settles**

1. **`SupportsBattery: false` in the catalog is correct**, and now on evidence rather than caution.
   There is nothing to read. The Cosmo situation does not repeat here: Cosmo's `0xBC` at least
   returned a populated buffer with one genuinely decodable byte, which is what made its
   never-moving battery guess tempting. Starlight offers no such temptation.
2. **Microphone mute is unreachable**, exactly as on Cosmo, and for the same reason: the device
   simply does not report it to the host.
3. **`0xFD` looks like a command channel, not a status channel.** A read that succeeds but returns
   all zeros is what a request/response channel does when no response is pending. Cosmo's `0xFD` was
   precisely that shape — the Xear I2C bridge, where the host writes a request and then reads the
   reply. If Starlight's `0xFD` follows the same convention, it will only ever answer *after* being
   asked.

That last point matters for the LED work: it makes `0xFD` **more** likely to be the LED channel, not
less, because a command channel is exactly what LED control needs. Phase 0b is now the only
remaining way to settle it, and there is no read-only alternative left to try first.

### 0b. Drive the LED (WRITES — requires explicit authorisation)

**This is the step that unblocks the RGB section, and it is the highest-risk step in the plan.**

Send the documented 16-byte LED sequence on report **`0xFD`** (the only channel that exists here):

```
Stop           FD 02 00 00 ...
Configuration  FD 03 00 01 00 00 00 ...      mode Static, array size 1, interval 0
Color Array    FD 04 00 00 RR GG BB 00 ...   offset 0, one RGB triple
Start          FD 01 00 00 ...
```

Static red first — unambiguous to see, and the smallest possible valid payload.

- **LED turns red** → the CM002X1 command set applies over `0xFD`. Proceed to the full service.
- **Device stalls the request** → the payload is wrong for this firmware. The spec's 16-byte frame
  may be wrapped in an envelope, since `0xFD` carries 31 bytes. Try the frame at offset 1 with a
  length or sub-command byte first. **Two shaped attempts, then stop** — do not sweep.
  *(Superseded 2026-08-07 — the boundary was widened with the user's authorisation. See "The
  operative safety boundary" below.)*
- **Nothing happens, no stall** → inconclusive. Stop and request vendor documentation or their
  Windows software.

**Why this is riskier than it looks.** An unknown write to an unknown vendor channel is exactly how
the BLAZE flash corruption happened (`docs/mouse/blaze`), and this device persists LED state *to
flash*. Mitigations: send only the documented layout; one command at a time; abort the instant the
device stops enumerating. The Cosmo opcode sweep was authorised in writing before it ran — do the
same here.

#### The operative safety boundary (revised 2026-08-07)

The original wording above — "two shaped attempts, then stop, never a sweep" — was written before
any write went out and conflated two different things: *how many* attempts, and *what is in them*.
Attempts 3 and 4 crossed the first while staying inside the second, so the rule in force is now
stated by content rather than by count:

- **Allowed:** the documented CM002X1 commands (`0x01` Start, `0x02` Stop, `0x03` Configuration,
  `0x04` Colour Array) in any framing, at any report ID or length, with documented parameters.
  Framing is a delivery question and cannot put the device into an undefined state — an unrecognised
  frame is discarded.
- **Allowed with explicit authorisation, recorded here:** widening a documented *parameter* past its
  documented range, e.g. the operation-mode byte beyond `0x00`-`0x03`.
- **Never:** unknown *opcodes*. A command byte outside `0x01`-`0x04` can address firmware routines
  nobody has documented — reset, DFU, factory mode — and this device persists to flash. That is the
  BLAZE failure mode and it stays prohibited.

**Authorisation record.** Both expansions below were authorised by the user in conversation on
2026-08-07 — the framing sweep ("give RGB another chance, please use some range of values … you can
use different colors etc.") and the mode probe ("let's try to change effect rather than color").
Unlike the Cosmo sweep, neither was recorded in writing *before* it ran; this section is that record,
written after the fact. Future range expansions should be written down first.

**Ask before running this:** is there vendor Windows software for Starlight? Capturing it is faster,
safer and is how every device in this repo has actually been decoded. See Open questions.

### 0b RESULT, attempt 1 (run 2026-08-07) — direct layout accepted and ignored

The documented sequence was sent on `0xFD` targeting static red: Stop, Configuration (Static, one
colour, 0 ms), Colour Array (offset 0, `FF 00 00`), Start.

**Every write completed. The LED did not change** — it continued its firmware colour cycle
throughout, and the Stop command did not interrupt it either.

**Correction to this plan's own reasoning.** Phase 0b was written expecting a stall to distinguish
"rejected" from "accepted", quoting the spec's promise that the device stalls a malformed
set-output-report. **That signal does not exist here, and the plan was wrong to rely on it.** The
spec describes report `0xFF` on a CM002X1; on Starlight, `0xFD` is a *declared* 32-byte output
report, so the HID stack accepts any 32 bytes and hands them to firmware whether or not it
understands them. A completed write proves only that the write completed. The probe's output has
been corrected to say so.

So the result is "accepted and ignored", not "rejected" — and the only real evidence available is
the LED itself.

**One attempt remains, then we stop.** `0xFD` carries 31 data bytes for a 15-byte LED payload, which
is the shape of a tunnel: the outer vendor report wrapping the spec's own frame, with the inner
report ID `0xFF` as the first payload byte. That is the conventional encoding for a HID report
tunnelled over a vendor channel, and it reuses the spec verbatim instead of inventing a layout:

```
FD FF 02 00 ...                    Stop
FD FF 03 00 01 00 00 00 ...        Configuration (Static, 1 colour, 0 ms)
FD FF 04 00 00 FF 00 00 ...        Colour Array (offset 0, RGB FF0000)
FD FF 01 00 ...                    Start
```

Run: `starlight led --i-mean-it envelope`.

If that also leaves the LED unchanged, **stop guessing**. Two shaped attempts against a documented
command set is the limit this plan set, and continuing would mean sweeping unknown bytes at a
flash-backed vendor channel — the BLAZE failure mode. The next step is the vendor, with the narrow
request already stated: *the LED command framing for PID `0x036D` on report `0xFD`*, or their tool.

*(Superseded 2026-08-07. This conflated varying the FRAMING with sweeping unknown BYTES; only the
second is the BLAZE failure mode. Attempts 3 and 4 followed with the user's authorisation and are
recorded below. See "The operative safety boundary".)*

### 0b RESULT, attempt 2 (run 2026-08-07) — envelope layout also ignored

The tunnelled framing (`FD FF <command> …`, the specification's own report ID leading the payload)
was sent as the same Stop / Configuration / Colour Array / Start sequence. **The LED did not change.**

**This was recorded at the time as "Phase 0b is closed, we are not guessing further".** That turned
out not to be the end: attempts 3 and 4 followed, with the user's authorisation, and are recorded
below. The reasoning that stopped things here was also partly wrong — it treated *framing* variation
as equivalent to *opcode* sweeping, and only the latter is actually dangerous. See "The operative
safety boundary" above.

**What is now known, and it is not nothing:**

- `0xFD` is the only host-writable channel on the device (full device tree confirms no vendor
  interface).
- It pushes nothing and reads back all zeros, so it is a request/response command channel, not a
  status one — which is still the best evidence that it *is* the LED channel.
- The CM002X1 command set does not apply to it in either the direct or the tunnelled layout.
- Neither attempt harmed the device; it continued its built-in colour cycle throughout, and audio
  was unaffected.

**Next step is the vendor, not the keyboard.** The request is written and ready to send:
`docs/headset/starlight/starlight-vendor-request-2026-08-07.md`. It states the two framings already
tried so they are not repeated back to us, and asks four specific questions — the framing, whether
an unlock handshake is required, whether Jieli firmware implements the CM002X1 set at all, and how
many LEDs are individually addressable.

**This does not block anything.** Phase 1 shipped and is complete: the card, Presets, Audio and
Firmware all work from the catalog entry, and `SupportsRgb: false` means nothing in the app claims
lighting it cannot deliver. Phase 2 stays parked until the vendor answers.

### 0b RESULT, attempt 3 (run 2026-08-07) — framing sweep, 12 shapes, none worked

Arm: `starlight sweep --i-mean-it` (`src/ProoveGaming.Device.Testing/StarlightLedSweep.cs`).
Authorised by the user in conversation; see "The operative safety boundary".

**The authoritative count is the `Framings` array in that file, not this prose.** The probe prints
`Framings.Length` when it runs. If the table below and the code ever disagree, the code is right —
three documents once carried three different numbers for this, which is exactly the failure mode
that makes a "here is what we already tried" claim worthless.

Rather than one framing at a time, this walks a matrix of **12 delivery shapes**, each sending the
full documented sequence (Stop → Configuration → Colour Array → Start) — **48 writes per run**. Only
the delivery varies; every frame uses documented commands `0x01`-`0x04`:

| # | Report ID | Length | Lead byte | Offset width | Target colour |
| --- | --- | --- | --- | --- | --- |
| 1 | `0xFD` | 32 | — | 2 | RED *(= attempt 1)* |
| 2 | `0xFD` | 32 | `FF` | 2 | GREEN *(= attempt 2)* |
| 3 | `0xFF` | 16 | — | 2 | BLUE *(= "native")* |
| 4 | `0xFD` | 16 | — | 2 | YELLOW *(= "short")* |
| 5 | `0xFD` | 32 | — | **1** | MAGENTA |
| 6 | `0xFD` | 16 | — | **1** | CYAN |
| 7 | `0xFF` | 32 | — | 2 | WHITE |
| 8 | `0xFF` | 16 | — | **1** | ORANGE |
| 9 | `0xFD` | 16 | `FF` | 2 | RED |
| 10 | `0xFD` | 32 | `0F` (length) | 2 | GREEN |
| 11 | `0xFD` | 32 | `00` | 2 | BLUE |
| 12 | `0xFD` | 32 | `01` | 2 | YELLOW |

Each attempt targeted a **different colour** deliberately: had the bytes landed at an offset we had
not accounted for, the LED would have shown a colour we never sent for that attempt, which is itself
a signal rather than a null result.

**Result: no change, at any attempt.** The LED stayed on its firmware colour cycle throughout, and a
replug afterwards showed nothing had been written to flash either.

**What this rules out.** The 1-byte offset variant (5, 6, 8) kills the "the RGB triple is shifted"
theory. Shapes 7 and 3 cover report `0xFF` at both lengths. Shapes 9-12 cover the plausible envelope
lead bytes. There is no remaining *framing* of the documented command set left to try.

### 0b RESULT, attempt 4 (run 2026-08-07) — operation-mode probe, both passes, no change

Arm: `starlight effects --i-mean-it` (`src/ProoveGaming.Device.Testing/StarlightEffectProbe.cs`).
Authorised by the user in conversation; see "The operative safety boundary".

Motivated by an observation from the user rather than from the spec: Button 2 cycles the headset
through a fixed set of effects, so the firmware owns an effect table and what is settable may be an
effect *selection* rather than arbitrary RGB.

It also corrects a mistake running through attempts 1-3: **every one of them opened with `Stop`, and
the specification says `Stop` erases the stored LED table.** If the device's built-in effects live in
that table, those attempts were wiping it and then asking the device to `Start` with nothing left —
which fits the LED sitting on a default far better than any colour-encoding theory did. This arm
therefore sends **no `Stop` at all** and lets the device keep its own colours, which the spec
explicitly permits.

Two passes, operation-mode byte `0x00`-`0x07`, 6 s hold each:

- **Pass 1** — `Configuration(mode)` then `Start`
- **Pass 2** — `Configuration(mode)` alone, no `Start` *(also a flash-wear question: `Start` is the
  command that writes flash, so knowing whether it is required matters for production)*

**This is the one place a documented parameter was pushed past its documented range.** The spec names
modes `0x00`-`0x03`; `0x04`-`0x07` test whether the firmware exposes more. The probe's own comment
flags this (`StarlightEffectProbe.cs:25-26`). It is a parameter widening, not an opcode guess — the
command byte stayed `0x03`/`0x01` throughout.

**Result: no change, in either pass, at any mode.** The effect stayed exactly where Button 2 had left
it. So the effect table is not reachable either, and `Stop` erasing the table was not the explanation.

### Phase 0b — final state

Four attempts, 12 distinct framings plus 16 operation-mode attempts, both with and without `Stop`, colour and mode as the
varied field. **The LED never responded to anything.** Every avenue that stays inside the documented
command set has been tried; what remains is the vendor, and unknown opcodes stay prohibited.

Arms that exist and have already run — **do not rebuild these**:

| Arm | What it did | Result |
| --- | --- | --- |
| `starlight led --i-mean-it` | direct framing, `0xFD` / 32 B | no change |
| `starlight led --i-mean-it envelope` | tunnelled, `FF` lead | no change |
| `starlight led --i-mean-it native` | `0xFF` / 16 B, spec verbatim | no change |
| `starlight led --i-mean-it short` | `0xFD` / 16 B | no change |
| `starlight sweep --i-mean-it` | 12-shape framing matrix | no change |
| `starlight effects --i-mean-it` | mode `0x00`-`0x07`, ±`Start`, no `Stop` | no change |

### 0d RESULT (run 2026-08-07) — one button IS readable; the rest are headset-internal

The headset has two buttons and a mechanical volume wheel:

| Control | Short press | Long press |
| --- | --- | --- |
| Button 1 | play / pause | power off |
| Button 2 | next RGB effect | microphone mute / unmute |
| Wheel | output level (mechanical) | — |

`starlight buttons` listened on **every** interface, through HidSharp **and** Raw Input at once,
across guided phases. Result:

| Phase | Frames | Finding |
| --- | --- | --- |
| idle | 0 | no background traffic — anything seen later is a real event |
| **button1-short** | **32** | `01-08-00` then `01-00-00`, on both paths |
| button2-short (RGB) | 0 | invisible to the host |
| button2-long (mic mute) | 0 | invisible to the host |
| wheel | 0 | invisible to the host |

**A correction to an earlier finding in this plan.** The `starlight watch` arm reported that the
device "pushes nothing", and that was recorded as a general fact. It was not: `watch` listened on
`COL02` (the 32-byte vendor collection) **only**, and the buttons are consumer-control keys on
`COL01`. Nothing had ever been listening where the buttons actually report. The device does push —
just not on the collection that was being watched.

#### Button 1 decoded

`COL01`'s report descriptor declares eight consumer usages as single bits in byte 1 of report `0x01`:

| Bit | Mask | Usage | Seen? |
| --- | --- | --- | --- |
| 0 | `0x01` | Volume Increment (`0xE9`) | never |
| 1 | `0x02` | Volume Decrement (`0xEA`) | never |
| 2 | `0x04` | Mute (`0xE2`) | never |
| 3 | `0x08` | **Play/Pause (`0xCD`)** | **yes — Button 1** |
| 4-7 | | Next / Previous / Fast-forward / Stop | never |

`01-08-00` is bit 3 set (press); `01-00-00` is it cleared (release). So Button 1 delivers a clean
**press/release pair**, which means the app can measure hold duration host-side and tell a short
press from a long one without any vendor protocol.

Both listening paths saw every frame, so **HidSharp on `COL01` is sufficient** — Raw Input is not
required for this device, which keeps any future implementation much simpler.

#### What is NOT readable, and why that is settled rather than open

Button 2 (both actions) and the wheel produce **nothing at all**, on either path, with a clean
zero-frame baseline either side. They are handled inside the headset.

Note the descriptor *declares* Volume Increment, Volume Decrement and Mute — the firmware simply
never sends them. So this is not a case of us looking in the wrong place a second time: the usages
exist, the device chooses not to use them. There is no further HID avenue to try.

#### What the app can do with this

- **Button 1 is bindable.** Press and release are both observable, so play/pause and hold-to-power-off
  are both detectable. Worth noting the power-off case has a second, simpler signal: the device
  disconnects.
- **Button 2 and the wheel are not bindable.** The app cannot know the RGB effect changed, and
  cannot see the mute keypress.
- **The remaining question is whether their SIDE EFFECTS are visible through Windows**, which is a
  different layer entirely and one the app already reads:
  - does the mic-mute long-press change the **Windows microphone endpoint mute state**?
  - does the wheel change the **Windows output endpoint level**, or attenuate in hardware only?

  Both are answerable by watching Windows' own sound UI while pressing — no probe needed. If the
  endpoint mute follows the button, `AudioMuteMonitor` already reads it and the card's mic indicator
  works for free. If the wheel does not move the endpoint, then our Output Volume slider and the
  wheel are independent controls, which is a real UX fact to design around rather than a bug.

### 0c + 0e RESULT (run 2026-08-07) — transport settled; nothing else is observable

**Transport is settled.** With the headset switched OFF the device stays enumerated with **both**
interfaces. So Starlight is a **2.4G dongle**, exactly like Cosmo, and `ConnectionType: Dongle2_4G`
in the catalog is correct rather than assumed. Open question 2 is closed.

**Everything else is not observable, on either layer.**

`starlight power` sampled USB enumeration and report `0xFD` once a second through a full power
cycle. Interface count never left 2, and `0xFD` returned 32 zero bytes in every phase.

`starlight audio` then watched both Windows endpoints — state, mute and volume — once a second
through mic-mute, the wheel, and a power cycle:

| Endpoint | baseline | mic-mute | mic-unmute | wheel | power-off | power-on |
| --- | --- | --- | --- | --- | --- | --- |
| Headphones (PG Starlight) | ACTIVE, muted=False, vol=53 | *identical* | *identical* | *identical* | *identical* | *identical* |
| Microphone (PG Starlight) | ACTIVE, muted=False, vol=92 | *identical* | *identical* | *identical* | *identical* | *identical* |

Not one value changed. Three conclusions, each now measured rather than assumed:

1. **Power state is invisible.** The endpoints stay `ACTIVE` with the headset switched off, and the
   dongle stays enumerated. Neither HID nor Core Audio can tell the app the headset is off.
2. **Microphone mute is invisible.** The long-press mutes inside the headset; Windows never learns.
   Same answer as Cosmo, now confirmed on the audio layer too rather than only on HID.
3. **The volume wheel is a hardware attenuator.** It does not move the Windows endpoint level at
   all — the render endpoint sat at 53 % throughout.

#### Endpoint Container ID — the app CAN resolve these endpoints

Measured 2026-08-07 by `starlight audio`, which now reads `DEVPKEY_Device_ContainerId` from every
matched endpoint using the same VT_CLSID extraction as `AudioEndpointService.ReadContainerId`:

| Endpoint | Container ID |
| --- | --- |
| Headphones (PG Starlight) | `99e3b024-79e0-5516-ace7-150b21eb9dd0` |
| Microphone (PG Starlight) | `99e3b024-79e0-5516-ace7-150b21eb9dd0` |
| HID interfaces (`MI_03`, both collections) | `99e3b024-79e0-5516-ace7-150b21eb9dd0` |

All three identical, so the audio function on `MI_00` and the HID interfaces on `MI_03` share one
Container ID and `AudioEndpointService.TryResolveEndpointVolume` will match. **The Audio sliders
bind to the right device.**

**Why this needed measuring rather than assuming.** The probe finds endpoints by *friendly name* —
that is what a human can check against Windows' own UI. The app does not: it parses the device key
as a GUID, reads the Container ID from each active endpoint, and matches on that, deliberately
**not** falling back to the default device when nothing matches. Two different keys. Finding an
endpoint by name proved nothing about whether the app could find it.

Had they differed, the failure would have been silent and squarely user-facing: the Audio section
renders, the sliders move, the OS volume does not change, and the one-second poll snaps the thumb
back — indistinguishable from "the slider is broken", with nothing but a debug-log miss to explain
it. Every other conclusion in this plan is backed by a measurement; this one now is too.

#### What conclusion 3 means for the UI, because it is a real UX consequence

Effective loudness is **our slider × the wheel position**, and the two are independent controls that
cannot see each other. A user who turns the wheel down and then drags our Output Volume slider to
100 % will hear something quieter than 100 % and may reasonably report the slider as broken.

This is not fixable from the host — there is nothing to read. It is worth knowing before support
tickets arrive, and it is worth raising with the vendor as a design consequence rather than a bug.

### 0c. Transport and power behaviour (read-only)

Does the device stay enumerated when the headset is powered off (a dongle, like Cosmo), or does it
disappear (a cable)? Sets `ConnectionType`, and decides whether Starlight needs Cosmo's
`PresentableDevices` power-state hiding.

### 0f RESULT (run 2026-08-19) — the dongle exposes no battery and no power state, exhaustively

Re-opened after PG-608 found a battery over **Bluetooth** (70 %, from the `PG Starlight Hands-Free
AG` device node). That raised a fair question: the dongle answer had only ever been tested one way,
so was it a real absence or an untried mechanism?

Two arms, both strictly read-only, run with `starlight battery`
(`src/ProoveGaming.Device.Testing/StarlightBatteryHuntProbe.cs`). Neither retries anything 0a already
settled; both exist because they were never tried.

**Arm A — report-id sweep. Nothing.** Every earlier read used report id `0xFD` and only `0xFD`. This
asked **all 256 ids** (`0x00`-`0xFF`) on **both** collections via `HidD_GetInputReport`, a
control-transfer read. Not one answered with a non-zero payload.

The lead was genuine, not a formality: Cosmo's battery does not live on its vendor channel either —
Cosmo's vendor report is `0xFD` and its status report is `0xBC` — so an undeclared status id was a
real possibility here. It is not there.

**Arm B — device-property dump, phased. Nothing moved.** The Bluetooth battery was not found in HID
at all; it was a Windows device property on a sibling node. Nobody had ever enumerated Starlight's
USB nodes, so this walks every DEVPKEY on every one of them — **60 properties across 5 nodes** — and
diffs the lot across a full power cycle:

```
DIFF — what actually moved
  ON -> OFF: nothing changed
  OFF -> ON: nothing changed
```

Not one value out of ~300 moved when the headset was switched off and on again.

**What this settles**

1. **`SupportsBattery: false` on the dongle transport is correct, exhaustively.** 0a established that
   `0xFD` reads back 32 zero bytes; this adds that no *other* report id answers at all, and that no
   Windows property carries anything either. There is nothing to read by any passive means.
2. **Power state is unreachable over the dongle too.** This re-confirms 0c from a second direction:
   with the headset off the dongle keeps its HID interface, its audio endpoints *and* every device
   property exactly as they were. Nothing at any layer distinguishes "headset off" from "headset on".
3. **Bluetooth's battery is not a sign the dongle hides one.** The two transports genuinely differ:
   over Bluetooth, Windows itself maintains the HFP battery property. Over the dongle there is no
   equivalent, because the host is talking to a receiver that reports nothing about what is behind it.

**The one avenue left, deliberately not taken.** 0a concluded `0xFD` is a *command* channel — a read
that succeeds with all zeros is a request/response channel with no reply pending — and that it will
only answer after being asked. Asking requires inventing an opcode, and this device persists its LED
table to flash, so unknown opcodes sit outside the standing safety boundary (see "The operative
safety boundary" above). That needs vendor documentation, not a sweep. It is the same ask already
open for the LED — `docs/headset/starlight/starlight-vendor-request-2026-08-07.md` — and a battery
query opcode should be added to it rather than raised separately.

### DECISION: battery ships only when proven

Cosmo shipped a battery reading from a byte that never changed across ~58 samples over four hours,
deliberately, to be observed in the field. It has still not been confirmed.

**Starlight will not repeat it.** `SupportsBattery: false` unless Phase 0a observes a value that
actually moves with the real battery level. A wrong battery indicator is worse than none, because
the user cannot tell it apart from a real reading. A plausible-but-static candidate byte gets
recorded in `docs/headset/starlight/` for a future session, not surfaced in the UI.

*(Outcome, 2026-08-19: no candidate byte was found to record — see 0f RESULT above. The dongle
transport keeps `SupportsBattery: false`. The **Bluetooth** transport added in PG-608 sets it to
`true` on a real, moving Windows property, which is the standard this decision asked for.)*

## Options considered

### Option A — Catalog first, LED service behind the probe *(recommended)*

Land the catalog entry immediately: Presets, Audio and Firmware work with no code. Run Phase 0, then
add the LED service and flip `SupportsRgb` in a second change.

- **Pro:** the device is usable straight away, and the audio behaviour a headset user actually wants
  is not held hostage to a vendor-channel investigation.
- **Pro:** no protocol code written against an unproven channel.
- **Con:** two changes and two QA passes.

### Option B — One complete change after the probe

- **Pro:** a complete device on arrival, one review.
- **Con:** blocks working audio behind an investigation that may be inconclusive. Cosmo's equivalent
  took a week and ended in a vendor request that is still unanswered.

### Option C — Implement the LED spec against `0xFD` on assumption, no probe

- **Pro:** if right, lighting lands in one step.
- **Con:** writes an unvalidated protocol to a flash-backed vendor channel on customer hardware. The
  probe in 0b costs one command and removes the guess entirely.
- **Rejected.**

## Recommended approach

**Option A.** Catalog entry first; LED service second, gated on 0b.

For the LED service itself, map the App's existing RGB effects onto the spec's four operation modes
rather than inventing a parallel vocabulary:

| App effect | Operation mode | Colour array |
| --- | --- | --- |
| Static / solid colour | `0x00` Static | 1 entry — the chosen colour |
| Colour cycle | `0x01` Repeat Forward | the palette, interval from the speed slider |
| Breathing / sweep | `0x02` Back And Forth | the palette, interval from the speed slider |
| Audio reactive *(new)* | `0x03` Lookup Table | power-of-two palette, quiet → loud |

`0x03` Lookup Table is genuinely new for this app — the firmware indexes the array by audio output
amplitude, giving a reactive effect with no host involvement. Worth exposing, and worth noting it is
the one effect no other supported device can do.

**Brightness** has no field in this protocol. If the App's brightness slider is shown for Starlight,
it must be applied by scaling the RGB values host-side before they are sent, or hidden. Decide in the
LED change, not here.

## File-level change list

### Phase 0 — probe (`ProoveGaming.Device.Testing`)

- `src/ProoveGaming.Device.Testing/StarlightProbe.cs` — **exists, extend.** Add `starlight watch`
  (guided passive listen on `0xFD`) and `starlight read` (`HidD_GetInputReport` on `0xFD`), both
  read-only; and `starlight led` for 0b, which **WRITES** and must refuse to run without an explicit
  opt-in argument.
- `src/ProoveGaming.Device.Testing/Program.cs` — dispatch the new arms; extend the
  "WHICH ARMS WRITE TO THE DEVICE" table to cover them.

### Phase 1 — catalog (no code)

- `src/ProoveGaming.DeviceHost/appsettings.json` — **new Starlight entry**, modelled on Cosmo:
  `Name`/`DeviceName` "Starlight", `VendorId` `"3468"`, `ProductId` `"877"`,
  `DeviceType` `"Wireless Headset"`, `CardType` `"single"`,
  `ImagePath` `/Assets/Headphones/Starlight-device-standard.png`,
  `SupportedAudioSettings: ["OutputVolume", "MicrophoneVolume"]`,
  `AudioVolumeTarget: "OperatingSystem"`, **`SupportsBattery: false`**,
  **`SupportsRgb: false`** (flipped to `true` in Phase 2), and one transport whose `ConnectionType`
  comes from Phase 0c.
- `src/ProoveGaming.App/Assets/Headphones/Starlight-device-standard.png` — **already added.**

### Phase 2 — RGB lighting (gated on Phase 0b confirming the channel)

- `src/ProoveGaming.Infrastructure/Classes/StarlightLedFrame.cs` — **new.** Pure encoder for the
  four commands: Start, Stop, Configuration (mode, LE array size, LE interval) and Colour Array
  (LE offset + four RGB triples). Pure and unit-testable, exactly as `X43ConfigComposer` is — the
  byte layout is the part worth locking down in tests rather than only on hardware.
- `src/ProoveGaming.Infrastructure/Services/StarlightLedService.cs` — **new.** Implements the
  device-facing RGB interface by driving the documented sequence in order
  (Stop → Configuration → Colour Array… → Start), chunking the palette four colours per report.
- `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` — register Starlight's
  VID/PID so the DeviceHost hands out the new service.
- `src/ProoveGaming.DeviceHost/appsettings.json` — `SupportsRgb: true`, and the device's supported
  effect list if the catalog drives effect visibility.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — only if the new
  audio-reactive mode is exposed; otherwise untouched.

### Docs

- `docs/CONTEXT.md` — glossary: **Starlight**, **Jieli**, **Starlight LED control**; cross-reference
  from the existing **CM002X1** entry noting the LED spec and that Starlight is Jieli silicon.
- `docs/device-identity.md` — correct the claim that Cosmo's `iSerialNumber` demonstrates per-unit
  identity; record the identical-serial finding.
- `docs/headset/starlight/` — Phase 0 results written up next to the LED spec PDF.

### Tests

- `tests/ProoveGaming.Infrastructure.Tests/StarlightLedFrameTests.cs` — **new** (Phase 2). Byte
  layout of all four commands; little-endian array size, interval and offset; palette chunked four
  per report with the last partial chunk handled; a Static config forces array size 1 and interval 0.
- `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs` — extend: a headset entry with
  `SupportsBattery: false` expands with battery unsupported, and audio settings survive per
  transport with the list cloned. Guards the one field Starlight sets differently from every
  existing device.
- `tests/ProoveGaming.App.Tests/DeviceDetailsNavVisibilityTests.cs` — extend: a Starlight-shaped card
  shows `Presets, Audio, Firmware` while `SupportsRgb` is false, and gains RGB Lighting when it is
  true — pinning the Phase 2 switch before it is thrown.
- `tests/ProoveGaming.App.Tests/DeviceCardConnectionTooltipTests.cs` — extend: `SupportsBattery:
  false` with no level yields a transport-only tooltip.

## Acceptance criteria

**Phase 1 (catalog)**

1. Given the Starlight is connected and the DeviceHost is running → open the App → a **single card**
   named "Starlight" appears with the Starlight artwork.
2. Given the card is shown → disconnect → the card goes to its disconnected appearance (faded
   artwork, "NO CONNECTION") within the normal discovery interval.
3. Given the card is connected → open details → the nav shows exactly **Presets, Audio, Firmware**,
   and no Buttons, DPI or Settings.
4. Given the Audio section is open → move **Output Volume** → the Windows volume for the Starlight
   speakers changes to match, and no other device's volume changes.
5. Given the Audio section is open → move **Microphone Volume** → the Windows level for the Starlight
   microphone changes to match.
6. Given a volume was changed → restart the App → the sliders show the set values, not defaults.
7. Given Audio levels changed → **Save Preset** enables; saving and switching presets restores them.
8. Given the headset's **physical volume +/- buttons** are pressed while the Audio section is open →
   the Output Volume slider follows the new level within ~1 second, without the user touching it.
9. Given the device is connected → the card shows **no battery indicator**, and hovering the
   connection icons shows a tooltip with **no battery line**.

**Phase 2 (lighting)**

10. Given lighting is supported → the details nav shows **RGB Lighting**.
11. Given RGB Lighting is open → pick a solid colour → the headset LED shows that colour within a
    second.
12. Given a solid colour was applied → unplug the device and plug it back in → the LED still shows
    that colour (the device persists its table to flash).
13. Given an effect with a palette is selected → the LED cycles that palette; changing the speed
    slider visibly changes the cycle rate.
14. Given lighting is turned off in the App → the LED goes dark and **stays** dark across a replug.

**Both**

15. `dotnet build ./src/ProveGaming.slnx` succeeds and `dotnet test ./src/ProveGaming.slnx` is green.

## Test strategy

This is an `add-*` change, so the coverage below is required, not optional.

**`tests/ProoveGaming.Infrastructure.Tests`**
- `StarlightLedFrameTests` *(Phase 2)* — the encoder is where a protocol bug would otherwise only be
  visible on hardware, so it carries the most weight: exact bytes for Start, Stop, Configuration and
  Colour Array; little-endian encoding of array size, interval and offset (the spec changed to LE in
  v0.2, so a big-endian slip is a live risk); a palette of 5 colours produces two Colour Array
  reports with the second carrying one colour; Static forces array size 1 and interval 0.
- `DeviceMapExpanderTests` — `SupportsBattery: false` survives expansion (a bool defaulting to
  **true** is exactly the kind of field that silently flips); audio settings copied per transport
  with the list cloned, not shared.

**`tests/ProoveGaming.App.Tests`**
- `DeviceDetailsNavVisibilityTests` — Starlight-shaped card yields `["Presets", "Audio", "Firmware"]`
  with RGB hidden, and gains RGB when `SupportsRgb` is true.
- `DeviceCardConnectionTooltipTests` — battery-less headset yields a transport-only tooltip.

**Deliberately not covered by automated tests**
- Core Audio endpoint matching and real volume changes — real COM against real endpoints. Cosmo
  established this is hand-verifiable only; it is in the QA list.
- Anything sent on report `0xFD` — requires the physical device. That is what Phase 0 is for, and
  why the frame *encoder* is split out as a pure class: the bytes are testable even though the
  transport is not.
- The physical volume buttons — the App cannot observe the keypress at all, only the resulting
  endpoint level, so there is nothing to unit-test. QA step 8 covers it.

## QA (manual test steps)

**Phase 1 — the device and its audio**

1. Plug in the Starlight and switch the headset on. Launch the app.
   **Expected:** a card named "Starlight" appears with the headset picture, shown as connected.
2. Switch the headset off, or unplug it, and wait a few seconds.
   **Expected:** the card dims and shows "NO CONNECTION". It does not disappear from the page.
3. Reconnect, then click the Starlight card to open its details.
   **Expected:** the left-hand menu shows **Presets**, **Audio** and **Firmware**. No Buttons, DPI or
   Settings.
4. Open **Audio**. Drag **Output Volume** to about 20%.
   **Expected:** Windows' volume mixer shows the Starlight speakers at about 20%. No other device's
   volume moves.
5. Drag **Microphone Volume** to about 80%.
   **Expected:** Windows Sound settings shows the Starlight microphone at about 80%.
6. Close the app completely, reopen it, return to **Audio**.
   **Expected:** both sliders show the values from steps 4 and 5, not 50%.
7. Change a slider and look at **Save Preset**.
   **Expected:** it enables. Save it, change the volume again, switch to that preset.
   **Expected:** the saved volume is restored.
8. **With the Audio section still open**, press the **volume + and volume −** buttons on the headset
   itself a few times.
   **Expected:** Windows' volume changes, *and* the Output Volume slider in the app follows it within
   about a second without you touching it. It should not fight you or jump back.
9. Look at the card on the home page, and hover the connection icons.
   **Expected:** no battery percentage anywhere, and the tooltip has no battery line. This is
   intended for now — battery is not readable on this device yet.

**Phase 2 — lighting** *(only after the lighting change lands)*

10. Open the Starlight details.
    **Expected:** the menu now also shows **RGB Lighting**.
11. Open **RGB Lighting** and pick a solid red.
    **Expected:** the headset LED turns red within about a second.
12. Unplug the headset, wait five seconds, plug it back in — **without** opening the app.
    **Expected:** the LED is still red. This device stores its lighting itself.
13. Pick an effect with several colours and drag the speed slider.
    **Expected:** the LED cycles through the colours, and the cycle visibly speeds up or slows down.
14. Turn lighting off in the app, then unplug and replug again.
    **Expected:** the LED stays dark. It must not come back on by itself.
15. If an **audio-reactive** effect is offered, select it and play music.
    **Expected:** the LED colour changes with how loud the audio is.

## Review focus

- `null-safety` — the catalog fields (`SupportedAudioSettings`, `AudioVolumeTarget`) are nullable and
  flow through the expander into the proto.
- `resource disposal` — Phase 2 opens a HID stream per LED write; it must be disposed on every path,
  matching how `EasenMouseService` handles its stream.
- `thread-safety` — Phase 2's LED writes must serialise against each other the way the other device
  services do; the RGB UI can produce rapid successive applies from the dispatcher thread.
- `gRPC contract` — Phase 1 adds **no** proto fields and must not; flag it if any appear.

## Performance & quality

**Phase 1: none.** A configuration entry — no new loop, allocation or device I/O. The audio volume
poll it enables already exists, is gated on the Audio section being visible, and is unchanged.

**Phase 2 — the one real constraint is flash wear.** The device writes its LED table to flash on
every **Start** and erases it on every **Stop**. So:

- **Never drive an animation from the host.** Effect playback is firmware-side by design; a
  host-side loop issuing Start repeatedly would wear the flash out. This is the opposite of BLAZE,
  where we drive frames.
- **Debounce the colour picker.** Dragging a colour wheel must not emit a write per pixel — reuse the
  existing RGB staging/debounce path so one apply follows the drag, as the other devices do.
- Each apply is a handful of 32-byte reports; there is no hot path and no allocation concern beyond
  reusing the report buffer.

**Shared state.** If Phase 2 adds any cache, the device services are singletons touched from both the
device-I/O thread and the UI thread — use a concurrent collection, matching the existing pattern.

## Risks

- **The LED spec may not describe this firmware.** `0xFF` is absent and the silicon is Jieli, not
  CM002X1. Settled by 0b attempts 1-4: 12 delivery framings plus 16 operation-mode attempts, the
  documented command set in every shape we could construct, none of which drove the LED. Unknown opcodes were never swept
  and must not be — see "The operative safety boundary".
- **Writes to `0xFD` could brick the device**, and this one persists to flash. This is the BLAZE
  failure mode. Mitigated by explicit authorisation, the documented layout only, and aborting on
  de-enumeration.
- **Flash wear** if lighting is ever driven per-frame from the host. Called out above; worth a
  reviewer check on any loop that reaches a Start command.
- **Two same-model units may share a Container ID**, because the serial is a shared ODM constant.
  Not measured — we have one Starlight. If a second appears, per-device settings could collide
  silently. Worth testing before any customer has two.
- **`SupportsBattery: false` may read as a regression** next to Cosmo, which shows a battery. It is
  deliberate; repeat the reasoning in the PR description.
- **Audio endpoints must carry the Container ID** for the sliders to bind to the right device.
  **Closed by measurement 2026-08-07** — both endpoints report
  `99e3b024-79e0-5516-ace7-150b21eb9dd0`, byte-identical to the HID interfaces, so
  `AudioEndpointService` resolves them. See "Endpoint Container ID" below. This was previously
  assumed from the Cosmo analogy and is now verified.

## Open questions

1. **Is there vendor software that drives the Starlight LED?** *(ask before authorising 0b)*
   Partly answered: the factory SDK bundle contains an *audio* APO package for a sibling product
   (PID `0x0367`) and no lighting tool at all — see the SDK section above. So the question narrows
   to: does an LED utility exist for PID `0x036D`? If it does, capturing it settles the channel and
   the payload in one session with no writes of our own. Every device in this repo was decoded that
   way — BLAZE most recently, where a capture ended a week of guessing in an afternoon.
2. **Is Starlight wireless (2.4G dongle) or wired?** Sets `ConnectionType` in the catalog. Phase 0c
   answers it; until then the entry is written as `Dongle2_4G` by analogy with Cosmo and **must be
   confirmed before merge**.
3. **How many LEDs does the headset have, and are they addressable individually?** The spec's colour
   array is a *time* sequence for one logical LED, not a per-LED map. If Starlight has several LEDs
   that should differ, this protocol cannot express that and the vendor needs asking.
4. **Should the brightness slider be shown?** The protocol has no brightness field. Either scale RGB
   host-side or hide the control for this device — decide during the Phase 2 change.

### 2026-09-09 — the Xear SDK was re-supplied and has now been RUN, not just read

The customer delivered `C:\Starlight_SDK` (CmXearSDK V1.0.28.0 + the AUSDOM A3 Pro driver/APO
package) asking what it enables. It is the same bundle this plan already describes, and it was
executed against the live dongle rather than read off its headers. Full results:
`docs/headset/starlight/starlight-research-2026-09.md`.

What it changes here — **nothing about lighting**, and question 1 above still stands: the SDK has no
LED API of any kind, so it is not the vendor LED utility we are asking for.

What it establishes:

- **We can already change things on the device, license-free.** `MutingState`, `VolumeLevelScalar`,
  `SystemEffectsControl`, `DefaultDevice` (both roles) and per-channel `CmPlayAudio` all land and
  read back on the live dongle. `DefaultFormat` is refused — correctly: `IsFormatSupported` shows
  the headset is **48 kHz / 16-bit / stereo only**, so there is no format choice to expose.
- **The license GUID exists.** `CM002X1_AUSDOM_A3_PRO_GUID.txt` holds
  `158FD125-36BD-4A73-9E07-518869EDD288`, which the Chinese demo-app guide identifies as the
  `CmActivateLicense` input. It activates (`CE_OK`) and registers globally.
- **But all 78 APO effect properties still return `CE_LicenseError`**, and per-endpoint
  `SupportedLicenseCategory` stays `0`.

**Why that last point is not yet a verdict.** The guide's step 3d says the per-endpoint License
Information is the APO reporting whether it is mounted — so with no APO installed there is nothing
to report, whatever the hardware holds. Starlight's all-zero `0xFD` (this plan's 2026-08-07 finding)
makes "no licensing coprocessor" plausible, but it is not proven: "APO absent" produces the same
reading. The supplied INFs bind `PID_0367`, so the APO cannot install on `036D` without C-Media
re-signing it. Separating the two causes is the open experiment.

The non-APO half that does work is Core Audio underneath, which `AudioEndpointService` already calls
directly — so **no SDK integration is recommended**; the APO effects are the only thing it offers
that we cannot write ourselves. The catalog entry is unchanged.

### 2026-09-09 — report `0xFD` is NOT dead, and a CM002X1 answers on Starlight

**This corrects a hardware fact this plan asserts above.** The plan records that report `0xFD`
returns 32 zero bytes in every state and treats the vendor channel as unreadable. That measurement
called `HidD_GetInputReport` without ever sending the documented `0x12` request first — but `0xFD`
is a request/response HID↔I2C bridge, so it was reading an unpopulated buffer.

Sent correctly (`FD 12 7A <map> <len>`, then get input report), the device answers
`FD 13 7A <map> <len> <data>` on **all 256** sub-addresses. The control that makes it conclusive:
the same request aimed at slave `0x10`/`0x50`/`0x00` returns all-`0xFF` — an undriven I2C bus —
while `0x7A` returns driven data. **A real chip acknowledges at `0x7A`**, the address C-Media
documents for the CM002X1.

So the claim "Starlight is Jieli silicon and the licensing coprocessor is absent" is withdrawn.

Consequences for this plan:

- **The vendor channel parses commands**, so a write to it is understood or refused on purpose, not
  swallowed. The Phase 0b LED conclusion ("12 framings x 16 modes, none changed the LED") was
  reached without any way to tell those apart, and is therefore weaker than it reads.
- **A response oracle now exists** — `0x12` visibly changes the input buffer, so "understood" can be
  distinguished from "ignored". This is the tool Phase 0b lacked.
- **RGB is still not driven.** Nothing here lit an LED, and the CM002X1 is an authentication part
  that carries no RGB data. The lighting question is open again, not answered.
- The deliberate stop on sweeping unknown opcodes at this channel **still stands**. Any oracle-driven
  retry must use only the four documented LED commands, never `0x02` (erases the flash table), never
  `0x11` (I2C write), and needs explicit sign-off.

Full write-up: `docs/headset/starlight/starlight-research-2026-09.md`. The 2026-08-07 vendor
letter needs its section 1 corrected before it is sent again.

### 2026-09-10 — the oracle was used, and the LED commands are not implemented

The response oracle above was put to work. Buffer semantics first: it is **consume-on-read**, so each
test is one plant, one command, one read. A **positive control** (a known-handled `0x12` at a
different sub-address, pushed through the identical code) was detected, so the negatives mean
something.

With that established, all three documented LED commands — `0x03` Configuration, `0x04` Colour Array,
`0x01` Start — were sent in both plausible framings (`FD <cmd> …` and `FD FF <cmd> …`). **Every one
left the marker intact**: no response at all, while `0x12` answers reliably through the same path.

So Phase 0b's conclusion is now supported by a real measurement rather than the absence of a visible
change: **the dongle does not recognise the CM002X1 LED command set on `0xFD`.** `SupportsRgb` stays
`false`.

**Confirmed visually the same day**: re-run interactively with the headset in view and a pause after
every command, the LED did not change at any point — no colour, effect, brightness or flicker, on
either framing. So both signals agree, and the lighting negative finally rests on a measurement
rather than on the absence of a visible change.

Two limits remain: the LED is on the headset while USB reaches only the dongle, and nothing documents
that 2.4G relay — a well-formed command could be parsed by the dongle and never reach the headset;
and only two of the twelve framings were retried with the oracle.

Question 1 of Open questions is unchanged and is now the whole ask: **a capture of vendor software
driving the LED**, or the framing for `PID_036D`.

Write-up: `docs/headset/starlight/starlight-research-2026-09.md`.

## Review findings

### Round 1 — Ship: no

#### Plan conformance

Plan: `docs/plans/add-starlight-headset/add-starlight-headset-plan.md`

- **Implemented as planned:** Phase 1 catalog entry (VID `3468` / PID `877`, `Wireless Headset`,
  `single`, Starlight PNG, both audio settings, `AudioVolumeTarget: OperatingSystem`,
  `SupportsBattery: false`, `SupportsRgb: false`, `Dongle2_4G`); the Phase 0 probe arms; the
  `StarlightCatalogTests` suite; the CONTEXT.md glossary entries; the LED spec transcription.
- **Resolved since the plan was written:** open question 2 (transport) is settled — the device stays
  enumerated with the headset off, so `Dongle2_4G` is measured, not assumed.
- **Deliberately not implemented:** Phase 2 (`StarlightLedFrame` / `StarlightLedService` /
  `StarlightLedFrameTests`). Correct — it was gated on Phase 0b confirming a channel, and 0b failed
  across 12 framings and 16 operation-mode attempts. Nothing in the tree claims RGB support.
- **Accepted limitation (per plan):** battery, power state, mic mute and RGB are all unreachable.
  Residual risk is user-visible and recorded in the vendor request: the app shows a powered-off
  headset as connected, and cannot show mic mute.
- **Acceptance criteria:** 1-9 are achievable with the shipped code; 10-14 (lighting) are
  correctly unmet and correctly not claimed.

#### Code

- [x] **B1. 16.2 MB of vendor binaries are permanently in this branch's history** — `docs/headset/starlight/SDK/**` — Blocker

  **Problem:** the SDK bundle was added in `5eaf0e9` (101 files) and deleted in `b3814f4` (101
  files). It is absent from the final tree, so `git diff main...HEAD` shows nothing — but the blobs
  are in the commit history and merge into `main` with it. Measured: **17.5 MB of new blobs on this
  branch, 16.2 MB of it SDK**, including `CmApoIce.dll` (6.3 MB), the Xear API PDF (4.3 MB), two
  driver ZIPs (3.0 MB), plus `.sys`, three `.cat`, a `.crt` certificate and two installer `.exe`s.

  **Why it blocks:** deleting a file does not remove it from history. Once this merges, every clone
  of `main` pays 16.2 MB forever for files nobody can see in the tree, and removing them afterwards
  requires a history rewrite coordinated across everyone who has cloned. Fixing it now is a squash;
  fixing it later is not. There is also a redistribution question — a vendor's signed driver, DLL,
  code-signing certificate and installers are being committed to a repo, which is a licensing call
  rather than a technical one.

  **Solution options:**
  - **A (correct):** squash the branch so the add-and-delete never enters `main`'s history —
    `git rebase -i main` (or reset to the merge-base and recommit the final tree as one commit),
    then force-push. The final tree is unchanged; only the history is. Verify with
    `git rev-list main..HEAD --objects | git cat-file --batch-check` showing no SDK blobs.
  - **B (keep the SDK deliberately):** if the bundle is *meant* to be versioned, restore it and add
    it in one commit rather than add-then-delete — but resolve the redistribution question first,
    and consider Git LFS for the binaries.

- [x] **S1. The `native` / `short` LED variants require a HidSharp open they never use** — `src/ProoveGaming.Device.Testing/StarlightProbe.cs:281` — Should-fix

  **Problem / why it matters:** `RunLed` opens a HidSharp stream at line 281 and returns early if
  `TryOpen` fails. The `native` and `short` paths then immediately `Dispose()` it (line 347) and do
  all their work through `CreateFileW` + `HidD_SetOutputReport` instead. So a device that HidSharp
  cannot open — but that a shared `CreateFileW` handle *can* — never reaches the spec-verbatim
  attempt, which is precisely the variant most likely to work. The dependency is accidental, not
  required.

  **Fix:** move the `TryOpen` inside the `else` branch that actually uses the stream, so the raw
  paths depend only on `device.DevicePath`.

- [x] **S2. `StarlightCatalogTests` reads the source tree at run time** — `tests/ProoveGaming.Infrastructure.Tests/StarlightCatalogTests.cs:114,131` — Should-fix

  **Problem / why it matters:** the test walks up from `AppContext.BaseDirectory` looking for
  `src/ProveGaming.slnx`, then reads `src/ProoveGaming.DeviceHost/appsettings.json`. Run the test
  assembly anywhere the source tree is not an ancestor — a published test bundle, a copied artifact
  directory — and `RepoRoot()` walks to null and the run fails on an assert rather than a clear
  message. CI does not currently run these tests, so this is latent rather than active.

  **Fix:** add a `<None Include="..\..\src\ProoveGaming.DeviceHost\appsettings.json" CopyToOutputDirectory="PreserveNewest" Link="appsettings.json" />` item to the test csproj and read it from
  `AppContext.BaseDirectory`. That also makes the test hermetic.

#### Tests

- [ ] `StarlightCatalogTests` — 7 tests, verified to discriminate (a typo'd PID and a dropped
  `SupportsBattery` line fail 4 of them, including the duplicate-VID/PID guard). Good coverage for a
  config-only change, which would otherwise be untested entirely.
- [ ] No automated coverage for the probes. Correct and deliberate — they require physical hardware
  and are diagnostic tools, not product code.
- [ ] Note `dotnet-ci.yml` builds only the App csproj, so none of these 1313 tests run in CI. Not
  introduced by this branch, but it means `StarlightCatalogTests` only protects developers who run
  tests locally.

#### Nits

- `RawInputDpi._onReport` is a static field set and cleared by `Listen`; two overlapping `Listen`
  calls would have the first's `finally` clear the second's callback. Only one listener is ever
  active today — `src/ProoveGaming.Device.Testing/RawInputDpi.cs:20`.
- `StarlightPowerProbe` uses `Task.Delay(...).Wait(token)` inside `Task.Run`, the sync-over-async
  pattern the repo bans. No deadlock risk in a console app with no sync context, and the
  surrounding probes already block on `Thread.Sleep`, so it is consistent with its neighbours —
  `src/ProoveGaming.Device.Testing/StarlightPowerProbe.cs`.
- `StarlightLedSweep.Send` opens and closes the device per frame rather than once per sequence.
  Works; just chattier than it needs to be.

#### What's done well

- **The negative results are documented as thoroughly as a feature would be.** Four probe results
  with exact payloads, phase tables and byte-level decoding. The next person cannot accidentally
  repeat two days of work, which is the main value this branch delivers.
- **Every write path is gated behind an explicit `--i-mean-it`,** and the refusal text explains the
  flash-persistence risk rather than just declining. The gate was verified to hold on all three
  writing arms.
- **Three of the author's own earlier conclusions are corrected in-place** rather than quietly
  dropped — the `watch` arm listening on the wrong collection, the "device pushes nothing" claim,
  and the stall signal that does not exist on a declared output report. Each correction says what
  was wrong and why.
- **The catalog is honest about what the device cannot do.** `SupportsBattery: false` and
  `SupportsRgb: false` are both backed by measurement, and nothing in the UI claims a capability the
  hardware lacks.
- **`RawInputDpi` was generalised without disturbing the BLAZE path** — `Run()` still routes through
  the original constants and extra pairs, and the `vid_{vid:x4}` format reproduces the previous
  hard-coded match exactly.
- **The vendor request is a genuinely strong document:** it leads with a status table, separates
  user-visible defects from missing extras, states what was already tried so it cannot be handed
  back, and notes that mic mute would work for free if the firmware sent the Mute usage it already
  declares.

#### Verdict

Ship: **no** — one blocker, and it is about history rather than code. B1 is trivial to fix now
(squash the branch) and expensive to fix after merge (coordinated history rewrite), so it must be
resolved before this lands. The code itself is sound: build clean, 1313 tests green, every write
gated, no leaks, and no false capability claims.

First: 1) B1 — squash so the SDK blobs never enter `main`, and settle whether the bundle should be
versioned at all. 2) S1 — drop the accidental HidSharp dependency in the raw LED variants. 3) S2 —
copy `appsettings.json` to the test output so the catalog test is hermetic.


*(filled in by /reviewer)*

### Round 1 — fixes applied

- **[x] B1 — squashed.** `main` was already a direct ancestor, so `git reset --soft main` plus one
  commit produced an identical tree with the add-and-delete gone from history. Verified: SDK blobs
  on the branch **101 -> 0**, total new blobs **17.5 MB -> 1.2 MB**, and
  `git diff pre-squash-backup HEAD` shows *only* the review journal — no file content was lost.

  **Not finished until the branch is force-pushed.** `origin/feat/PG-Starlight-headset` still
  carries all 16.2 MB, so merging without `git push --force-with-lease` reintroduces every blob.
  The old tip is tagged `pre-squash-backup`; the SDK stays recoverable from it
  (`git checkout 5eaf0e9 -- docs/headset/starlight/SDK`) until that tag is deleted, which is worth
  doing before deleting it if those files should be kept anywhere.

- **[x] S1 — fixed.** `StarlightProbe.RunLed` no longer opens a HidSharp stream before branching.
  The `native` / `short` paths run entirely through `CreateFileW` + `HidD_SetOutputReport` and
  return before any `TryOpen`, so a device HidSharp cannot open can still receive the spec-verbatim
  attempt. The HidSharp open moved into the branch that actually uses it.

- **[x] S2 — fixed.** The DeviceHost catalog is now copied into the test output as
  `appsettings.DeviceHost.json` (`<None Include=... Link=... CopyToOutputDirectory="PreserveNewest">`)
  and read from `AppContext.BaseDirectory`. The directory walk is gone, the failure message names
  the csproj item to check, and the link name cannot be confused with the test host's own config.

  Re-verified the test still discriminates rather than reading a stale snapshot: breaking the real
  `appsettings.json` (typo'd PID, dropped `SupportsBattery`) still fails the same 4 of 7 tests, so
  `PreserveNewest` is picking up catalog edits.

Build clean, no new warnings, 1313 tests green.

### Round 2 — external review (M1), addressed

**M1 — the plan asserted a "two attempts then stop" boundary the branch had already crossed, and
the results of the extra attempts were missing.** Correct on all three counts, and it defeated the
plan's stated purpose: someone reading it in six months would have concluded two framings were tried
and written a third and fourth arm that already exist and had already run — re-doing risky work on a
flash-backed channel, which is exactly what the plan was written to prevent.

Taken as reported, and fixed with **Option A**:

- **[x] Attempts 3 and 4 are now recorded** as full result sections — the 12-shape framing matrix
  (`StarlightLedSweep`, 48 writes per run, with the shape table and what each rules out) and the
  operation-mode probe (`StarlightEffectProbe`, modes `0x00`-`0x07`, both passes, no `Stop`). Each
  names the arm that produced it, so a reader can map a finding to the code that found it.
- **[x] A "Phase 0b — final state" section** lists all six arms with their results in one table,
  headed *do not rebuild these*.
- **[x] The four boundary statements are rewritten.** They stated the rule by **count** ("two
  attempts"), which conflated *how many* attempts with *what is in them*. The operative rule is now
  stated by **content**: documented commands in any framing are allowed; widening a documented
  parameter needs recorded authorisation; unknown **opcodes** stay prohibited. That last line is the
  one that actually protects the device, and it was never crossed.
- **[x] The authorisation gap is recorded** rather than papered over. Both expansions were
  authorised by the user in conversation on 2026-08-07, but — unlike the Cosmo sweep — neither was
  written down *before* it ran. The plan now says so explicitly and states that future range
  expansions should be recorded first.
- **[x] Attempt 2's heading no longer says "STOPPING"**, and its closing paragraph now says plainly
  that the stop did not hold and that its reasoning was partly wrong: it treated *framing* variation
  as equivalent to *opcode* sweeping, when only the latter is dangerous.

The reviewer's point about the vendor request is worth keeping visible: the letter described both
runs as done while the research journal did not, which meant the outgoing document was ahead of the
internal record. The journal is now the authority and the letter agrees with it.

### Round 2 — external review (M2), addressed

**M2 — three documents gave three different counts for the same work, and the inflated one went to
the vendor.** Verified against the code and correct on every point.

Counted from `StarlightLedSweep.Framings`: **12** entries, of which the first four are the
previously-tried single-shot framings, deliberately included so the run is self-contained. So 12 is
the total number of distinct framings, not 12 on top of 4. The separate `StarlightEffectProbe` axis
is **8 mode values × 2 passes = 16 attempts**. "Sixteen framings" was those two axes conflated.

Fixed with **Option A** — counted from the code, then all three documents brought into line and the
two axes named separately rather than summed:

- **[x] `starlight-vendor-request-2026-08-07.md`** — heading now reads *"12 command framings and 16
  operation-mode attempts"*, and section 2 states the two axes explicitly: Axis 1, the 12 delivery
  combinations (48 output reports per pass); Axis 2, the 16 mode attempts without `Stop`. This is
  the document that leaves the building, and its whole value is that a vendor engineer can check it.
- **[x] `docs/CONTEXT.md`** — the glossary said *"Four framings were tried"*, written before the
  sweep existed and never updated. Now 12 framings plus 16 mode attempts, with the axes listed. This
  was the more insidious of the two: the glossary is what the next developer reads, and it
  understated the work by two thirds — the same trap as M1.
- **[x] The plan** — three inflated "sixteen framings" claims corrected.
- **[x] Drift guard added** — attempt 3 now states that the `Framings` array is authoritative and the
  probe prints `Framings.Length` at run time, so prose can be checked against code rather than
  against other prose.

`Program.cs:52` and `StarlightLedSweep.cs:9` were already accurate and are unchanged.

### Round 2 — external review (M3), addressed

**M3 — `docs/device-identity.md` was in this plan's Scope and File-level change list but was never
touched; only the glossary was updated.** Confirmed: `git log main..HEAD -- docs/device-identity.md`
returned nothing. A genuine plan-conformance miss, and the reviewer is right that the glossary does
not cover it — a glossary defines a term, while `device-identity.md` is the document someone opens
when *deciding how to key per-device settings*, and it was recommending the wrong thing.

Fixed with **Option A**. Four edits, because the stale claim appeared in more places than the two
cited:

- **[x] TL;DR (lines 20-31).** The bullet claiming Cosmo *"proves the fix works"* and is *"the first
  device tested here that does"* is split into the half that survives and the half that does not:
  a serial does make the Container ID **port-stable** (still true, still valuable), but it does
  **not** confer per-unit identity, because Starlight (`0x036D`, Jieli) reports the byte-identical
  string to Cosmo (`0x0334`, C-Media).
- **[x] "The weakness is stability, not uniqueness" (line 45).** This was the sentence the reviewer
  did not cite and the one that most needed changing: it is true for a device with *no* serial and
  false for one with a *constant* serial. It now states both cases and names which failure is worse
  — a port-derived ID fails loudly and recoverably, a shared serial fails silently and permanently.
- **[x] "1. Firmware reports a unique `iSerialNumber` — the real fix" (line 105).** The
  recommendation stands, but the ask is reworded to **per-unit unique, not merely present**, with the
  note that this ODM already ships a serial that is neither — so "report an `iSerialNumber`" is
  demonstrably not a sufficient request.
- **[x] New subsection "Serial numbers that are not unique"** carrying the measurement, both linked
  anchors resolving. It separates what actually breaks (nothing across models, since Container ID
  incorporates VID/PID) from what plausibly breaks (two units of one model sharing a `deviceKey`),
  and flags that the same-model collision is **implied but not measured** — we have one of each, not
  two of either.

The existing line at 94-97 — *"even when a serial is present it may not be unique; it has to be both
present and distinct per unit"* — already said the right thing. The document contained its own
correction and the Cosmo bullet contradicted it; that contradiction is now gone.

### Round 2 — external review (M4), addressed

**M4 — nothing in the branch verified the endpoints' Container ID, which is the key the app matches
on and the one thing the entire user-facing change depends on.** Correct, and the sharpest of the
four findings: the probe searched by *friendly name* while `AudioEndpointService` matches by
*Container ID*, so "we found the endpoints" was evidence for a different proposition than the one
that mattered. The reviewer's observation that the probe was already opening the property store —
one key short of closing the question — is exactly right.

Fixed with **Option A**, and then run:

- **[x] `StarlightAudioProbe` now reads `DEVPKEY_Device_ContainerId`** for every matched endpoint,
  mirroring `AudioEndpointService.ReadContainerId` including the `VT_CLSID` check and
  `PropVariantClear`. It prints the value per endpoint, compares against the HID Container ID, and
  states plainly whether the app can resolve them.
- **[x] Measured:** both endpoints report `99e3b024-79e0-5516-ace7-150b21eb9dd0`, identical to the
  HID interfaces. The app resolves them; the sliders bind correctly.
- **[x] The plan's risk entry is rewritten** from "if Starlight's do not, the sliders will silently
  do nothing" to the measurement that closed it, with a results subsection recording the values.
- **[x] The probe header no longer implies it tested the app-side claim.** It now says explicitly
  which key it matches on, which key the app matches on, and why finding an endpoint by name proves
  nothing about the app finding it.

The outcome is benign — Windows assigned one Container ID across the composite device's functions,
as it did for Cosmo. But the reviewer's framing is the right one: "probably fine by analogy" was not
the standard the rest of this branch held itself to, and it is now measured like everything else.

### Round 3 — Ship: no

**Scope note.** This round reviews the whole branch `fix/fixes-on-09.10.26` (commit `6eceda1`),
which spans two unrelated changes. The Starlight research belongs to this plan. The **uninstall
self-delete race fix** has **no owning plan** — its intent document is
`docs/support/missing-files-crash-analysis.md` (cause 0) — and is reviewed code-only. Its findings
are recorded here because this is the only plan in the branch delta and the next `/coder` round has
to find them somewhere; they are *not* Starlight work.

#### Plan conformance

Plan: `docs/plans/add-starlight-headset/add-starlight-headset-plan.md` (Starlight portion only)

- Implemented as planned: research notes appended for 2026-09-09 (Xear SDK run on hardware) and
  2026-09-10 (LED command oracle). `SupportsRgb` correctly stays `false`; the catalog entry is
  unchanged, as the notes claim. The 2026-08-07 "report `0xFD` returns nothing" finding is corrected
  rather than overwritten, and the earlier review rounds are left intact — both per the repo's
  documentation rules.
- Deviates from plan: none. The notes explicitly leave Open question 1 (vendor LED utility /
  capture) standing rather than claiming to have closed it.
- Accepted limitation (per the new notes): the dongle-vs-headset relay is undocumented, so a
  negative LED result cannot distinguish "wrong command set" from "command never forwarded to the
  headset". Residual risk: none to shipping code — `SupportsRgb` is already `false`.
- No plan exists for the uninstall fix (`UninstallHandoff`, `UninstallService`,
  `StopLingeringUninstallCleanup`). Reviewed against `docs/support/missing-files-crash-analysis.md`,
  which is accurate and honestly marks items 4-7 and 9 unimplemented.

#### Code

- [x] **B1. Elevated installer can kill an unrelated process tree** — `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:493` — Blocker

  **Problem:** the pid-reuse guard has a lower age bound but **no upper bound**, and it is skipped
  entirely when the marker has no parseable timestamp:

  ```csharp
  var plausibleAge = startedUtc == default
      || process.StartTime.ToUniversalTime() >= startedUtc.AddMinutes(-1);
  ```

  Anything started *after* the marker satisfies `>= startedUtc - 1min`, so the check only excludes
  processes older than the marker. Combined with `process.Kill(entireProcessTree: true)` at line
  498, a mis-identified process loses its children too.

  **Why it blocks:** the marker is deleted by the self-delete loop's final `Remove-Item` — which
  never runs if that loop is interrupted (a reboot, or the very crash this feature exists for). The
  marker then sits in `%TEMP%` indefinitely. Days later the user reinstalls to the same folder;
  Windows has long since recycled the pid. If it now belongs to any `powershell.exe` / `cmd.exe` — a
  common thing to have open — the name check passes, the age check passes, and the **elevated**
  installer kills that session and every child process it spawned. The user loses unrelated work
  during an install, with no message.

  Note that the same file at line 670 deliberately uses `entireProcessTree: false`, with a comment
  explaining why (the app's child browser). This new call takes the opposite choice silently.

  **Solution options:**
  - Option A (correct): bound the age on both sides — require `process.StartTime` within a few
    minutes *after* `startedUtc` — **and** treat a marker older than, say, 30 minutes as stale:
    delete it and return without killing anything. Also verify the process actually references the
    install path before killing (the pid is a hint, not proof).
  - Option B (quick): require a parseable `startedUtc` (drop the `startedUtc == default` bypass),
    add an upper bound, and use `entireProcessTree: false` to match the documented convention in
    this file. The `cmd` fallback's children (`attrib` / `del` / `ping`) are short-lived and exit on
    their own.

- [x] **S1. Early return skips the marker removal the comment calls unconditional** — `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:477` — Should-fix

  **Problem / why it matters:** `StopLingeringUninstallCleanup` returns immediately when `TryRead`
  fails, so the `finally` at line 515 — commented "Last, and unconditionally" — never runs.
  `TryRead` returns false whenever the marker is unreadable *or* carries `pid=0`, and
  `ScheduleSelfDelete` writes exactly that placeholder (`UninstallService.cs:414`) before starting
  the process, so there is a real window — plus any partially-written marker. In those cases a
  **live** self-delete loop keeps its marker and carries on deleting into the fresh install: the
  precise failure this change exists to prevent, and marker removal is the half of the fix that
  would still have stopped it.

  **Fix:** remove the marker unconditionally — call `UninstallHandoff.Remove(installPath)` before
  the `TryRead` early return, or restructure so the `finally` always covers it. Removing a marker
  when no loop is running is harmless.

- [x] **S2. Marker path is not fully normalised, unlike its sibling** — `src/ProoveGaming.Common/Classes/UninstallHandoff.cs:47` — Should-fix

  **Problem / why it matters:** `GetMarkerPath` normalises with `TrimEnd` + `ToLowerInvariant` only,
  while `ClearPendingRebootDeletions` in the same flow uses `Path.GetFullPath`. The install path
  reaches the two processes by different routes — a user-typed text box in the installer, the
  registry in the uninstaller — so forms like `C:/Games/ProoveGaming` (interior forward slashes are
  not normalised), a trailing space, `..` segments, or an 8.3 short path hash differently. The
  marker is then never found, `StopLingeringUninstallCleanup` does nothing, and the fix is
  **silently inert** with no error anywhere.

  **Fix:** `Path.GetFullPath(installPath)` before trimming and lower-casing, matching the sibling.

- [x] **S3. `%TEMP%` is profile-relative, and this codebase already knows that bites** — `src/ProoveGaming.Common/Classes/UninstallHandoff.cs:52` — Should-fix

  **Problem / why it matters:** the marker lives in `Path.GetTempPath()`. That works today only
  because the uninstaller, its spawned loop and the installer all elevate into the same admin
  profile. `src/ProoveGaming.Uninstaller/Program.cs:65-68` already documents this hazard for
  `%LocalAppData%` and the shell folders, and forwards them across the elevation boundary. A
  standard user who uninstalls with one admin's credentials and reinstalls with another's gets two
  different `%TEMP%` directories: the marker is never found and the fix is silently inert — back to
  the original bug.

  **Fix:** put the marker somewhere profile-independent (e.g. under `%ProgramData%`), or forward the
  path across elevation the way the user folders already are. If neither is wanted, state the
  assumption in the XML doc so the next reader does not have to rediscover it.

#### Tests

- [x] **No coverage of the installer-side decision — which is exactly where B1 lives.**
  `tests/ProoveGaming.Installer.Tests/UninstallHandoffTests.cs` covers the `UninstallHandoff` helper
  well (10 tests: path stability, case / separator equivalence, marker-outside-install-folder, pid
  round-trip, `Remove` then `TryRead` false, pid-0 rejection). Nothing exercises
  `StopLingeringUninstallCleanup`'s "should I kill this process?" logic, so the missing upper age
  bound and the `startedUtc == default` bypass are untested.
  **What to cover:** extract the predicate — `(processName, processStartTime, markerStartedUtc)`
  to `bool shouldKill` — into a testable static (in Common, or an internal helper with
  `InternalsVisibleTo`) and pin: rejects a non-shell name; rejects a process started long after the
  marker; rejects when the marker timestamp is missing; accepts the genuine case. Test project:
  `tests/ProoveGaming.Installer.Tests`.
- [x] **The `cmd.exe` fallback script's guard is unverified.** The PowerShell variant was confirmed
  by decoding and running it (exits in ~44 ms with no marker present); the `cmd` string has had no
  equivalent check.
  **What to cover:** at minimum a string-level assertion that the emitted command contains
  `if not exist "<marker>" exit /b 0` as its first action inside the loop.

#### Nits

- `StarlightXearProbe` uses mutable statics `_vendorId` / `_productId`; the `cosmo` arm sets
  `_productId = "820"` and never restores it —
  `src/ProoveGaming.Device.Testing/StarlightXearProbe.cs`. Harmless in a one-shot console process,
  but it makes the arms order-dependent.
- The status text during the new step still reads `Status_StoppingDeviceHostService` while the
  installer is also killing the cleanup process — `MainWindow.Installation.cs:84-86`.
- `UninstallHandoff.cs` is indented with **4 spaces**; every other file in the solution — including
  its own folder, `ProoveGaming.Common/Classes/` — uses tabs. `CLAUDE.md` asks new code to match the
  surrounding file, and there is no `.editorconfig` to normalise it.

#### What's done well

- **The marker is deliberately outside the install folder**, with the reasoning recorded in the XML
  doc: inside, the loop's own recursive delete would remove its own marker mid-cleanup, stop early,
  and leave an uninstall unfinished. That is the non-obvious trap in this design and it was avoided
  on purpose — there is even a test pinning it. Do not "simplify" this later.
- **SHA-256 rather than `string.GetHashCode`**, with the reason stated (per-process randomisation in
  .NET Core would make the two sides compute different names). Exactly the sort of silent
  cross-process bug that is invisible in testing.
- **The marker is written before the process starts**, so the loop can never observe its absence and
  exit immediately; the pid is filled in afterwards.
- **Belt and braces by design** — marker removal stops the *next* iteration, the kill stops an
  *in-flight* one — and the comment explains why neither alone suffices.
- The PowerShell guard was verified empirically rather than assumed, which is the right level of
  rigour for a script embedded in a C# string literal.
- The research probes are correctly isolated: `Device.Testing` is referenced by no shipping project
  and appears in no payload, and every writing arm refuses without `--i-mean-it`.
- `docs/support/missing-files-crash-analysis.md` rules out a plausible-but-wrong cause (a missing
  VC++ runtime) with direct evidence — the DLL's import table — instead of speculating, and is
  explicit about which recommendations are not implemented.

#### Verdict

Ship: no — B1 lets an elevated installer kill an unrelated process tree from a stale marker, which
is a worse failure than the bug being fixed, and it is a small contained change to correct. S1 and
S2 are both "the fix silently does nothing" paths and are cheap to close at the same time. The
design and its documentation are otherwise strong.

**Because this branch changes `ProoveGaming.Installer` and `ProoveGaming.Uninstaller` — both of
which run elevated and ship an embedded payload — run the built-in `/security-review` on the branch
before merging.** The new elevated code path kills a process identified by a pid read from a file,
which is exactly the shape that review exists to scrutinise.

First: 1) fix B1's age bound and the `startedUtc == default` bypass, 2) make the marker removal in
S1 unconditional, 3) `Path.GetFullPath` in S2.

### Round 3 — fixes applied

All six Round 3 items are closed; the three nits are fixed too.

- **B1** — the kill decision moved out of the installer into
  `UninstallHandoff.ShouldStopSelfDeleteProcess`, a pure function with three independent guards:
  the marker must be younger than `MarkerMaxAge` (30 min), the process must be `powershell`/`cmd`,
  and its start time must fall inside a window **both sides** of the marker timestamp (-1 min /
  +5 min). A marker with no parseable timestamp now returns `false` instead of skipping the check.
  `Kill(entireProcessTree: false)` matches the documented convention at
  `MainWindow.Installation.cs:667`.
- **S1** — `StopLingeringUninstallCleanup` is wrapped in `try`/`finally`, so
  `UninstallHandoff.Remove` runs on every exit including the `TryRead` early return.
- **S2** — `GetMarkerPath` resolves through `Path.GetFullPath` (with a deterministic fallback both
  sides share for unresolvable input) before trimming and lower-casing.
- **S3** — the marker moved from `%TEMP%` to `%ProgramData%`
  (`Environment.SpecialFolder.CommonApplicationData`), which does not change with the elevated
  profile. `Write` creates the directory if needed.
- **Tests** — `UninstallHandoffTests` grew from 10 to 26 cases. Seven cover the kill predicate (genuine
  case, both shells, non-shell names including `pwsh`, started-long-after — the blocker's exact
  scenario — started-before, stale marker, missing timestamp). Two decode/inspect the emitted
  scripts and assert the marker guard precedes any delete, covering the previously unverified
  `cmd` fallback. Two existing location tests were rewritten for the `%ProgramData%` contract, and
  one new theory pins that `C:/Games/PG` and `..`-segment forms resolve to the same marker.
  `BuildSelfDeleteCommand` / `BuildLegacySelfDeleteCommand` became `internal`, with
  `InternalsVisibleTo` on the uninstaller and a `ProjectReference` from the test project — there is
  no `ProoveGaming.Uninstaller.Tests` and adding one would mean editing `ProveGaming.slnx`.
- **Nits** — `StarlightXearProbe`'s mutable `_vendorId`/`_productId` statics replaced by a
  `DeviceId` record threaded through as a parameter; `UninstallHandoff.cs` reindented to tabs; the
  installer status text is unchanged (see below).

**Not done, deliberately:** the status-text nit. The new step runs inside the window already
labelled `Status_StoppingDeviceHostService`; giving it its own label means a new key in both
`Strings.en.axaml` and `Strings.uk.axaml` for a sub-second phase the user cannot act on. Raising it
rather than silently skipping it.

Verification: `dotnet build ./src/ProveGaming.slnx` passed; full Release suite green
(App 1601, Infrastructure 998, Installer 34 — 2633 total, 0 failed).

### Round 4 — Ship: no

Re-review of `fix/fixes-on-09.10.26` after the Round 3 fixes. Surface: commit `6eceda1` (reviewed in
Round 3) plus 10 modified files in the working tree, nothing staged or untracked — `git status
--porcelain` reconciles exactly with `git diff`.

#### Plan conformance

Plan: `docs/plans/add-starlight-headset/add-starlight-headset-plan.md` (Starlight portion); the
uninstall fix still has no owning plan and is reviewed against
`docs/support/missing-files-crash-analysis.md`.

**All six Round 3 items verified closed**, not just marked:

- **B1** — the decision moved to `UninstallHandoff.ShouldStopSelfDeleteProcess:120`, bounded on both
  sides (`-1 min` / `+5 min` around the marker), gated on `MarkerMaxAge`, and the
  `startedUtc == default` bypass now returns `false` instead of skipping the check. The installer
  calls it and uses `entireProcessTree: false`. Direction verified: the marker is rewritten *after*
  the process starts, so `processStart <= markerStartedUtc` — the `-1 min` allowance is the one that
  matters and is ample.
- **S1** — `StopLingeringUninstallCleanup:474` now wraps everything in `try`/`finally`; the
  `TryRead` early return is inside the `try`, so `UninstallHandoff.Remove` runs on every exit.
- **S2** — `Normalise:86` resolves through `Path.GetFullPath`.
- **S3** — marker moved to `Environment.SpecialFolder.CommonApplicationData`.
- **Tests** — 26 cases. Spot-checked that they are not vacuous: the upper-bound test uses
  `marker + 20 min` with `now = marker + 21 min`, so the staleness gate passes and it genuinely
  isolates the new upper bound; the script tests locate the guard by index and assert it precedes
  the *first* `Remove-Item` / `del /f /q`, which is the in-loop delete rather than the trailing
  marker cleanup.
- The self-reported deviation (status-text nit deferred, with the localization cost as the reason)
  is accurate and correctly surfaced rather than silently skipped.

Docs were updated to match (`%ProgramData%`, the three-signal guard, test counts), and the counts
were corrected to the measured 26 after an initial mis-statement.

#### Code

- [x] **B1. A failed marker write silently disables the entire uninstall cleanup** — `src/ProoveGaming.Uninstaller/UninstallService.cs:414` — Blocker

  **Problem:** `UninstallHandoff.Write` returns `void` and swallows `UnauthorizedAccessException`
  and `IOException` (`UninstallHandoff.cs:171-176`). `ScheduleSelfDelete` never checks that the
  marker landed, then launches a loop whose **first action on every iteration** is:

  ```powershell
  if (-not (Test-Path -LiteralPath '<marker>')) { break }
  ```

  So if the marker was not written, the loop exits on its first pass having deleted **nothing**.

  Its own XML doc still promises the opposite — *"Best-effort: if the marker cannot be written the
  uninstall still proceeds, it just loses the safety net"* (`UninstallHandoff.cs:150`). That was
  true before this branch; the guard inverted it, and the comment was not updated. A best-effort
  safety net has quietly become load-bearing.

  **Why it blocks:** the marker now lives in `%ProgramData%` root, which is more write-restricted
  than the `%TEMP%` it replaced — endpoint-protection products commonly block writes there, and a
  full disk or a hardened ACL does the same. When that happens the user uninstalls, sees the
  uninstaller finish normally, and is left with **the entire install folder and the uninstaller EXE
  still on disk** while the Windows uninstall registration has already been removed — so there is no
  longer an obvious way to remove it. Silent, total, and worse than the bug this branch set out to
  fix. The probability is low (the uninstaller always elevates), but the failure is invisible and the
  guard is exactly what makes it fatal rather than harmless.

  **Solution options:**
  - Option A (correct): make `Write` return `bool`. When it fails, emit the **unguarded** script —
    the pre-branch behaviour — so cleanup still happens and only the race protection is lost. That
    restores the documented contract: the safety net degrades, the uninstall does not.
  - Option B (quick): after `Write`, verify with `File.Exists(markerPath)`; if it is missing, fall
    back to the unguarded command string. Same effect, no signature change.

  Either way, correct the XML doc so the stated contract and the code agree.

- [ ] **S1. Marker lifecycle: `%ProgramData%` is never auto-cleaned, and the `cmd` fallback leaks one** — `src/ProoveGaming.Common/Classes/UninstallHandoff.cs:72`, `src/ProoveGaming.Uninstaller/UninstallService.cs` (`BuildLegacySelfDeleteCommand`) — Should-fix

  **Problem / why it matters:** the move from `%TEMP%` to `%ProgramData%` fixed the profile problem
  but changed the litter semantics, which the change does not account for. `%TEMP%` is cleaned by
  Windows and Storage Sense; `%ProgramData%` is cleaned by nobody. Two paths leave a permanent
  orphan:

  - the loop is interrupted (reboot mid-run — the very scenario this feature exists for), so its
    trailing `Remove-Item` never runs;
  - the `cmd.exe` fallback deletes the marker **only on the success path**
    (`if not exist "<install>" (del /f /q "<marker>" & exit /b 0)`). If it exhausts its 60
    iterations the marker stays. The PowerShell variant does not have this gap — it removes the
    marker after the loop whichever way it ended, so the two fallbacks disagree.

  Behaviourally this is contained: `MarkerMaxAge` neutralises a stale marker, and the next install
  to the same path removes it in the `finally`. But an install path that is never reinstalled leaves
  a file in a system folder forever, and it is the kind of thing that is noticed later and hard to
  attribute.

  **Fix:** mirror the PowerShell cleanup in the `cmd` fallback (delete the marker as the loop exits,
  not only on success), and have `StopLingeringUninstallCleanup` — or the uninstaller's own startup
  — sweep `ProoveGaming.uninstall.*.marker` files older than `MarkerMaxAge`. Alternatively keep the
  marker under a `%ProgramData%\ProoveGaming\` subfolder so orphans are contained and obvious rather
  than loose in the root.

#### Tests

- [x] **The B1 path has no coverage, because `Write` cannot currently report failure.** Fixing B1
  via Option A makes it testable: assert that `Write` returns `false` for an unwritable marker path
  and that `ScheduleSelfDelete` then emits the unguarded script.
  **What to cover:** `Write` returns `false` when the target cannot be created; the emitted script
  omits the `Test-Path` guard in that case. Test project: `tests/ProoveGaming.Installer.Tests`
  (which already reaches `UninstallService` through the reference added this round).
- [ ] Optional, low cost: pin that the PowerShell script removes the marker after the loop
  regardless of how it ended, so the cleanup contract does not silently regress. The two script
  tests currently assert only the guard's position.

#### Nits

- `docs/support/diagnose-missing-files.ps1` is the support-facing tool for this exact failure and
  does not know about the marker. A section reporting any `%ProgramData%\ProoveGaming.uninstall.*.marker`
  and its age would let support see a stale handoff directly, which is now a diagnosable state.
- `Normalise` falls back to `installPath.Trim().ToLowerInvariant()` when `Path.GetFullPath` throws,
  skipping the trailing-separator trim the success path applies. Both sides run the same fallback so
  they still agree; it is only an internal inconsistency —
  `src/ProoveGaming.Common/Classes/UninstallHandoff.cs:94`.
- `Path.GetFullPath` resolves relative paths against the current working directory, which differs
  between the installer and the uninstaller's spawned loop (`WorkingDirectory = Path.GetTempPath()`).
  Install paths are absolute in every real flow, so this is theoretical — worth a word in the XML
  doc rather than code.

#### What's done well

- **Every Round 3 finding was fixed at the root rather than patched at the call site.** The kill
  decision became a pure, named function with its own reasoning in the XML doc; that is why it could
  be tested at all, and the seven cases around it are the sharpest tests in the branch.
- **The `startedUtc == default` case was inverted from most-permissive to safest**, which was the
  subtle half of B1 and easy to miss while fixing the obvious half.
- **A bug introduced *by the fix* was caught and fixed in the same pass** — `ToUniversalTime()` on a
  failed parse would have shifted `default(DateTime)` off zero west of UTC and quietly defeated the
  new guard. It is covered by a test that writes a malformed marker. Self-caught regressions of that
  subtlety are the difference between a fix and a fix that holds.
- **`entireProcessTree: false` now matches the documented convention** 180 lines below in the same
  file, so the two kill sites no longer disagree without explanation.
- **The deferred nit was reported with its cost** (a new key in both `Strings.en.axaml` and
  `Strings.uk.axaml` for a sub-second phase) instead of being quietly dropped.
- Verification was proportionate and honest: full solution build, Release suite (2633, 0 failed),
  and a serial Release installer build to prove the payload publish still works after the
  `InternalsVisibleTo` and project-reference changes — with the earlier concurrent-restore failure
  correctly attributed to parallel builds rather than to the change.

#### Verdict

Ship: no — B1 only. The Round 3 blocker is genuinely closed and the fixes are good, but S3's move to
`%ProgramData%` combined with the new guard created a path where a silently-failed marker write
turns the uninstall into a no-op, contradicting the helper's own documented contract. It is a
handful of lines to make `Write` report failure and fall back to the unguarded script.

**`/security-review` on the branch is still outstanding and still recommended** — it was raised in
Round 3 and the elevated kill path has changed again since, though it is materially safer now.

First: 1) B1 — make `Write` report failure and degrade to the unguarded script, and correct the XML
doc, 2) S1's `cmd` fallback marker cleanup, 3) the stale-marker sweep or subfolder.

### Round 4 — fixes applied

**B1 only.** S1 and all three nits are left open deliberately — the request was blockers.

- `UninstallHandoff.Write` now returns `bool`, and its XML doc states the caller's obligation
  instead of the stale "best-effort, the uninstall still proceeds" claim that the guard had
  invalidated.
- `BuildSelfDeleteCommand` / `BuildLegacySelfDeleteCommand` take `string? markerPath`. When it is
  null they emit the **unguarded** loop — the pre-branch behaviour — omitting both the `Test-Path`
  guard and the trailing marker cleanup.
- `ScheduleSelfDelete` derives `markerPath` from the result of `Write`, so a failed write produces
  an unguarded loop that still cleans up; only the race protection is lost. The pid rewrite and the
  `Remove` fallback are skipped when there is no marker.

Verified empirically by emitting both real scripts through reflection and running them against a
probe folder:

| Script | Folder after run | Meaning |
| --- | --- | --- |
| unguarded (marker write failed) | **deleted** | cleanup still happens — B1 fixed |
| guarded, marker missing | **preserved** | reproduces the exact pathology B1 described |

The second row is the important one: it confirms the guard really was the mechanism, so the fix
addresses the cause rather than the symptom.

Tests: `UninstallHandoffTests` 26 -> 30 cases — `Write` returns true on success and false when the
marker cannot be written (simulated with a directory occupying the marker's path, which raises the
same `UnauthorizedAccessException` a hardened ACL or endpoint protection would), plus one per script
asserting the unguarded form drops the guard while keeping the delete.

Verification: `dotnet build ./src/ProveGaming.slnx` passed; Release suite green; Release installer
build passed serially (payload publish intact).

### Round 5 — Ship: yes

Final review of `fix/fixes-on-09.10.26`. Surface: commit `6eceda1` plus 10 modified files in the
working tree, nothing staged or untracked — `git status --porcelain` reconciles with `git diff`.

#### Plan conformance

Plan: `docs/plans/add-starlight-headset/add-starlight-headset-plan.md` (Starlight portion); the
uninstall fix has no owning plan and is reviewed against
`docs/support/missing-files-crash-analysis.md`.

- **Round 4 B1 verified closed.** `UninstallHandoff.Write` returns `bool`; both script builders take
  `string? markerPath` and emit an unguarded loop when it is null; `ScheduleSelfDelete:419` derives
  `markerPath` from the write result. The pid rewrite and the `Remove` fallback are correctly skipped
  when there is no marker. The XML doc now states the caller's obligation rather than the stale
  "best-effort" claim.
- **Round 3 fixes remain closed** — re-checked, not assumed.
- **Deferred by request (user chose blockers only):** Round 4 **S1** (marker lifecycle) and its three
  nits stay open. Residual release risk, reported rather than passed:
  - A marker orphaned by an interrupted loop persists in `%ProgramData%` root forever for any
    install path that is never reinstalled. **Behaviourally contained** — `MarkerMaxAge` neutralises
    a stale marker and the next install to that path removes it in the `finally` — so this is
    litter, not a functional risk.
  - The `cmd.exe` fallback still deletes the marker only on its success path, while the PowerShell
    variant always does. The fallback runs only when `powershell.exe` cannot be started, so the
    exposure is small.
- Test counts in the plan and in `missing-files-crash-analysis.md` match the measured 38 for the
  project.

#### Code

- Blockers: **none.**

- [ ] **S1. The fix cannot protect the one transition it was written for** — `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:479` — Should-fix

  **Problem / why it matters:** `StopLingeringUninstallCleanup` can only stop a loop that left a
  marker. `main` contains **no marker logic at all** (verified: zero `UninstallHandoff` references in
  `git show main:src/ProoveGaming.Uninstaller/UninstallService.cs`), so an uninstall performed by the
  **currently released** uninstaller writes nothing and spawns an unguarded loop. On the next
  install, `TryRead` returns false, the method returns early, and that loop keeps running.

  Concrete scenario — and it is precisely the reporting tester's: they are on a released build. They
  uninstall (old uninstaller, unguarded loop still retrying), immediately install the fixed build,
  and the old loop deletes part of the new install. The original symptom, on the first upgrade to the
  build that fixes it. From the *second* uninstall onward both sides have the marker and the fix
  works as designed.

  This is inherent to fixing a defect that lives in the already-deployed uninstaller, not a mistake
  in the implementation — but it must not go unstated, because the fix will look like it did not
  work.

  **Solution options:**
  - Option A (covers legacy): in addition to the marker, enumerate `powershell.exe` / `cmd.exe`
    whose command line references the install path and stop those too. Command lines need
    `Win32_Process` (WMI/CIM), and the PowerShell variant is `-EncodedCommand`, so the base64 has to
    be decoded before matching — which is why the marker was preferred. Worth it only if legacy
    coverage matters.
  - Option B (no code, recommended): document it in the release note and tell testers that the
    **first** uninstall after adopting this build must wait for the install folder to disappear (or
    reboot) before reinstalling. Every later cycle is protected automatically.

#### Tests

- No gaps found. 38 cases in `ProoveGaming.Installer.Tests`, all passing in **Release** —
  independently re-run for this review rather than taken from the previous report.
- Confirmed these actually run in CI: `.github/workflows/dotnet-ci.yml:43` runs
  `dotnet test ./src/ProveGaming.slnx --configuration Release`, so the whole suite is exercised, not
  only the App csproj that the build step names.
- Spot-checked the new B1 tests for substance: `Write_ReturnsFalse_WhenTheMarkerCannotBeWritten`
  places a directory where the marker file must go, which raises the same
  `UnauthorizedAccessException` a hardened ACL or endpoint protection produces — a real failure, not
  a mocked one — and cleans up after itself.

#### Nits

- `UninstallHandoff.Write`'s XML doc says the caller "**must** honour a `false` result", but the
  second call site (`UninstallService.cs:437`, the pid rewrite) discards the return. It is safe there
  — the loop is already running guarded and a failed rewrite cannot change that — but the doc's
  blanket "must" and the code disagree. Scope the sentence to the pre-launch write, or add a one-line
  comment at :437 saying why ignoring it is correct.
- `PowerShellSelfDelete_WithNoMarker_OmitsTheGuardSoCleanupStillRuns` asserts
  `DoesNotContain("Test-Path -LiteralPath ''")`, which only catches a null-to-empty-string
  regression; the `DoesNotContain(".marker")` assertion beside it is what actually pins the
  behaviour. Harmless, slightly obscure.
- The three nits carried from Round 4 remain open by request (probe statics were fixed; the status
  text, the `Normalise` fallback asymmetry and the diagnostic script's missing marker check stay).

#### What's done well

- **The B1 fix was verified in both directions.** Emitting the real scripts through reflection and
  running them proved not only that the unguarded loop deletes, but that a guarded loop with a
  missing marker does **not** — reproducing the exact pathology the blocker described. Demonstrating
  the failure mode as well as the fix is what makes it credible that the cause was addressed rather
  than the symptom.
- **The degradation is the right way round.** A failed marker write now costs the race protection
  and keeps the cleanup, which is what the helper's doc always promised and briefly stopped
  delivering.
- **`markerPath` as `string?` rather than a duplicated script builder** keeps one source of truth for
  each script; the guarded and unguarded forms cannot drift apart.
- **Scope was held exactly.** S1 and the nits were left untouched as instructed, and the deferral was
  stated with its reasoning in the journal instead of being quietly skipped.
- Across all three rounds the pattern held: root-cause fixes with the reasoning recorded next to the
  code, self-caught regressions (the `ToUniversalTime()` bug), and verification proportionate to the
  risk — full Release suite plus a serial Release installer build every time the payload could be
  affected.

#### Verdict

Ship: yes — no blockers. The uninstall race is fixed at the root, the guard can no longer disable
the cleanup it was meant to protect, and the elevated kill path is bounded on three independent
signals. S1 is not a defect in this change but a limit of fixing code that already ships; it needs a
release note, not a code change.

**Before merge, run `/security-review` on the branch** — outstanding since Round 3, and mandatory
here by the repo's own rule: this diff touches both `ProoveGaming.Installer` and
`ProoveGaming.Uninstaller`, which run elevated and carry an embedded payload, and it adds an elevated
code path that terminates a process named by an on-disk file.

First: 1) `/security-review`, 2) release-note the S1 first-upgrade caveat for testers, 3) pick up
Round 4's S1 marker lifecycle when convenient.
