# PG Starlight — host control and state reporting: what the device does not expose

> **SUPERSEDED 2026-09-10 — do not send this version.**
> Section 1 below ("Report `0xFD` returns nothing, in every state") is **wrong**: that measurement
> read the input buffer without first issuing the documented `0x12` request, so it was reading an
> unpopulated buffer. `0xFD` works, and a CM002X1 acknowledges at I2C `0x7A`.
> Send `starlight-vendor-request-2026-09-10.md` instead — it corrects this openly and carries the
> September findings. This file is kept as the dated record of what was known in August.


**Device:** PG Starlight — USB `VID_0D8C` / `PID_036D`, Manufacturer "Jieli Technology",
ProductName "PG Starlight".

**Date:** 2026-08-07

We are adding the PG Starlight to the Proove Gaming desktop app. Audio volume control works and is
finished. **Four other things a user reasonably expects do not work, and none of them can be fixed
from the host** — the device does not report the state, or accept the command, at all.

This document lists exactly what we measured so you do not have to repeat it, and asks four
specific questions. Two of the four are **user-visible wrong behaviour**, not missing features.

## Summary

| Capability | Status | Impact |
| --- | --- | --- |
| Audio + microphone volume | **works** | via the Windows endpoints |
| Play/pause button | **works** | reported as a standard consumer key |
| **Headset power state** | not reportable | the app shows the headset as **connected while it is switched off** |
| **Microphone mute** | not reportable | the app cannot show the microphone is muted |
| **RGB lighting** | not controllable | the app cannot offer any lighting control |
| Battery level | not reportable | no battery indicator |
| Volume wheel position | not reportable | see the note on conflicting volume below |

## The device's control surface, as measured

USB composite. `MI_00` is the USB Audio Class function. `MI_03` is the only HID interface, with two
collections, both usage page `0x000C` (Consumer), usage `0x01`:

| Collection | Reports | Size | Purpose |
| --- | --- | --- | --- |
| `COL01` | IN `0x01` | 3 bytes | consumer keys |
| `COL02` | IN **and** OUT `0xFD` | 32 bytes | the only vendor channel |

`COL02` raw report descriptor:

```
05 0C 09 01 A1 01 85 FD 09 10 15 00 25 FF 35 00 45 00 65 00 55 00 75 08 95 1F 81 02 09 10 91 02 C1 00
```

No feature reports on either collection. The full Windows device tree shows **no vendor / WinUSB
interface**, so `0xFD` is the only host-writable channel the device has.

## 1. Report `0xFD` returns nothing, in every state

- **Passive listening** on `0xFD` through idle, volume, microphone mute, power off, power on and
  every button: **zero frames**. The device never pushes on this report.
- **`HidD_GetInputReport` on `0xFD`** succeeds but returns **32 zero bytes**, every time, across 30
  samples spanning a power cycle:

  ```
  00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00
  ```

So there is no battery, no power state and no mute state readable on the vendor channel.

## 2. RGB: 12 command framings and 16 operation-mode attempts, none accepted

We have *"CM002X1 LED Control Specification"* v0.3. It defines LED control as a 16-byte HID output
report on report ID **`0xFF`**, commands `0x01` Start, `0x02` Stop, `0x03` Send LED Configuration,
`0x04` Send Color Array.

**This device does not declare report `0xFF`.** The specification itself says a driver detects LED
support by finding that report ID in the descriptor. It is absent; the only vendor report is `0xFD`.
The specification is also written for a *"CM002X1 demo board"*, while this device reports **Jieli**
silicon.

We sent your documented command sequence, in the documented order, over two independent axes.

**Axis 1 — delivery framing: 12 distinct combinations** of

