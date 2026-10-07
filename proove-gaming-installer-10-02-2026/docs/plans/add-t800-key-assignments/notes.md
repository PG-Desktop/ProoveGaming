# T800 key matrix — Phase 0 reconnaissance

Output of the Phase 0 gate in [`add-t800-key-assignments-plan.md`](./add-t800-key-assignments-plan.md).
Measured against a physically connected T800 (VID `0x3151` / PID `0x502D`) with
`dotnet run --project ./src/ProoveGaming.Device.Testing -- keys`.

**Status: R0–R5 answered. Writes are confirmed working on hardware.**

## R0 — does `GET_KEYMATRIX (0x8A)` answer? **Yes**

Request framing that works:

```
0x8A | Profile=0 | Type=255 | Page | Index=0 | 00 | 00 | CS
CS = 0xFF - (sum of bytes 0..6 & 0xFF)      (the Bit7 variant)
```

Replies carry real data rather than the driver's cached echo of the request. The echo check matters
on this device — a rejected command is silent, and `GetFeature` then returns our own bytes back,
which reads as a plausible reply. The probe compares request against reply and labels the difference.

## R2 — page geometry: **8 pages of 64 bytes = 512 bytes**

The vendor spec contradicts itself here (`0x0A` says 512/56 = 10 pages; `0x8A` says "504/56, 9
pages" in one place and "8 pages of 64 bytes" in another). Measured answer: **8 pages of 64**.

Critically, a `0x8A` reply is **64 bytes of raw matrix data with no echoed header** — the first
record starts at protocol byte 0, not after an 8-byte header. This was the first wrong assumption
of the run: reading the payload from byte 8 produced data that still looked plausible (the stride
survives inside a page) but was misaligned across page boundaries. Page 0 begins:

```
00 00 00 00 | 00 00 29 00 | 00 00 2B 00 | 00 00 39 00
   (empty)   |   Escape     |    Tab      |  CapsLock
```

Pages 6 and 7 are entirely zero — the board uses 96 of the 128 slots.

## R1 — record encoding: **4 bytes, usage at index 2**

Determined by scanning every (stride, offset) pair and counting how many of the 26 letters land on
it. Only one combination collects all 26:

```
stride 3 offset 0:  7/26        stride 4 offset 0:  1/26
stride 3 offset 1: 10/26        stride 4 offset 1:  0/26
stride 3 offset 2:  9/26        stride 4 offset 2: 26/26   <== all 26
                                stride 4 offset 3:  0/26
```

So a record is `[Type, Code1, Code2, Code3]`, 512 / 4 = **128 slots**:

| Kind | Bytes | Example |
|---|---|---|
| Plain keyboard key | `00 00 <usage> 00` | Escape = `00 00 29 00` |
| FN | `0A 01 00 00` | slot 65 |
| (spec) multimedia | `03 00 <usage> 00` | Scan Next Track = `03 00 B5 00` |
| (spec) macro | `09 <type> <index> 00` | — |

This matches the YC3123 action table exactly, which is the independent confirmation that the decode
is right rather than merely self-consistent.

### Two differences from Cyrex that matter

1. **`Code3` is always `00`** in every one of the 68 factory records. The App's shared
   `KeyMatrixRecord` is a 3-byte `{Type, CodeLow, CodeHigh}`; T800 records map onto it as
   `Type→Type, Code1→CodeLow, Code2→CodeHigh` with the fourth byte an implementation detail of the
   service. **This resolves the plan's biggest risk in the favourable direction — the shared model
   does not have to change.** The service must still write the fourth byte as `0x00`.
2. **Modifiers are ordinary HID usages `0xE0`–`0xE7`, not a bitmask.** Cyrex stores Left Shift as
   `{0x10, 0x02, 0x00}` — mask set, usage zero — and is therefore identified by the (mask, usage)
   pair. T800 stores it as `00 00 E1 00`, i.e. usage `0xE1` and no mask. So on this board a key
   **is** identified by its usage code alone, and `T800KeyCatalog` must leave `ModifierMask` at 0
   and put the modifier's own usage in `HidUsageCode`. Copying Cyrex's catalog convention here
   would produce 8 keys that never match.

Note also that a plain key's type byte is `0x00`, **not** Cyrex's `0x10`. Anything that assumes
`TypeKeyboardKey == 0x10` (e.g. `KeyMatrixRecord.IsUnusedSlot`, `IsPlausibleKeyTable`) is wrong for
this family and must not be reused unchanged.

## R3 — slot → physical key: **68 keys, cross-check passes**

The matrix is **column-major on a 6-row grid**: `column = slot / 6`, `row = slot % 6`. Row 0 of
every column is empty — that is the function row this 65% board does not have. 16 columns × 6 rows
= 96 addressed slots, 68 of them real.

| Row | Count | Keys |
|---|---|---|
| 1 | 15 | Escape, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0, Minus, Equal, Backspace, Home |
| 2 | 15 | Tab, Q, W, E, R, T, Y, U, I, O, P, BracketLeft, BracketRight, Backslash, Delete |
| 3 | 14 | CapsLock, A, S, D, F, G, H, J, K, L, Semicolon, Quote, Enter, PageUp |
| 4 | 14 | LeftShift, Z, X, C, V, B, N, M, Comma, Period, Slash, RightShift, Up, PageDown |
| 5 | 10 | LeftCtrl, LeftWin, LeftAlt, Space, RightAlt, FN, RightCtrl, Left, Down, Right |

**Row counts 15 / 15 / 14 / 14 / 10 = 68 — identical to the connected-component pass over
`PG_T801_keys.svg`.** That is the plan's stated cross-check for R3, and it passes exactly. Two
independent sources (firmware table and artwork pixels) agree on the key set and its row structure.

FN is at **slot 65** and must be excluded from the remappable catalog, the same as Cyrex's slot 70.
That leaves **67 remappable keys**.

### Full factory table

```
slot   1  00 00 29 00  Escape        slot  49  00 00 25 00  8
slot   2  00 00 2B 00  Tab           slot  50  00 00 0C 00  I
slot   3  00 00 39 00  CapsLock      slot  51  00 00 0E 00  K
slot   4  00 00 E1 00  LeftShift     slot  52  00 00 10 00  M
slot   5  00 00 E0 00  LeftCtrl      slot  55  00 00 26 00  9
slot   7  00 00 1E 00  1             slot  56  00 00 12 00  O
slot   8  00 00 14 00  Q             slot  57  00 00 0F 00  L
slot   9  00 00 04 00  A             slot  58  00 00 36 00  Comma
slot  11  00 00 E3 00  LeftWin       slot  59  00 00 E6 00  RightAlt
slot  13  00 00 1F 00  2             slot  61  00 00 27 00  0
slot  14  00 00 1A 00  W             slot  62  00 00 13 00  P
slot  15  00 00 16 00  S             slot  63  00 00 33 00  Semicolon
slot  16  00 00 1D 00  Z             slot  64  00 00 37 00  Period
slot  17  00 00 E2 00  LeftAlt       slot  65  0A 01 00 00  FN
slot  19  00 00 20 00  3             slot  67  00 00 2D 00  Minus
slot  20  00 00 08 00  E             slot  68  00 00 2F 00  BracketLeft
slot  21  00 00 07 00  D             slot  69  00 00 34 00  Quote
slot  22  00 00 1B 00  X             slot  70  00 00 38 00  Slash
slot  25  00 00 21 00  4             slot  71  00 00 E4 00  RightCtrl
slot  26  00 00 15 00  R             slot  73  00 00 2E 00  Equal
slot  27  00 00 09 00  F             slot  74  00 00 30 00  BracketRight
slot  28  00 00 06 00  C             slot  76  00 00 E5 00  RightShift
slot  31  00 00 22 00  5             slot  77  00 00 50 00  Left
slot  32  00 00 17 00  T             slot  79  00 00 2A 00  Backspace
slot  33  00 00 0A 00  G             slot  80  00 00 31 00  Backslash
slot  34  00 00 19 00  V             slot  81  00 00 28 00  Enter
slot  37  00 00 23 00  6             slot  82  00 00 52 00  Up
slot  38  00 00 1C 00  Y             slot  83  00 00 51 00  Down
slot  39  00 00 0B 00  H             slot  85  00 00 4A 00  Home
slot  40  00 00 05 00  B             slot  86  00 00 4C 00  Delete
slot  41  00 00 2C 00  Space         slot  87  00 00 4B 00  PageUp
slot  43  00 00 24 00  7             slot  88  00 00 4E 00  PageDown
slot  44  00 00 18 00  U             slot  89  00 00 4F 00  Right
slot  45  00 00 0D 00  J
slot  46  00 00 11 00  N
```

Note the board has **Home** (slot 85) where Cyrex has Delete in that position — T800's right column
is Home / Delete / PageUp / PageDown, so its catalog is genuinely its own, not a Cyrex clone.

## R4 — does a write stick? **Yes, and single-key writes are surgical**

Confirmed on hardware with `dotnet run --project ./src/ProoveGaming.Device.Testing -- keys write`.

The important finding is not just that writes persist, but that the **single-key form works** —
`Type` set to the slot number instead of 255 rewrites one key and provably touches nothing else:

```
slot 71 = 00 00 E4 00 (RightCtrl), will try -> 00 00 E6 00 (RightAlt)

SET_KEYMATRIX single   0A 00 47 00 04 01 00 A9 00 00 E6 00
diff: 1 slot(s) changed
  slot  71 -> 00 00 E6 00   <== the one we asked for

restore                0A 00 47 00 04 01 00 A9 00 00 E4 00
restore verified: the matrix is byte-for-byte back to its original state.
```

Framing that works:

```
0x0A | Profile=0 | Type=slot | Page=0 | Len=4 | FlashEnable=1 | Index=0 | CS | record(4 bytes)
CS = 0xFF - (sum of bytes 0..6 & 0xFF)      (the Bit7 variant)
```

**This removes the whole-matrix read-modify-write from the design.** Smart Infinite needs it because
its firmware only accepts a whole-table write; RY5088 does not. That deletes the plan's largest
risk — a malformed 512-byte write scrambling every key — along with the plausibility guard and the
abort-on-failed-read dance built around it. The service reads only to *show* current assignments;
it never needs a read to *write* safely.

`FlashEnable=1` is sent on the single packet. Persistence across a replug is not covered by the
matrix read-back above and is left to the QA checklist.

## R5 — pacing: **not a constraint on this path**

The pacing risk in the ROYUAN notes is about pushing many *pages* in a burst. A single-key remap is
one packet, so there is no burst to pace. The read path (8 pages, 20 ms between transfers) ran
repeatedly without a failure, and the write/read/restore/read cycle above completed cleanly at the
same cadence.

Worth carrying into the service anyway: every accepted write with `FlashEnable=1` writes flash, so
a UI that fires a write per keystroke of a slider-like interaction would wear it. Key remapping is
one write per user action, which is fine.

## Answers to the plan's Open Questions

| # | Question | Answer |
|---|---|---|
| 2 | Does the fourth record byte carry meaning? | **No** — `Code3` is `0x00` in all 68 factory records. `KeyMatrixRecord` stays a 3-byte type; the service writes the fourth byte as zero. Risk closed. |
| 3 | Profile handling | Profile 0 holds a fully populated, sane factory table, so pinning Profile 0 for reads is correct. Whether the board can *boot* on a different profile is still unverified — if it can, a remap written to profile 0 would appear not to work. |
| 1 | Write timing — immediate or staged? | **Still open.** A product decision, not answerable by reconnaissance. |
| — | Is a whole-matrix write needed? | **No.** Single-key writes work and are surgical (R4), so the read-modify-write Smart Infinite requires does not apply here. |
