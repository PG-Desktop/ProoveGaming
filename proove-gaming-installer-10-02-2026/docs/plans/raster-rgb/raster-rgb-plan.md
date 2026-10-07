# RGB Support Plan — Raster Keyboard

## Status

**Working** — all six effects render on hardware, color/brightness/speed control from the app,
Off toggles the backlight cleanly. One known quirk: Neon looks slightly off vs the app preview
(likely a wrong choice among the 20 vendor mode indices — cosmetic, easy to iterate).

## Context

Raster is a wireless gaming keyboard (VID 14000, PID 12385) from a different vendor family
than Rampage / Aress / Cyrex (VID 10473). The vendor's own dist bundle ships only firmware-
upgrade code, no RGB effect setter, so the protocol was reverse-engineered from
`chunks/app/page-*.js` (`setFuncInfo` / `getFuncInfo`) and per-keyboard JSON configs.

## Protocol summary

- **HID interface**: `mi_01` raw HID, `outLen=65 / inLen=65`, report ID `0x00`. The 64/64
  interface on this device is the FW-update channel (report ID `0x3F`, `AA`-magic
  proprietary protocol) and rejects control writes with `ERROR_INVALID_FUNCTION`.

- **Wire packet layout** (65 bytes total on wired USB):
  ```
  byte[0]     = 0x00     (HID report ID)
  byte[1]     = 0xAA     (Raster magic)
  byte[2]     = command  (0x10 START_COMM, 0x14 GET_FUNCINFO, 0x15 SET_FUNCINFO)
  byte[3..4]  = address / offset (little-endian, from qW = e => [255&e, e>>8])
  byte[5]     = length (payload len for SET / requested response size for GET)
  byte[6..8]  = 0, 0, 0 (reserved on write; byte[8]=0x55 = success on GET/SET response)
  byte[9..]   = payload data
  ```
  No CRC. **Every command is a request/response pair — the ack response must be consumed
  before sending the next command, otherwise the read stream is out of sync.**

- **Feature area** (64 bytes, from vendor `setFuncInfo`):
  ```
  [0]        profile
  [1]        lightSwitch — INVERTED: 0 = ON, 1 = OFF (verified against vendor JS `e?0:1`)
  [2]        lightMode
  [3]        lightBrightness (0-100 scale, matches app slider linearly)
  [4]        6 - lightSpeed          (firmware inverts speed)
  [5]        lightMixColor (0 = mono R/G/B, 1 = multicolour flow)
  [6]        lightColorIndex — preserved from read (0-6 preset colours, 7 = White, 8 = Multi)
  [7..9]     R, G, B
  [10]       lightCustomIndex — preserved from read (only meaningful for user modes >= 253)
  [11..19]   logoLight (untouched)
  [20..28]   sideLight (untouched)
  [29..37]   matrixScreenLight (untouched)
  [38..45]   misc user prefs — sixKeys, winLock, wasd, snapTap, ... (untouched)
  [46..48]   sleepTime (3-byte LE seconds) — untouched
  [49..51]   deepSleepTime — untouched
  [52..58]   LCD screen / snapTap — untouched
  ```

- **Effect mode indices (1-indexed)** — pulled from vendor per-keyboard JSON configs where
  each entry has `{"name":"呼吸","lang":"223","value":3,...}`:

  | lang | Effect | value |
  |---|---|---|
  | 221 | 流光溢彩 Streaming | 1 |
  | 222 | 五彩缤纷 Colorful | 2 |
  | 223 | 呼吸 Breathing | 3 |
  | 224 | 常亮 Steady | 4 |
  | 253-257 | Custom user modes | 253-257 |

## Architectural decision

**Read-modify-write via `CMD_GET_FUNCINFO (0x14)`.** Writing a fresh zero-filled 64-byte
block clobbers `matrixScreenLightSwitch`, sleep timers, and user preferences — the visible
effect was the keyboard going dark. The vendor tool always reads current state first, mutates
only the UI-touched fields, and writes back. This service does the same: main-light bytes
1-5 and 7-9 plus LOGO (11-19) and side-light (20-28) sub-blocks are overwritten; every other
byte comes from the read.