- report ID `0xFD` and `0xFF`
- lengths 32 (the declared output length) and 16 (your specification's report size)
- with and without a leading inner report-ID / length byte (`FF`, `0F`, `00`, `01`)
- colour-array offset field as 2 bytes (per spec) and as 1 byte

Each combination sent the full Stop → Configuration → Colour Array → Start sequence, so 48 output
reports per pass.

**Axis 2 — operation mode: 16 attempts**, the mode byte `0x00`–`0x07` in two passes — with `Start`
and without it — and **without the `Stop` command**, since `Stop` erases the stored table and we
wanted to leave your firmware's own colours in place.

**None changed the LED**, on either axis. It stayed on the effect selected by the headset's own
button throughout.

Note we cannot distinguish "rejected" from "ignored": `0xFD` is a declared output report, so the
Windows HID stack accepts any write to it regardless of whether the firmware parses the payload.
Your specification's promise that a malformed request is stalled does not apply on this channel.

**We stopped there deliberately.** We will not sweep unknown opcodes at a vendor channel that
persists to flash — that risks putting customer hardware into a state we cannot recover.

## 3. The buttons: one reaches the host, the others do not

The headset has two buttons and a mechanical volume wheel:

| Control | Short press | Long press |
| --- | --- | --- |
| Button 1 | play / pause | power off |
| Button 2 | next RGB effect | microphone mute / unmute |
| Wheel | output level | — |

We listened on **every** interface, through both HidSharp and the Windows Raw Input API:

- **Button 1 reports correctly** — `01-08-00` press, `01-00-00` release: bit 3 of report `0x01`,
  usage `0xCD` Play/Pause. This works and we use it.
- **Button 2 reports nothing**, on either action, on either path.
- **The wheel reports nothing.**

`COL01`'s descriptor *declares* Volume Increment (`0xE9`), Volume Decrement (`0xEA`) and **Mute
(`0xE2`)** as bits 0, 1 and 2 — but the firmware never sends any of them. The usages exist in the
descriptor and go unused.

## 4. Nothing is visible on the Windows audio layer either

We sampled both Windows endpoints — device state, mute and master volume — once per second through
microphone mute, the wheel, and a power cycle:

| Endpoint | every phase, throughout |
| --- | --- |
| Headphones (PG Starlight) | `ACTIVE`, muted=False, volume 53 |
| Microphone (PG Starlight) | `ACTIVE`, muted=False, volume 92 |

**Not one value changed.** In particular:

- The endpoints stay `ACTIVE` with the headset switched **off**, and the dongle stays enumerated —
  so the app has no way at all to know the headset is off.
- Muting the microphone with Button 2 does not mute the Windows capture endpoint.
- The volume wheel does not move the Windows render endpoint.

## The questions

1. **How does the host learn that the headset is switched off?** The dongle stays enumerated and
   every endpoint stays `ACTIVE`, so our app shows a powered-off headset as connected. This is the
   most user-visible of these issues.
2. **How does the host read microphone mute state?** The button mutes inside the headset and the
   Windows capture endpoint is unaffected, so we cannot show a mute indicator. If the firmware
   simply sent the Mute usage (`0xE2`, bit 2) it already declares on `COL01`, this would work with
   no protocol at all.
3. **What is the LED command framing for `PID_036D` on report `0xFD`?** If the CM002X1 command set
   applies, we need the envelope around it — any header, length, sub-command or checksum bytes, and
   where the documented frame begins. If Jieli firmware uses a different protocol entirely, we need
   that one. **A capture of your own software driving the LED is equally useful** — we can decode it
   ourselves and need nothing written.
4. **Is battery level reportable at all?** The descriptor declares no HID Power Device (`0x84`) or
   Battery System (`0x85`) page, and `0xFD` is empty, so there is currently no route.

## One design consequence worth raising

Because the wheel does not move the Windows volume, effective loudness is **the Windows level
multiplied by the wheel position**, and the two controls cannot see each other. A user who turns the
wheel down and then sets our volume slider to 100 % will hear something much quieter and may
reasonably report the software as broken. This is inherent to the hardware design, not something we
can compensate for.

## What we do not need

- The Xear audio SDK (`CmXearSDK`) — we have it. EQ, audio enhancements, licensing and device
  management; nothing about LED, battery or the HID vendor channel.
- The `AUSDOM_A3_Pro` driver / APO package — we have it. Its INF binds
  `USB\VID_0D8C&PID_0367&MI_00`: a different product, and the audio interface rather than the HID
  one, so it cannot reach the LED.

## Contact for follow-up

A traffic capture of your own tool exercising lighting, mute and power reporting would answer all
four questions at once and is the fastest path for both sides.
