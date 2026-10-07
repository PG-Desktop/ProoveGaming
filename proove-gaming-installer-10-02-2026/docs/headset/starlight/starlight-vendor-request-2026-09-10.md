# PG Starlight — information request: host control and state reporting over the 2.4G dongle

**Device:** PG Starlight — USB `VID_0D8C` / `PID_036D`. USB descriptor reports Manufacturer
"Jieli Technology", ProductName "PG Starlight".
**Date:** 2026-09-10
**Supersedes:** our request of 2026-08-07. Please use this version — see *Correction* below.

We are adding the PG Starlight to the Proove Gaming desktop application. Audio output and microphone
volume work and are finished. Four things a user reasonably expects still do not work over the
**2.4G dongle**, and we have not been able to solve any of them from the host.

This document records exactly what we measured, so you do not have to repeat it, and asks five
specific questions. Everything below was measured on production hardware.

---

## Correction to our previous request

Our 2026-08-07 letter stated that *"report `0xFD` returns nothing, in every state"*. **That was our
measurement error, and we withdraw it.**

Report `0xFD` is a request/response channel, not a pollable status register. Our earlier test called
`HidD_GetInputReport` without first sending a request, so it was reading an unpopulated buffer.
Issued correctly, the channel answers reliably — details in the next section. We are correcting this
ourselves because the earlier statement may have made the question look like a hardware fault when
it was not.

---

## Summary

| Capability | Status over the dongle | Impact on the user |
| --- | --- | --- |
| Audio + microphone volume | **works** | via the Windows endpoints |
| Play/pause button | **works** | reported as a standard consumer key |
| **RGB lighting** | not controllable | the app can offer no lighting control at all |
| **Headset power / link state** | not reportable | the app shows the headset as **connected while it is switched off** |
| **Microphone mute state** | not reportable | the app cannot show a mute indicator |
| **Battery level** | not reportable | no battery indicator (it *does* work over Bluetooth — see below) |
| Volume wheel position | not reportable | see the note at the end |

---

## What we have established

### The device's control surface

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

No feature reports on either collection. The Windows device tree shows no vendor/WinUSB interface,
so `0xFD` is the only host-writable channel the device has.

### Report `0xFD` works, and a CM002X1 answers behind it

Using the flow in your *CM002X1 Xear Audio Technology Authentication User Guide* (§3.2–3.4):

```
Set Output Report (32 B)   FD 12 7A <map_addr> <len> ...          host asks the device to I2C-read
Get Input Report  (32 B)   FD 13 7A <map_addr> <len> <data...>    host collects the result
```

- All **256** sub-addresses echo the header back correctly.
- The input buffer is **consume-on-read** (one read per request).
- **Control test** — the same request sent to slave addresses where nothing should exist:

  | Slave | Data returned |
  | --- | --- |
  | `0x7A` (documented CM002X1) | driven data, e.g. `C7 B5 C8 1A CA 94 CB 05 CD 8B CE 44 D0 D0 D0 7A` |
  | `0x10` | `FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF` |
  | `0x50` | `FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF` |
  | `0x00` | `FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF` |

  An I2C slave that never acknowledges leaves the bus pulled high — all `0xFF`, which is what the
  control addresses show. `0x7A` does not. **A real device acknowledges at `0x7A`.**

We did not pursue the authentication exchange itself and have sent no I2C **write** (`0x11`).

### RGB: the commands are not recognised on `0xFD`

We have your *CM002X1 LED Control Specification* v0.3 (16-byte HID output report on report ID
`0xFF`; commands `0x01` Start, `0x02` Stop, `0x03` Send LED Configuration, `0x04` Send Color Array).

**This device does not declare report `0xFF`** — its only vendor report is `0xFD`.

Because `0x12` visibly populates the input buffer, we could test whether a command is *parsed*,
rather than only whether the LED changed. Method: write a known marker into the buffer with a
documented `0x12` read, send the candidate LED command, then read once.

- **Positive control:** a known-handled command (another `0x12` at a different sub-address) pushed
  through the identical code path was detected. The test works.
- **Result:** commands `0x03`, `0x04` and `0x01` — in both `FD <cmd> …` and `FD FF <cmd> …` framings
  — produced **no response at all**, while `0x12` answered every time through the same path.
- **Visually confirmed:** with the headset in view and a pause after every command, the LED did not
  change at any point — no colour, effect, brightness, or momentary flicker.

We deliberately did **not** send `0x02` (LED Stop), because your specification says the firmware
erases the stored LED table on Stop, and we did not want to destroy the factory effects. We have
also not swept undocumented opcodes on a channel that persists to flash.

**The hardware is capable.** The headset's own RGB button cycles effects normally, including a
**wave** effect and a **static colour**. So the gap is purely the host-side command path.

### Power state, microphone mute, wheel: nothing reaches the host

Sampled once per second across microphone mute, the volume wheel, and a full power cycle:

| Endpoint | Throughout every phase |
| --- | --- |
| Headphones (PG Starlight) | `ACTIVE`, muted = False, volume 53 |
| Microphone (PG Starlight) | `ACTIVE`, muted = False, volume 92 |

