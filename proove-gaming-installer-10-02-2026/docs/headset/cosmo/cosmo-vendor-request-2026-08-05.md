# PG Cosmo headset — request for the vendor HID protocol (report `0xBC`)

**Date:** 2026-08-05
**Device:** PG Cosmo wireless headset + 2.4G USB dongle
**USB IDs:** VID `0x0D8C` / PID `0x0334`
**Strings:** Manufacturer `THI Headphone`, Product `PG Cosmo`
**Serial:** device reports a real `iSerialNumber` (e.g. `00006F64096B22E7`)

## What we are asking for

The command format of **HID report `0xBC`** on usage page `0xFF02` (interface `MI_03`), so the
desktop application can show:

1. **Battery level** of the headset (percentage, or the raw value and its scale).
2. **Power state** — whether the headset is currently on or off.
3. **Microphone mute state** — whether the mic is muted at the headset.

Specifically, for each of the three: the **request bytes** the host sends on the `0xBC` OUT report,
and the **layout of the reply** on the `0xBC` IN report (which byte holds the value, and its range).

If lighting (RGB) is also controllable over this report, the command format for that would let us
add the lighting page for this headset too.

## What we have already established ourselves

We are not asking you to explain the device from scratch. Measured directly from the hardware:

### The HID interface

`MI_03` exposes one collection, top-level usage page `0x000C` (Consumer), containing three reports:

| Report ID | Usage page | Direction | Payload |
| --- | --- | --- | --- |
| `0x03` | `0x000C` Consumer | IN | media / volume keys |
| `0xBC` | **`0xFF02` vendor** | IN and OUT | 15 data bytes each way |
| `0xFD` | **`0xFF02` vendor** | IN and OUT | 31 data bytes each way |

### `0xFD` is already understood — it is not what we need

`0xFD` is the C-Media **Xear authentication** channel, documented in *"CM002X1 Xear Audio Technology
Authentication User Guide" Rev 1.0*: a transparent HID↔I2C bridge to a CM002X1 coprocessor at slave
address `0x7A` (`0x11` = I2C write, `0x12` = I2C read, `0x13` = returned data). It carries licensing
data only, so it is not relevant to battery or device state.

**This leaves `0xBC` as the only vendor channel, and it is undocumented.**

### `0xBC` sends nothing on its own

We ran a read-only capture on `MI_03` while operating the headset through a scripted sequence:
idle → mute/unmute the microphone → power the headset **off** → power it back **on** → press
volume up/down → idle. Roughly 90 seconds, all traffic logged.

Result:

- **Report `0xBC`: zero frames.** The dongle sent nothing unsolicited on the vendor channel at any
  point, including across a full power-off / power-on cycle.
- **Report `0x03`: 60 frames, all volume keys.** Only bit 0 (Volume Increment) and bit 1 (Volume
  Decrement) of byte 1 were ever set. The Mute bit (byte 1, bit 2) never fired.
- **Muting the microphone at the headset produced no HID traffic at all.**
- **Powering the headset off and on produced no HID traffic**, and the dongle's USB interface count
  did not change — the dongle stays enumerated with the headset switched off.

### `0xBC` answers `GET_REPORT`, but always with the same bytes

A `HidD_GetInputReport` (USB control-transfer read) on `0xBC` succeeds and returns:

```
offset 16..23:   01 57 01 03 B4 0E 01 06
```

**Byte 19 is live and useful:** it reads `0x03` while the headset is switched on and `0x0F` while it
is off, tracking the power switch exactly. We use this today to show whether the headset is
connected. Thank you — this is the one piece of device state we have been able to identify.

**Every other byte in that window is frozen.** Across roughly 58 reads spanning two hours,
including a full power cycle, `01 57 01 · B4 0E 01 06` never changed by a single bit. We initially
read `0x57` (87) as a battery percentage and `0x0EB4` (3764) as a battery voltage in millivolts, but
a value that never moves cannot be a live measurement, so we withdrew that.

**What do the constants at offsets 16–23 mean?** If battery lives there under some condition, we
would like to know what triggers it.

### What we tried, so you know what not to repeat back to us

- `GET_REPORT` on report `0x03` and on report `0xFD`: **both fail**. `0xBC` is the only report on
  this device that answers a control-transfer read, so the Xear/CM002X1 I2C bridge appears to be
  present in the report descriptor but **not implemented in this dongle's firmware**.
- Following your section 3.4 pattern (send an OUT request, then `GET_REPORT` to collect the reply),
  we sent `BC <opcode> 00…` for every opcode from `0x01` to `0x30` and re-read the buffer after
  each. **Not one byte changed.** The device remained healthy throughout.
- The report descriptor contains no HID Power Device (`0x84`) or Battery System (`0x85`) usage page,
  and the interface exposes no feature reports, so there is no standard route either.

We stopped there rather than continuing to guess. Sending unknown bytes to an undocumented vendor
channel risks putting the device into a firmware or factory state we cannot get it out of, and we
are not willing to do that to customer hardware.

## Either of these would fully unblock us

1. **The `0xBC` command table** — request bytes and reply layout for battery, power state and mic
   mute (and lighting, if supported).
2. **A USB capture** of your own configuration software talking to this headset while it displays
   the battery level. A Wireshark/USBPcap log showing the `0xBC` exchange would let us read the
   format directly, and is often faster to produce than a written spec.

## Contact for follow-up questions

Please reply with the report format, or with the capture, and we will confirm against the hardware
on our side.
