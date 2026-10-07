# X43 Wired Gaming Mouse — USB HID Protocol

## Device Capabilities

- 8 profiles stored in flash
- 8 macro stages (G0–G48), each macro 250 bytes (125 actions), shared across all profiles
- Macro loop: 1–65535 repetitions
- All standard keys can be remapped or assigned a macro
- Report rates: 1 / 2 / 4 / 8 ms
- Up to 8 DPI stages

---

## USB Protocol Overview

| # | Description |
|---|-------------|
| 1 | `SetFeatureReport` — issue commands to device |
| 2 | `GetFeatureReport` — read command responses |
| 3 | **EP5** — data IN (device → host), 64-byte reports |
| 4 | **EP6** — data OUT (host → device), 64-byte reports |
| 5 | Checksum: `CS = (Byte0 + Byte1 + … + Byte7) XOR 0xAA` — **not validated by firmware** |

Vendor Page `0xFF01H`, Vendor Usage `0x22H`.

---

## Commands

### `80H` — Get Firmware Version

**OUT (SetFeature, 8 bytes):**
```
80 00 00 00 00 00 00 CS
```

**IN (GetFeature, 8 bytes):**
```
80 H-Ver L-Ver mcu_type sensor_type key_sum 00 00
```

**MCU types:**

| Code | MCU |
|------|-----|
| 01H  | 50343 |
| 02H  | 52341 |
| 03H  | 571   |
| 04H  | 572   |
| 05H  | 574   |
| 06H  | 553   |
| 07H  | 560   |
| 08H  | 541   |
| FFH  | Other |

**Sensor types:**

| Code | Sensor |
|------|--------|
| 01H  | PMW3050 |
| 02H  | PMW3205 |
| 03H  | PMW3325 |
| 04H  | PMW3327 |
| 05H  | PMW3336 |
| 06H  | PMW3337 |
| 07H  | PMW3360 |
| 08H  | PMW3389 |
| 09H  | PMW4090 |
| 21H  | OM36    |
| 22H  | OM37    |

---

### `01H` — Set Current Profile

**OUT (SetFeature, 8 bytes):**
```
01 Profile# 00 00 00 00 00 CS
```
Profile range: **0–5**

---

### `81H` — Read Current Profile

**OUT:**
```
81 00 00 00 00 00 00 CS
```

**IN:**
```
81 Profile# 00 00 00 00 00 00
```

---

### `02H` — Set Parameter 0

**OUT (SetFeature, 8 bytes):**
```
02 Profile# 00 00 00 00 01 CS
```
Followed by **1 × 64-byte WriteFile chunk via EP6**.

**Parameter 0 data layout (64 bytes):**

| Byte | Field | Description |
|------|-------|-------------|
| 0 | `reportrate` | Report rate: 0=1ms, 1=2ms, 2=4ms, 3=8ms |
| 1 | `dpi_stage` | Active DPI stage index (0–7) |
| 2 | `ledEffect` | LED effect (see table below) |
| 3 | `ledBrightness` | Brightness: 00H–FFH |
| 4 | `ledSpeed` | Speed: 01H–07H |
| 5 | `ledColor` | Color slot: 0–7 (7 = loop all slots) |
| 6 | `
` | Flow direction: 0 = forward, 1 = reverse |
| 7–63 | — | Reserved |

**LED Effect codes (Byte 2):**

| Code | Effect (CN) | Effect (EN) | Brightness | Speed | Color | Option |
|------|-------------|-------------|-----------|-------|-------|--------|
| 00H | 全灭 | All Off | — | — | — | — |
| 01H | 流光 | Flowing Light | 00–FF | 01–07 | 0–7 | 0 / 1 |
| 02H | 常亮 | Always On (Static) | 00–FF | 01–07 | 0–7 | — |
| 03H | 霓虹 | Neon | 00–FF | 01–07 | 0–7 | — |
| 04H | 呼吸 | Breathing | 00–FF | 01–07 | 0–7 | — |
| 05H | 指动 | Finger Move | 00–FF | 01–07 | 0–7 | — |
| 06H | 雨滴 | Raindrop | 00–FF | 01–07 | 0–7 | — |
| 07H | 跑马 | Marquee | 00–FF | 01–07 | 0–7 | 0 / 1 |
| 08H | 点动 | Spot Move | 00–FF | 01–07 | 0–7 | — |
| 09H | 对碰 | Collision | 00–FF | 01–07 | 0–7 | — |
| 0A–9FH | — | Reserved | — | — | — | — |

---

### `82H` — Read Parameter 0

**OUT:**
```
82 00 00 00 00 00 CS
```

**IN (GetFeature):**
```
82 Profile# 00 00 00 00 01 00
```
Followed by **1 × 64-byte ReadFile chunk via EP5**.

---

### `03H` — Set Parameter 1

