# Magnetic-axis travel (actuation) protocol — Cyrex / Aress

Extracted 2026-08-05 from **Keyborder-v3** (`C:\Program Files\PixelPrime\Keyborder-v3.exe`), the
client-supplied vendor driver app, whose Actuation page works against a real PG Cyrex. The app is
Electron; the protocol lives in `resources/app.asar` → `out/renderer/assets/index-C1SFvu4g.js`
(readable, non-mangled build). Extraction: `npx @electron/asar extract`, then reading the bundle.

Everything below is **as implemented by the vendor app** — not yet verified against our units.
Where the app agrees with what we already measured on hardware (`0x17`/`0x18` key matrix, `0x20`
factory reset, report ID `0x06`, additive checksum), it is called out; those agreements are why
this table is credible for our firmware where the V3.1 workbook was not.

## Device coverage

The app's device config list (`deviceConfigList`) declares, among others:

| USB PID | Vendor model | Our name | Config |
|---|---|---|---|
| `12914` | MK25005 | **Cyrex** | `keyboardType: "magneticAxis"`, protocol `GamingKeyboard2`, `supportAdvancedKey: true` |
| `12910` | MK620 | **Aress** | same |

All on VID `10473` (`0x28E9`), usage page `0xFF87` (65415), usage `0x20` (32). Firmware-internal
product id `9220` is aliased to `12914` ("PG Cyrex / MK25005"; MK620 may share `9220` — USB HID
PID wins when directly connected). A shared 2.4G dongle ("Dango", VID `1389`) carries 32-byte
reports (`reportId + 31`) instead of 64 — chunk math must use 31, not 63, on that transport.

## Transport and framing

Identical to the framing already implemented in `SmartInfiniteProtocol`:

- Report ID **`0x06`**, 64-byte report: `reportId + 63` data bytes (`DEFAULT_DATA_LENGTH = 63`).
- Packet: `[cmd, addr_lo, addr_hi, len, sum_lo, sum_hi, status, payload…]` — 7-byte header,
  **56** payload bytes per chunk (`63 − 7`).
