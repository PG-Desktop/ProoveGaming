# RY5088 Magnetic-Axis Keyboard Protocol

English translation of the Rongyuan (Ronron) magnetic-axis MCU HID protocol used by the
T800 keyboard. The same protocol covers the whole RY5088 MCU family: YC500, YC3121,
RY1060, YC3123, PANG1086, RY5088, RY5088_3121.

> **For lighting, read [`rongyuan-yc3123-gaming-protocol.md`](./rongyuan-yc3123-gaming-protocol.md)
> instead.** This document defines the magnetic-axis extensions but leaves the LED `Effect` and
> `Option` byte values undefined. The YC3123 "Gaming SPEC" — same command set, same vendor —
> enumerates both, and is what `T800KeyboardRgbService` is built against.

## Transport

Commands are **HID feature reports** on the vendor-defined interface (`MI_02`), report ID `0x00`,
64 data bytes — i.e. a 65-byte HidSharp `SetFeature` buffer. See the YC3123 document's Transport
section for the decoded USB control transfer.

## Packet framing

All commands are HID reports laid out as:

```
[Cmd] [P1] [P2] [P3] [P4] [P5] [P6] [CS] [Byte8..Byte63 payload]
```

- `Cmd` — command byte (see per-command sections below).
- `P1..P6` — command-specific parameters. Number of parameters and position of `CS`
  varies per command: a seven-parameter command such as `0x07` pushes `CS` out to `Byte8`.
- `CS` — checksum. Neither this document nor the YC3123 one specifies the algorithm. It is
  `0xFF - (sum & 0xFF)`, the one's complement of the sum — resolved externally and confirmed on
  hardware, see [`royuan-led-encoding-notes.md`](./royuan-led-encoding-notes.md).
- Bytes after `CS` are extended payload (only used by a subset of commands, e.g. key
  matrix downloads).

## Precision note

Trip distance values use the following unit convention:

- `0.1 mm → 10`
- `0.11 mm → 11`
- `0.13 mm → 13`
- `…`
- `4.0 mm → 400`

RT tuning range: `0.01 mm` to `3.00 mm`.

## Commands

### 1. Magnetic-axis trip set — `0x65`

```
OUT
0x65 | Option | Select | Index | FlashEnable | 00 | 00 | CS | 8..63 payload
```

`Option`:

| Value | Meaning |
|---|---|
| `0` | Set trigger travel |
| `1` | Set release travel |
| `2` | Set RT trigger travel |
| `3` | Set RT release travel |
| `4` | Set dynamic-travel starting travel |
| `5` | Set MT long-press time |
| `6` | Set dead zone |
| `7` | Set key mode (Normal = 0, RT `|= 0x80`, DKS = 2, MT = 3, TGL = 4 long-press, TGL = 5 tap, Snap = 7) |
| `8` | Dynamic-travel selectable flags |
| `9` | Snap match |
| `0xFF` | Dynamic press value |
| `0xFE` | Dynamic press travel |
| `0xFC` | Axis body label |
| `0xFB` | Top: `0..1.0 mm` (`0..100`), default `0.3 mm` (30) |

`Select = 0` (single-key set) — `Index` is the key position; `Byte8..Byte9` carry the
per-key travel setting. For `Option = 5, 7, FB`, only `Byte8` is used. For `Option = 8`,
`Byte8..Byte11` encode the four selectable layers of the dynamic-travel flags:

| Bits | Meaning |
|---|---|
| `bit0..1` | Press-0.7mm selectable flag |
| `bit2..3` | Press-3.6mm selectable flag |
| `bit4..5` | Release-3.6mm selectable flag |
| `bit6..7` | Release-0.7mm selectable flag |

Flag values: `0` = no trigger; `1` = trigger once.

`Select = 1` (bulk set) — data is packed across pages of ~28 keys per report; `Index`
selects the page (`0..4` for 1..128 keys). Fewer keys per page apply when the parameter
takes only one byte (Option `5, 7, 9, FB` = 56 keys per page).

`FlashEnable` — the last command in a batch must set `FlashEnable = 1` to persist
settings to flash; otherwise the firmware buffers changes without flashing. `0xFC` (axis
label) must be flashed separately from the other commands.

