# BLAZE / X43 — Macros

The BLAZE (Easen HT571, classic X43 protocol) supports **hardware macros**: a timed
sequence of key make/break events stored in device flash and assignable to a button.
Once uploaded, the macro plays on the device with no app involvement.

Source: [`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) §III (macro data
format), the `0x04`/`0x84` commands, and §II/§V (key-matrix mode `0x05`).

## Macro data format (§III)

A macro is a byte buffer:

```
[repeat_lo][repeat_hi]  [attr1][code1]  [attr2][code2]  …  [00][00]
```

- **Repeat count** — first 2 bytes, little-endian, 1–65535.
- **Events** — pairs of `[Attribute, KeyCode]`:
  - `Attribute` bit 7 = **make (0) / break (1)** — i.e. press vs release.
  - `Attribute` bits 0–6 = **delay after this event**, ×10 ms → 0.01–1.27 s (`0` = go
    immediately to the next event).
  - `KeyCode` = a key from the FLASH key-code table (keyboard keys, mouse buttons).
- **Terminator** — `00 00`.
- **Special codes** for relative movement inside a macro: `F5`/`F6` wheel, `F7`/`F8`
  AC-pan (horizontal scroll), `F9` 8-bit X, `FA` 8-bit Y — each followed by its value.
- **Not allowed**: multimedia/consumer keys.

Spec example — "1 2 3" ×2 (`1`=0x1E, `2`=0x1F, `3`=0x20):
```
00 02  01 1E  81 1E  01 1F  81 1F  01 20  81 20  00 00
└repeat┘ └P 1┘ └R 1┘ └P 2┘ └R 2┘ └P 3┘ └R 3┘ └end┘
(attr 0x01 = make + 10 ms delay; 0x81 = break + 10 ms delay)
```

## Repeat mode (chosen at bind time — the "macro type")

Set in the key-matrix binding, not the macro data:

| Type | Behaviour |
|------|-----------|
| `0x00` | Play `repeat` times; **any** button press stops it early. |
| `0x01` | Loop forever (ignores the repeat count); any button press stops. |
| `0x02` | **Loop while the assigned button is held** (release to stop). |

## Device operations

1. **Upload a macro** — command **`0x04` Set Macro Key**:
   header `[04, profile, macroIndex(0–47), 0, 0, 0, 02, cs]` then **128 bytes** of macro
   data on the interrupt-OUT endpoint. Up to **48 macros** in flash (~63 events each, from
   the 128-byte buffer). This is a *separate* flash buffer from param_0/param_1.
2. **Assign to a button** — a key-matrix entry with **mode `0x05`**:
   `[macroIndex, 00, macroType, 05]` written to the button's slot. This is the **same
   block-2 write we already do for button remapping** (see [key-matrix.md](key-matrix.md)) —
   binding a macro is just a new action type in the encoder that points at a macro index.

## CONFIRMED wire protocol (VS750 capture 2026-07-12)

Captured VS750 recording "1 2 3" and assigning it to the Forward button
(`cap-12.07.2026-macros-assignement.pcapng`). Ground truth:

**Upload — command `0x04`:**
```
header (SET_REPORT): 04 00 03 00 00 00 00 00     ← [04, profile, macroIndex, 0,0,0, 0, cs]
                     └ NOTE byte6 = 00 (NOT the "times=2" the generic spec shows)
data (2 × 64-byte interrupt-OUT = 128 bytes):
  01 00                                            ← repeat count, LITTLE-ENDIAN (=1)
  01 1E 01 1F 01 20  81 1E 81 1F 81 20             ← press 1,2,3 then release 1,2,3
  01 1E 01 1F 01 20  81 1E 81 1F 81 20             ← (this macro recorded the run twice)
  00 00                                            ← terminator, then zero padding
```
- `[attr, code]` per event: `attr` bit 7 = press(0)/release(1), low 7 bits = delay ×10 ms.
- The **repeat field is little-endian** `[lo, hi]` (so `01 00` = 1) — the spec's *format
  table* is right; its worked *example* (`00 02`) is misleading, ignore it.
- Header **byte 6 is `00`**, not `02`. Setting it to `02` makes the upload fail (that was
  the bug in the first spike).

**Bind — key matrix (block 2), mode `0x05`:**
```
KEY3 (Forward slot) = 03 00 01 05   ← [Index=03, 00, Type=01, mode=05]
```
So the entry is `[macroIndex, 00, macroType, 05]`. VS750 used **type `0x01`**. Confirmed:
mode `0x05` = macro, and macro binding reuses the normal block-2 key-matrix write.

The spike (`src/ProoveGaming.Device.Testing/Program.cs`) now replicates these bytes exactly.

## App integration notes (2026-07-12)

- **Every button write rebuilds the whole key matrix** (`EasenMouseService` starts from
  `CreateDefaultKeyMatrix()`), so a macro-bound slot is reverted to default unless *that
  write* re-includes the slot **with its full macro definition**. The App must therefore
  remember which macro is on which button and re-send it on every `SetButtonBindings`.
- **The App SQLite is the single source of truth for button assignments** (`button_bindings`
  table: `(vid, pid, button) → action, macro_id`). The DeviceHost no longer keeps its own
  copy — `SetButtonBindings` just writes the device; the old `button-bindings.json` /
  `ButtonBindingStore.SaveAsync` path is retired. On load the App reads `button_bindings`
  directly (no `GetButtonBindings` host round-trip), which is what reflects the action AND the
  specific macro, and lets it re-send the macro definition on the next write. Note this store
  is per-user (`%LocalAppData%`), where the host JSON was machine-wide (`%ProgramData%`).
- **Min 1 delay unit per event.** The encoder clamps every step to ≥1 unit; 0-delay
  make/break events can replay too fast for the target to register.
- **Delay unit is 10 ms, but the top of the range misbehaves.** Normal delays play at the
  right speed with value × 10 ms (matching the spec). BUT a step at the max (127 units,
  `0x7F`) played back as **~63 s** instead of 1.27 s. So `EasenMouseService` keeps the 10 ms
  base (`MacroDelayUnitMs = 10`) and caps the delay at `MaxMacroDelayUnits = 100` (≈1 s),
  staying below the misbehaving high values; the recorder caps recorded pauses to 1 s to match.
  (An earlier attempt to treat the base as ≈500 ms made all delays ~50× too fast — the base is
  10 ms; only the near-max values are pathological.) **TODO:** find the exact bad threshold.
- **Recorder captures keyboard AND mouse clicks.** The Create Macro dialog records keyboard
  make/break (HID codes via `HidKeyCodeMap`) and mouse-button make/break (device codes F0–F4,
  mapped in `MainWindow.TryMapMouseButton`). While recording, the window swallows every
  click via a tunnel `PointerPressed`/`PointerReleased` handler EXCEPT on the record button
  (`MacroRecordButton`), so STOP always works; to cancel/edit, stop recording first.
- **Macro `type` byte — resolved to `0x01`.** A play-count macro that uploaded + bound fine
  still did nothing on press while bound with type `0x00`; the button lost its default
  function but no macro played. `0x00` ("play N times; any button press stops it early")
  self-cancels on the triggering press. `EasenMouseService.MacroTypeByte` now sends `0x01`
  for a normal macro (matching the OEM capture) and `0x02` for "repeat until released".

## Caveats specific to BLAZE

- **Command confirmed `0x04`** (above). The OEM exe's `0x13 SetMacroKey` / `0x04 SetGunData`
  symbols are for other MCU families and do **not** apply to our PID.
- **Write-only → host-persisted.** Like the key matrix, macro data almost certainly can't
  be read back (feature reads return zeros; no 64-byte input endpoint). Store macros
  host-side (same pattern as button bindings) — the device is a write target.
- **Brick risk.** *Binding* a macro is a key-matrix (block 2) write — the brick-prone op;
  keep `X43Mouse.RestoreAll()` staged. The *upload* (`0x04`) writes a different flash
  buffer (lower risk, but also untested).
- **Limits.** Keyboard keys + mouse buttons + wheel/X/Y; **no** multimedia keys; per-step
  delay 10 ms–1.27 s; ~63 events per macro; 48 macros max.

## How it maps to our UI

The half-built macro UI already matches the protocol:

| UI field | Maps to |
|----------|---------|
| Name | app-side label only (device stores by index) |
| Repetitions | the 2-byte repeat count |
| Repeat until released | macro **type `0x02`** |
| Auto-record delays | capture inter-event timing into the Attribute delay bits (vs no delay) |

What's missing to make it real: an actual **key-event recorder** (capture events +
timing), an **encoder** to the byte format, a **`SetMacro` command** (`0x04`) in
`EasenMouseService`, a **"Macro N" button action** (mode `0x05`), and **host persistence**.
See the implementation plan: [`docs/plans/add-mouse-macros/add-mouse-macros-plan.md`](../../plans/add-mouse-macros/add-mouse-macros-plan.md).