- Checksum = plain 16-bit additive sum over the packet with the sum/status bytes zeroed
  (the app's `calcByteLH`), little-endian at bytes 4–5. Matches our measured
  [[Smart Infinite checksum]].
- Response: same header; byte 6 (`status`) = `0x55` success, `0x0F` fail. Response payload from
  byte 7. The app matches responses on `cmd + addr` echo with a 5 s timeout.
- Multi-chunk reads/writes address by `addr16` = absolute byte offset into the area; `len` = bytes
  in this chunk. Reads send no payload (only `len`); writes carry the slice.

## Command table (`GamingKeyboard2`)

The vendor app's full command map for this family. Bold rows are the magnetic-axis commands that
were missing from every previous document. Agreements with our hardware measurements are marked ✔.

| Cmd | Meaning | Note |
|---|---|---|
| `0x10` / `0x11` | begin / end communication | ✔ known |
| `0x12` / `0x13` | read / write device info | ✔ known |
| `0x14` / `0x15` | read / write general (function-area) info | ✔ known |
| `0x16` | read default key matrix | ✔ known |
| `0x17` / `0x18` | read / write key matrix | ✔ measured on our Cyrex |
| `0x19` / `0x1A` | read / write custom lighting | |
| `0x1B` / `0x1C` | **read / write macro data** | ⚠ we ship `0x2C`/`0x2D` (confirmed working); discrepancy unresolved |
| `0x1D` / `0x1E` | light sync on / off | |
| `0x1F` | read battery | ⚠ V3.1 sheet said `0x30` |
| `0x20` | reset | ✔ measured as **factory reset** — never send |
| `0x21` | read light matrix (key→LED map) | |
| `0x22` | read SN code | |
| `0x23` | read default function-area info | |
| `0x24` | read default custom light effect | |
| `0x25`–`0x2A` | Mac / Mac-FN key matrices | |
| `0x30` / `0x31` | read / set active profile (1 byte) | |
| `0x32`–`0x34` | FN key matrix (default / read / write) | |
| `0x35` | read FN not-modifiable list (256 B) | |
| **`0x36`** | **magnetic axis: enter/exit key simulation test** | travel test mode |
| **`0x37`** | **magnetic axis: enter/exit key calibration mode** | |
| **`0x38`** | **magnetic axis: read key travel area** | |
| **`0x39`** | **magnetic axis: write key travel area** | |
| **`0x3A`** | **magnetic axis: read advanced-key storage area** | DKS/MT/TGL etc. |
| **`0x3B`** | **magnetic axis: write advanced-key storage area** | |
| `0x3C` | read keyboard voltage | |
| `0x40`–`0x42` | screen-control packets | LCD models |
| `0x55` | dongle↔keyboard connection status | |
| `0x80` | sync LED colour calibration | |
| `0xB0`–`0xB7` | firmware version / bootloader / DFU | |

## Device Info (`0x12`) — magnetic-axis fields

The app reads 64 bytes of device info. Fields relevant to travel (offsets into that block;
16-bit values are little-endian `[lo, hi]`):

| Offset | Field | Meaning |
|---|---|---|
| 4 | `key_rect_size / 3` | key-matrix records (× 3 = bytes) — known |
| 5 | `hong_space_size / 256` | macro space — known |
| 6 | `keySize` | physical key count |
| 7 | travel slot count | **travel area slots**; total area bytes = `[7] × keyTravelStep` |
| 8 | advanced-key area size `/ 64` | `advancedKeyStorageAreasSize = [8] × 64` bytes |
| 9 | `deviceType` | device class (screen/light variants) |
| 10–11 | firmware version | `hex([11]).hex([10])` |
| 16 | `keyStepMarker` | **step unit**: bit 7 selects factor (0 → 0.01 mm, 1 → 0.001 mm); low 7 bits = multiplier. Step in mm = multiplier × factor |
| 17–18 | `keyMaxMarker` | max key travel, in steps |
| 19–20 / 21–22 | `setPrMax` / `setPrMin` | allowed press/release range, in steps |
| 23–24 / 25–26 | `setFastPrMax` / `setFastPrMin` | allowed rapid-trigger range, in steps |
| 27–28 | `setBaseDeadZoneMax` | max bottom dead zone, in steps |
| 29–30 / 31–32 | `defaultPressMarker` / `defaultReleaseMarker` | factory actuation / release |
| 33 | `defaultFastTiggerEn` | factory rapid-trigger on/off |
| 37–38 / 39–40 | `defaultFastPressMarker` / `defaultFastReleaseMarker` | factory RT press / release |
| 41–42 | `defaultBaseDeadZone` | factory bottom dead zone |
| 43 | `defaultAxis` | factory switch (axis) type |
| 44 | `keyTravelStep` | **bytes per key in the travel area**; `0` or `10` → 10, else as reported (12 on boards with a top dead zone) |
| 45–46 | `defaultTopDeadZone` | factory top dead zone |

UI values in mm are `steps × keyStepMarker`; conversely `steps = mm / keyStepMarker`
(e.g. step 0.01 mm → 2.0 mm = 200).

## Key travel area (`0x38` read / `0x39` write)

One flat byte array, `slotCount × keyTravelStep` bytes, chunk-addressed by absolute byte offset in
56-byte pieces (both directions). **The app always writes the whole area**, then reads it back.

Per-key record (`keyTravelStep` bytes, all fields uint16 LE, values in steps):

| Offset | Field | UI name | Encoding |
|---|---|---|---|
| 0 | `DOWN` | Initial Actuation Point | plain value |
| 2 | `UP` | Release | plain value |
| 4 | `QUICKLY_DOWN` | Quick Press (RT) | `0b01` in bits 15–14, value in bits 13–0 (`value \| 0x4000`) |
| 6 | `QUICKLY_UP` | Quick Release (RT) | plain value |
| 8 | `BOTTOM_DEAD_ZONE` | bottom dead zone | bits 15–12 = **axis (switch) type nibble**, bits 11–0 = value |
| 10 | `TOP_DEAD_ZONE` | top dead zone | plain value; only present when `keyTravelStep = 12` |

Key order in the area is the same slot order as the key matrix (slot × `keyTravelStep`).

Rapid Trigger appears to be governed by the `QUICKLY_DOWN` flag bits (`0b01…`) — the app sets that
prefix whenever it writes RT values; the separate function-area `PERFORMANCE_MODE` byte (offset 63
of the general-info area) is used on 8 kHz boards to pick the RT precision step. Vendor presets
(Balanced / Esports / Extreme) are pure client-side value sets (e.g. Balanced: press 1.0 mm,
RT 0.1 mm, dead zone 0.1 mm on WASD).

## Calibration and travel test

- `0x37` enters/exits **calibration mode** (UI: press each key to full bottom, firmware re-learns
  the magnetic range).
- `0x36` enters/exits **key simulation (travel test)** mode, where the board streams per-key travel
  samples the app plots live. Payload formats for both were not extracted yet — capture them from
  the app when implementing (`useAxisCalibration` in the bundle).

## Advanced keys (`0x3A` / `0x3B`)

Storage area of `[8] × 64` bytes for DKS / Mod-Tap / Toggle / SOCD / Rapid Snap / END behaviours
(`supportAdvancedKey: true` on both boards). Record format and write sequence extracted 2026-09-09:
[`advanced-keys-rapid-snap.md`](advanced-keys-rapid-snap.md).

## Open questions before implementation

1. **Macro command discrepancy** — ~~the app uses `0x1B`/`0x1C` for macros where we confirmed
   `0x2D` writes work~~. **Resolved 2026-08-06** in favour of `0x1B`/`0x1C`: the deeper
   extraction (the macro-binding page chunk) shows the vendor app reads and writes the macro
   region exclusively with those commands, and our `0x2D` "confirmation" had indeed tested only
   the ack. Full format and binding records:
   [`macro-format-gamingkeyboard2.md`](macro-format-gamingkeyboard2.md).
2. Verify `0x38` read on a real Cyrex first (harmless read): device info offsets 7/16/44 must
   yield a coherent area size and step before any write.
3. `QUICKLY_DOWN` flag bits: confirm `0b00` (flag absent) disables RT per-key, and how the app's
   global RT toggle maps onto them.
4. Whether the travel area is also gated by the single-session rule ([[Smart Infinite device
   gate]]) — assume yes and serialise through the same gate.
