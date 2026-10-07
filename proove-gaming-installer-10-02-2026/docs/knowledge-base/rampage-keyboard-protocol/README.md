# Rampage keyboard — Smart Infinite ("智慧无限") HID protocol

English translation and analysis of the vendor drop for the Rampage keyboard's key-assignment / macro / lighting engine. Original files:

- `smart-infinite-protocol-original.docx` — vendor-supplied Word doc, mostly annotated HID-capture screenshots (reproduced under `images/`).
- `keyboard_key_define.h` — vendor C header with the type/opcode catalog.
- `keyboard_user_flash.c` — vendor default `key_matrix_tab` (factory assignment for every physical key).

The transport is a plain USB HID pipe using report ID `0x06` — the same magic byte the current `RampageKeyboardRgbService` already uses for lighting.

---

## 1. Transport summary

Translation of the doc's Chinese text:

> "Keyboard-device connection logic — mainly implemented via HID library functions."
>
> "The physical layout you draw yourself, but every key has a corresponding **index**; after you read the key data back, you match by that index. The total key count, per-key index, and USB HID code are all in the supplied file (`keyboard_key_define.h`, `keyboard_user_flash.c`)."
>
> "**Storage sizes**" — see [§3](#3-firmware-table-sizes).
>
> "When you write a command, you must send **the full length** shown above — the keyboard will only store the payload if the transfer is complete."
>
> "Command-related" — see [§4](#4-hid-command-catalog).
>
> "Note: you must send a **full 384-byte** payload for the keyboard to store the assignment / lighting table."

Practical wire format (extracted from images 3–6):

- **Report ID** = `0x06` (every request and every reply starts with this byte).
- **Packet size** = 64 bytes (raw HID report), of which **56 bytes** are payload after the 8-byte header `(report_id, cmd, seq/offset, sub_index, ...)`.
- **Payload is always 56 bytes** (`64 − 8` header). A 64-byte report can never carry 64 payload
  bytes; an earlier revision of this file claimed `6 × 64 = 384`, which is arithmetically impossible.
- **Transfer length is derived from the key count, not the table reserve.** image4 spells it out:
  `96 keys × 3 = 288 bytes` of real data, `64 − 8 = 56` bytes per packet, `288 / 56 = 5.14 → 6`
  packets. Both captures (image5 write, image6 read) show exactly six packets at offsets
  `0x00, 0x38, 0x70, 0xA8, 0xE0, 0x118`, every one carrying a full 56 bytes — so the transfer
  covers 336 bytes, running past the 288 bytes of data into the table reserve.
- **Never send a partial per-key write.** The unit of a write is the whole key table.

> [!IMPORTANT]
> **Resolved by the factory update of 2026-08 (RM1) — a key write is 384 bytes, with no session
> bracket.** The docx said twice that the keyboard only stores a table if you "send the full 384
> bytes" (`注意需要发满384Byte键盘才会存储`), while image5 — a capture of the vendor's own driver
> performing a remap — sends only 336 bytes in 6 packets. The factory has now settled the
> conflict: commands `0x10` and `0x11` must **not** be used at all, the key write is a bare `0x18`
> transfer, and the total transfer length must be exactly **384 bytes** (7 packets: 6 × 56 + 48).
> The 336-byte capture shape is what the firmware acks and silently discards. Reads still follow
> the capture (6 packets / 336 bytes); only the write requires the full reserve.

## 2. Assignment cell format — every position is `{TYPE, CODE_L, CODE_H}`

Each physical key occupies three bytes in the 384-byte block. The first byte switches the interpretation of the next two:

| TYPE | Meaning | CODE_L | CODE_H |
|------|---------|--------|--------|
| `0x10` | Standard keyboard key | Modifier bitmask (`KEY_CTRL_L=0x01` … `KEY_WIN_R=0x80`, ORed) | USB HID usage code (see `keyboard_key_define.h`) |
| `0x11` | Repeat / key-repeat | modifier mask | HID usage |
| `0x20` | Consumer control (multimedia / browser / launcher) | low byte of the 16-bit consumer usage ID | high byte |
| `0x30` | System control | `0x01` power / `0x02` sleep / `0x04` wake-up | `0x00` |
| `0x40` | Mouse button | `MS_LEFT/RIGHT/MIDDLE/K4/K5` (`0x01/0x02/0x04/0x08/0x10`) | `0x00` |
| `0x41` | Mouse-axis swing | axis | delta |
| `0x42` | Mouse wheel | direction | step |
| `0x43` | Mouse "fire" | mode | count |
| `0x44` | Mouse DPI action | `0x01` +, `0x02` −, `0x03` loop | `0x00` |
| `0x45` | Mouse-report toggle | flag | `0x00` |
| `0x50` | Macro — single-shot | macro-slot index | flags |
| `0x51` | Macro — N-plays | macro-slot index | play count |
| `0x52` | Macro — gun / burst | macro-slot index | rate |
| `0x60` | Custom function (see below) | function-id L | function-id H |
| `0x61` | Text string | pointer L | pointer H |
| `0x70` | FN-combo (looks in the FN-layer table) | index L | index H |
| `0x80` | Advanced key — Dynamic Key Stroke (DKS) | slot | mode |
| `0x81` | Advanced key — Modifier-Tap (hold vs click) | tap code | hold code |
| `0x82` | Advanced key — Toggle | code | flags |
| `0x83` | Advanced key — SOCD ("急停 / Snappy Tappy") | code A | code B |
| `0x84` | Advanced key — Rapid-Trigger ("迅洁 / RS") | on-threshold | off-threshold |

USB HID usage codes for TYPE `0x10`/`0x11` are the standard set (`KEY_A = 0x04` … `KEY_F24 = 0x73`) plus a handful of vendor-specific ISO/JIS positions (`KEY_K29 = 0x31`, `KEY_K42 = 0x32`, `KEY_K45 = 0x64`, `KEY_K56 = 0x87`, `KEY_K107 = 0x85`, `KEY_K131 = 0x8B`, `KEY_K132 = 0x8A`, `KEY_K133 = 0x88`, `KEY_K150 = 0x91`, `KEY_K151 = 0x90`).

### 2.1 Custom-function IDs (`TYPE = 0x60`)

Reproduced from `keyboard_key_define.h`. IDs go in `CODE_L`; `CODE_H` is `0x00`.

| ID | Symbol | Purpose |
|----|--------|---------|
| `0x01` | `KEY_FN` | FN layer modifier |
| `0x02` | `KEY_MACRO` | Trigger any macro slot |
| `0x03` | `KEY_FUNCTION` | PC shortcut |
| `0x04` | `KEY_WIN_LOCK` | Disable Win keys |
| `0x05` | `KEY_RESET` | Factory reset |
| `0x06` | `KEY_KEYBOARD_LOCK` | Lock all keys |
| `0x07` | `KEY_WASD_CHANGE` | Swap WASD ↔ arrows |
| `0x20`…`0x22` | `KEY_LIGHT_SET/UP/DOWN` | Backlight brightness |
| `0x23`…`0x25` | `KEY_SPEED_SET/ADD/DEC` | Backlight speed |
| `0x26`…`0x28` | `KEY_TREND_SET/LEFT/RIGHT` | Backlight direction |
| `0x29`, `0x2A` | `KEY_COLOUR_BOARD_SET`, `KEY_COLOUR_SET` | Colour palette |
| `0x2B` | `KEY_LED_OFF` | Backlight off |
| `0x2C`…`0x32` | `KEY_LED_SET`, `KEY_LED_SET_1..6` | Preset mode cycle / direct switch |
| `0x33`…`0x39` | `KEY_LED_DEFINE`, `KEY_DEFINE_SET_1..6` | Custom-lighting slots (matches the 384-byte block at cmd `0x19`) |
| `0x3A`…`0x3F` | `KEY_LOGO_*` | LOGO-lamp controls |
| `0x40`…`0x46` | `KEY_SIDE_*` | Side-lamp controls |
| `0x50`…`0x56` | `KEY_G_CLASS`, `KEY_G1..G6` | Macro G-slots |
| `0x60`…`0x67` | `SWITCH_BT_MODE`, `SWITCH_2P4G_MODE`, `PAIR_KEY`, `SWITCH_BT_1..3`, `SWITCH_2D4G`, `SWITCH_USB` | Wireless mode + pairing |
| `0x68` | `BATT_VALUE_SHOW` | Battery LED indicator |
| `0x69`, `0x6A` | `SWITCH_WIN_MODE`, `SWITCH_MAC_MODE` | OS mode |
| `0x6C`, `0x6D`, `0x6E` | `SWITCH_USER_LAYER`, `SWITCH_OFFICE_LAYER`, `SWITCH_GAME_LAYER` | Profile / layer switch |
| `0x6F` | `KEY_LED_TEST_MODE` | Full-white test |

## 3. Firmware table sizes

Constants from image 2 (`keyboard_user_flash.c` companion header — not shown in the header we received but referenced by the doc):

| Symbol | Value | Notes |
|--------|-------|-------|
| `PROFILE_NUM` | `3` | Currently active profile count. `MAX = 5`. |
| `INFO_TAB_SIZE` | `64` | Device-info block (product name, versions, counters). |
| `FUNC_TAB_SIZE` | `64` | Function / config block. |
| `KEY_MATRIX_TAB_SIZE` | `384` | Base assignment table (96 keys × 3 bytes = 288, padded to 384). |
| `FN_KEY_MATRIX_TAB_SIZE` | `384` | FN-combo assignment table. |
| `LED_DEFINE_TAB_SIZE` | `384` | Custom per-key lighting (96 × RGB = 288, padded to 384). |
| `MACRO_TAB_SIZE` | `2048` | Macro storage (all slots concatenated). |

## 3.1 Device discovery — picking the right HID interface (image1)

The vendor's enumeration code matches on **four** fields, not two:

```c
t_pTempDeviceInfo->vendor_id  == VID
&& t_pTempDeviceInfo->product_id == PID
&& t_pTempDeviceInfo->usage      == 32      // 0x20
&& t_pTempDeviceInfo->usage_page == 65415   // 0xFF87
```

annotated "in wired mode DeviceUsage and DeviceUsagePage are fixed at 32 and 65415".

This matters because the screenshot's device tree shows the keyboard exposing **three** separate
`HID-compliant vendor-defined device` nodes, only one of which is the config interface. Filtering
by report-length shape alone (`outLen == 64 && inLen == 64`) cannot distinguish them, so it reduces
to enumeration order — which is exactly the "works on one machine, not another" class of bug.
`DeviceKeyMappingServiceFactory` uses the usage/usage-page rule, falling back to the shape
heuristic only when a report descriptor cannot be read.

> `DeviceRgbServiceFactory` still selects Rampage / Aress / Cyrex interfaces by shape alone and
> should be migrated to the same rule.

## 3.2 Device info (`0x12`) layout (image3)

64 bytes, so **two** packets (offset 0 and offset 56) — not the single packet §4 used to claim.
Reading only the first packet silently drops fields 56–63.

| Index | Field | Notes |
|-------|-------|-------|
| 0, 1 | validity marker | `0xAA`, `0xBB` |
| 2, 3 | Product ID L/H | `USBD_PID` |
| 4 | key-matrix size | **in units of 3 bytes** (capture shows `0x80` = 128 slots = 384 B reserve) |
| 5 | macro space size | **in units of 256 bytes** |
| 6 | actual key count | 96 on Rampage |
| 9 | device type | `0` USB, `1` USB+BLE, `2` USB+2.4G, `3` USB+BLE+2.4G |
| 10, 11 | product version L/H | `USBD_REV` |
| 12 | max brightness level | relevant to the existing RGB service |
| 13 | max speed level | |
| 14 | default brightness | |
| 15 | default speed | |

Fields 4 and 6 are what should drive transfer length (`key_count × 3`) rather than a hardcoded
constant. The current implementation hardcodes Rampage's 96 keys; reading it from device info would
generalise the code to Aress and Cyrex.

## 4. HID command catalog

Every command starts with report ID `0x06` followed by the command byte. Reads return one or more 64-byte packets containing the requested table.

> [!WARNING]
> **This table originally listed `0x17` as the base-matrix *write* and `0x18` as the FN-combo
> table. Both were wrong.** Corrected below against PR #45's hardware testing, which drove a real
> Rampage through the whole command set — see [`hardware-findings.md`](hardware-findings.md).
> Trust the table below and `RampageProtocol.cs`, not the vendor screenshots, wherever they differ.

| Cmd | Direction | Purpose | Notes |
|-----|-----------|---------|-------|
| `0x10` | write | Start communication | **Do not send** — the factory update of 2026-08 (RM1) states `0x10`/`0x11` are not used, for the key write or any other command. Kept for decoding old captures. |
| `0x11` | write | End communication | **Do not send** — see `0x10`. |
| `0x12` | read | Device info | Reply carries product ID, product name, firmware/protocol version, key count, macro count. Single-packet. |
| `0x14` | read | Function / config table | Contents include display-mode, current profile, wireless status, etc. Single-packet (fits in 64 B). |
| `0x15` | write | Set general info | The RGB/feature-area write. **Side effect:** zeroes the key-assignment staging buffer, so an RGB change can wipe a pending key remap. |
| `0x16` | read | **Default** (factory) key-matrix table | 384 B total → **7 packets** (see §1). Each key = 3 bytes at `offset = index × 3`. Example: `10 00 29 = Esc`, `10 00 3A = F1`. |
| `0x17` | read | **Current** key-matrix table | Reads back what has actually been written. Not a write — this was the doc's most damaging error. |
| `0x18` | write | **Set** key info (base matrix) | Acks with status `0x55`. Must push all 384 bytes — see the note below. |
| `0x19` | read / write | Custom per-key lighting | Same 384 B block; each key is 3 bytes = R, G, B. |
| `0x1D` | write | Sync lighting | Refreshes the backlight only. Does **not** reload the key scan matrix. |
| `0x20` | write | ⚠️ **FACTORY RESET — DO NOT SEND** | The vendor sheet says only "Reset". Behavioural testing showed it wipes all user config and leaves the board in rainbow/pairing mode needing vendor recovery. |
| `0x30` / `0x31` | read / write | Current profile | A `0 → 1 → 0` round-trip on `0x31` **freezes the firmware** until the keyboard is physically replugged. |
| `0x32` / `0x33` / `0x34` | read / read / write | FN-combo (layer 2) matrix | Same 384 B format, addressing the FN-combo table: read-default / read-current / set. |

**The full-length write requirement.** The vendor text states twice that the keyboard only *stores*
a table when the complete declared length arrives (384 bytes for the key matrix). A partial write
is acked and is even visible to a `0x17` read-back, but lands only in a volatile staging buffer —
PR #45 wrote 3 bytes per key and found the physical key kept emitting its factory scancode until
the keyboard was replugged. Always send all 7 packets.

(Macros are stored in a separate 2 KB block; the exact command byte is not shown in the six
screenshots we have — likely follows the same read/write pair convention.)

### 4.1 Packet layout

> [!WARNING]
> This section previously described byte 2 as a *sequence number*, byte 3 as a *sub-index* and
> bytes 4–7 as *reserved*. That is wrong. The real layout is below, and it matches both the shipped
> `RampageKeyboardRgbService` (working against hardware since before this document existed) and the
> §4.2 hex sample in this very file.

**Request:**

```
byte 0     report_id = 0x06
byte 1     cmd
byte 2..3  addr       little-endian byte offset into the addressed table (NOT a packet number)
byte 4     len        payload bytes carried (writes) or requested (reads); 0x38 = 56
byte 5..6  checksum   little-endian additive sum — see below
byte 7     0x00 in a request; 0x55 in a reply = success
byte 8..   payload    (writes only)
```

**Response:** identical, except byte 7 carries `0x55` on success and bytes 8.. carry the returned
data. Byte 7 is the only ACK the protocol offers — do not discard it.

The receiver concatenates the payload of all 6 packets by `addr`.

### The checksum is a plain sum, not a CRC

```
checksum = cmd + addr_lo + addr_hi + len + (sum of all payload bytes)
```

16-bit, stored little-endian at bytes 5–6. Verified against every packet of image6 (lighting read,
cmd `0x19`):

| offset | `0x19 + lo + hi + 0x38` | bytes in capture |
|--------|--------------------------|------------------|
| `0x0000` | `0x51` | `38 51 00` ✓ |
| `0x0038` | `0x89` | `38 89 00` ✓ |
| `0x0070` | `0xC1` | `38 c1 00` ✓ |
| `0x00A8` | `0xF9` | `38 f9 00` ✓ |
| `0x00E0` | `0x131` | `38 31 01` ✓ (little-endian carry) |
| `0x0118` | `0x6A` | `38 6a 00` ✓ |

And on the write side, image5's first packet `06 18 00 00 38 5c 05 00`: the header contributes
`0x18 + 0 + 0 + 0x38 = 0x50`, leaving `0x055C − 0x50 = 0x050C` as the sum of the 56 payload bytes.

> Earlier revisions of this repo used CRC16/Modbus here, inherited from the RGB service. That was
> wrong, and it is a live suspect for why previous remap attempts were acknowledged but never took
> effect. `RampageProtocolTests` now locks the formula against these captures.

### 4.2 Worked example — default-matrix read (image4)

```
06 16 00 00 38 4e 00 00 …     checksum = 0x16+0x00+0x00+0x38 = 0x4E
```

Response payload begins `10 00 29 10 00 3A …` = Esc, F1, F2, … matching the first row of the
factory `key_matrix_tab`.

> An earlier revision of this file printed this packet with checksum `4a`. That value is the
> correct checksum for command **`0x12`** (`0x12+0+0+0x38 = 0x4A`, see image3) — the packet had been
> copied from a neighbouring screenshot with the command byte changed and the sum not recomputed.
> A reader who trusted it would have sent a packet the firmware rejects.

### 4.2.1 Worked example — key remap (image5)

The complete vendor procedure for `Esc → A`:

1. Send `0x17` six times and reassemble the current key table.
2. Patch `KeyData[index*3 + 0..2]`. Esc is index 0, so bytes 0–2 become `10 00 04` (A).
3. Send the whole patched table back with `0x18`, six packets:
   `06 18 00 00 38 5c 05 00 | 10 00 04 10 00 0a …`

Each reply echoes the request with byte 7 = `0x55`.

### 4.3 Lighting example (image6)

Custom-lighting **read** — image6 is annotated `0x19：读取自定义灯光` ("read custom lighting") and
consists entirely of OUT-request / IN-reply pairs:

```
06 19 00 00 38 51 00 00     checksum = 0x19+0x00+0x00+0x38 = 0x51
06 19 38 00 38 89 00 00
06 19 70 00 38 c1 00 00
06 19 a8 00 38 f9 00 00
06 19 e0 00 38 31 01 00
06 19 18 01 38 6a 00 00
```

Same six-packet chunking as the key table; each key is 3 bytes = `R G B`. An earlier revision
labelled this a *write* — there is no capture of a lighting write in the supplied material.

## 5. Default `key_matrix_tab` (from `keyboard_user_flash.c`)

96 entries in 6 rows of 16 columns. Every entry is `{TYPE, CODE_L, CODE_H}`; empty positions are `{0x10, 0x00, 0x00}`. Reproduced compactly (row / column → key):

- **R0 (0..15)** — Esc, F1–F12, PrtSc, ScrLk, Pause
- **R1 (16..31)** — \`~, 1..0, -, =, Backspace, Insert, Home
- **R2 (32..47)** — Tab, Q W E R T Y U I O P, `[`, `]`, `\` (K29 = `0x31`), Delete, End
- **R3 (48..63)** — Caps, A S D F G H J K L, `;`, `'`, (empty), Enter, PgUp, PgDn
- **R4 (64..79)** — L-Shift, (empty), Z X C V B N M, `,`, `.`, `/`, R-Shift, (empty), Up, `{0x60, 0x00, 0x00}` (custom slot, factory blank)
- **R5 (80..95)** — L-Ctrl, L-Win, L-Alt, (empty×3), Space, (empty×2), R-Alt, **`{0x60, KEY_FN, 0x00}`** (FN), Menu, R-Ctrl, Left, Down, Right

The FN key at matrix index **90** is the only entry with `TYPE=0x60` in the factory layout — it selects the FN-combo table for whichever key is pressed next.

## 6. Fit with the existing implementation

The device is registered in `src/ProoveGaming.DeviceHost/appsettings.json` (VID `10473` = `0x28E9`,
PID `12888` = `0x3258`).

**What is shared:** report ID `0x06`, 64-byte reports, the 56-byte payload, and the framing and
checksum — all now in `src/ProoveGaming.Infrastructure/Classes/RampageProtocol.cs`, used by the key
mapping service *and* by the three RGB services (Rampage, Aress, Cyrex — see
`DeviceRgbServiceFactory`). Any protocol work here is immediately three-device work.

**What is not shared:** the command sets do not overlap at all. `RampageKeyboardRgbService` runs
`0x10 (start) → 0x15 (feature area) → 0x1D (sync lighting) → 0x11 (end)`. It does **not** use
`0x19`, despite what an earlier revision of this file claimed — the key-table family
(`0x12/0x14/0x16/0x17/0x18/0x19`) was built from scratch.

Key assignment lives in `RampageKeyboardKeyMappingService`, with slots supplied as constants by
`RampageKeyCatalog` rather than probed at runtime.

## 7. Open questions for the next revision of this doc

**Genuinely open:**

- **The FN layer.** All that is actually known: the firmware reserves a second table of the same
  size (`FN_KEY_MATRIX_TAB_SIZE = 384`) and defines a cell type `FN_COM_TYPE = 0x70`. No capture
  shows a command that reads or writes it, nor how its indices relate to the base matrix, nor how
  the `TYPE=0x60 KEY_FN` position connects to it. The opcodes `0x32`/`0x33`/`0x34` in
  `RampageProtocol` are inherited from earlier work and are **unverified**. Earlier revisions of
  this file described a confident "FN-combo layer 2" model built entirely on inference — that has
  been removed.
- **Command byte for the 2 KB macro table.**

**Resolved, kept for the record:**

- ~~336 vs 384 bytes on a write~~ — settled by the factory update of 2026-08 (RM1): the key write
  must total exactly **384 bytes** (7 packets, 6 × 56 + 48), sent as a bare `0x18` with no
  `0x10`/`0x11` session bracket. The 336-byte shape image5 shows is acked and discarded.
- ~~Whether a write needs a commit/flush step~~ — per the same factory update, no: `0x18` alone is
  the whole write procedure. The "never persisted" symptom was the transfer length (336 ≠ 384),
  not a missing commit command.
- ~~Exact write command~~ — `0x18`, per image5's annotation and capture. `0x17` is a *read*.
- ~~Role of the byte at packet position 3~~ — it is the high byte of a little-endian address, not a
  marker. Position 7 is the ACK (`0x55`).