**OUT (SetFeature, 8 bytes):**
```
03 Profile# index 00 00 status times CS
```
Followed by **`times` × 64-byte WriteFile chunks via EP6**.

| Field | Description |
|-------|-------------|
| `index` | Starting block index (`index × 64` = byte offset) |
| `times` | Number of 64-byte chunks to follow (max 3) |
| `status` | Bitmask of which blocks have changed: bit0=block0, bit1=block1, bit2=block2 |

**Parameter 1 data layout (192 bytes, 3 × 64-byte blocks):**

#### Block 0 — Sensor / DPI Config (bytes 0–63)

| Bytes | Field | Description |
|-------|-------|-------------|
| 0 | `btn_debounce` | Button debounce time (ms, max 50) |
| 1 | `wheel_debounce` | Wheel debounce time (ms) — *not used* |
| 2 | `sensor_config` | Sensor settings — *not used* |
| 3 | `sensor_lift` | Lift-off height: 2 or 3 mm |
| 4 | `dpiStage_Max` | Number of active DPI stages (max 8) — *not used* |
| 5–6 | `scale_x` | DPI X scale (`scale_x / 100`) — *not used* |
| 7–8 | `scale_y` | DPI Y scale (`scale_y / 100`) — *not used* |
| 9–24 | `dpi_x_value[8]` | 8 × 16-bit LE DPI X values (raw register, no driver processing needed) |
| 25–40 | `dpi_y_value[8]` | 8 × 16-bit LE DPI Y values (same format as X) |
| 41–63 | — | Reserved (23 bytes) |

#### Block 1 — Color Data (bytes 64–127)

| Bytes | Field | Description |
|-------|-------|-------------|
| 64–87 | `dpi_RGB_Color[8]` | DPI indicator colors: 8 × [R, G, B] |
| 88–111 | `LED_RGB_Color[8]` | LED effect colors: 8 × [R, G, B] |
| 112–127 | — | Reserved (16 bytes) |

#### Block 2 — Key Matrix (bytes 128–191)

16 keys × 4 bytes each. Default assignments:

| Key | Default |
|-----|---------|
| KEY0 | Mouse Left Button |
| KEY1 | Mouse Right Button |
| KEY2 | Mouse Middle Button |
| KEY3 | Mouse B4 |
| KEY4 | Mouse B5 |
| KEY5–KEY15 | Reserved |

---

### `83H` — Read Parameter 1 *(not used)*

**OUT:**
```
83 00 00 00 00 00 CS
```

**IN (GetFeature):**
```
83 Profile# 00 00 00 00 04 00
```
Followed by **4 × 64-byte ReadFile chunks via EP5** (256 bytes total).

---

### `04H` — Set Macro Key

**OUT:**
```
04 Profile# Macro# 00 00 00 02 CS
```
Followed by **2 × 64-byte WriteFile chunks via EP6** (128 bytes per macro).

`Macro#` range: 0–47.

---

### `84H` — Read Macro Key

**OUT:**
```
84 00 00 00 00 00 00 CS
```

**IN:**
```
84 Profile# Macro# 00 00 00 02 00
```
Followed by **2 × 64-byte ReadFile chunks via EP5**.

---

### `05H` — Set USB String *(not used)*

**OUT:**
```
05 00 00 00 00 00 08 CS
```
Followed by **8 × 64-byte WriteFile chunks** (Manufacturer / Product / SerialNumber / Date strings, 100 bytes each, Unicode).

---

### `85H` — Read USB String *(not used)*

**OUT:**
```
85 00 00 00 00 00 00 CS
```

**IN:**
```
85 00 00 00 00 00 08 00
```
Followed by **8 × 64-byte ReadFile chunks via EP5**.

---

## Vendor Code Report (Interface 1, EP2, Report ID 0x05)

The device sends this 11-byte report to the host after each parameter write.

| Byte | Field |
|------|-------|
| 0 | ID (0x04) |
| 1 | Profile Number |
| 2 | reportrate |
| 3 | dpi_stage |
| 4 | ledEffect |
| 5–8 | Reserved |
| 9 | Code |
| 10 | Type |

**Status codes:**

| Type | Code | Meaning |
|------|------|---------|
| FEH | 02H | Set parameter_0 succeeded |
| FEH | 03H | Set parameter_1 succeeded |
| FEH | 04H | Set macro succeeded |
| FEH | 05H | Set strings succeeded |
| FFH | 00H | Profile reset to defaults (write rejected) |
| F1H | — | Vendor code AP identification |

---

## Key Matrix Format

Each key uses **4 bytes**: `[Byte0 (1st), Byte1 (2nd), Byte2 (3rd), Byte3 (4th=Mode)]`

### Key Modes (Byte3)

