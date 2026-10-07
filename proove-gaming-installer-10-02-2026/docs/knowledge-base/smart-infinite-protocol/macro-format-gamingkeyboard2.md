# Macro format — Smart Infinite (GamingKeyboard2 firmware line)

Extracted 2026-08-06 from **Keyborder-v3** (`C:\Program Files\PixelPrime\Keyborder-v3.exe`), the
vendor driver whose `GamingKeyboard2` command table matches our Cyrex/Aress firmware's measured
numbering. Source chunk: `resources/app.asar` → `out/renderer/assets/index-CDyQl7nP.js` (the
macro-binding page), functions `formatHongRect`, `getHongRect`, `setHongRect`,
`applyHongMacroMapping`, and the `KeyType` enum in `index-C1SFvu4g.js`.

Implemented by `SmartInfiniteMacroEncoder` / `SmartInfiniteMacroService`; binding types in
`SmartInfiniteProtocol.TypeMacroStopMode` / `TypeMacroPlayCount`.

## Commands

| Cmd | Meaning | Notes |
|---|---|---|
| `0x1B` | read macro region (`gethongMessage`) | standard envelope, chunk-addressed by byte offset |
| `0x1C` | write macro region (`sethongMessage`) | the app sends the **whole formatted buffer** |

`0x2C`/`0x2D` (the V3.1 workbook's macro commands, previously shipped by us) appear **nowhere**
in the vendor app. Every "0x2D confirmed working" observation was ack-only; the all-zero read-back
recorded in `docs/plans/add-cyrex-aress-parity/macro-investigation-result.md` is what a
wrong-command write looks like on this firmware.

## Region buffer (`formatHongRect(settings, size)`)

`size` = the macro space Device Info reports (byte 5 × 256; **1024 on Cyrex**). All values
little-endian:

| Offset | Field |
|---|---|
| 0–1 | `0xAA 0x55` magic |
| 2–3 | **whole region length** (`hong_Rect.length` — not the used length the V3.1 sheet implies) |
| 4–5 | slot count = **20**, empty slots included (`hongCount` in the app) |
| 6–7 | reserved 0 |
| 8 + slot×2 | per-slot block offset; `0x0000` = empty slot. 20 entries → first block at **48** |

Per-macro block, packed sequentially from 48:

| Offset in block | Field |
|---|---|
| +0–1 | action count LE |
| +2–3 | reserved 0 |
| +4… | actions, 4 bytes each: `{delay_L, delay_H, code, attr}` |

**`attr`**: bit 7 = press (1) / release (0); low bits select the action kind:

- `0x01` — normal key; `code` is the HID usage.
- `0x02` — modifier ("hot key"); `code` is the **modifier bitmask** (`1 << (usage − 0xE0)`:
  LCtrl `0x01` … RWin `0x80`). The app derives this from its `codeValues` table — a modifier's
  usage column is `0x00` there, so its bitmask column is sent instead.

The vendor UI reads the region back with `0x1B` (it fetches 256 bytes for its list; we read the
occupied length) and derives each macro's slot from its offset-table position (`(entry − 8) / 2`).

## Key-binding records (`applyHongMacroMapping`)

Written into the ordinary key matrix (`0x18`), pointing into the library by slot:

| Record | Meaning |
|---|---|
| `{0x50, slot, stopMode}` | `MACRO_NO_COUNT` — stop mode `0` play-through, `1` stop on release, `2` press-and-release starts / press stops, `3` press toggles |
| `{0x51, slot, count}` | `MACRO_WITH_COUNT` — plays `count` (1..255) times |
| `{0xC0/0xC1, …}` | legacy macro types the app recognises on read but never writes |

## The KeyType enum (this firmware line)

From `index-C1SFvu4g.js` — the authority that settles the thrice-flipped macro-type question:

```
0x10 KB_BASIC      0x11 KB_ACTUATION   0x20 KB_MULTIMEDIA  0x30 KB_SYSTEM
0x40 MS_BASIC      0x41 MS_SWING       0x42 MS_SCROLL      0x43 MS_RAPID_FIRE
0x50 MACRO_NO_COUNT                    0x51 MACRO_WITH_COUNT
0x60 FUNCTION                          0x61 TEXT_OUTPUT    0x70 FN_COMBO
0x80 ADV_DKS  0x81 ADV_MT  0x82 ADV_TGL  0x83 ADV_SOCD  0x84 ADV_RS  0x85 ADV_END
```

Note **`0x60` is FUNCTION** here — a `{0x60, slot, mode}` binding is stored, verifies on
read-back, and plays nothing. The V3.1 workbook's enum (0x60 = macro, 0x50 = FUNC) belongs to a
different firmware line; the Raster/T-Racer family genuinely uses `0x60`/`0x61`
(hardware-verified) — the families do **not** share the byte.

## Family coverage

Keyborder's device list registers **Rampage (PID `12888`) on the same `GamingKeyboard2`
protocol** (`keyboardType: "mechanical"`, no advanced keys) with the same shared macro UI — so the
macro path above is the vendor's route for the whole family, not just the magnetic 65% boards.
Rampage's Device Info reports a 2048-byte macro space, which sizes its region buffer the same way
Cyrex's 1024 does.

Everything above is as-implemented-by-the-vendor-app; the `0x1C` write itself is pending our own
hardware confirmation — which the macro service's authoritative read-back verify performs on
every upload.
