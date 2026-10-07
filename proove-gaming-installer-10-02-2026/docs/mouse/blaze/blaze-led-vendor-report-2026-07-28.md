# BLAZE / X43 — LED does not light: reset data + result (for the vendor)

A concrete report for the factory / firmware team. It shows **exactly what we send** to reset the
device to its default profile (the `0x02` and `0x03` tables, as instructed in the
[27.07 Q&A](blaze-vendor-qa-2026-07-27.md)) and **what happens**: buttons, DPI, polling and the
whole profile work — but the **LED never lights**, not from software and not from the physical
lighting-effect switch.

## Summary of the result

| Function | Result |
|---|---|
| Left / Right / Middle / Forward / Back buttons | ✅ Work |
| DPI-stage cycle (DPI button) | ✅ Works |
| Report-rate cycle (polling button) | ✅ Works |
| 7 DPI stages written to flash | ✅ Applied |
| **LED (any effect)** | ❌ **Stays dark** |

So the profile write itself is correct and **commits to flash** — everything except the LED
responds. The LED does not light for **any** effect value, brightness, colour slot, or speed, and
**it does not light when the underside lighting-effect switch is pressed either**.

## Device

- BLAZE / X43, USB **VID `0x04D9` / PID `0xA245`** (cable connection).
- Framing per the Easen param protocol: 8-byte header via HID SET_REPORT (feature), then 64-byte
  data block(s) on interrupt-OUT endpoint `0x03`. Checksum byte is sent but, per the Easen spec,
  the firmware does not validate it.

## Exact data we send to reset to default

This is the full default profile write. All values reproduce the OEM capture. Bytes are hex.

### 1) `0x03` — SetParameter_1 (three 64-byte blocks, written together)

Header (`[cmd, profile, index, 0, 0, status=0x07 (all blocks), times=3, cs]`):
```
03 00 00 00 00 07 03 00
```

**Block 0 — DPI table & sensor config** (debounce/scale = 0, `dpiStage_Max = 7`, stages
200/800/1600/3200/6400/9600/12000):
```
00 00 00 00 07 00 00 00 00 C8 00 20 03 40 06 80
0C 00 19 80 25 E0 2E 00 00 C8 00 20 03 40 06 80
0C 00 19 80 25 E0 2E 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

**Block 1 — colours** (DPI-indicator RGB all 0; LED slots = red, blue, green, yellow, cyan,
magenta, orange, white):
```
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 FF 00 00 00 00 FF 00 FF
00 FF FF 00 00 FF FF FF 00 FF FF 80 00 FF FF FF
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

**Block 2 — key matrix** (16 entries × `[code, b1, b2, mode]`):
```
F0 00 00 02  F1 00 00 02  F2 00 00 02  F4 00 00 02
F3 00 00 02  03 00 00 0B  03 00 00 06  03 00 00 0A
00 00 00 02  00 00 00 02  00 00 00 02  00 00 00 02
00 00 00 02  00 00 00 02  00 01 00 04  00 02 00 04
```

### 2) `0x02` — SetParameter_0 (one 64-byte block)

Header (`[cmd, profile, index, 0, 0, status=0, times=1, cs]`):
```
02 00 00 00 00 00 01 00
```

Data (`[0]` reportrate=1000 Hz, `[1]` dpi_stage=0, `[2]` ledEffect, `[3]` brightness=255,
`[4]` speed=3, `[5]` colour slot=0, `[6]` option=0):
```
01 00 02 FF 03 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

After the writes we also send `0x01` (select/activate profile 0) — no change to the LED.

## What we varied for the LED — none of it lit the LED

Byte `[2]` of `0x02` (ledEffect) was tried across every value the spec lists, with brightness `0xFF`:

| ledEffect | Name (per spec) | Result |
|---|---|---|
| `0x00` | Off | LED off (as expected) |
| `0x02` | Breathing (呼吸) | **No light** |
| `0x03` | Neon (霓虹) | **No light** |
| `0x06` | Raindrop (雨滴) | **No light** |

Also varied with no effect on the LED: brightness `0x00`–`0xFF`, speed `0x01`–`0x07`, colour slot
`0`–`7` (incl. `7` = loop), option `0`/`1`, and writing the colour table (block 1) before the
effect. The LED never lit in any combination.

## Evidence that the write reaches and commits to flash

- Writing block 2 with the **DPI-button slot (KEY5) left empty** makes the **DPI button stop
  working**; writing it back with `03 00 00 0B` restores it. So block-2 writes are received and
  persisted by the firmware.
- Buttons, DPI cycling and polling-rate cycling all behave exactly as the key matrix above
  specifies. Only the LED is unresponsive.

## Physical lighting-effect switch also does not light the LED

Per the 27.07 answer, the underside buttons are a report-rate switch and a **lighting-effect
switch**. With the key-matrix LED slot (KEY7) left at its native binding, pressing the underside
lighting-effect switch **does not light the LED either** — a physical control that does not go
through our software at all.

## Questions for the vendor

1. With the default profile written and any effect selected, the LED stays dark — **what enables
   the LED engine?** Is there an LED-enable byte/step (e.g. in param_0, block 1 reserved bytes, or a
   separate command) that our write is missing?
2. The **physical lighting-effect switch** on the underside also does not light the LED. Does that
   indicate a **hardware/firmware fault** on this unit, or is there a state the switch depends on?
3. Is there a factory/production step (or an LED self-test command) that must run before the LED
   works? If so, can you share the exact byte sequence?
4. Can you confirm the **default param_0** the firmware expects (report rate, dpi_stage, ledEffect,
   brightness, speed, colour, option) for a lit LED, as an exact byte example?

## References

- Wire protocol (ground-truth capture): [protocol.md](protocol.md)
- LED research history: [led.md](led.md)
- Vendor Q&A this follows up on: [blaze-vendor-qa-2026-07-27.md](blaze-vendor-qa-2026-07-27.md)