| Mode | Name | Byte2 | Byte1 | Byte0 |
|------|------|-------|-------|-------|
| 00H | Null (disabled) | — | — | — |
| 01H | Keyboard | Code#2 | Code#1 | Code#0 |
| 02H | Mouse Button | — | — | Code |
| 03H | Multimedia key | — | Code#1 | Code#0 |
| 04H | ACPI key | — | — | Code |
| 05H | Macro key | Macro Type | — | Macro Index |
| 06–09H | Reserved | — | — | — |
| 0AH | LED Control | Category | Value | Sub-function |
| 0BH | DPI Settings | Code#2 | Code#1 | Code#0 |
| 0CH | Fire Key | Repeat# | Delay | Code#0 |
| F0H | Special Switch | Sub | Param | Function |
| F1H | Vendor Hot Key | Code#2 | Code#1 | Code#0 |

### LED Control (0AH) — Sub-functions

Format: `[Sub-fn, Value, Category, 0AH]`

| Category | Name |
|----------|------|
| 00H | Effect |
| 01H | Brightness |
| 02H | Speed |
| 03H | Color |
| 04H | Option |

| Sub-fn | Action |
|--------|--------|
| 01H | Increment |
| 02H | Decrement |
| 03H | Loop |
| 04H | Set Value |

**Examples:**

| Entry `[B0,B1,B2,B3]` | Action |
|------------------------|--------|
| `03 00 00 0A` | Effect: cycle (loop) |
| `04 02 00 0A` | Effect: set to 02H (Always On) |
| `04 00 00 0A` | Effect: set to 00H (All Off) |
| `04 FF 01 0A` | Brightness: set to FFH (max) |
| `03 00 02 0A` | Speed: cycle (loop) |
| `04 00 03 0A` | Color: set to slot 0 |

### DPI Settings (0BH)

| Entry | Action |
|-------|--------|
| `01 00 00 0B` | DPI+ |
| `02 00 00 0B` | DPI− |
| `03 00 00 0B` | DPI+ Loop |
| `04 VH VL 0B` | DPI set to value |

### Special Switch Key (F0H)

| `[B0,B1,B2,B3]` | Action |
|------------------|--------|
| `00 00 00 F0` | Fn key |
| `01 00 01 F0` | Profile+ |
| `02 00 01 F0` | Profile− |
| `03 00 01 F0` | Profile loop |
| `04 V  01 F0` | Profile set value |
| `00 01 02 F0` | Report rate+ |
| `00 02 02 F0` | Report rate− |
| `00 03 02 F0` | Report rate loop |
| `04 V  02 F0` | Report rate set value |
| `VL VH 03 F0` | Snipe (DPI value) |
| `AltOrWin Delay 04 F0` | Tab function |

---

## Macro Format

Each macro: 250 bytes. First 2 bytes = repeat counter (1–65535). Then pairs of `[Attribute, KeyCode]`, terminated by `00 00`.

**Macro loop types:**

| Type | Behavior |
|------|----------|
| 0x00 | Normal: run until repeat counter, stop on any button press |
| 0x01 | Always repeat, stop on any button press |
| 0x02 | Always repeat until button release |

**Attribute byte:**
- Bit 7: `0` = make (press), `1` = break (release)
- Bits 0–6: delay in 10 ms units (0–127, i.e. 0–1.27 s)

---

## Key Code Table (partial)

### Mouse Buttons (mode 02H)

| Key | Code | Matrix entry `[B0,B1,B2,B3]` |
|-----|------|------------------------------|
| Left Button | F0H | `F0 00 00 02` |
| Right Button | F1H | `F1 00 00 02` |
| Middle Button | F2H | `F2 00 00 02` |
| B4 (back) | F3H | `F3 00 00 02` |
| B5 (forward) | F4H | `F4 00 00 02` |
| Wheel up | F5H | `F5 V  00 02` |
| Wheel down | F6H | `F6 V  00 02` |
| AC Pan left | F7H | `F7 V  00 02` |
| AC Pan right | F8H | `F8 V  00 02` |

### Keyboard Keys (mode 01H, selected)

| Key | Code | Matrix entry `[B0,B1,B2,B3]` |
|-----|------|------------------------------|
| a–z | 04H–1DH | `XX 00 00 01` |
| 1–0 | 1EH–27H | `XX 00 00 01` |
| Return | 28H | `28 00 00 01` |
| Escape | 29H | `29 00 00 01` |
| F1–F12 | 3AH–45H | `XX 00 00 01` |
| Left Ctrl | E0H | `E0 00 00 01` |
| Left Shift | E1H | `E1 00 00 01` |
| Left Alt | E2H | `E2 00 00 01` |
| Left GUI | E3H | `E3 00 00 01` |

### Multimedia Keys (mode 03H, selected)

| Key | Matrix entry `[B0,B1,B2,B3]` |
|-----|------------------------------|
| Play/Pause | `CD 00 00 03` |
| Mute | `E2 00 00 03` |
| Volume Up | `E9 00 00 03` |
| Volume Down | `EA 00 00 03` |
| Next Track | `B5 00 00 03` |
| Prev Track | `B6 00 00 03` |
