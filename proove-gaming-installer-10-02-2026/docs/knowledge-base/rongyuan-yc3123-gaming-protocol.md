# Rongyuan YC3123 Gaming Keyboard Protocol (Gaming SPEC v100.8)

English translation of `docs/ry-yc3123-机械轴__2025_07_18.doc` (Rongyuan / Ronron "Gaming SPEC",
MCU YC3123, revision V100.8 dated 2025-07-18).

This is the **sibling document** to [`ry5088-magnetic-axis-protocol.md`](./ry5088-magnetic-axis-protocol.md).
Both MCUs speak the same feature-report command set; the RY5088 document describes the
magnetic-axis extensions but leaves the LED parameter encoding undefined. **This document is
where the concrete LED effect / colour / option values come from**, and is therefore the
authoritative reference for [[T800]] backlight control.

The sections needed to drive lighting and **macros** are translated in full. The key-matrix,
OLED, 2.4 G-receiver and HID key-code tables are summarised — translate further on demand.

## Transport

> USB Protocol:
> Use SetFeatureReport command for issuing command.
> Use GetFeatureReport command for getting results.

Every command is a **HID feature report**, not an output report. The doc spells out the USB
control transfer for each command, and it is identical throughout:

| Direction | bmRequestType | bRequest | wValue | wIndex | wLength |
|---|---|---|---|---|---|
| OUT (set) | `0x21` | `0x09` (SET_REPORT) | `0x0300` | `0x0002` | `0x0040` |
| IN (get) | `0xA1` | `0x01` (GET_REPORT) | `0x0300` | `0x0002` | `0x0040` |

Decoded:

- `wValue = 0x0300` → report type **3 (Feature)**, report ID **0x00**.
- `wIndex = 0x0002` → **interface 2** (`MI_02`), the vendor-defined collection.
- `wLength = 0x0040` → **64 data bytes**.

A HidSharp/`HidStream.SetFeature` buffer is therefore **65 bytes**: a leading `0x00` report-ID
byte followed by the 64 protocol bytes.

## Packet framing

The 64 protocol bytes are laid out as:

```
Byte0  Byte1  Byte2  Byte3  Byte4  Byte5  Byte6  Byte7   Byte8 .. Byte63
Cmd    P1     P2     P3     P4     P5     P6     CS      extended payload
```

- `Cmd` — command byte (see the command table below).
- `P1..P6` — command-specific parameters.
- `CS` — checksum. **The document never defines the algorithm.** See "Checksum" below.
- `Byte8..Byte63` — extended payload, used only by bulk commands (key matrix, macros, pictures).

Commands that take **seven** parameters push `CS` out by one — `SET_LEDPARAM` is the notable
case, where `CS` sits at `Byte8` and the extended payload starts at `Byte9`.

### Checksum

The spec marks a `CS` byte in every command layout but **nowhere states how it is computed**.
The only checksum the document defines explicitly is unrelated: the OLED firmware-download
verification (`0xB1`), which returns a 32-bit little-endian checksum of the downloaded image.

**Resolved externally and confirmed on hardware — it is the one's complement of the sum:**

```
CS = 0xFF - (sum & 0xFF)
```

covering bytes 0–6 and stored at byte 7 for most commands, or covering bytes 0–7 and stored at
byte 8 for the seven-parameter `SET_LEDPARAM` / `SET_SLEDPARAM`. See
[`royuan-led-encoding-notes.md`](./royuan-led-encoding-notes.md) for the sources and the hardware
transcript.

A packet with a wrong `CS` is discarded **silently** — no ack, no error, and reads stop answering —
so this is not a field that can be left to a plausible guess. A plain sum is off by exactly one.

## Command table

`FEA_CMD_*` codes as listed in the spec. Get commands are the Set code with bit 7 set
(`0x07` → `0x87`), with a handful of exceptions.

| Command | Set | Get |
|---|---|---|
| Device info | `0x00` (disabled) | `0x8F` |
| Reset | `0x01` | `0x81` (disabled) |
| Battery / low-power threshold | `0x02` | `0x82` |
| Report rate | `0x03` | `0x83` |
| Profile | `0x04` | `0x84` |
| **LED on/off** | **`0x05`** | `0x85` |
| Debounce | `0x06` | `0x86` |
| **LED parameters (main backlight)** | **`0x07`** | `0x87` |
| SLED parameters (side light) | `0x08` | `0x88` |
| Keyboard options | `0x09` | `0x89` |
| Key matrix | `0x0A` | `0x8A` |
| Macro | `0x0B` | `0x8B` |
| User picture | `0x0C` | `0x8C` |
| Music mode | `0x0D` | — |
| Windows-key colour | `0x0E` | — |
| SignalRGB | `0x0F` | — |
| Fn matrix | `0x10` | `0x90` |
| Sleep time | `0x11` | `0x91` |
| User GIF | `0x12` | — |
| OLED block | `0x20`–`0x32` | `0xA2`–`0xB2` |
| 2.4 G receiver block | `0xF6`, `0xF7`, `0xFA`–`0xFE`, `0x7A` | — |