### 2. Magnetic-axis trip read — `0xE5`

```
OUT
0xE5 | Option | Select | Index | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes with per-key travel data
```

`Option` mirrors §1 with additional read-only options:

| Value | Meaning |
|---|---|
| `0..7` | Same as §1 |
| `8` | Read dynamic-travel selectable flags for one key (all 4 layers) |
| `9` | Snap match |
| `A` | Read dynamic-travel selectable flags for all keys, per layer |
| `0xFF` | Dynamic press values |
| `0xFE` | Dynamic press travels |
| `0xFC` | Axis body labels |
| `0xFB` | Top / dead zone |

Bulk reads are chunked the same way as the bulk writes in §1.

### 3. Device info — `0x8F`

```
OUT
0x8F | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response:

| Byte | Meaning |
|---|---|
| `Byte0` | `0x8F` (echo) |
| `Byte1..Byte4` | Device ID |
| `Byte5` | Connection kind: `0=BT1, 1=BT2, 2=BT3, 3=BT4, 4=BT5, 5=2.4G, 6=USB` |
| `Byte6` | `0` = keyboard, `1` = mouse |
| `Byte7..Byte8` | Version info |
| `Byte9` | `1` = `PAN_RF_BOOT` |

### 4. Device reset — `0x01`

```
OUT
0x01 | 00 | 00 | 0 | 00 | 00 | 00 | CS | 8..63 payload
```

### 5. Low-power threshold set — `0x02`

```
OUT
0x02 | LowPower | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`LowPower` — default 30, adjustable range `30..90`.

### 6. Battery read — `0x82`

```
OUT
0x82 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response:

| Byte | Meaning |
|---|---|
| `Byte0` | Battery level `1..100` |
| `Byte1` | Charging state: `0` idle, `1` charging, `2` fully charged |
| `Byte2` | Low-battery threshold value |

### 7. Report rate set — `0x03`

```
OUT
0x03 | 00 | Report | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`Report`:

| Value | Rate (Hz) |
|---|---|
| `0` | 8000 |
| `1` | 4000 |
| `2` | 2000 |
| `3` | 1000 |
| `4` | 500 |
| `5` | 250 |
| `6` | 125 |

### 8. Report rate read — `0x83`

```
OUT
0x83 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response: `Byte0=0x83`, `Byte1=0`, `Byte2=Report` (same encoding as §7).

### 9. Debounce set — `0x06`

```
OUT
0x06 | Debounce | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`Debounce` — `0..255` (mechanical switches only, ignored on magnetic-axis).

### 10. Debounce read — `0x86`

Symmetric with §9. Response: `Byte0=0x86`, `Byte1=Debounce`.

### 11. Sleep time set — `0x11`

```
OUT
0x11 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`Byte8..15` encode four little-endian 16-bit timers:

| Bytes | Timer | Default |
|---|---|---|
| `Byte8..9` | Shallow sleep 1 (BT) | 2 × 60 s |
| `Byte10..11` | Shallow sleep 1 (2.4 G) | 2 × 60 s |
| `Byte12..13` | Deep sleep 2 (BT) | 30 × 60 s |
| `Byte14..15` | Deep sleep 2 (2.4 G) | 30 × 60 s |

Maximum: 18 hours for either sleep depth.

### 12. Sleep time read — `0x91`

Symmetric with §11 (same 16-bit little-endian timers at `Byte8..15`).

### 13. Profile set — `0x04`

```
OUT
0x04 | Profile | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`Profile` — `0..3`.

### 14. Profile read — `0x84`

Response: `Byte0=0x84`, `Byte1=Profile`.

### 15. Backlight on/off set — `0x05`

