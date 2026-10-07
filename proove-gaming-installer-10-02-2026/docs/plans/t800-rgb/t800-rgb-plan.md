# RGB Support — T800 Keyboard (second attempt)

## Status

**Done and confirmed on hardware.** The lighting command is fully decoded, and a T800
(VID `0x3151` / PID `0x502D`) round-trips writes byte-for-byte. See § Verification.

Supersedes the first attempt (commit `620b852`, "T800 rgb (not working)", branch
`origin/features/PG-326-t800-rgb`), which was written before the effect encoding was known and
shipped with the effect codes, the parameter scales and the `Option` byte all marked
`TODO(t800-r1)`.

## Goal

Drive the T800's backlight (VID `12625` / `0x3151`, PID `20525` / `0x502D`) from the App's
existing RGB tab, on the same effect / brightness / speed / colour surface every other keyboard
already offers. No gRPC contract changes and no App changes — the RGB tab is model-agnostic and
lights up as soon as `SupportsRgb` is set and the factory returns a service.

## What was actually wrong

The device was failing at two independent layers, and fixing either alone would still have looked
broken.

### 1. The RGB tab was never shown

`T800` was the only keyboard in `AllowedDevices` with no `SupportsRgb` field. It gates
`DeviceDetailsViewModel.IsRgbSupported`, so the whole tab was hidden regardless of what the
DeviceHost could do. The first attempt did set this; the change was never merged to `main`.

The App-side artwork was **not** a factor and needed no change: `RgbKeyboardArtCatalog` already
maps `T800` to the split `PG_T801_base.svg` / `PG_T801_keys.svg` pair with a `Glow/T800.path`
key-well rectangle, and compositing those layers renders the backlight correctly.

### 2. The lighting packet was wrong in four places

`docs/ry-yc3123-机械轴__2025_07_18.doc` — the Rongyuan YC3123 "Gaming SPEC", already committed to
this repo — turned out to document the same feature-report command set as the RY5088 magnetic-axis
spec, **including the LED Param Table that the RY5088 doc omits**. Translated to
`docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md`. Against it:

| Field | First attempt | Actual (spec) |
|---|---|---|
| `Effect` | sequential `1..6` invented to match the app's effect list | table codes: Off `0x00`, Static `0x01`, Breathe `0x02`, Neon `0x03`, Wave `0x04`, … `0x19` |
| `Bri` | `0..100`, copied from Raster | **`0..4`** |
| `Speed` | `0..6` | **`0..4`** |
| `Option` | `0` = monochrome / `1` = multicolour, guessed from Rampage's `lightMixColor` | `(Type << 4) \| Colour`; **low nibble selects the colour source** |
| `CS` | sum-mod-256 | **`0xFF - (sum & 0xFF)`** — one's complement; a plain sum is off by one and silently discarded |

The `Option` byte is the one that made the device look completely dead to the picker. Its low
nibble is a colour-source selector where `0..6` are hard-coded preset colours and the firmware
**ignores the packet's R/G/B bytes** for them. The first attempt sent `0` for Static and
Breathing — i.e. "preset red" — so the colour picker could not affect the board by construction.
Only slot **`7` (AP colour)** makes the firmware read R/G/B; slot `8` is firmware-cycled full
colour for the multicolour effects.

The framing was right in the first attempt and is unchanged: feature report on `mi_02`, report ID
`0x00`, 64 protocol bytes, `CS` at protocol byte 8 for the seven-parameter `0x07` and byte 7 for
the six-parameter `0x05`.

## Effect mapping

| App effect | Firmware effect | `Option` | Colour follows picker |
|---|---|---|---|
| `0x04` Static | Static `0x01` | `0x07` | yes |
| `0x01` Breathing | Breathe `0x02` | `0x07` | yes |
| `0x00` RainbowBreathing | Breathe `0x02` | `0x08` | no — full colour |
| `0x02` Neon | Neon `0x03` | `0x08` | no — effect ignores colour per spec |
| `0x05` Wave | Wave `0x04` | `0x07` | yes |
| `0x03` ColorCycle | Wave `0x04` | `0x08` | no — full colour |
| `0xED` Off | — | — | sends `SET_LEDONOFF 0x05` instead |

ColorCycle and Wave sharing a firmware effect and differing only in colour source mirrors what
`RasterKeyboardRgbService` does with the same two app effects.

On/off is a firmware state variable independent of the effect (vendor code `05 03 xx 04`), and its
polarity is inverted (`0` = on). Every non-Off write is preceded by an unconditional
`0x05 00 00` re-enable so an Off → non-Off transition relights the board.

## Files changed

