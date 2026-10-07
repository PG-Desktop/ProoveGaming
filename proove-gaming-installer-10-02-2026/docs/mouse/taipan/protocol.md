# PG TAIPAN — Telink protocol reference

Distilled, in-English reference for the PG TAIPAN mouse, derived from the vendor documents
kept next to this file:

| Vendor file | What it gives us |
|---|---|
| `Telink-USB20260205.xlsx` | **Authoritative.** Full command set with worked example frames, the `BtFuncNum` button-function table, LED-effect struct, macro action types. Dated 2026-02-05 and describes the *current* firmware. |
| `Telink_USB协议_地址表v1.3.pdf` | EEPROM address tables (config / DPI / button / LED / macro) and parameter enumerations. |
| `taipan-button-write-reference-2026-09-01.md` | Vendor-shareable: the exact 8 frames for one button remap (right click → My PC), cable vs dongle, with a standalone C reproducer. |
| `Telink GM协议 V1.1.doc` | Older generic 2.4G "GM" protocol. Function numbers agree with the xlsx; other details **conflict** (see [Conflicts](#conflicts-between-vendor-docs)). Supplementary only. |

TAIPAN is a **Telink**-based mouse (VID `9354` = `0x248A`). This is a *different silicon and a
different protocol* from BLAZE/X43, which is Holtek HT68FB571 / Easen (see
[../blaze/protocol.md](../blaze/protocol.md)). Nothing in the BLAZE wire format applies here.

Several items below were confirmed or corrected by the vendor's firmware team — see
[taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) for the full Q&A and analysis.

## Transports and USB identity

TAIPAN reaches the host three ways. Each is a distinct USB identity:

| Transport | VID | PID | Notes |
|---|---|---|---|
| Wired (USB cable) | `9354` (`0x248A`) | `64257` (`0xFB01`) | The mouse itself enumerates. |
| 2.4G dongle | `9354` (`0x248A`) | `33651` (`0x8373`) | The **dongle** enumerates; the mouse talks through it. |
| Bluetooth | `9354` (`0x248A`) | `33651` (`0x8373`) | Same PID as the dongle; distinguished by the BT stack. **Not supported for writes, or at all, by the vendor's own stack** — confirmed in the vendor Q&A (T2/T9). |

The dongle and the wired mouse are **separate USB devices** with separate Windows Container IDs.
One physical mouse can therefore present as two entries at once (cable + dongle plugged in).

**HID interface per transport** (vendor-confirmed, Q&A T8):

| Transport | Usage | UsagePage | Feature report len | Input report len | Output report len |
|---|---|---|---|---|---|
| Wired | `0x0000` | `0xFFEF` (vendor-specific) | `0x0040` (64) | `0x0040` (64) | `0x0040` (64) |
| 2.4G dongle | `0x0000` | `0x0001` (Generic Desktop) | `0x0008` (8) | `0x0021` (33) | `0x0021` (33) |

The dongle's 33-byte input/output length matches the documented frame size directly; the wired
64-byte reports carry the same 33-byte frame padded to the larger report.

## Frame format

Every command is a **33-byte** HID report:

```
Byte0      = ReportID       0x05
Byte1      = dummy          checksum (see below)
Byte2      = driverID       0xFA
Byte3      = driverCMD      command (see Command set)
Byte4..7   = driverPara1..4 command parameters
Byte8..32  = FIFO_DATA[0..24]   25 payload bytes
```

**Checksum (`dummy`, Byte1):**

```
dummy = (driverID + driverCMD + driverPara1 + driverPara2 + driverPara3 + driverPara4) ^ 0x66
```

Sum is byte-wide (carry discarded). Unlike BLAZE — where the firmware ignores the checksum —
every documented TAIPAN example frame carries a correct `dummy`, so treat it as **required**.

**Read vs write:** the same command exists in two forms — write `driverCMD = 0x0X`, read
`driverCMD = 0x4X` (e.g. button write `0x03`, button read `0x43`).

### Address / length / packet-sequencing parameters

For the table read/write commands (`0x01`–`0x05`, `0x0C`):

| Param | Meaning |
|---|---|
| `driverPara1` | logical address **low** byte (offset into the selected table) |
| `driverPara2` | logical address **high** byte |
| `driverPara3` | number of payload bytes in this packet (max `0x19` = 25) |
| `driverPara4` | packet sequencing: `0xD1` first, `0xD2` continuation, `0xD3` last/only |

> **`0xD1` erases the flash sector.** The vendor notes *"D1 会擦除扇区"* — starting a write with
> `0xD1` wipes the table, so a partial write must still re-send every other byte of that table
> or the untouched bytes are lost. This is the TAIPAN analogue of the BLAZE
> "partial-content write zeroed the key matrix" incident.

### Commit sequence (mandatory)

After writing DPI / DPI colour / Button / LightEffect tables:

1. Write `ValidBankID = 0x1A` to offset **`0x007F`** of that table (a 1-byte `0xD2` packet).
2. Send a final packet with `driverPara4 = 0xD3` to trigger the firmware's `Update_Para()`.

For **Macro** the ValidBankID goes to offset **`0x0FFF`** instead. Without this the write is not
committed.

## Command set

| CMD (write / read) | Name | Purpose |
|---|---|---|
| `0x00` | `CMD_Reset` | `Para1=0xF1` reboot; `Para1=0xF3` restore defaults (`Para2=0x80` = all params) |
| `0x01` / `0x41` | `CMD_RW_Config` | MsConfig block, `0x13` (19) bytes — polling rate, active DPI, lift-off, profile index |
| `0x02` / `0x42` | `CMD_RW_DPI` | DPI stage table (values only, no colour) |
| `0x0C` / `0x4C` | `CMD_RW_DPI_Color` | Per-DPI-stage indicator RGB. **Vendor-confirmed not implemented in shipped firmware** (Q&A T1) — writes are accepted but have no effect; treat as display-only in the App. |
| **`0x03` / `0x43`** | **`CMD_RW_Button`** | **Button map table** — see [Button map](#button-map) |
| `0x04` / `0x44` | `CMD_RW_LightEffect` | LED effect table (8 bytes per profile) |
| `0x05` / `0x45` | `CMD_RW_Macro` | Macro slots |
| `0x06` | `CMD_SetMouseState` | Echoes back (ACK); enable/disable, all-LEDs-off |
| `0x07` | `CMD_SetProfile` | Set active profile (0–4). **Vendor-confirmed not implemented in shipped firmware** (Q&A T5) — the App never sends this command. |
| `0x08` → reply `0x48` | `CMD_GetBATV` | Battery + identity, see [Battery](#battery-and-identity). **Over 2.4G the request must be sent as `0x48`, not `0x08`** — see below |
| reply `0x49` | `Report_MouseState` | Device-pushed: current profile / DPI stage / report rate / LED stage |

## Profiles

The device has **5 profiles** (0–4). DPI, button map, LED and polling rate are **per profile**.
`ProfileIndex` lives at MsConfig offset `0x0006`; `ProfileSwitch` (`0x0010`) is a bitmask
enabling profiles 1–5 (bit0..bit4).

## Button map

**This is the table the first implementation slice targets.**

Command `0x03` (write) / `0x43` (read). EEPROM base per profile is `0x0070`; command offsets are
**table-relative**:

| Offset | Content |
|---|---|
| `0x0000` | Max button-event count, **low** byte |
| `0x0001` | Max button-event count, **high** byte |
| `0x0002` | Button 1 — 4 bytes |
| `0x0002 + (N-1) * 4` | Button N — 4 bytes |
| `0x0062` | Button 25 (last) |

Each button entry is **4 bytes**:

```
[ BtFuncNum, BtModify, BtAlpha1, BtAlpha2 ]
```

Buttons 23 and 24 carry the wheel pair, but **in the reverse of the order the vendor address
table implies**: slot 23 is **Wheel Down** and slot 24 is **Wheel Up**. Verified by hand on
2026-08-12 — writing the table in the documented order inverts scroll direction on the physical
mouse; writing it in this order restores it.

This was originally recorded here as a *convention* taken from the address table and never
confirmed against hardware, and it caused a real user-visible bug: `TaipanButtonMap.BuildTable`
starts from `CreateDefault()`, which populates both wheel slots, so **every** button write
rewrites the wheel pair — remapping right-click silently inverted the user's scrolling. The
lesson generalises: a slot whose meaning we assume but never exercise is untested regardless of
how many table writes pass through it.

### `BtFuncNum` — function table

`bit7 = 1` marks the entry active; the function is `BtFuncNum & 0x7F`.

| Value | `&0x7F` | Function | Parameters |
|---|---|---|---|
| `0x00` | — | Empty / placeholder | — |
| `0x81` | `0x01` | Mouse Left | — |
| `0x82` | `0x02` | Mouse Right | — |
| `0x83` | `0x03` | Mouse Middle | — |
| `0x84` | `0x04` | Mouse B4 (Back) | — |
| `0x85` | `0x05` | Mouse B5 (Forward) | — |
| `0x88` | `0x08` | DPI cycle | — |
| `0x89` | `0x09` | DPI decrease | — |
| `0x8A` | `0x0A` | DPI increase | — |
| `0x8B` | `0x0B` | Wheel up | default Button **24** (hardware-verified) |
| `0x8C` | `0x0C` | Wheel down | default Button **23** (hardware-verified) |
| `0x8D` | `0x0D` | Profile cycle | — |
| `0x8E` | `0x0E` | **Media / Consumer key** | `BtAlpha1` = usage low, `BtAlpha2` = usage high |
| `0x8F` | `0x0F` | **General key (combo)** | `BtModify` = HID modifier bitmask, `BtAlpha1` = key code |
| `0x90` | `0x10` | **Single key** | `BtAlpha1` = HID key code |
| `0x91` | `0x11` | **Macro key** | `BtModify`: bit7 = infinite, bit6 = interrupt running macro, bits0–4 = slot; `BtAlpha1` = loop count |
| `0x92` | `0x12` | Fire key (simple rapid-fire) | params unused by current firmware |
| `0x93` | `0x13` | User-defined ID key | `BtModify`/`BtAlpha1`/`BtAlpha2` = reported ID |
| `0x94` | `0x14` | Profile + | — |
| `0x95` | `0x15` | Profile − | — |
| `0x96` | `0x16` | DPI lock (hold) | — |
| `0x97` | `0x17` | Polling-rate increase / cycle | `BtModify = 0x01` → cycle 1/2/4/8 |
| `0x98` | `0x18` | Polling-rate decrease | — |
| `0x99` | `0x19` | Advanced rapid-fire | `BtModify` = mode, `BtAlpha1` = count, `BtAlpha2` = interval × 10 ms |
| `0x9B` | `0x1B` | Normal key (LED control) | `BtModify` bit0 = toggle all RGB; other bits: restore factory / switch effect / speed / brightness |
| `0x9F` | `0x1F` | Long-press mode key | short press = CPI, long press = polling rate |

HID modifier bits for `0x8F`: `0x01` LCtrl, `0x02` LShift, `0x04` LAlt, `0x08` LGUI.

Consumer usages seen in the vendor examples: Play/Pause `0x00CD`, Next `0x00B5`,
Previous `0x00B6`, Stop `0x00B7`, Mute `0x00E2`, Volume+ `0x00E9`, Volume− `0x00EA`,
Media Player `0x0183`. Encoded little-endian into `BtAlpha1` (low) / `BtAlpha2` (high).

### Factory default button map (Profile 1)

Reconstructed from the vendor's worked "full write" example:

| Button | Bytes | Function |
|---|---|---|
| 1 | `81 00 00 00` | Left |
| 2 | `82 00 00 00` | Right |
| 3 | `83 00 00 00` | Middle |
| 4 | `85 00 00 00` | B5 / Forward |
| 5 | `84 00 00 00` | B4 / Back |
| 6 | `88 00 00 00` | DPI cycle |
| 7 | `97 01 00 00` | Polling-rate cycle |
| 23 | `8C 00 00 00` | Wheel **down** — see the note above; this is the reverse of the address table |
| 24 | `8B 00 00 00` | Wheel **up** |

Max button-event count in that example is `0x000A` (10).

Unlike BLAZE — where the DPI / report-rate / LED buttons live in *fixed* key-matrix slots that
must never be rewritten — on TAIPAN **Button 6 and Button 7 are ordinary entries in the same
table** and are therefore remappable.

### Worked example — write the default map

```
TX(D1)     : 05 B6 FA 03 00 00 02 D1 0A 00 ...        ; offset 0x0000, len 2 → MaxButtonIndex = 10
TX(D2)     : 05 8F FA 03 02 00 18 D2 81 00 00 00  82 00 00 00  83 00 00 00
                                     85 00 00 00  84 00 00 00  88 00 00 00
                                                              ; offset 0x0002, 24B → Buttons 1-6
TX(D2)     : 05 67 FA 03 1A 00 18 D2 97 01 00 00 ...  ; offset 0x001A → Button 7
TX(BankID) : 05 29 FA 03 7F 00 01 D2 1A ...           ; ValidBankID = 0x1A at 0x007F
TX(D3)     : 05 B6 FA 03 00 00 00 D3 ...              ; commit → Update_Para()
```

Reading the map back is supported (`0x43`) — a real advantage over BLAZE, whose key matrix
cannot be read back.

## DPI

Command `0x02` / `0x42`; per-stage colour is a **separate** table (`0x0C` / `0x4C`).

- Up to **8 stages**; `driverPara4`-adjacent payload byte 0 carries the max stage count.
- Per stage, 5 bytes: `[enable, X_L, X_H, Y_L, Y_H]` — one 16-bit little-endian CPI register,
  mirrored to both X and Y (current App assumption, from the vendor's worked full-table write
  example). The xlsx annotates a per-axis "double" flag instead; the PDF address table describes
  plain L/H pairs. The vendor Q&A (T7) re-confirmed a per-axis "doubled" flag exists but the answer
  is ambiguous about the exact byte split — **the two encodings decode the same raw bytes into
  different DPI values, so this still needs a hardware read-back to settle before either is trusted
  as final.** See [taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) T7 and
  [Open items](#open-items--to-confirm-on-hardware).
- Active stage lives in MsConfig `0x0011` (`DpiSwitch`), range `0x00`–`0x07`. **Lags the physical
  DPI button by one press** — a read can return the previous stage, not the one just selected
  (vendor Q&A T3, unresolved on the vendor's side). Don't poll `0x0011` for "current stage right
  now"; rely on the device-pushed `Report_MouseState` (`0x49`) / live event tracking instead.
- DPI colour table: 3 bytes (R,G,B) per stage, 8 stages.
- **Why a 12000 DPI ceiling makes the T7 layout ambiguity moot (2026-09-13).** The App now lets the
  user set each stage's value (see `docs/plans/add-custom-dpi-values/`), bounded by the catalog at
  200–12000 in steps of 50. That ceiling is deliberate: the register is CPI/50, so 12000 → 240 and
  **every settable value fits in one byte**. With the high byte at `0x00`, the two candidate layouts
  — `[enable, X_L, X_H, Y_L, Y_H]` (what `TaipanDpiTable` implements) and
  `[enable, X_value, X_double, Y_value, Y_double]` (the vendor's T7 answer) — emit *identical bytes*
  and decode to the same DPI. The ambiguity only bites above 12750 DPI. Anyone raising `DpiMax` past
  that must settle T7 on hardware first.

## Polling rate

Per profile, MsConfig offsets `0x000B`–`0x000F`. Encoding is the **raw ms period**, identical to
BLAZE: `0x01` = 1000 Hz, `0x02` = 500 Hz, `0x04` = 250 Hz, `0x08` = 125 Hz.

## LED / RGB

Command `0x04` / `0x44`. **8 bytes per profile** (5 profiles + 1 reserved), EEPROM base `0x0030`:

```
[ LED_EnEffectSW, LED_R, LED_G, LED_B, LED_EffectValue, LED_LuminanceSpeed,
  LED_ColorSelect, LED_BrightScale ]
```

- `LED_EnEffectSW` — bit7: 1 = enable this effect config. bits0–6: colour index (0–6) for
  single-colour breathing modes.
- `LED_EffectValue` — effect mode:
  `0` off · `1` base-effect group (sub-mode chosen by `LED_ColorSelect`) · `2`–`4` reserved/internal ·
  `5` magic running · `6` trailing running · `7` reaction · `8` YOYO · `9` pinball.
- `LED_LuminanceSpeed` — speed 1–5 (1 slowest).
- `LED_ColorSelect` — when `LED_EffectValue == 1`, a bitmask: `0x01` seven-colour breathing,
  `0x02` steady, `0x04` single-colour breathing (uses R/G/B), `0x08` neon.
- `LED_BrightScale` — `0` off, `1` ≈30 %, `2` ≈70 %, `≥3` 100 %.

The PDF additionally documents a "colour source select" (`0x02` = look up DPI LED stage table,
`0x03` = look up Profile LED stage table) — this maps onto `LED_ColorSelect` and needs hardware
confirmation.

## Macros

Command `0x05` / `0x45`. **20 slots × 200 bytes**, one action per **3 bytes**:

```
[ Action_Type, Action_Data_L, Action_Data_H ]
```

`Action_Type` low nibble = type; **bit7 = 1 → Make (press), 0 → Break (release)**.

| Type | Meaning | Data |
|---|---|---|
| `0x01` | Mouse button | `Data_L` bitmask: `0x01` L, `0x02` R, `0x04` M, `0x08` B4, `0x10` B5 |
| `0x02` | 8-bit relative X/Y | `Data_L` = X, `Data_H` = Y (signed) |
| `0x03` | Wheel | `Data_L` = signed delta |
| `0x04` | Keyboard HID key | `Data_L` = key code (`0xE0`–`0xE7` = modifiers) |
| `0x05` | Consumer key | `Data_H:Data_L` = 16-bit usage |
| `0x06` | Delay | `(Data_H<<8 \| Data_L) × 10 ms` |
| `0x07` | Loop | `(Data_H<<8 \| Data_L)` = repeat count |
| `0x08` / `0x09` | 16-bit X / Y | signed little-endian |
| `0x00` / `0xFF` | End of macro | also implied when slot offset > 198 |

Capacity per slot: 200 B / 3 B ≈ **66 actions** (the PDF says "64 actions"; end-marker consumes
one). Compare BLAZE: 1 macro buffer of 128 B / 62 events.

## Battery and identity

`CMD_GetBATV` (`0x08`) → device replies with `driverCMD = 0x48`:

```
Para1 = voltage low, Para2 = voltage high   (mV, e.g. 0x1068 = 4200 mV)
Para3 = USB connected flag
Para4 = app-connected flag
FIFO[0..1] = customer/product ID (little-endian)
FIFO[2]    = high nibble: terminal ID, low nibble: sensor ID
```

Note this is a **voltage in mV**, not a percentage — converting to a battery % needs a curve or
threshold, which the vendor docs do not supply. The vendor Q&A (T6) later supplied a formula
(`% = 100 × (V − 750) / (985 − 750)`, clamped to 100), but the example values (`750`–`985`) are far
too small to be this mV field — they likely refer to a different, unidentified raw field. **Do not
wire this formula to `Para1/Para2` without hardware confirmation of which byte it actually applies
to** — see [taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) T6.

### What the App actually does — active query, with a passive fallback (implemented)

**Corrected 2026-08-07.** The App now sends a battery request on every refresh pass and caches the
reply. This replaced a purely passive listener.

#### The request opcode over 2.4G is `0x48`, not the documented `0x08`

The vendor confirmed (2026-08-07) that battery is **request/response**: the device answers once per
request and does not repeat without a new one. Measured on hardware the same day:

```
request  05-24-FA-48-00-00-00-00…      cmd 0x48, all parameters zero
reply    05-77-FA-48-CC-03-00-00…      raw=972 -> 94 %, usb=False
```

Three requests, three replies, one each, on `vid_248a&pid_8373&mi_00&col04`.

**This contradicts the opcode table above, and the table is what the vendor documents.** `CMD_GetBATV`
is specified as `0x08` replying `0x48`. Hardware testing on 2026-07-31 and 2026-08-04 sent `0x08` —
the documented value — and correctly recorded **zero reply** over the dongle. What actually works is
sending **`0x48`**, the *reply* opcode, as the request.

So the earlier finding was not a mistake in method; it followed the specification. The generalisable
lesson is narrower: **on this protocol, if the documented request opcode is silent, try the reply
opcode before concluding the transport is dead.** A single wrong byte had hardened into a belief
that the dongle ignored host→mouse traffic entirely.

Not tested: `0x08` or `0x48` as an active query over the **wired** cable.

#### Why the active query matters to a user

Passively, a battery value only appeared after a link-establishment event. A mouse connected for
hours showed whatever it last reported on reconnect, and a fresh app start could show nothing at
all until the user unplugged something. The request is issued each refresh pass, so the value is
current and appears immediately on connect.

#### How it is wired

`TaipanStateMonitor.RequestBatteryFromAll` sends the `0x48` frame to every unit it holds a reader
for, once per refresh pass. It is fire-and-forget: the reply arrives on the reader thread, which
already parses and caches `0x48`, so nothing blocks waiting for it. The request needs its own write
handle because the reader opens `GENERIC_READ` only.

The passive path is unchanged and still useful — the mouse volunteers a `0x48` on link
establishment, which updates the cache without waiting for the next pass.

Instead, `TaipanStateMonitor` (`ProoveGaming.DeviceHost`) holds a HID read handle open on the
TAIPAN vendor collection and listens for the SAME `0x48` report the mouse volunteers
**unsolicited** on every 2.4G link-establishment event: dongle plug-in, USB unplug, physical
mode-switch flip, wake from sleep. `TaipanStateReport.TryParseBattery` decodes `Para1`/`Para2`
from that push, and `PercentFromRaw` converts it — but real hardware captures show the raw value
is **not** the documented mV field either (captures read `1001`–`1008`, nowhere near a `4200`-mV
full-charge reading): it is interpreted as **quarter-millivolts** (`raw × 4` ≈ `4004`–`4032` mV, a
realistic charged Li-ion reading), clamped against a generic `3300`–`4200` mV Li-ion curve. This is
a **third, independently-calibrated interpretation** of the same `Para1/Para2` position — distinct
from both the doc's plain-mV reading and the vendor's T6 `750`–`985` formula, neither of which
matched what the hardware actually sends.

This push-based value is what reaches the App: `TaipanStateMonitor.TryGetBattery` → `DeviceService.GetBatteryLevel`
(gRPC) → `HomeViewModel.RefreshBatteryAsync` → the device card's battery pill / low-battery
notification. **No new code is needed to "get battery level" over the dongle — it is already
implemented and wired end-to-end**; what is still open is whether the `3300`–`4200` mV endpoints
match this specific battery's real empty/full curve (the code comment flags this: "until a
full-charge reference calibrates it") and whether `CMD_GetBATV` (`0x08`) works as an **active**
query over the **wired** cable — that combination has not been tested in this repo.

## Confirmed on hardware — 2.4G dongle read/write behaviour

Live-hardware pass with `ProoveGaming.Device.Testing`'s `TaipanDongleProbe` (2026-07-31), TAIPAN
connected via the 2.4G dongle, `ProoveGamingDeviceHost` stopped:

| Command | Direction | Result |
|---|---|---|
| `0x42` DPI table read | active query | **No reply** |
| `0x41` MsConfig read | active query | **No reply** |
| `0x08` battery query | active query | **No reply** (the documented opcode) |
| `0x48` battery query | active query | **Reply every time** — 3/3, verified 2026-08-07 |
| `0x43` button map read | active query | **No reply** |
| `0x03` button map write | active write | **Frame accepted on the wire (no transport error), but confirmed to have NO effect on the device** |

The write result comes from a distinguishing test, not just "no exception": a factory-default
rewrite is inconclusive on a mouse already at defaults (every button still behaved normally
afterward, which is equally consistent with "the write landed" and "the write silently no-op'd").
So a second write remapped **only** Button 3 (wheel/middle-click) to a consumer **Mute** key,
leaving every other slot untouched — an effect that would be unmistakable by hand (does the wheel
click mute system audio?) if the write landed. It did not: the wheel still behaved as a normal
middle-click, so the button-map write **did not reach the device**. The table was written back to
factory defaults afterward regardless, as a precaution.

> ### ⚠️ SUPERSEDED 2026-09-02 — configuration writes DO work over the 2.4G dongle
>
> The conclusion recorded below stood for four weeks and was **wrong**. The dongle was never
> refusing our writes; we were never asking correctly. The vendor's own Bus Hound capture of
> 2026-09-02 showed their driver bracketing every write with a handshake we had never sent:
>
> 1. `0x46` Para1 = `0x00` — **enter configuration mode**, retried until acknowledged.
> 2. …the table write, unchanged…
> 3. commit as **ONE** frame — ValidBankID carrying `driverPara4 = 0xD3` — not our two
>    (a `0xD2` ValidBankID followed by an empty `0xD3`).
> 4. `0x40` Para1 = `0xF3`, Para2 = a per-table bitmask — **apply**.
> 5. `0x46` Para1 = `0x01` — **exit configuration mode**.
>
> Confirmed on hardware the same day, over the dongle, with the cable unplugged: every frame of a
> button-table write is acknowledged, the commit answers `driverPara4 = 0xEE`, and the remap takes
> effect. The cable had always tolerated the missing handshake, which is exactly why this went
> unnoticed — the transport that worked hid the bug in the transport that did not.
>
> The framing lives in `TaipanConfigSession` and is pinned byte-for-byte against the vendor capture
> by `TaipanConfigSessionTests`. See [the configuration-mode session](#configuration-mode-session)
> below, and `taipan-button-write-reference-2026-09-01.md` for the full frame listings.
>
> **What this does not change:** reads (`0x41`/`0x42`/`0x43`) still return no table data on either
> transport. `0x48` battery still round-trips. The paragraph below is kept as the record of what was
> believed, and why — it was a reasonable reading of the evidence available at the time.

**Conclusion (2026-08-04, superseded — see the box above): button remapping (and, by the existing
DPI/MsConfig evidence, every other config write) does not work over the 2.4G dongle — reads AND
writes are both dead on this transport.**
This matched `TelinkMouseService`'s pre-existing assumption ("reads return null... writes no-op
over the dongle") and appeared to answer the vendor Q&A's T2 follow-up ("if 2.4G writes aren't
landing on your end..."). Only the mouse's own **unsolicited pushes** (`0x48` battery, `0x49` state)
were thought to cross the dongle in either direction; see
[What the App actually does](#what-the-app-actually-does--active-query-with-a-passive-fallback-implemented) above.

## Configuration-mode session

Every flash-table write (`0x01` MsConfig, `0x02` DPI, `0x03` buttons, `0x04` LED, `0x05` macros)
is bracketed by this sequence. The cable tolerates its absence; **the 2.4G dongle does not**.

| Step | Frame | Reply |
|---|---|---|
| Enter config mode | `0x46` Para1 `0x00` | `driverPara4 = 0xEE` |
| Table write | `0xD1` header (erases the sector) → `0xD2` chunks | per-frame echo, `driverPara4 = 0x00` |
| Commit | `cmd`, offset `0x007F`, len `0x01`, `driverPara4 = 0xD3`, payload `0x1A` | `driverPara4 = 0xEE` |
| Apply | `0x40` Para1 `0xF3` Para2 `<mask>` | `driverPara4 = 0xEE` |
| Exit config mode | `0x46` Para1 `0x01` | `driverPara4 = 0xEE` |

Apply masks: `0x01` MsConfig, `0x02` DPI, `0x04` button, `0x08` LED, `0x10` macro.

**`driverPara4 = 0xEE` on a reply means committed**, as distinct from the ordinary per-frame ACK,
which echoes the frame with `driverPara4 = 0x00`. Both appear in the vendor's capture.

The commit for **MsConfig carries no ValidBankID** — writing one puts `0x1A` far outside the 19-byte
block, the firmware rejects the transaction, and the sector is left erased (hardware-confirmed
2026-07-20). That is the "800 DPI + polling change ⇒ max DPI" bug.

> **Safety.** `0x06` / `0x46` is `CMD_SetMouseState`, and Para1 `0x00` suppresses the mouse's own
> reporting. A device left in configuration mode **stops working as a mouse** until it is replugged,
> so the exit frame must be sent on every path out of a write, exceptions included — see
> `TelinkMouseService.InConfigSessionAsync`, which sends it from a `finally`.

The handshake is retried: the vendor's driver needed five attempts ~170 ms apart, ours was
acknowledged on the second. A write proceeds even when the handshake is unacknowledged, because the
cable has always accepted one without it and refusing would turn a dongle fix into a cable
regression.

### One session per apply, not per table

**Entering configuration mode suppresses the mouse's own reporting, so every session is a visible
freeze** — about a second over the 2.4G dongle, and the factory software behaves the same way for a
button change. What the factory software does *not* do is freeze twice: its capture enters config
mode once, writes MsConfig, buttons, DPI and LED, applies them, and exits once.

Our first implementation opened a session per table, so a DPI change — which writes the stage table
*and* MsConfig for the active stage — froze the mouse **twice with a pause between**. Reported from
hardware 2026-09-02 and fixed the same day:

- `InConfigSessionAsync` is **nestable**. Only the outermost scope sends the enter/exit frames;
  inner scopes are no-ops. That lets the MsConfig path keep its read *outside* config mode when it
  runs standalone, at no cost when it runs inside an apply.
- `ApplyConfigurationAsync` acquires the device once and opens one session around every table it
  touches. Inner writes reuse the open stream through `_sharedStream` and take neither the device
  gate nor `_writeLock` — `SemaphoreSlim` is not re-entrant, so trying would deadlock.

Measured after the fix: a two-table apply (DPI stage table + MsConfig active stage) over the dongle
completes in ~1.6 s as **one** freeze.

### An interrupted `0xD1` sequence over the radio — vendor-acknowledged, unmitigated

Asked of the vendor 2026-09-02 and answered the same day: *"理论上是可能出现这个问题，但是我们现在做的
数据发送不完全，没有遇到过不公过的现象。"* — **"Theoretically this problem can occur"**, followed by
"the data sending we currently do is not complete, and we have not encountered any failure cases"
(`不公过` is a typo, most likely `不通过`/`不成功`).

So a sequenced flash write interrupted over the radio **is possible by the vendor's own account**.
What they did not answer is what the firmware then does with the half-written table — no rollback,
validity check or host-side recovery is described. Treat the failure mode as real and unhandled.

Weigh their "we have not encountered it" accordingly: their Bus Hound capture is a **wired** session,
and a wired write never crosses the radio, so cable-dominated testing would not exercise this.

Two observations on our side that may be instances of it, still unseparated:

- **The MsConfig sector reads back erased.** Over the dongle, `0x41` is silent OUTSIDE configuration
  mode and ANSWERS inside it (`driverPara4 = 0xEE`) — but with an all-`0xFF` payload, consistently
  across three runs. `0x42` stays silent inside config mode while `0x41` answers, so the `0xFF` is
  not a generic "nothing to give you" placeholder; it reads as a genuinely erased sector. Consequence
  for the App: `IsPlausible` rejects it, `ReadMsConfigAsync` returns null, and a cold-start MsConfig
  write falls through to `GetFactoryDefaultBlock()` — the vendor's example values, committed over
  whatever the user had.
- **Our MsConfig commit does not match theirs.** We send `05 A8 FA 01 00 00 00 D3` (no ValidBankID);
  their driver sends `05 28 FA 01 7F 00 01 D3 1A` — **with** ValidBankID at `0x007F`. Our no-BankID
  form comes from the 2026-07-20 finding that including one left the sector erased, but that
  predates configuration mode, so the correct form may be config-mode-dependent.

Either could explain the erased sector: an interrupted erase (which the vendor now says is possible)
or a commit that never lands. **A cable read of MsConfig separates them and is non-destructive** — a
real block means the `0xFF` is a radio artefact; all-`0xFF` on the cable too means the sector really
is erased. Do that before changing the commit form or the fallback policy.

### The app flag (`Para4`) on an 0x48 battery reply

Once the App has run a configuration-mode session, the device starts setting **`Para4 = 0x01`** (the
"app flag") on its `0x48` battery replies — and **computes the frame's checksum before setting it**,
so such a reply carries the checksum of the same frame with `Para4 = 0`.

The App's parser validated the checksum strictly, with `Para4` included, so every app-flagged reply
was rejected as corrupt and the battery indicator silently went blank. That bug had been present
since the parser was written; nothing had made the device raise the flag until configuration-mode
writes landed, so 2026-09-02 **exposed** it rather than causing it.

The two vendor artefacts disagree, and both are real:

| Source | Frame | Checksum includes `Para4`? |
|---|---|---|
| Documented worked example | `05 DA FA 48 68 10 01 01` | **yes** (0xDA) |
| 2026-09-02 capture, two consecutive replies | `05 49 fa 48 ea 03 00 00` and `… 00 01` | **no** — same checksum byte, different `Para4` |
| Our unit over the dongle | `05 EB FA 48 48 03 00 01` | **no** — 0xEB is the `Para4 = 0` value |

`TaipanStateReport` therefore accepts **either** form for the battery reply (`eitherPara4: true`).
Picking one alone throws away genuine readings from a device that demonstrably sends the other.
Config-write ACKs keep the strict rule — the commit reply `05 0D FA 03 7F 00 01 EE` validates only
with `Para4 = 0xEE`, so this relaxation is per-reply-type, not global.

Open question for the vendor: **is `Para4` meant to be inside or outside the battery checksum?** The
documentation and the shipped firmware disagree.

## Conflicts between vendor docs

Where the docs disagree, **the xlsx wins** — it is dated (2026-02-05) and explicitly describes
"current firmware"; the GM `.doc` is an older, generic 2.4G spec.

| Topic | GM `.doc` (older) | Telink USB xlsx/PDF (authoritative) |
|---|---|---|
| Function `0x1F` | Bluetooth / 2.4G mode switch | `LongMode1_Key` (short = CPI, long = polling rate) |
| Macro capacity | 6 slots × 600 B, 199 actions | 20 slots × 200 B, ~66 actions |
| Profiles | 0–3 (4) | 0–4 (5) |

The GM doc's per-function *number* table (`0x01` Left … `0x1B` LED) agrees with the xlsx and is a
useful cross-check.

## Open items — to confirm on hardware

1. **Per-axis DPI "double" flag — still open.** The xlsx annotates an X/Y "翻倍 (double)" byte per
   axis; the PDF address table shows plain `X_L/X_H/Y_L/Y_H`. The vendor Q&A (T7) re-confirmed a
   doubling flag exists but described it ambiguously — still two candidate byte layouts for the
   same 5-byte stride. Confirm by reading the DPI table back (`0x42`) from a real unit before
   writing it. See [taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) T7.
2. **Which physical buttons exist — partially resolved.** The table has 25 slots, but the factory
   map only populates 1–7 (+23/24). Hardware check (2026-07-31, hand-tested against the factory
   default map): Buttons 1–5 (Left/Right/Middle/Back/Forward) and Button 6 (DPI cycle) all work as
   documented. **Button 7 (polling-rate cycle) has no corresponding physical button on this unit**
   — it's a valid table slot with no hardware control mapped to it, so it can't be hand-verified
   either way. Still open: whether every TAIPAN unit lacks a physical polling-rate button, or just
   this one/this SKU variant.
3. **Report/interface selection — resolved, and since re-confirmed by exhaustive measurement.**
   Vendor-confirmed per-transport Usage/UsagePage/report lengths, see
   [Transports and USB identity](#transports-and-usb-identity) and
   [taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) T8.

   The vendor continued to suggest afterwards that wireless configuration failures were an
   interface-selection problem on our side, so on 2026-08-12 every interface the dongle exposes was
   enumerated and the full button-map write attempted on each (`taipan paths`, `taipan all`):

   | Interface | in/out | Write attempt |
   | --- | --- | --- |
   | `mi_00&col01` (mouse) | 7 / 0 | Cannot open — claimed exclusively by the Windows mouse driver |
   | `mi_00&col02` (consumer control) | 3 / 0 | Rejected — no output report |
   | `mi_00&col03` (system control) | 2 / 0 | Rejected — no output report |
   | **`mi_00&col04` (vendor)** | **33 / 33** | **Accepted** — the collection we already use |
   | `mi_01` (keyboard) | 9 / 2 | Cannot open — claimed exclusively by the Windows keyboard driver |

   **`mi_00&col04` is the only interface on the dongle capable of receiving a configuration write,
   and it is the one the App uses.** The exclusions come from the device's own descriptor, not from
   our filter: two collections are owned by Windows class drivers, and two have an output report
   length of 0, so a 33-byte frame cannot be sent to them at all. Interface choice is therefore
   eliminated as an explanation for
   [Configuration over the 2.4G dongle does not work](#configuration-over-the-24g-dongle-does-not-work).

   For contrast, the **wired** unit exposes a third USB interface the dongle does not:
   `mi_02&col01`, a vendor-defined collection at usage page `0xFFEF` with 64-byte in/out/feature
   reports. The App does not use it — cable configuration works through the 33/33 collection alone —
   but its absence over 2.4G is recorded here in case it turns out to matter.
4. **`CMD_SetProfile` (0x07) — resolved.** Vendor-confirmed not implemented in shipped firmware;
   the App never sends it. See Q&A T5.
5. **Battery percentage curve — resolved via a different mechanism than either documented option.**
   **Calibrated 2026-08-14 against the device's own percentage.** Over Bluetooth the mouse publishes
   its own level as the standard GATT Battery Service (`0x180F` / `0x2A19`) — the value Windows shows
   in its tray — which gives a reference the dongle path has no other way to obtain. Two paired
   samples ten minutes apart, discharging, on one unit:

   | raw (protocol, over dongle) | GATT % (device's own) | vendor curve said |
   | --- | --- | --- |
   | 987 | 100 % | 100 % |
   | 985 | **99 %** | 100 % |

   So the vendor's `985` full point is too low: it reports 100 % for a battery the device itself
   calls 99 %, which is why the app showed a permanent 100 % over the dongle while the tray showed
   99 %. `RawFull` is now **987**, the lowest raw observed at 100 %, which reproduces both samples
   exactly. This calibrates the TOP of the range only — both samples sit at 99–100 %, `RawEmpty`
   (750) is still the vendor's and unvalidated, and a lithium discharge curve is not linear, so
   mid-scale readings may still drift. More pairs as the battery drains would fix that: the
   DeviceHost logs the GATT percentage on Bluetooth and the raw value on the dongle, so a session on
   each transport at the same charge level yields a usable pair.

   **Why the Bluetooth and dongle percentages differ slightly (2026-08-16).** They are produced by
   different computations, not by different hardware readings: over Bluetooth the App shows the
   mouse's OWN percentage from the GATT Battery Service, while over the dongle it converts the raw
   voltage with the linear curve above. Paired samples, closest GATT reading to each dongle push:

   | dongle raw | our % | GATT % |
   | --- | --- | --- |
   | 973 | 94 | 96 |
   | 972 | 94 (was 93) | 94 |
   | 969 | 92 | 94 |

   Two effects. The first was ours and is fixed: the vendor's integer division truncates, reading
   low by up to a point — raw 972 works out at 93.67 and was reported as 93 — so `PercentFromRaw`
   now **rounds**. The second is the curve shape and remains: in that window the raw fell 973 → 969
   while GATT fell 96 % → 94 %, so the device's own curve is several times steeper here than a
   straight line. Solving for the `RawFull` each pair would need gives 982 / 986 / 986 / 983 — they
   disagree, which is what fitting a line to a curve looks like. Closing the remaining point or two
   needs paired samples across the whole discharge range, not more constants tuned near the top.

   **The dongle reports whether a mouse is behind it (2026-08-14).** The receiver is its own USB
   device and stays enumerated with no mouse on the radio, so "dongle present" never meant "mouse
   present". It does, however, answer a `0x48` request either way, and the answer distinguishes the
   two. Measured with `taipan off`, 8 requests per state on one unit:

   | State | Reply | Parsed |
   | --- | --- | --- |
   | Mouse ON | `05-73-FA-48-D0-03-…` | raw 976, 8/8 |
   | Mouse OFF | `05-24-FA-48-00-00-…` | raw 0, 8/8 |

   So **a zero reading, not silence, is the "no mouse" signal** — a positive per-request fact rather
   than something inferred from a timeout. The frame is otherwise well-formed (checksum included),
   so it must be told apart from garbage: `TaipanStateReport.IsMouseAbsentReply` validates the frame
   and checks for a zero reading, and `TaipanStateMonitor` drops the cached level when it sees one.
   Without that the App kept showing the last percentage the mouse ever reported after it was
   switched off, because the receiver never disappeared.

   **Over the CABLE the reading is unusable, and the App hides it (2026-08-14).** Measured on
   hardware minutes apart on one unit: at rest on the dongle raw was 979-982 (96-97 %); plugged into
   the cable the same mouse reported **raw 1002 with `usb=True`**, three requests running. A charge
   state cannot move 20 % in ten minutes — that is a **+22 raw charging offset** (~4008 mV under
   charge against ~3920 mV at rest), because the value is the cell's terminal VOLTAGE, which rises
   while current flows into it. So on the cable the percentage pins at 100 % regardless of the real
   level. TAIPAN's wired transport therefore declares `SupportsBattery: false` in `AllowedDevices`,
   and the App shows no battery for it — the dongle and Bluetooth are unaffected.

   Neither the doc's plain-mV `Para1/Para2` reading nor the vendor's T6 `750`–`985` formula matches
   what real hardware sends. The App doesn't use `CMD_GetBATV` as an active query at all — it reads
   the same `Para1/Para2` position off the mouse's own unsolicited `0x48` push and interprets it as
   quarter-millivolts against a generic Li-ion curve, calibrated from real captures. Already
   implemented and live in the App; see [What the App actually does](#what-the-app-actually-does--active-query-with-a-passive-fallback-implemented)
   above. Still open: whether the generic `3300`–`4200` mV curve endpoints match this battery's real
   capacity. Whether `0x08` works as an active query over the **wired** cable is still untested;
   over 2.4G it does not, but `0x48` does — see
   [What the App actually does](#what-the-app-actually-does--active-query-with-a-passive-fallback-implemented).
6. **Bluetooth transport — resolved (out of scope).** Vendor-confirmed: Bluetooth is not supported
   by their own stack at all, for reads or writes. See Q&A T2/T9.
7. **Active DPI stage byte (`MsConfig 0x0011`) lags the physical button by one press** —
   vendor-confirmed to exist (Q&A T3), but the vendor did not explain when the byte actually
   updates; still needs a direct re-ask. Don't rely on a fresh read of `0x0011` for "current stage
   right now" — use the device-pushed `Report_MouseState` (`0x49`) instead.
8. **No reliable device-side factory reset.** Vendor-confirmed (Q&A T10): `CMD_Reset`
   `0xF3`/`0x80` has no dedicated acknowledgement and isn't guaranteed to restore every table.
   A "reset to default" feature, if we want one, must be App-side — push a canonical default
   profile through the normal write path, the same way BLAZE's reset was resolved
   ([blaze-vendor-qa-2026-07-27.md](../blaze/blaze-vendor-qa-2026-07-27.md) item A). Not yet
   implemented for TAIPAN.
9. **Cold-start apply does nothing until the DPI button is pressed once — three contributors
   found and fixed in code, awaiting hardware confirmation.** Right after the app starts, EVERY
   apply (DPI and otherwise) can silently do nothing to a TAIPAN until the user presses the
   physical DPI button once, after which applies work normally for the rest of the session; resets
   on every restart. Two earlier hypotheses (read-timing budgets; the App's `_dpiStageKnown` gate)
   were tried/considered and ruled out — see the `taipan-cold-start-msconfig` memory entry for the
   full history.

   **First contributor (2026-07-30, still in place):** `TelinkMouseService.ReadReply` blocked on a
   **synchronous** `stream.Read` while holding `_writeLock` — the lock shared by every write for
   that unit — nominally bounded by `ReadTimeout`/`ReadWindowMs` but not provably so if the HID
   driver doesn't honour that timeout. A stuck read would hold the lock hostage forever, explaining
   why ALL writes (not just DPI) stall, and why a physical button press (which makes the device
   emit *some* input report) unblocks it. Fixed by racing that read against a hard outer `~1.75s`
   deadline (`ReadTableAsync` in `TelinkMouseService.cs`) so the lock can never be held past it
   regardless of the underlying driver's behaviour.

   That alone didn't fully resolve it. **Second contributor (2026-07-31):**
   `TaipanStateMonitor.RequestStateRefresh` fires once per App connect — effectively at app
   startup — and calls `TryInferActiveDpiStageAsync`, a display-only bootstrap guess at the
   current stage. On a cold/unresponsive mouse (not yet "woken" by a button press) this call was
   still doing the **full `ReadRetries` (3×) MsConfig read budget** plus a DPI-table-header read,
   holding `_writeLock` for up to ~8s total. The App's own real preset-apply writes (e.g.
   `EnsureDefaultPresetAppliedAsync`, which fires as soon as Device Details opens — often within
   seconds of launch) queue behind whatever holds that lock, so a real apply could sit blocked for
   several seconds right after startup. Fixed by giving `TryInferActiveDpiStageAsync` a
   single-attempt read (`ReadMsConfigAsync(..., retries: 1)`) instead of the full retry budget
   meant for real writes — a wrong guess here is harmless and self-corrects on the first real push
   or user action.

   That still wasn't the whole story — reported as still fully broken afterward. **Third
   contributor / root cause (2026-07-31):** `PatchMsConfigAsync` (the read-modify-write behind
   active stage, polling rate, and lift-off) needs a real MsConfig block to patch onto — from a
   fresh read, or from `_lastMsConfig` (only ever populated by a previous successful read or
   write, this session). Over the 2.4G dongle, reads **never** succeed at all (confirmed
   separately — see [taipan-vendor-qa-2026-07-30.md](taipan-vendor-qa-2026-07-30.md) T2/T9 and the
   dongle probe testing). So on a genuinely cold session — no prior read, no prior write,
   `_lastMsConfig` still null — `ResolveSourceBlock` had nothing to offer, and the write was
   **silently skipped every single time**, forever, regardless of read-retry tuning or lock
   contention: there was no amount of waiting that would fix it, because nothing was ever
   "stuck" — the write just never happened. A physical DPI-button press didn't unblock
   anything; it only happened to precede the next apply attempt, which is what made it look like
   the click was the fix. Fixed by falling back one step further when there is neither a read nor
   a cache: build the write from the vendor's own worked full-write example
   (`TaipanMsConfig.GetFactoryDefaultBlock()`) instead of giving up, and only when doing so can't
   silently move the device's real DPI stage (`IsFactoryDefaultFallbackSafe`) — otherwise the write
   is skipped rather than committing a fabricated stage. A real write — even one where untouched
   fields (wheel speed, DPI-LED behaviour, profile switch/index) land on the vendor's example value
   instead of the device's actual one — beats silently applying nothing. **Not yet confirmed on
   hardware.**

   **Scope note (2026-07-31):** this whole cold-start bug — and all three fixes above — was
   observed and reasoned about in terms of *timing/locking*, which only makes sense for a
   transport where writes actually land. Item 10 below confirms the 2.4G dongle's writes don't
   land **at all**, timing aside — so the factory-default fallback in the third contributor's fix
   cannot repair a cold-start apply over the dongle even when it fires: falling back to a blind
   write just swaps "silently skip the write" for "silently send a write the device silently
   ignores." This item's fixes matter for the **wired** cable; over the dongle, config writes need
   a different resolution entirely (see item 10).
10. **2.4G dongle: reads AND writes are both confirmed dead — resolved (negative result).**
    Direct hardware test (2026-07-31, `TaipanDongleProbe.RunMuteDistinguishTest`): remapped Button 3
    (wheel-click) to Mute, wrote it over the dongle with no transport error, and confirmed by hand
    that the wheel still behaved as a normal middle-click — the write never reached the device.
    Combined with the read side (dead for `0x41`/`0x42`/`0x43`/`0x08` — though note `0x48`
    battery DOES reply, so "reads are dead" is now known to be too broad; all confirmed the
    same session), this appeared to settle the vendor Q&A's T2 follow-up: **this unit's 2.4G dongle
    does not accept host→device configuration writes at all.**
    **SUPERSEDED 2026-09-02** — it does. The writes were never framed the way the firmware expects
    over the radio: they were missing the enter/exit configuration-mode handshake, used a two-frame
    commit instead of one, and never sent the apply command. With the correct framing
    (`TaipanConfigSession`) a button-table write is acknowledged frame by frame over the dongle and
    takes effect. The vendor's claim that "the protocol is identical across transports" was right;
    our framing was incomplete, and only the cable was forgiving enough to hide it.

## Terms

See [`docs/CONTEXT.md`](../../CONTEXT.md) for **TAIPAN**, **Telink**, `BtFuncNum`, **ValidBankID**.

## Configuration over the 2.4G dongle — SUPERSEDED 2026-09-02, it works

> **This section's original conclusion was wrong and is kept only as the record of it.** Config
> writes DO work over the dongle; they need the [configuration-mode session](#configuration-mode-session)
> the vendor's driver uses. Hardware-confirmed 2026-09-02: every frame acknowledged, commit answers
> `0xEE`, remap takes effect. Reads (`0x41`/`0x42`/`0x43`) are still silent — that part still holds.

Hardware-tested 2026-08-04 with the cable unplugged and the mouse live on the dongle: the receiver
relays the mouse's own `0x48`/`0x49` pushes, but **host→mouse configuration frames never take
effect**, and every read (`0x41`/`0x42`/`0x43`/`0x08`) is silent on every dongle collection.
*(The write half of this is what 2026-09-02 overturned: the frames were reaching the mouse, but
without the enter-config-mode handshake it neither acknowledged nor applied them.)*

This was retested because the vendor's T2 answer states that wired and 2.4G share one protocol and
both support writes. It still fails when the frames are made byte-identical to the vendor's own USB
capture — including their per-field chunking and their `ValidBankId`-carries-the-`0xD3` commit,
which differ from what `TelinkMouseService` sends over the cable.

Full evidence package, for the vendor follow-up they offered:
`docs/mouse/taipan/taipan-dongle-config-findings-2026-08-04.md`.

Practical consequence, unchanged: **configuration requires the cable.** Bluetooth is confirmed
unsupported for configuration by the vendor (T2), so the cable is the only write path.

## Link state and battery over each transport (hardware-measured 2026-08-04)

Measured with `TaipanDongleProbe.RunLinkStateProbe` while cycling the mouse's physical switch
through OFF → 2.4G → Bluetooth → OFF → 2.4G.

| Action | What the host sees |
| --- | --- |
| Switch **OFF** (from 2.4G) | **Nothing at all.** The receiver's HID interfaces stay enumerated, no report arrives |
| Switch to **2.4G** | A `0x48` battery push arrives within a few seconds |
| Switch to **Bluetooth** | Four new HID collections **appear**, under a *different* PID — `0x8266`, over BLE |
| Switch **OFF** (from Bluetooth) | Those BLE collections **disappear** |

### Consequences

**There is no liveness signal over the 2.4G dongle.** The receiver is a USB device in its own right
and stays enumerated whether or not a mouse is talking to it, so "the receiver is present" cannot be
read as "the mouse is connected" — a mouse switched fully off is indistinguishable from a mouse in
use. Bluetooth does not have this problem: the BLE device appears and disappears with the link.

**Battery only arrives on 2.4G link establishment.** Confirmed: a `0x48` push follows every switch
into 2.4G, and nothing arrives otherwise. So a host that starts with the link already up sees no
battery at all until something re-links. This matches the vendor's T4 answer that the state report
cannot be polled.

Two real pushes captured, both checksum-valid:

```
05 79 FA 48 DA 03 00 00 …   raw = 0x03DA = 986
05 7D FA 48 D6 03 00 00 …   raw = 0x03D6 = 982
```

Under the vendor's T6 curve these are **100 %** and **98 %** — consistent with a charged battery and
with their own worked example (981 → 98 %). Under the previous quarter-millivolt curve they would
have read 71 % and 69 %, which is the under-reporting that curve was replaced for. Real-hardware
confirmation that the T6 formula is the correct one.

USB flag (`Para3`) is `0` in both, correctly reflecting no cable. Payload byte 10 is `0x01` in both;
its meaning is undocumented and it is not parsed.

### Bluetooth uses a different Product ID

In Bluetooth mode the mouse enumerates as **PID `0x8266` (33382)**, not the dongle's `0x8373`. The
catalog had the Bluetooth transport configured with `33651` (`0x8373`), so the App could never match
a TAIPAN over Bluetooth at all. Corrected in `appsettings.json`.

The BLE interface also exposes a 33/33 driver collection, the same shape as the wired and 2.4G
config channels — but the vendor states plainly (T2/T9) that the shipping firmware does not support
configuration over Bluetooth, so this is recorded, not relied on.
## Cold-start writes need a known DPI stage

`TelinkMouseService.PatchMsConfigAsync` reads the MsConfig block, patches it, and writes it back.
When there is **neither a usable read nor a cached block** — the normal state at app start, and the
permanent state over the 2.4G dongle, which answers no reads — it can only fall back to the vendor's
factory-default block. Every field in that block is the vendor's example value, including the DPI
stage, so writing it would silently move the user's real DPI.

`TaipanMsConfig.IsFactoryDefaultFallbackSafe` therefore allows that fallback **only** when the patch
itself sets the DPI stage, or when the host already knows the live stage. `_liveDpiStage` has exactly
two authoritative writers: a stage the host wrote itself, and the device's `0x49` push — which the
mouse only sends on a **physical DPI-button press**.

The practical consequence: **a rate-only (or lift-off-only) write is refused at cold start.** The
observable symptom is a setting that appears stuck until the user presses the physical DPI button,
which is simply the moment the stage becomes known.

So any App-side write of a single MsConfig field must either run after the stage has been asserted
(`AssertActiveDpiStageAsync`), or be folded into the coordinated `ApplyDeviceConfiguration` call that
carries the stage alongside it. The coordinated call is the safe default — it is also the only write
shape BLAZE tolerates (see `docs/mouse/blaze`).

Regression test: `DeviceDetailsPresetApplyTests.PresetApply_DoesNotIssueABareReportRateWrite`.