## LED on/off — `0x05` / `0x85`

```
OUT
Byte0 = 0x05 (SET_LEDONOFF)
Byte1 = LED  on/off   0 = on, 1 = off
Byte2 = SLED on/off   0 = on, 1 = off
Byte3..Byte6 = 0
Byte7 = CS
```

`GET_LEDONOFF` (`0x85`) returns the same two bytes at `Byte1..Byte2`.

Note the polarity: **0 turns the light ON, 1 turns it OFF.** On/off is tracked by the firmware
as a state variable independent of the effect (see vendor code `05H 03 xx 04`), so writing a new
effect while the light is off does not switch it back on — send `0x05 00 00` first.

## LED parameters — `0x07` / `0x87`

```
OUT
Byte0 = 0x07 (SET_LEDPARAM)
Byte1 = Effect#
Byte2 = Speed
Byte3 = Bri        (brightness)
Byte4 = Option     (high nibble = Type, low nibble = Colour)
Byte5 = R
Byte6 = G
Byte7 = B
Byte8 = CS
Byte9..Byte63 = 0
```

`GET_LEDPARAM` (`0x87`) returns `Effect#, Speed, Bri, Option, R, G, B` at `Byte1..Byte7`.

`SET_SLEDPARAM` (`0x08`) has an identical layout and drives the side-light strip; it is silently
ignored on hardware without one.

### LED Param Table

`Speed` and `Brightness` are **0–4**, not 0–100 and not 0–255. `xxx` means the field is not
meaningful for that effect.

> **`Speed` is inverted on the wire.** The logical 0–4 below transmits as `5..1` — 5 slowest,
> 1 fastest, 0 never sent. Brightness is not inverted. This document does not mention it; see
> [`royuan-led-encoding-notes.md`](./royuan-led-encoding-notes.md).

| Effect | `Effect#` | Speed | Brightness | Colour (Option low nibble) | Type (Option high nibble) |
|---|---|---|---|---|---|
| Off | `0x00` | xxx | xxx | xxx | xxx |
| Static | `0x01` | xxx | 0–4 | Single / Full / AP | xxx |
| Breathe | `0x02` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Neon | `0x03` | 0–4 | 0–4 | xxx | xxx |
| Wave | `0x04` | 0–4 | 0–4 | Single / Full / AP | 0 = right, 1 = left, 2 = down, 3 = up |
| Ripple | `0x05` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Raindrop | `0x06` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Snake | `0x07` | 0–4 | 0–4 | Single / Full / AP | 0 = default, 1 = clip |
| Press-action on | `0x08` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Converge | `0x09` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Sine wave | `0x0A` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Kaleidoscope | `0x0B` | 0–4 | 0–4 | Single / Full / AP | 0 = out, 1 = in |
| Line wave | `0x0C` | 0–4 | 0–4 | Single / Full / AP | 0 = right, 1 = left |
| User picture | `0x0D` | xxx | 0–4 | Single / Full / AP | index 0–4 |
| Laser | `0x0E` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Circle wave | `0x0F` | 0–4 | 0–4 | Single / Full / AP | 0 = clockwise, 1 = anticlockwise |
| Dazzling | `0x10` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Rain down | `0x11` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Meteor | `0x12` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Press-action off | `0x13` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Music 1 | `0x14` | xxx | 0–4 | Single / Full / AP | 0 = up, 1 = separate, 2 = intersect |
| Windows | `0x15` | xxx | 0–4 | Single / Full / AP | xxx |
| Music 2 | `0x16` | xxx | 0–4 | Single / Full / AP | 0 = up, 1 = separate, 2 = intersect |
| Train | `0x17` | 0–4 | 0–4 | Single / Full / AP | xxx |
| Fireworks | `0x18` | 0–4 | 0–4 | Single / Full / AP | xxx |
| GIF | `0x19` | — | — | — | — |

### Option byte — colour selector

The `Option` **low nibble** picks where the effect's colour comes from. This is the single most
important field for driving the board from a colour picker: values 0–6 are hard-coded presets
that make the firmware **ignore the R/G/B bytes entirely**.