Not one value changed. In particular the endpoints stay `ACTIVE` and the dongle stays enumerated
with the **headset switched off**.

On the HID side: `COL01`'s descriptor *declares* Volume Increment (`0xE9`), Volume Decrement
(`0xEA`) and **Mute (`0xE2`)** as bits 0, 1 and 2, but the firmware never sends any of them. Only
the play/pause button reports (`01-08-00` press, `01-00-00` release, usage `0xCD`).

### Battery

The descriptor declares no HID Power Device (`0x84`) or Battery System (`0x85`) usage page. A sweep
of all 256 report IDs on both collections returned nothing, and roughly 300 Windows device
properties across the device's five nodes were unchanged by a power cycle.

For contrast: over **Bluetooth** the same headset does report a battery percentage (70 % measured),
through the standard Hands-Free profile. Only the dongle path lacks it.

---

## Our questions

### 1. RGB — what is the LED command framing for `PID_036D`? *(highest priority)*

And, just as important: **does the LED command go to the dongle, or must it be relayed to the
headset over 2.4G?** Your LED specification is written for a CM002X1 demo board, where the chip on
the USB bus is the chip driving the LED. Here the LED is on the headset and USB reaches only the
dongle. We cannot distinguish "wrong command framing" from "the command is never forwarded", so
please tell us which layer accepts it.

If the CM002X1 command set does apply on `0xFD`, we need the envelope around it: any header, length,
sub-command or checksum bytes, and where the documented frame begins. If the firmware uses a
different protocol entirely, we need that one.

### 2. How does the host learn the headset is switched off or out of range?

The dongle stays enumerated and both audio endpoints stay `ACTIVE`, so our application currently
shows a powered-off headset as connected. This is the most user-visible of these issues.

Is there a status byte on `0xFD` we should be polling, or a notification the firmware can send?

### 3. How does the host read microphone mute state?

The button mutes inside the headset and the Windows capture endpoint is unaffected, so we cannot
show a mute indicator.

Note this one may be very cheap for you: the firmware **already declares** the Mute usage (`0xE2`,
bit 2) on `COL01` and simply never sends it. If the firmware emitted that existing usage on
mute/unmute, it would work with no new protocol at all.

### 4. Is battery level reportable over the dongle?

There is no route today. If the dongle can expose it — either as a standard HID Battery System page
or as a byte on `0xFD` — we would like to show it, as we already do over Bluetooth.

### 5. Xear / APO — is `PID_036D` licensed, and can we have a package for it?

We evaluated the `CmXearSDK` and the `AUSDOM_A3_PRO` driver/APO package you supplied:

- `CmXearInit()` succeeds and both Starlight endpoints enumerate correctly.
- The non-effect properties work (identity, format, volume, peak level, default-device role).
- **All 78 effect properties return `CE_LicenseError`**, and `SupportedLicenseCategory` reports `0`.
- We activated the GUID `158FD125-36BD-4A73-9E07-518869EDD288` from
  `CM002X1_AUSDOM_A3_PRO_GUID.txt`. `CmActivateLicense` returns `CE_OK` and the GUID registers, but
  the per-endpoint `ActivatedLicense` stays empty.
- `CmApoIce.dll` is not installed on the test machine, and every INF in the supplied package binds
  `USB\VID_0D8C&PID_0367&MI_00` — the AUSDOM A3 Pro, a different product. Our device is `PID_036D`,
  and the catalogues are signed for `0367`, so the package cannot be retargeted on our side.

So, three sub-questions:

- **(a)** Is PG Starlight (`PID_036D`) licensed for Xear at all?
- **(b)** If yes, can you reissue the extension + APO package **signed for
  `VID_0D8C&PID_036D`**?
- **(c)** Is `158FD125-…` the correct licence GUID for this product, or is there another?

---

## The fastest way to answer all of this

**A USB capture of your own configuration software driving these features** would answer questions
1–4 at once, and you would not have to write anything.

Anything that records the URBs is fine — Wireshark with USBPcap, Bus Hound, or your own tooling.
Ideally one capture per action, or one capture with a note of the timestamps:

1. changing the RGB effect and the RGB colour,
2. switching the headset off and on again while the tool is running,
3. muting and unmuting the microphone,
4. the battery indicator updating, if your tool shows one.

We will decode the frames ourselves.

---

## What we already have, and do not need again

- The **Xear audio SDK** (`CmXearSDK` V1.0.28.0) with its API reference and demo application.
- The **`AUSDOM_A3_Pro`** driver / APO package (`CM002X1_AUSDOM_A3_PRO_Driver_APO_Package_V1.0.0.1`).
- The **CM002X1 LED Control Specification** v0.3.
- The **CM002X1 Xear Audio Technology Authentication User Guide** v1.0.

---

## One design consequence worth raising

The volume wheel does not move the Windows volume, so effective loudness is the Windows level
**multiplied by** the wheel position, and the two controls cannot see each other. A user who turns
the wheel down and then sets our software slider to 100 % will hear something much quieter and may
reasonably report the software as broken.

This is inherent to the hardware design rather than something we can compensate for, but if the
wheel position were readable on `0xFD` we could at least warn the user.

---