**Pair every write with a read.** Because the firmware acks every command, the read is
required to keep the stream in sync — `SendAndAwaitAck` does write+read as one atomic unit
and validates the command echo (`buf[2] == cmd`) and success flag (`buf[8] == 0x55`).

**Mirror main effect into LOGO/side sub-blocks.** Raster's volume-button LED is driven by
the LOGO channel (bytes 11-19); without mirroring it kept whatever pattern the firmware's
Fn+PGUP/PGDN shortcuts last set — typically a rainbow — ignoring the app. The side channel
(bytes 20-28) is mirrored too so any case-edge strip on hardware variants stays in sync.
Both are silently ignored by the firmware on units that don't have those LEDs.

## Implementation flow (`RasterKeyboardRgbService.SetLedColorAsync`)

1. Open mi_01 stream.
2. `SendAndAwaitAck(CmdStartComm)`.
3. `TryReadFeatureArea`: two `GET_FUNCINFO` chunks (offsets 0/56, lens 56/8), each validated
   and copied into a 64-byte buffer.
4. `LogBlock("read", …)` — main-light fields plus the raw 64-byte dump, at **Debug** level.
   A matching `verify` read-back runs after the write, also Debug-gated, so the log can show
   what the firmware retained rather than only what we asked for.
5. `MutateLightFields`: overwrite the main-light sub-block (bytes 1-5, 7-9), then mirror the
   same mode / speed / mix / R / G / B into LOGO (11-19) and side (20-28). `colorIndex` and
   `customIndex` on the main sub-block are preserved from the read; LOGO/side colorIndex is
   zeroed since we always supply R/G/B.
6. Two `SendAndAwaitAck(CmdSetFuncInfo)` chunk writes (offsets 0/56).
7. Log `[LED] Raster read-modify-write complete`.

The 64/64 FW-update interface is filtered out by the factory predicate (`outLen==65`).

## App-effect → protocol mapping

| App effect | lightMode | lightMixColor | lightSwitch | Notes |
|---|---|---|---|---|
| 0x04 Static | 4 (Steady) | 0 | 0 (ON) | Solid R/G/B — confirmed |
| 0x01 Breathing | 3 | 0 | 0 | R/G/B breathing — confirmed |
| 0x00 RainbowBreathing | 3 | 1 | 0 | Multi-colour breathing — confirmed |
| 0x02 Neon | 2 (Colorful) | 1 | 0 | Multi-colour flow — **slightly off** vs app preview |
| 0x03 ColorCycle | 1 (Streaming) | 1 | 0 | Streaming lights — confirmed |
| 0x05 Wave | 1 (Streaming) | 1 | 0 | Streaming lights — confirmed |
| 0xED Off | 4 | 0 | 1 (OFF) | Backlight off via lightSwitch=1 |

## Files