```
OUT
0x05 | Byte1 | Byte2 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

| Byte | Meaning |
|---|---|
| `Byte1` | `0` = main light on, `1` = main light off |
| `Byte2` | `0` = side light on, `1` = side light off |

### 16. Backlight on/off read — `0x85`

Symmetric with §15. Response: `Byte0=0x85`, `Byte1..Byte2` = same as §15.

### 17. Keyboard options set — `0x09`

```
OUT
0x09 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
```

`Byte0..5` after Cmd:

| Byte | Meaning |
|---|---|
| `Byte0` | `0x09` (echo) |
| `Byte1` | OS: `0=Win, 1=Mac, 2=iOS, 3=Android` |
| `Byte2` | Fn layer: `0` = layer 0, `1` = layer 1 |
| `Byte3` | Debounce toggle: `0` = off, `1` = on |
| `Byte4` | RT stability (RT smoothing): `0`=0%, `1`=25%, `2`=50%, `3`=75%, `4`=100% |
| `Byte5` | WASD ↔ arrow-key swap: `0` = off, `1` = on |

### 17b. Keyboard options read — `0x89`

Symmetric with §17. Response mirrors the same field layout.

### 18. Main backlight set — `0x07`

```
OUT
0x07 | Effect | Speed | Bri | Option | R | G | B | CS | 9..63 payload
```

This document does **not** enumerate the concrete `Effect` byte values nor the semantics of the
`Option` byte. The sibling YC3123 spec does — see its
[LED Param Table](./rongyuan-yc3123-gaming-protocol.md#led-param-table) and
[Option byte — colour selector](./rongyuan-yc3123-gaming-protocol.md#option-byte--colour-selector).

The two facts that matter most, because guessing them wrong yields a board that ignores the app
entirely:

- `Speed` and `Bri` are **0–4**, not 0–100 and not 0–255 — and `Speed` is **inverted on the wire**
  (0–4 transmits as 5–1).
- `Option` low nibble selects the colour source. `0..6` are hard-coded preset colours that make
  the firmware **ignore the R/G/B bytes**; **`7` = AP colour** is the value that makes the
  keyboard follow the picker; `8` = full-colour RGB cycling. The high nibble is the per-effect
  `Type` variant (wave direction etc.).

### 19. Main backlight read — `0x87`

```
OUT
0x87 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response:

| Byte | Meaning |
|---|---|
| `Byte0` | Effect |
| `Byte1` | Speed |
| `Byte2` | Bri |
| `Byte3` | Option |
| `Byte4` | R |
| `Byte5` | G |
| `Byte6` | B |

### 21. Side light set — `0x08`

Same layout as §18 but with `Cmd = 0x08`. Silently ignored on hardware without a side
strip.

### 22. Side light read — `0x88`

Same layout as §19 but with `Cmd = 0x88`.

### 23. Keyboard key set — `0x0A`

```
OUT
0x0A | Profile | Type | Page | Len | FlashEnable | Index | CS | 8..63 payload
```

| Field | Meaning |
|---|---|
| `Profile` | `0..7` |
| `Type` | `255` = whole matrix; `0..125` = single key |
| `Page` | When `Type = 255`, `512 / 56` = 10 pages of key data |
| `Len` | Effective payload bytes in this chunk (`0..56`) |
| `FlashEnable` | `0` = keep buffering, `1` = stop and flash-persist |
| `Index` | `0..3` — one of the four magnetic-axis key layers |

Key values start at `Byte8`.

### 24. Keyboard key read — `0x8A`

```
OUT
0x8A | Profile | Type | Page | Index | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

`Type = 255` reads a whole page (8 pages of 64 bytes = 512 bytes total); `Type = 0..125`
reads a single key.

### 24b. Macro set — `0x0B`

```
OUT
0x0B | Macro | Page | Len | End | 00 | 00 | CS | 8..63 payload
```

| Field | Meaning |
|---|---|
| `Macro` | `0..63` (64 macros total) |
| `Page` | `256 / 56` = 5 pages |
| `Len` | Effective payload bytes |
| `End` | `1` = macro end marker |

This document does **not** define what goes inside the 256-byte macro blob or how a key is
bound to a macro slot. The sibling YC3123 spec does — see its
[Macros section](./rongyuan-yc3123-gaming-protocol.md#macros) for the blob layout (repeat
header, 2-byte action words, extended delays, end marker) and the `[0x09, playType, slot]`
key-matrix binding record.

### 25. Macro read — `0x8B`

```
OUT
0x8B | Macro | Page | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Replies are 64-byte pages (`256 / 64` = 4 pages per the YC3123 doc), raw with no echoed
header — note the asymmetry with the 56-byte write pages of §24b.