| Value | Meaning | R/G/B bytes |
|---|---|---|
| `0` | Single colour — red `FF 00 00` | ignored |
| `1` | Single colour — orange `FF 80 00` | ignored |
| `2` | Single colour — yellow `FF FF 00` | ignored |
| `3` | Single colour — green `00 FF 00` | ignored |
| `4` | Single colour — cyan `00 FF FF` | ignored |
| `5` | Single colour — blue `00 00 FF` | ignored |
| `6` | Single colour — purple `FF 00 FF` | ignored |
| **`7`** | **AP colour — defined by the application** (default `FF FF FF`) | **used** |
| `8` | Full colour — RGB mode (firmware cycles the spectrum) | ignored |

The `Option` **high nibble** carries the per-effect `Type` variant from the table above
(wave direction, kaleidoscope in/out, and so on). Effects with `xxx` in the Type column ignore it.

So the byte is assembled as `Option = (Type << 4) | Colour`.

> The same 0–6 / 7 / 8 colour-slot convention shows up on the Raster keyboard's unrelated `0xAA`
> protocol (see the `lightColorIndex` note in `RasterKeyboardRgbService.MutateLightFields`). It
> appears to be a house convention across this ODM's firmware rather than an RY5088/YC3123
> specific.

## Other commands (summary)

