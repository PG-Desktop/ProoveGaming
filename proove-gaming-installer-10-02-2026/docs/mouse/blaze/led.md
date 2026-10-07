# BLAZE / X43 — LED / RGB Status

## Short version

**2026-07-30 update: the LED lights up and RGB colour changes are now visible on the
device**, driven by the unified full-configuration write
(`EasenMouseService.ApplyConfigurationAsync` — see
[refactor-unify-device-writes](../../plans/refactor-unify-device-writes/refactor-unify-device-writes-plan.md)),
i.e. via **Save Preset** / preset switch, which writes the full all-blocks param_1 +
param_0 + the `0x0D`/`0x80` LED drive together in one coordinated sequence. This
reverses the "hardware dead" conclusion below, which was reached testing LED writes in
isolation (standalone `SetLedColorAsync`, and the `Device.Testing` spikes) — apparently
those isolated writes were not enough to light this unit, but the full coordinated write
is. See [2026-07-30 — LED confirmed working via the unified apply](#2026-07-30--led-confirmed-working-via-the-unified-apply)
below for what's confirmed vs. still open (persistence across reconnect in particular is
unverified under this new path).

The rest of this document, including the "effectively dead" conclusion, is kept as the
historical investigation record — it explains the LED command mechanics that the
unified write still relies on.

On our test unit the LED was believed to be effectively **dead** (see the 2026-07-30
update above for the reversal). None of the LED commands lit it reliably in isolation —
**including the OEM's own commands**. The working hypothesis at the time was a
**hardware / flash fault on this specific unit**. Long-form investigation:
[`docs/led-protocol-investigation.md`](../../led-protocol-investigation.md) and
[`docs/manufacturer-inquiry-led.md`](../../manufacturer-inquiry-led.md).

## The "changing DPI turns the LED off" bug (root cause + fix)

This was a **real software bug we caused**, distinct from the hardware fault above.

**Cause:** in param_0 (cmd `0x02`) the active DPI stage (byte 1) and the LED effect
(byte 2) live in the **same 64-byte block**. Our old "set active DPI stage" rewrote
the whole param_0 block, so every DPI change also rewrote `ledEffect`. When that
byte was `0x00` (All Off), the DPI change and an LED-off happened in a single write.

**Fix:** DPI-stage and LED writes are now **block-scoped** so one never disturbs the
other:
- LED colours → param_1 **block 1 only** (`WriteLedColoursBlockAsync`).
- DPI stage / mouse params → param_1 **block 0 only** (`WriteParam1BlockAsync`),
  never the shared param_0 LED bytes.

This matches the OEM design intent (they use a dedicated DPI path and never touch
LED state when switching stages). It prevents the failure mode on healthy hardware;
it does not revive the already-damaged unit.

## The misfiring LED toggle (removed)

An earlier DeviceHost listener (`StartSwitchListener`) misread the Col04 DPI
notifications (`0x05…` reports) as RGB on/off toggles and fired param_0 LED writes —
which is why the logs said "LED on/off" while the DPI button appeared to "do
nothing." **That listener was removed.** DPI notifications are now consumed only by
`DpiStageMonitor` (see [dpi-stage.md](dpi-stage.md)), which never writes to the
device.

## LED command reference (for healthy hardware)

