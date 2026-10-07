# ROYUAN / RongYuan LED Encoding — the parts no vendor document states

Saved from external reverse-engineering work, because two vendor specs in this repo
([RY5088](./ry5088-magnetic-axis-protocol.md), [YC3123](./rongyuan-yc3123-gaming-protocol.md))
mark a `CS` byte in every packet and **neither defines how to compute it**, and neither mentions
that the speed field is inverted on the wire. Both facts are required to drive the [[T800]] and
both were confirmed against our own hardware — see § Verification.

**Sources**

- <https://dnim.dev/blog/royuan-keyboard-protocol> — "Reverse engineering the ROYUAN keyboard HID protocol"
- <https://github.com/dniminenn/sharkfin> — `sharkfin`, an open-source configurator for Attack Shark /
  Akko / Epomaker / ROYUAN keyboards; protocol notes at `docs/PROTOCOL.md`
- <https://github.com/echtzeit-solutions/monsgeek-akko-linux> — Linux driver for MonsGeek / Akko
  magnetic keyboards on the same RY5088 / YC3121 firmware

These describe the same firmware family as the T800, not the same product. Everything below was
re-confirmed on our hardware before being relied on.

## Checksum

```
CS = 0xFF - (sum & 0xFF)
```

One's complement of the sum — **not** the sum, and **not** the two's complement (which is off by
exactly one and is the mistake that made two earlier attempts fail silently).

Two variants, differing only in the covered range and where the byte lands:

| Variant | Covers | Stored at | Used by |
|---|---|---|---|
| Bit7 | bytes 0–6 | byte 7 | `SET_LEDONOFF` (`0x05`), and everything else |
| Bit8 | bytes 0–7 | byte 8 | `SET_LEDPARAM` (`0x07`), `SET_SLEDPARAM` (`0x08`) |

A packet with a wrong `CS` is **discarded silently**. The firmware does not ack, does not answer a
read, and does not report an error — so on the write side an accepted and a rejected command look
identical. That is the trap this device sets.

## Speed is inverted on the wire

The `Speed` field transmits **inverted** — a low byte is fast, a high byte is slow. Brightness is
*not* inverted; it goes out as a plain 0–4.

The two sources disagree on the range, and the disagreement is load-bearing:

| Source | Range |
|---|---|
| YC3123 LED Param Table | field is `0–4` |
| ROYUAN notes | wire carries `5..1` (so the vendor tool never emits `0`) |

We send the **vendor-proven `5..1`** and do not use wire `0`. The union would suggest `0` is one
step faster than anything the vendor app emits, and it was tried because full slider felt slow
against the other boards — but it is unverified, and an invalid speed that freezes the animation is
indistinguishable from a slider that does nothing.

**This cannot be settled by read-back.** The firmware stores any speed byte without clamping: 0, 6
and 7 all read back unchanged, so a value being retained says nothing about it being meaningful.
Two ways to settle it by observation:

- `dotnet run --project ./src/ProoveGaming.Device.Testing -- speed` — steps a red **Breathing**
  effect through wire 0–5, 8 s each, re-latching via Static between steps in case the firmware only
  re-reads animation parameters on an effect change.
- `dotnet run --project ./src/ProoveGaming.Device.Testing -- learn` — listens only, no writes.
  Changing the speed with the board's **own** lighting shortcut makes the firmware announce
  `05 05 <speed>`, giving the real range and direction straight from the device.

Note also that the LED Param Table marks `Speed` as `xxx` (not meaningful) for **Static**, so a
speed slider will correctly appear dead on that effect.

## Option byte

`Option = (variant << 4) | colourSlot`, confirming the YC3123 table exactly. The low nibble:

| Slot | Meaning |
|---|---|
| `0`–`6` | preset colour (red, orange, yellow, green, cyan, blue, magenta) — **R/G/B ignored** |
| `7` | fixed colour — the R/G/B bytes in the packet are used |
| `8` | rainbow / full colour — firmware cycles the spectrum |

Exceptions worth knowing before adding effects beyond the six the app exposes:

- Modes 22 / 23 invert the nibble (`0` = rainbow, `4` = fixed).
- Mode 13 puts its pattern slot in the variant nibble and forces R/G/B to `(0, 200, 200)`.

## Colour quirks

- Full white transmits as `FA FA FA`, not `FF FF FF`.
- A colour with every channel below `0x08` renders **unlit**.
- "Off" is an option/on-off command, not a black colour.

Note that a saturated colour such as `FF 00 00` is stored unchanged — the `FA` rule is about white
specifically, not a blanket per-channel cap. Confirmed by read-back on our hardware.

## Rate limiting

Per-key colour uploads go to **flash** as seven 56-byte pages, and pushing them too fast makes the
config endpoint stop answering until the device is re-enumerated; the source above recommends no
more than 2 uploads per 10 s with a 2 s settlement.

We do not write per-key colour, but the same concern touches `SET_LEDPARAM`: our hardware emits the
vendor-code **Flash Write** bracket (`05 0F 01` … `05 0F 00`) on every accepted LED command, i.e.
each colour change persists to flash. The App's existing slider debounce is what keeps this to one
write per settled interaction — do not bypass it.

## Verification

Confirmed on a T800 (VID `0x3151` / PID `0x502D`) with
`ProoveGaming.Device.Testing/T800LedProbe.cs`:

```
GET_LEDPARAM per checksum candidate
  OnesComplement  sent 87 00 00 00 00 00 00 78  ->  87 04 02 04 08 FF FF FF   <== real reply
  Sum             sent 87 00 00 00 00 00 00 87  ->  87 00 00 00 00 00 00 87   (our own echo)
  NegatedSum      sent 87 00 00 00 00 00 00 79  ->  87 00 00 00 00 00 00 79   (our own echo)
  Xor / Zero                                        (our own echo)
```

Only the one's-complement frame produced a real answer — `effect=0x04 (Wave), speed=2, bri=4,
option=0x08 (rainbow)`, the board's actual state at the time. Under every other rule `GetFeature`
returns the driver's cached copy of the request, which is why a read-back cannot be used as an
acceptance test unless the checksum is already right.

Writes then round-tripped exactly:

```
send:      07 01 02 02 07 76 20 EE 68
read-back: 87 01 02 02 07 76 20 EE      effect=Static, speed=2, bri=2, slot 7, rgb=7620EE
```