| File | State |
|---|---|
| `ProoveGaming.DeviceHost/appsettings.json` | `SupportsRgb: true` on the Raster entry |
| `ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs` | Read-modify-write flow |
| `ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | Raster dispatched by config `Name` |
| Rampage / Aress / Cyrex services and configs | Untouched |

## Sources

Files supplied by the user that were consulted to reverse-engineer the protocol:

- **`dist (5).zip`** — vendor's Next.js web app bundle. Primary source of protocol
  discovery. Not committed; unpacked to `/tmp/raster_docs/dist5/` during investigation.
- **`Raster keyboard dist (2).zip`** — Raster-specific dist bundle (variant of the above).
  Confirmed the vendor tool only ships firmware-upgrade code — no RGB effect setter
  reachable from its UI.
- **`FW (1) (5).zip`** — contains `PG Raster-CSA6D6-V0100-20260330.exe` (used to factory-
  reset the keyboard during testing) and `PG DragonFang-CSF0CB-V0102-20260328.exe`
  (confirming Raster shares the vendor family with DragonFang / T-Racer).
- **`Shortcut Card Proove Gaming Raster 20.05.2026.pdf`** — Fn+key layout reference,
  confirmed which shortcut owns the volume-button LED and the brightness step count.
- **`Software macro definition table(1) (1).xlsx`** — not directly used for RGB
  (macros only).
- **`智慧无限软件驱动协议_V1_4_20251114机械键盘外发 (4).xlsx`** — Chinese SmartInfinite v1.4
  protocol spec. Cross-check for command codes and feature-area byte semantics.

Specific chunks inside `dist (5).zip` that carried the load-bearing information:

- **`_next/static/chunks/app/page-ba6d4e19076b59c9.js`** — contains `setFuncInfo` /
  `getFuncInfo`, the exact 64-byte feature-area layout, `CMD_SET_FUNCINFO=0x15` /
  `CMD_GET_FUNCINFO=0x14` / `CMD_START_COMM=0x10`, the `REPORT_ID=170` (`0xAA`) magic
  constant, `webhid_write_command` / `webhid_read_command`, and the `[56,24][deviceMode]`
  chunk-size selection.
- **`_next/static/chunks/6152.7148db8266477b7c.js`** — WebHID `sendReport` wrapper,
  CRC-16-Modbus poly `0xA001` reference, `qW = e => [255&e, e>>8]` little-endian offset
  encoder.
- **`_next/static/chunks/{1448,1646,1763,2897,3301,3715,4240,4593,5043,5306,5881,7256,7662,9777}.js`** —
  per-keyboard JSON configs. Each file has a `lighting.backlight` array whose `{"lang":<key>,
  "value":<n>}` entries gave the 1-indexed mode-value table (221 Streaming = 1, 222 Colorful
  = 2, 223 Breathing = 3, 224 Steady = 4, ..., 253+ = custom).
- **`_next/static/chunks/5342.c392286a86e04118.js`, `8167.4d7e4468aa87a622.js`** — Chinese
  localization tables mapping `lang` keys 221-240 to effect names (五彩缤纷, 呼吸, 常亮, …).

Runtime observations that unblocked specific bugs (all captured in the daemon log):

- **`[RawDescriptor] VID=0x36B0 PID=0x3061`** dumps — identified the two vendor HID
  interfaces (`0xFF00` FW-update at report ID `0x3F`, `0xFF60` raw-HID at report ID `0x00`),
  which drove the `outLen==65 && inLen==65` factory predicate.
- **Battery scan responses** on mi_01 (`00-30-00-00-00-00-00-00-0F-...`) — first proof the
  raw HID interface accepts commands and returns a coherent response.
- **`[LED] Raster GET_FUNCINFO invalid response header at offset 0: AA 10 status=55`** —
  told us START_COMM's ack was still in the read buffer, driving the fix to pair every
  write with a read (`SendAndAwaitAck`).
- **`[LED] Raster read block[0..10] = 00 00 01 55 02 01 00 FF 00 00 00`** on a fresh
  firmware flash while the keyboard was visibly animating — proved `lightSwitch = 0` means
  ON (inverted vs every other keyboard in this codebase).

## Known / open items

- **LOGO/side animation drifts vs main on slow effects.** Even with identical `speed` bytes,
  the firmware runs each channel's animation on its own independent phase timer. Visible on
  Breathing as a small offset between main-backlight and volume-LED fade cycles. No protocol
  knob exists to lock them; documented as a firmware-level constraint. Alternative if this
  ever becomes user-facing painful: pin LOGO/side to Steady (mode 4) so no fade, no drift.
- **Neon (`0x02`) may not exactly match the app preview.** Currently maps to mode 2
  (五彩缤纷 Colorful), which is close but reportedly a hair off. The full vendor list has
  20 effects (values 1-20) — Neon might be a better fit at mode 7 (旋转乾坤 Rotation),
  mode 8 (流星赶月 Meteor), or mode 12 (百花齐放 Flowers). Easy to iterate: change one
  line in `MapAppEffectToMode` and observe.
- **Wireless dongle mode (2.4G) not implemented.** The vendor JS uses 33-byte reports with
  24-byte chunks for `deviceMode=1`. Adding it means the factory needs to select
  the dongle-side HID interface too, and the service needs a mode flag.
- **Diagnostic log kept.** `[LED] Raster read block[0..10] = ...` is INFO-level. If it
  starts polluting production logs, dropping it to Debug is fine.
- **`lightColorIndex` and `lightCustomIndex` preserved from read.** The vendor JS treats
  these as UI-only fields — Static uses R/G/B directly. Not touching them keeps whatever
  the firmware initialised on power-up (0 and 0 after fresh flash).