| Command | Notes |
|---|---|
| `0x8F` device info | `Byte0-3` device ID; `Byte4` link kind (0–4 = BT1–BT5, 5 = 2.4 G, 6 = USB); `Byte5` 0 = keyboard, 1 = mouse |
| `0x01` reset | No parameters |
| `0x02` / `0x82` battery | Low-voltage threshold, default 30, range 30–90. Read returns level 1–100 |
| `0x03` / `0x83` report rate | 1 / 2 / 4 / 8 ms (1000 / 500 / 250 / 125 Hz) |
| `0x06` / `0x86` debounce | `Byte0` 0–255; debounce = `Byte0 × 2 ms + 3`, 0 → 2 ms |
| `0x04` / `0x84` profile | Profile 0–7 |
| `0x09` / `0x89` keyboard options | `Byte0` OS (0 = Win, 1 = Mac, 2 = iOS, 3 = Android); `Byte1` Fn layer (0 or 1) |
| `0x11` / `0x91` sleep time | `Byte8..15`, four 16-bit little-endian timers (BT / 2.4 G × shallow / deep) |
| `0x0A` / `0x8A` key matrix | Profile 0–7, 56 payload bytes per page |
| `0x0B` / `0x8B` macro | 64 macros (0–63), 256 bytes each — full translation in [Macros](#macros) below |
| `0x0F` SignalRGB | `Option = 0` switch effect, `= 1` send RGB data; 56 bytes of RGB payload |
| `0x0E` Windows-key colour | `R`, `G`, `B` at `Byte1..3` |

## Macros

Full translation of the "Set macro" / "Read macro" commands and the "Macro Table memory
information" section. This is what `Ry5088MacroEncoder` and `T800KeyboardMacroService` are
built against.

### Set macro — `FEA_CMD_SET_MACRO (0x0B)`

```
OUT
0x0B | Macro | Page | Len | End | 00 | 00 | CS | 8..63 payload (56 bytes)
```

- `Macro` — slot `0..63` (64 macros total).
- `Page` — `256 / 56` = 5 pages.
- `Len` — effective payload bytes in this page.
- `End` — end flag: `= 1`, macro end. Set on the **last** page of each macro; this command's
  analogue of the `FlashEnable` byte the key-matrix and trip commands carry.

### Read macro — `FEA_CMD_GET_MACRO (0x8B)`

```
OUT
0x8B | Macro | Page | 00 | 00 | 00 | 00 | CS
IN
64 bytes
```

- `Page` — `256 / 64` = 4 pages. Note the asymmetry with the write path: read replies are raw
  64-byte pages (no echoed header, like `GET_KEYMATRIX` replies), write pages carry 56 bytes
  behind the header.

### Macro blob format ("Macro Table memory information")

Each macro is a self-contained blob of up to 256 bytes:

```
Byte0..1   repeat count, uint16 LE (doc says "0~65536 times")
then per action, one 2-byte word:
  [Code, Attribute]
  Attribute bit7:    0 = break (release), 1 = make (press) — kb/mouse buttons only
  Attribute bit0-6:  delay after this action, max 127 ms. If bits 0-6 are ZERO, the
                     NEXT word is a 16-bit LE delay time instead.
Code:      0x04-0xEF keyboard code, 0xF0-0xF8 mouse code
0x00 0x00  end-of-macro marker
```

Vendor examples (inline delay, then the extended two-word form):

```
0x01, 0x00,   // repeat time
0x04, 0x81,   // press A, delay 1 ms, read next word
0x04, 0x01,   // release A, delay 1 ms, read next word
0x00, 0x00    // end macro

0x01, 0x00,   // repeat time
0x04, 0x80,   //
0x01, 0x00,   // press A, and delay 1 ms (extended 16-bit word), read next word
0x04, 0x00,   //
0x01, 0x00,   // release A, delay 1 ms, read next word
0x00, 0x00    // end macro
```

Special codes (not used by our encoder):

- `0xF9` — XY motion. Next word is the delta: `Code = X`, `Attribute = Y` (`1..127` or
  `-1..-127`).
- `0xFA` — jump macro: macro A jumps to macro B (the second byte is the target slot); the
  repeat count stays macro A's.

Because `0x00` in the code position **is** the end marker and `0xF9`/`0xFA` are commands, an
encoder must never let a code outside `0x04..0xEF` into the blob.

### Macro key binding

From the key-code table, a key-matrix record that plays a macro is:

```
Macro Key | 0x09 | Type | Index | 00
```

i.e. `[0x09, playType, slot, 0x00]` — **code1 is the play type, code2 the slot**, the mirror
image of the Raster / Smart Infinite macro records. Play types:

| Value | Meaning |
|---|---|
| `0` | repeat — plays the blob's repeat count; any key press stops playback |
| `1` | always — loops until any key press (or the macro key) stops it |
| `2` | release — plays while pressed, stops on release |

The repeat count lives in the **blob header**, not in the binding record — the record has no
room for one.

## Vendor code reports

The device pushes unsolicited 4-byte reports when state changes on-device (an Fn shortcut, a
lighting hotkey). `Byte1` is always `0x05`; `Byte2` selects the category.

| Report | Byte1 | Byte2 | Byte3 | Byte4 |
|---|---|---|---|---|
| Normal vendor code | `05` | `00` | vendor code 1 | vendor code 2 |
| Current profile | `05` | `01` | profile | `00` |
| Current report rate | `05` | `02` | rate | `00` |
| Keyboard option — Winlock | `05` | `03` | value | `01` |
| Keyboard option — OS | `05` | `03` | value | `02` |
| Keyboard option — WASD swap | `05` | `03` | value | `03` |
| **Keyboard option — LED on/off** | `05` | `03` | value | `04` |
| Keyboard option — SLED on/off | `05` | `03` | value | `05` |
| Keyboard option — KB mode | `05` | `03` | value | `06` |
| Keyboard option — KB lock | `05` | `03` | value | `07` |
| Keyboard option — Fn matrix | `05` | `03` | value | `08` |
| Keyboard option — power save | `05` | `03` | `0/1` | `09` |
| **Current LED effect** | `05` | `04` | effect | `00` |
| **Current LED speed** | `05` | `05` | speed | `00` |
| **Current LED brightness** | `05` | `06` | brightness | `00` |
| **Current LED option** | `05` | `07` | option | `00` |
| Current SLED effect / speed / brightness / option | `05` | `08`–`0B` | value | `00` |
| Reset | `05` | `0D` | reset | `00` |
| Battery | `05` | `0E` | data | `00` |
| Flash write bracket | `05` | `0F` | `01` start / `00` end | `00` |
| Fn lock / wheel swap / caps swap / sleep / caps LED / Fn swap / Alt-Tab / language / charge LED / app control | `05` | `10`–`19` | value | `00` |
| Vendor key | `05` | `FF` | value (MSB = Fn layer) | `1` press / `0` release |
| Keyboard microphone | `05` | `80` | `00` | `00` |

Extension convention per the spec: additional driver features continue the `05H 80 xx 00` range.

## Hardware specification (for reference)

- MCU: YC3123
- Flash: 512 KB, SRAM: 320 KB, 56 pins
- 8 profiles in flash
- 64 macros (G0–G63), 256 bytes each (63–127 actions), shared across profiles
- Macro loop count 1–65535
- Report rates 1 / 2 / 4 / 8 ms

---

**Source.** Translated from `docs/ry-yc3123-机械轴__2025_07_18.doc` (Rongyuan / Ronron
"Gaming SPEC", MCU YC3123, V100.8, 2025-07-18, author Evan). Translation is best-effort —
verify against the original for anything protocol-critical. The original `.doc` is an OLE2
compound file; extract its text with the piece-table walk in
`docs/plans/t800-rgb/extract-doc.py`.