| File | Change |
|---|---|
| `docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md` | **new** — English translation of the YC3123 spec: transport, framing, command table, LED Param Table, `Option` colour slots, vendor codes |
| `docs/knowledge-base/ry5088-magnetic-axis-protocol.md` | **new** (restored from `620b852`) — magnetic-axis command set, cross-referenced to the YC3123 doc for lighting |
| `src/ProoveGaming.Infrastructure/Services/T800KeyboardRgbService.cs` | **new** — the service |
| `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | `T800` name branch + `CreateT800KeyboardRgbService`, selecting on **feature**-report length and `mi_02` |
| `src/ProoveGaming.DeviceHost/appsettings.json` | `"SupportsRgb": true` on the T800 entry |
| `tests/ProoveGaming.Infrastructure.Tests/T800KeyboardRgbServiceTests.cs` | **new** — 37 tests pinning framing, effect codes, colour slots, the measured checksum, wire-speed inversion and colour clamping |
| `src/ProoveGaming.Device.Testing/T800LedProbe.cs`, `Program.cs` | **new** — probe: `auto` resolves the checksum by read-back, `send` round-trips an arbitrary frame, `speed` steps the wire speed ladder, interactive mode listens to the vendor-code channel |
| `docs/knowledge-base/royuan-led-encoding-notes.md` | **new** — the checksum, wire-speed inversion and colour quirks no vendor doc states, with sources and the hardware transcript |
| `docs/CONTEXT.md` | glossary: T800, RY5088, RY5088 checksum, wire speed inversion, Rongyuan, LED Param Table, AP colour slot |

## The checksum — resolved

Both vendor documents mark a `CS` byte in every packet layout and **neither defines how it is
computed**. It is the **one's complement of the sum**:

```
CS = 0xFF - (sum & 0xFF)
```

A plain sum — the assumption the first two attempts made — is off by exactly one. The firmware
discards a bad packet **silently**: no ack, no error, and `GetFeature` falls back to returning the
driver's cached copy of the request, so an accepted and a rejected command are indistinguishable
from the host. That is what made this device fail invisibly twice.

Found in independent reverse-engineering of the same ROYUAN/RongYuan firmware family, saved to
`docs/knowledge-base/royuan-led-encoding-notes.md`, and confirmed here on hardware. The covered
ranges and `CS` positions were already correct; only the formula was wrong.

Two further undocumented facts came from the same source and are now implemented:

- **Speed is inverted on the wire** — a low byte is fast. We send the vendor-proven `5..1`. The sources disagree on range (YC3123 says the field is `0–4`; the ROYUAN notes say the wire carries `5..1`), which would make `0` a faster step, but it is unverified and is **not** used: the firmware stores any speed byte unclamped, so an invalid speed that freezes the animation looks exactly like a dead slider. See § Open — speed.
- **White ships as `FA FA FA`**, and a colour with every channel below `0x08` renders unlit.
  Scoped narrowly: hardware read-back confirms a saturated `FF 00 00` is stored unchanged, so the
  `FA` rule is applied only to near-white rather than as a per-channel cap.

## Verification

Confirmed against a connected T800 with `ProoveGaming.Device.Testing`.

**Checksum**, by read-back (`dotnet run --project ./src/ProoveGaming.Device.Testing -- auto`):

```
GET_LEDPARAM per candidate
  OnesComplement  sent 87 00 00 00 00 00 00 78  ->  87 04 02 04 08 FF FF FF   <== real reply
  Sum             sent 87 00 00 00 00 00 00 87  ->  87 00 00 00 00 00 00 87   (our own echo)
  NegatedSum / Xor / Zero                           (our own echo)
```

Only the one's-complement frame drew a real answer — the board's actual state at the time
(Wave, rainbow, brightness 4). Accepted writes also emit the vendor-code Flash Write bracket
(`05 0F 01` … `05 0F 00`) on `col05`; rejected ones emit nothing.

**Round-trip of the exact frames the service builds**
(`dotnet run --project ./src/ProoveGaming.Device.Testing -- send 07 01 02 02 07 76 20 EE`):

| Sent | Read back | Decoded |
|---|---|---|
| `07 01 02 02 07 76 20 EE 68` | `87 01 02 02 07 76 20 EE` | Static, speed 2, bri 2, slot 7, `#7620EE` |
| `07 02 03 04 08 76 20 EE 63` | `87 02 03 04 08 76 20 EE` | Breathe, speed 3, bri 4, slot 8 (rainbow) |
| `07 04 01 04 07 FF 00 00 E9` | `87 04 01 04 07 FF 00 00` | Wave, speed 1, bri 4, slot 7, red |

Still worth a human pass in the App, since read-back proves the firmware *stored* the state but not
what the board *looks like*: check that each of the six effects is visually distinct, that the
brightness and speed sliders move monotonically over their coarse 5-step ranges, and that Off
darkens the board and re-picking an effect relights it.

## Note — every LED write hits flash

