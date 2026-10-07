# CM002X1 LED Control Specification

English transcription of `CM002X1 LED Control Specification_v0.3.pdf` (C-Media Electronics), kept
next to the PDF so the byte layouts are greppable and diffable. Content is the vendor's; the notes
marked **Implementation note** are ours.

## Revision history

| Version | Change |
| --- | --- |
| V0.1 | Initial version by Hank. |
| V0.2 | **Changed to Little Endian.** |
| V0.3 | Saving LED Control table to flash. |

## Overview

Defines an LED control interface over a **USB HID output report**. The application can control the
LED colour and operation mode through it.

## HID output report definition

The output report is **16 bytes total**. The first byte is the report ID.

| Byte | Description | Size |
| --- | --- | --- |
| 0 | HID report ID. **`0xFF`** = LED control | 1 |
| 1 | LED control command | 1 |
| 2–15 | Parameters | 14 |

> The report ID is defined as `0xFF` for the LED control interface. **The driver recognises this
> report ID in the HID report descriptor to tell whether the device supports this feature or not.**

**Implementation note.** PG Starlight (`VID_0D8C` / `PID_036D`) does **not** declare report `0xFF`
in its descriptor. Its only vendor report is `0xFD`, 32 bytes, IN and OUT. See
`../../plans/add-starlight-headset/add-starlight-headset-plan.md` for what has been tried.

## Command sequence

The application must follow this order to activate a display mode:

```
LED Stop  ->  Send LED Configuration  ->  Send Color Array ...  ->  LED Start
```

The device keeps the previous LED Configuration and Color Array if the application does not send
them between a Stop and a Start.

> If the device gets a command whose format or parameter is wrong or unsupported, **the device
> should stall the set-output-report request directly.**

**Implementation note.** This stall signal is not observable on Starlight. Report `0xFD` is a
*declared* 32-byte output report, so the Windows HID stack accepts any 32-byte write and passes it
to firmware regardless of whether the payload is understood. A completed write proves nothing there.

## Commands

### `0x01` — LED Start

| Byte | Description | Size |
| --- | --- | --- |
| 0 | Report ID `0xFF` | 1 |
| 1 | Command `0x01` | 1 |
| 2–15 | Reserved | 14 |

Starts the LED display sequence using the previously supplied Configuration and Color Array. The
application must send Configuration and Color Array **before** Start.

### `0x02` — LED Stop

| Byte | Description | Size |
| --- | --- | --- |
| 0 | Report ID `0xFF` | 1 |
| 1 | Command `0x02` | 1 |
| 2–15 | Reserved | 14 |

Stops the display sequence. The device drives the LED to its default state.

### `0x03` — Send LED Configuration

| Byte | Description | Size |
| --- | --- | --- |
| 0 | Report ID `0xFF` | 1 |
| 1 | Command `0x03` | 1 |
| 2 | Operation mode — see below | 1 |
| 3 (LSB) – 4 (MSB) | **Color Array Size** — number of elements, each 3 bytes (RGB). Always `1` for Static. | 2 |
| 5 (LSB) – 6 (MSB) | **LED Update Interval** — display interval in **ms** between two colour elements. Always `0` for Static and Lookup Table. | 2 |
| 7–15 | Reserved | 9 |

#### Operation modes

| Value | Mode | Behaviour |
| --- | --- | --- |
| `0x00` | **Static** | Drives the LED with the single colour in the Color Array. |
| `0x01` | **Repeat Forward** | Steps through the array in order, repeatedly, at the update interval. |
| `0x02` | **Back And Forth** | Steps forward then backward through the array, repeatedly. |
| `0x03` | **Lookup Table** | The array is indexed by **audio output amplitude** — index 0 is minimum amplitude, index n-1 is maximum. Array size **should be a power of two** for easy searching. |

### `0x04` — Send Color Array

| Byte | Description | Size |
| --- | --- | --- |
| 0 | Report ID `0xFF` | 1 |
| 1 | Command `0x04` ("Send LED Table") | 1 |
| 2 (LSB) – 3 (MSB) | **Offset** — zero-based index into the Color Array of the four elements in this report | 2 |
| 4–6 | RGB of element `Offset` (in R, G, B order) | 3 |
| 7–9 | RGB of element `Offset + 1` | 3 |
| 10–12 | RGB of element `Offset + 2` | 3 |
| 13–15 | RGB of element `Offset + 3` | 3 |

After the Color Array Size is sent via Configuration, the application sends the whole array using
multiple Send Color Array commands — **four colours per report**, sequentially. The device ignores
elements whose index exceeds the declared Color Array Size.

## Saving the LED control table to flash

The LED colour and control mode must survive unplugging from the PC, so the table is stored in
flash. On the CM002X1 demo board it occupies the sector **`0x8000`–`0x9000`**.

- Firmware **erases** the sector when it receives **LED Stop**.
- Firmware **saves** the table when it receives **LED Start**.

**Implementation note.** This is why lighting must never be animated from the host: every Start is a
flash write. Effect playback is firmware-side by design.

### LED control table layout (in flash)

| Byte | Description | Size |
| --- | --- | --- |
| 0–2 | `"LED"` — marks the table as present; absent means no table | 3 |
| 3 | Operation mode | 1 |
| 4 | LED colour count (N) | 1 |
| 5 | LED update interval, **in units of 10 ms** | 1 |
| 6–8 | RGB of the 1st element | 3 |
| … | … | |
| (3+3N) – (5+3N) | RGB of the last element | 3 |

> **Note the unit change.** The update interval is **milliseconds** in the Configuration command
> (bytes 5–6, 16-bit) but **tens of milliseconds** in the flash table (byte 5, 8-bit). Firmware
> divides by 10 on the way in, so the effective maximum interval is 2550 ms and the effective
> resolution is 10 ms.

## Worked example — static red

```
FF 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00   Stop
FF 03 00 01 00 00 00 00 00 00 00 00 00 00 00 00   Configuration: Static, 1 colour, 0 ms
FF 04 00 00 FF 00 00 00 00 00 00 00 00 00 00 00   Color Array: offset 0, RGB = FF 00 00
FF 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00   Start
```

## Mapping to the app's RGB effects

| App effect | Operation mode | Color Array |
| --- | --- | --- |
| Static / solid colour | `0x00` Static | 1 element — the chosen colour |
| Colour cycle | `0x01` Repeat Forward | the palette; interval from the speed slider |
| Breathing / sweep | `0x02` Back And Forth | the palette; interval from the speed slider |
| Audio reactive | `0x03` Lookup Table | power-of-two palette, quiet → loud |

**Brightness has no field in this protocol.** It must be applied by scaling the RGB values on the
host before they are sent, or the control must be hidden for this device.