### 26. SKU set — `0x50`

The vendor doc has a copy-paste inconsistency here — the section header says `0x50` but
the OUT layout shows `Cmd = 0xD0`. Confirm from a capture before using.

```
OUT
0xD0 | index | 00 | 00 | 0 | 00 | 00 | CS | 8..63 payload
```

`Index` — `0..49`.

### 27. SKU read — `0xD0`

```
OUT
0xD0 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response: `Byte0` = `Index` (`0..49`).

### 28. Function list read — `0xE6`

```
OUT
0xE6 | 00 | 00 | 00 | 00 | 00 | 00 | CS | 8..63 payload
IN
0..63 bytes
```

Response:

| Byte | Meaning |
|---|---|
| `Byte0` | `0xAA` (validity marker) |
| `Byte1` | Precision: `0` = 0.01 mm, `1` = 0.005 mm, `2` = 0.001 mm |

## Vendor code definitions

Vendor code reports are 4-byte payloads sent unsolicited by the device to signal state
changes. Byte 1 is fixed at `0x05H`; byte 2 selects the sub-category:

| Category | Sub-code | Extra bytes | Meaning |
|---|---|---|---|
| Normal vendor code | — | `code1, code2, 00` | Generic vendor event |
| Current Profile | `01` | `Profile, 00` | Active profile |
| Current Rate | `02` | `Rate, 00, 00` | Active report rate |
| Winlock | `03 01` | `state, 00` | Windows-key lock toggle |
| OS | `03 02` | `os, 00` | Current OS mode |
| WASD swap | `03 03` | `state, 00` | WASD ↔ arrow toggle |
| LED on/off | `03 04` | `state, 00` | Main light on/off |
| Side LED on/off | `03 05` | `state, 00` | Side light on/off |
| Keyboard mode | `03 06` | `state, 00` | Six-key / N-key / etc. |
| Keyboard lock | `03 07` | `state, 00` | Full keyboard lock |
| Fn matrix | `03 08` | `state, 00` | Fn layer swap |
| Power save | `03 09 0/1` | — | Power-save toggle |
| LED Effect | `04` | `effect, 00, 00` | Current main-light effect |
| LED Speed | `05` | `speed, 00, 00` | |
| LED Brightness | `06` | `bri, 00, 00` | |
| LED Option | `07` | `option, 00, 00` | |
| SLED Effect | `08` | `effect, 00, 00` | Side-light |
| SLED Speed | `09` | `speed, 00, 00` | |
| SLED Brightness | `0A` | `bri, 00, 00` | |
| SLED Option | `0B` | `option, 00, 00` | |
| Reset | `0D` | `reset, 00, 00` | Device reset acknowledged |
| Battery | `0E` | `data, 00, 00` | Battery level report |
| Flash write | `0F` | `01=start/00=end, 00, 00` | Flash write bracketing |
| Vendor key | `FF` | `value, 0/1` | Vendor key press; MSB of `value` = Fn layer; second byte = press (1) / release (0) |
| FN lock | `10` | `state, 00, 00` | |
| Wheel swap | `11` | `state, 00, 00` | |
| Caps swap | `12` | `state, 00, 00` | |
| Sleep option | `13` | `state, 00, 00` | |
| Caps LED | `14` | `state, 00, 00` | |
| Fn swap | `15` | `state, 00, 00` | |
| Alt-Tab | `16` | `state, 00, 00` | |
| Language switch | `17` | `state, 00, 00` | |
| Charge LED | `18` | `state, 00, 00` | |
| App control | `19` | `state, 00, 00` | |
| Magnetism height | `1B` | `height_low, height_high, index, 00` | |
| Keyboard microphone | `80` | `00, 00, 00, 00, 00` | Extension slot |

Extension conventions per the doc:

- Add-on driver features extend `05H 80 XX 00`.
- Add-on device-stored info reports extend `05H 20 data 00`.

---

**Source.** Translated from `磁轴协议_RY5088_2025_07_22.docx`
(Rongyuan / Ronron internal protocol document, dated 2025-07-22). Translation is
best-effort — verify against the original for anything protocol-critical.