Accepted commands emit the Flash Write vendor code, i.e. each colour change is persisted by the
firmware. The App's existing slider debounce keeps this to one write per settled interaction;
don't bypass it. The external notes report the config endpoint stops answering until
re-enumeration if page writes are pushed too fast.

## Out of scope

- Side lighting (`0x08` / `0x88`). No app-side UI for it, and the T800 may have no side strip.
- Read-back (`0x87`, `0x85`). Not needed: RY5088 writes are independent, unlike Raster's
  read-modify-write.
- Per-key LEDs, key remapping, macros, layer switching, magnetic-axis trip settings (`0x65`,
  `0x0A`, `0x0B`). The T800 supports all of them — separate plans.
- The 20 firmware effects the app has no slot for (Ripple, Snake, Meteor, Fireworks, …).

## Open — speed

Reported from the field: full slider looks slower than the other keyboards, and then that speed
appeared not to change at all. Unresolved, and the one part of this device still open.

What is known:

- The speed byte is at protocol byte 2 and is inverted (low = fast); the firmware **retains**
  whatever we send, including out-of-range 0, 6 and 7. Retention is therefore not evidence that a
  value animates.
- `Speed` is marked not-meaningful for **Static** in the LED Param Table, so the slider is
  correctly inert there. Worth confirming which effect the report came from.
- Some firmware in this family only re-reads animation parameters when the effect itself changes,
  so a speed written under an unchanged effect may be stored and never applied. The `speed` probe
  re-latches via Static between steps to rule this in or out.

Two probes settle it, neither relying on our command path being right:

```
dotnet run --project ./src/ProoveGaming.Device.Testing -- speed   # ladder, Breathing, wire 0-5
dotnet run --project ./src/ProoveGaming.Device.Testing -- learn   # listen only; use the board's
                                                                  # own speed shortcut
```

`learn` is the stronger of the two: the firmware announces its own speed as `05 05 <speed>` when
changed on-device, which gives the real range and direction with no inference at all.

## Artwork cropping

The T800 artwork was delivered as a **1920-square canvas with the board in a band across the
middle** — roughly two thirds of it empty. Every other keyboard ships a tightly-cropped wide strip,
so T800 scaled to the empty square and rendered about a third of the size the others got: on the
home card (`MaxWidth=336 / MaxHeight=160`) it was capped by height at 160x160 with the board only
~49 px tall, and on the details page (`Width="628"`) the control became 628x628 with ~435 px of
empty padding shoving the sections below it down.

Content measured at `(177, 677) - (1909, 1267)`, stable down to alpha > 1. All five assets are
cropped to the same window with a 5-unit margin — no pixels altered, only the frame:

| Asset | Change |
|---|---|
| `Card/PG_T801_card.svg` | `viewBox` -> `172 672 1742 600` |
| `PG_T-801_Keyboard_SVG.svg` (flat, `ImagePath`) | same |
| `PG_T801_base.svg` | same |
| `PG_T801_keys.svg` | same |
| `Glow/T800.path` | shifted by `(-172, -672)` |
| `RgbKeyboardArtCatalog` | `1920, 1920` -> `1742, 600` |

The glow is a separate `Path` drawn in Canvas coordinates while the SVGs carry their own viewBox,
so the shift has to match the crop origin exactly or the backlight drifts off the key wells. Verified
by compositing chassis -> glow -> keycaps in the new coordinate system: the glow fills the gaps flush
with the well edges, no bleed onto the chassis frame.

Same technique Aress already uses (`viewBox="34.5 541 1503.5 481.5"`). Rendered card width now
matches every other board at 336 px across (115.7 tall, between Cyrex at 109.7 and Rampage at 131.9).

## Macros — UI only, deliberately

`SupportsMacros: true` is set so the Macros section appears, and recording/saving to the App's
SQLite library works. **Nothing reaches the device.** This is a UI-visibility decision, not a
feature; do not read the flag as "T800 macros work".

Three things are missing, and the first is a hard blocker:

1. **The macro payload encoding is undocumented.** Both vendor specs define the *transport*
   (`0x0B SET_MACRO`: 64 macros, 5 pages of 56 bytes, `Len`, `End`) but neither says how a
   keystroke or a delay is represented inside a macro's 256 bytes. Same class of gap as the
   [[RY5088 checksum]], and it needs the same treatment: read a macro back with `GET_MACRO`
   (`0x8B`) and decode it, rather than guessing.
2. **No macro service.** `DeviceKeyboardMacroServiceFactory` has no T800 branch, so
   `UploadMacrosAsync` resolves to null and logs "not a macro-supported keyboard".
3. **No way to bind a macro to a key.** That needs `IKeyboardKeyMappingService` (no T800 branch in
   `DeviceKeyMappingServiceFactory`, and RY5088 key writes are `0x0A`), plus a T800 key catalog and
   a measured keycap layout table — `HasInteractiveKeyboardOverlay` gates the assign path off
   without them.