Per the Easen spec, LED is controlled via the param_0 `ledEffect/brightness/speed/
color/option` bytes plus the colour block in param_1 block 1. The `0x0C`/`0x0D`/`0x10`
dedicated LED commands in the OEM exe target **other MCU families** and are **not**
used for this PID (the capture shows VS750 driving this unit with `0x02`/`0x03`
only — see [protocol.md](protocol.md)). Effect ids and their argument ranges are in
[`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) §"LED Effect Types".

The colour data layout in param_1:
- **block 1 bytes 0–23** — DPI-stage indicator colours (8 stages × RGB).
- **block 1 bytes 24–47** — LED effect colours (8 slots × RGB). `colorSlot` in param_0
  byte 5 picks which slot the effect uses; slot `7` = "all/loop" (rainbow).
- `EasenMouseService.WriteLedColoursBlockAsync` writes **block 1 only**, so a colour
  change never disturbs the DPI table (block 0) or key matrix (block 2).

App-effect → protocol mapping (`MapAppEffectToProtocol`):

| App effect | proto effect | colour slot |
|------------|-------------|-------------|
| Breathing (`0x01`) | `0x02` | 0 (custom) |
| RainbowBreathing (`0x00`) | `0x02` | 7 (all) |
| Neon (`0x02`) | `0x03` | 0 |
| Wave (`0x05`) | `0x04` | 0 (custom) |
| ColorCycle (`0x03`) | `0x04` | 0 (custom) |
| Off (`0xED`) | `0x02`, brightness 0 | 0 |

**Wave vs ColorCycle (2026-08-17).** Protocol `0x04` on this single-LED mouse is a travelling
gradient, not a cycle of solid colours, so the App presents it to mice as **Wave** and no longer
offers them "ColorCycle" at all — that is a keyboard effect, and a separate one from Wave there. Both
App bytes are mapped because RGB state persisted under the old name must keep lighting the mouse the
same way. Note protocol `0x04` also ignores byte 3 as a brightness level (see `ResolveBrightness`),
which is why Wave shows no brightness slider on a mouse.

### The block 1 palette (bytes 24–47)

Corrected 2026-08-06 — the previous version of this table gave ColorCycle slot 7, and the
surrounding text assumed slot 7 meant "the firmware supplies its own colours". Both were wrong,
and each caused a different effect to look like the LED simply switching off.

**Slot 7 does not generate colours — it sweeps the eight RGB triples stored at block 1 bytes
24–47.** Leaving that region zeroed cycles eight blacks, which is exactly what "gradient
breathing turns the LED off" was.

**The palette is a constant table, not a per-effect one.** In
`cap-06.08.26-for-blaze-neon-color-cycle.pcapng` the OEM toggled neon ↔ colour cycle four times;
block 1 is byte-identical in all eight writes, and identical again to the gradient capture
`cap-06.08.26-for-blaze.pcapng`. Only `param_0` changes between effects. The palette:

```
00 B7 FF   00 00 FF   00 FF 00   FF FF 00   00 FF FF   FF 00 FF   FF 80 00   FF FF FF
light blue  blue      green      yellow     cyan       magenta    orange     white
```

**Which effects read it is decided by the effect, not the slot** (`X43LedEncoder.SweepsPalette`):
slot 7 sweeps it on any effect, and neon (`0x03`) and colour cycle (`0x04`) sweep it too even
though the OEM sends both on slot **0**. Only breathing on slot 0 shows a single custom colour,
and that is the one case where the custom colour is written into all eight entries.

Observed `param_0` payloads, for reference:

| effect | param_0 bytes 0–5 | proto | brightness | speed | slot |
|--------|-------------------|-------|-----------|-------|------|
| Gradient breathing | `01 07 02 fc 04 07` | `0x02` | `0xFC` | 4 | 7 |
| Neon | `01 07 03 bd 04 00` | `0x03` | `0xBD` | 4 | 0 |
| Colour cycle | `01 07 04 bd 04 00` | `0x04` | `0xBD` | 4 | 0 |

**"Rainbow" is not a separate effect — it's Breathing with colorSlot 7.** Confirmed by
`cap-13.07.2026-gradient-color.pcapng`: selecting the OEM's gradient colour wrote param_0 =
`01 07 02 fc 02 07 …` → byte2 effect `0x02` (breathing), byte5 colorSlot `0x07` (all colours).
So the App UI folds RainbowBreathing into a **gradient swatch inside Breathing's colour
picker** (`RgbEffectSettingsViewModel.IsRainbow`, Breathing-only). Picking it sends app-effect
`0x00`, which still maps to (`0x02`, slot 7) here — the device side is unchanged.

## Research history (why we believe it's hardware)

This is the condensed record of the LED investigation, moved here from working notes.
Long-form: [`docs/led-protocol-investigation.md`](../../led-protocol-investigation.md),
[`docs/manufacturer-inquiry-led.md`](../../manufacturer-inquiry-led.md).

### 2026-07-30 — LED confirmed working via the unified apply
After the `ApplyDeviceConfiguration` unified write landed (one coordinated write: full
all-blocks param_1 + param_0 + `0x0D`/`0x80` LED drive, see
[EasenMouseService.cs](../../../src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs)),
triggered from the App via **Save Preset** / preset switch, the user confirmed the LED
lights up and RGB colour changes are now visible live on the device.

This is a real reversal of the 2026-07-28 "decisive isolation" conclusion below, not a
contradiction of the mechanics it documented — the `0x0D`/`0x80` drive commands were
already used by the standalone `SetLedColorAsync` at the time of that isolation test, so
the difference is not "a new command." What's different is that the drive now runs
**after the full coordinated block0+block1+block2+param_0 write**, not after an isolated
LED-only write. Root cause for why the full write succeeds where isolated LED writes
didn't is **not yet understood** — open items below.

Open items (unconfirmed, follow up before treating this as fully resolved):
- Does the LED stay lit / keep responding to colour changes on a **second** Save
  Preset, or was this a one-time unstick (similar to the 2026-06-30 event below)?
- Does it survive a **reconnect** (power-cycle / unplug-replug)? The "no persistence"
  finding below was for isolated writes — unverified under the coordinated write.
- Does an isolated `SetLedColorAsync` call (outside of Save Preset) still fail to light
  it, confirming the full-write shape is actually what matters and not some other
  session/state change?

### 2026-07-28 — the decisive isolation: physical switch also dark
Acting on the vendor Q&A ([blaze-vendor-qa-2026-07-27.md](blaze-vendor-qa-2026-07-27.md)),
we implemented the vendor's reset (write the default profile to the `0x02` + `0x03` tables,
`X43Mouse.ResetToDefault` / `LedNativeSwitchTest` in the Device.Testing spike) and confirmed:
- **Our writes DO commit to flash** — nulling the DPI slot (5) killed the DPI button, then
  binding it restored it. So the block-2 write reaches and persists in firmware.
- **The LED stays dark even when pressing the underside *lighting-effect* switch physically**
  (the vendor's own native LED control), with slot 7 left null so the switch keeps native
  control. A physical switch bypasses our software entirely.

Conclusion: the LED being off is **not** a protocol/host-software problem — the effect payload
and the profile write are correct and committing. The LED engine is gated in **hardware/firmware
on this unit** (or the LED is defective). Next: isolate against the OEM software and, if that
can't light it either, escalate to the vendor as a hardware/firmware question.

### The one time it lit (2026-06-30) — and what it means
The LED lit up **once**, after the user mashed many button combinations and briefly
re-plugged USB. The initial theory was a physical LED gating switch; the user later
clarified it was more likely an accidental **factory-reset button combo** (hold
buttons while plugging in — common on these mice). It has not lit since. Takeaway:
there may be a hidden reset combo, but it's not a reliable software-reachable path.

### Confirmed-working command mechanics (from when it did respond)
- `cmd=0x02` param_0: `byte2=ledEffect, byte3=brightness (00–FF direct, no rescale),
  byte4=speed (01–07), byte5=colorSlot (0–7, 7=loop), byte6=ledOption`.
- **Transport matters:** 64-byte data blocks reach the firmware **only via `WriteFile`
  to the interrupt-OUT endpoint (EP3)**. Both `HidD_SetOutputReport` (forces control
  transfer on EP0) and `HidD_SetFeature`-for-bulk were tested as transports and **broke
  live updates entirely** — Windows returns success but the firmware ignores EP0 data.
  Feature reports (EP0) are a separate, working path for the 9-byte command headers.
- **Checksum is not validated** — deliberately corrupting it (XOR 0xFF) changed nothing.

### Two open problems (both deprioritized, not disproven-impossible)
1. **No true static effect.** Every documented effect (`01`–`09`) animates; even
   `0x02` "Always On" (which does respect the chosen colour, unlike the others that
   rainbow-cycle) still breathes. There is no "set RGB directly, no animation" command
   in the spec — the LED is only driven through the effect engine.
2. **No persistence across reconnect.** Writes apply live only; power-cycling reverts
   to the factory profile. Narrowed findings: `brightness` and `speed` **do** survive a
   reconnect, but `ledEffect` (the effect *type*) does **not** — even though all three
   are adjacent bytes in the same param_0 write. `brightness=0` also survives. So
   firmware treats brightness/speed as live-committed but the effect type needs a
   commit step we never found (`cmd=0x01` "Select Profile" was tested and ruled out).
   The device advertises "8 Profiles in Flash", so persistence *should* be achievable —
   the flash-commit trigger is just undiscovered. Confirmed by the user across multiple
   commits: persistence **never** worked in any version (so we didn't regress it).

### Reads don't work for LED state
`cmd=0x82`/`0x83` (read param_0/param_1) return zeros/timeouts on this firmware; only
`cmd=0x80` (firmware version) echoes real data. So there is **no** way to read the
device's current effect/colour — see the read-back matrix in [README.md](README.md).
`brightness`/`speed` aren't in the `0x05` notification either, so even a partial read
is impossible; only the effect *byte* is observable (offset 4), and it maps ambiguously.
