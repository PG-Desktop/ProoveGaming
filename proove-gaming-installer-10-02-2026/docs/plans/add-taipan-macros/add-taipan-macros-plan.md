# Plan: TAIPAN macros — record, upload, bind (USB)

## Goal

Let a user assign a recorded macro to a TAIPAN button, exactly as they already can on BLAZE. The
whole App pipeline (recorder, SQLite library, gRPC `ButtonBinding.Macro`) is device-agnostic and
already delivers the macro to `TelinkMouseService.SetButtonBindingsAsync` — which currently skips
it. This slice encodes the macro into TAIPAN's on-wire format, uploads it to a device macro slot
(command `0x05`), and binds the button to that slot (function `0x91`). Config writes are
**cable-only** on this hardware (the dongle does not relay host→mouse frames — established during
the buttons/battery work), so macros carry the same USB-only constraint as button remaps.

## Scope

**In:**
- Pure macro encoder for the Telink 3-byte-action format (`TaipanMacroEncoder`) + unit tests.
- Macro-bind entry encoding (function `0x91` + repeat-mode params) in `TaipanButtonMap` + tests.
- Macro upload + bind in `TelinkMouseService.SetButtonBindingsAsync` (write sequence
  `0x05`: `0xD1` → `0xD2` chunks → ValidBankID at `0x0FFF` → `0xD3`).
- `SupportsMacros: true` on the three PG TAIPAN config entries (surfaces the Macros UI section).
- Device-aware recorder event cap (TAIPAN fits fewer events per macro than BLAZE).

**Out:**
- Dongle/Bluetooth macro writes (hardware limitation, same as buttons).
- TAIPAN-only macro action types the recorder can't produce (mouse XY moves `0x02/0x08/0x09`,
  wheel `0x03`, consumer keys `0x05`, in-slot loop `0x07`).
- Any recorder/library UI change beyond the event cap.
- Reading macros back from the device (`0x45`) — the App library stays the source of truth.

## Protocol facts (from `docs/mouse/taipan/protocol.md`, confirmed-on-hardware framing)

- Command `0x05` write / `0x45` read. **20 slots × 200 bytes**; slot *k* starts at table offset
  `k × 200`. Commit = ValidBankID `0x1A` at offset **`0x0FFF`** (not `0x007F`), then `0xD3`.
- One action = 3 bytes `[Action_Type, Data_L, Data_H]`. `Action_Type` low nibble = type,
  **bit7 = 1 Make (press), 0 Break (release)**. End marker `0x00` (or `0xFF`).
- Types used by this slice: `0x01` mouse button (`Data_L` bitmask: `0x01` L, `0x02` R, `0x04` M,
  `0x08` B4/Back, `0x10` B5/Forward), `0x04` keyboard HID key (`Data_L` = usage, `0xE0–0xE7` =
  modifiers), `0x06` delay (`Data_H:Data_L` × 10 ms, 16-bit, `0` = none).
- Bind entry: `[0x91, BtModify, BtAlpha1, 0x00]` — `BtModify` bit7 = infinite, bit6 = interrupt
  running macro, bits0–4 = slot; `BtAlpha1` = loop count.
- The first write packet (`0xD1`) **erases the flash sector**, so a macro write must re-send
  every byte it wants to survive — same rule as the button table.

### Event → action mapping

The recorder produces `MacroEvent(KeyCode, IsPress, DelayMs)` where `KeyCode` is a USB HID
keyboard usage or the mouse codes `0xF0–0xF4`. Encoding per event:

| Recorder event | TAIPAN action(s) |
|---|---|
| `0xF0–0xF4` press/release | `0x01`-type, bitmask `F0→0x01, F1→0x02, F2→0x04, F3→0x08, F4→0x10`, bit7 = IsPress |
| HID key press/release | `0x04`-type, `Data_L` = key code, bit7 = IsPress |
| `DelayMs > 0` | followed by one `0x06` delay action, `round(DelayMs / 10)` units (16-bit) |

**Capacity:** 200 B / 3 B = 66 actions; 1 reserved for the end marker → 65 usable. Worst case
(every event carries a delay) = **32 events per macro** — half of BLAZE's 62. The recorder cap
must be device-aware or a TAIPAN upload silently truncates.

### Repeat modes

| `MacroRepeatMode` | Bind params | Note |
|---|---|---|
| `PlayCount(n)` | `BtModify = slot`, `BtAlpha1 = n` | direct |
| `LoopForever` | `BtModify = slot \| 0x80 \| 0x40`, `BtAlpha1 = 0` | infinite + interruptible (press again to stop) |
| `WhileHeld` | same as LoopForever | **not documented on TAIPAN** — closest approximation is toggle-until-pressed-again; must be hardware-verified and is called out in QA |

## Options considered

### A. Where the macro table write happens
1. **All used slots in one `0x05` sequence per `SetButtonBindingsAsync`** — *recommended.* The
   `0xD1` erase makes partial writes dangerous (erasing the sector wipes other slots), and the
   binding write already receives the full desired set. Slots are allocated sequentially
   (0, 1, …) per call, exactly like `EasenMouseService`'s `macroSlot++`, so the erase is harmless
   — every referenced slot is rewritten in the same sequence.
2. Per-macro upload on library save — rejected: the library is owner-scoped and shared across
   devices; uploading on save would need per-device slot bookkeeping and still couldn't survive
   the sector erase done by an unrelated later write.

### B. Where macro-entry encoding lives
1. **`TaipanButtonMap.EncodeMacroEntry(slot, mode, repeatCount)`** + the service overlays it into
   the table — *recommended.* Byte-exact and unit-testable, mirrors how `EasenMouseService`
   overlays `[slot, 0, type, 0x05]` after uploading. `BuildTable` stays macro-agnostic.
2. Teach `BuildTable` a slot-allocation callback — rejected: couples the pure encoder to upload
   ordering for no testing gain.

### C. Recorder event cap
1. **Device-aware cap in the recorder** (`MaxMacroEvents` becomes a property resolved from the
   open device: TAIPAN → `TaipanMacroEncoder.MaxEvents` (32), else X43's 62) — *recommended.*
   Small, honest, and the limit banner (`Macros_LimitReached`) keeps working.
2. Catalog-driven per-device limit (proto + config) — better long-term (data, not code), but a
   contract change for one number; recorded as a follow-up.
3. Keep 62 and truncate on upload — rejected: silently plays half the macro.

## Recommended approach

1. **`TaipanMacroEncoder`** (pure): `Encode(MacroDefinition)` → 200-byte slot image
   (actions + delay actions + `0x00` terminator, zero-padded); `MaxEvents = 32`;
   `DelayUnitMs = 10`. Events beyond capacity are dropped (the recorder cap prevents this
   in practice; the encoder guard is defence in depth). Mouse-code map as per the table above.
2. **`TaipanButtonMap.EncodeMacroEntry`** (pure): `[0x91, modify, loopCount, 0]` per the repeat
   table.
3. **`TelinkMouseService.SetButtonBindingsAsync`**: for bindings with `Action == Macro &&
   Macro != null`, allocate slots sequentially, build one contiguous macro-table image
   (`slotCount × 200` bytes), write it as `0x05`: `0xD1` first chunk → `0xD2` chunks (25-byte,
   paced with the existing `PacketDelayMs`) → ValidBankID at `0x0FFF` → `0xD3`; then overlay each
   `EncodeMacroEntry` into the button table at the button's `EntryOffset` before the existing
   button-table write. Macro table first, buttons second — a bound slot must exist before the
   binding points at it.
4. **Recorder cap**: `MaxMacroEvents` const → property choosing 32 for TAIPAN (by device name,
   same dispatch the hotspots use), 62 otherwise.
5. **Config**: `"SupportsMacros": true` on the three PG TAIPAN entries (binding plumbing for
   `AllowedDevice.SupportsMacros` → `DeviceMap` already exists and is verified).

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/TaipanMacroEncoder.cs` *(new)* — pure encoder:
  `MacroDefinition` → 200-byte slot image; constants `SlotLength = 200`, `MaxSlots = 20`,
  `MaxEvents = 32`, `DelayUnitMs = 10`; the mouse-code→bitmask map.
- `src/ProoveGaming.Infrastructure/Classes/TaipanButtonMap.cs` *(edited)* — add
  `MacroCommand = 0x05`, `MacroValidBankIdOffset = 0x0FFF`, and
  `EncodeMacroEntry(int slot, MacroRepeatMode mode, int repeatCount)`.
- `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs` *(edited)* — macro-table
  build + `0x05` write sequence; overlay macro entries into the button table; keep everything
  under the existing `_writeLock` and packet pacing.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.MacroRecorder.cs`
  *(edited)* — `MaxMacroEvents` device-aware (TAIPAN 32 / default 62). *(2026-09-30:)* the limit
  label is re-notified when recording stops at the cap — it used to keep the first value its binding
  read, so a TAIPAN stopped at 32 strings while the banner said 62.
- `src/ProoveGaming.DeviceHost/appsettings.json` *(edited)* — `"SupportsMacros": true` ×3
  TAIPAN entries.
- `tests/ProoveGaming.Infrastructure.Tests/TaipanMacroEncoderTests.cs` *(new)* — see Test
  strategy.
- `tests/ProoveGaming.Infrastructure.Tests/TaipanButtonMapTests.cs` *(edited)* — macro-entry
  cases.
- `docs/CONTEXT.md` *(edited)* — add **Macro slot** term.

**No changes** to: proto, `MouseButton`/`MouseButtonAction`, `LocalStorageService`,
`DeviceService`, the recorder UI beyond the cap.

## Acceptance criteria

1. Given a TAIPAN connected by **cable** → its details page shows the **Macros** section.
2. Given a recorded macro (e.g. two keystrokes with delays) assigned to **Forward** → the
   DeviceHost log shows one `0x05` macro write and one `0x03` button write, no errors →
   pressing the physical Forward button plays the macro (keystrokes with the recorded timing).
3. Given `PlayCount = 3` → the macro plays exactly 3 times per press.
4. Given two different macros on two buttons → each button plays its own macro (slots don't
   collide).
5. Given a macro bound and then the button reassigned to Volume Up → the button does Volume Up;
   no stale macro fires.
6. Given the TAIPAN recorder → recording stops at **32** events with the limit banner; on BLAZE
   it still stops at 62.
7. Given a BLAZE with macros → behavior unchanged (regression: the Easen path is untouched).
8. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` green,
   including the new encoder tests.

## Test strategy

`add-*` feature — required coverage, pure-logic side (device I/O is QA):

- **`TaipanMacroEncoderTests`** (new):
  - Golden encoding: a two-event keyboard macro (press A +50 ms, release A +50 ms) → exact
    bytes `[0x84, 0x04, 0x00, 0x06, 0x05, 0x00, 0x04, 0x04, 0x00, 0x06, 0x05, 0x00, 0x00, …]`
    (Make A, delay 5, Break A, delay 5, terminator).
  - Mouse-code map: each of `0xF0–0xF4` → `0x01`-type action with the right bitmask, press and
    release bit7 both ways.
  - Delay quantization: 0 ms → no delay action; 4 ms → 0 units (dropped) vs 5 ms → 1 unit
    (pick and lock the rounding rule); 655350 ms cap.
  - Capacity: 33 delayed events → encodes 32, terminator present, exactly 200 bytes.
  - Slot image is always exactly `SlotLength` bytes.
- **`TaipanButtonMapTests`** (extended):
  - `EncodeMacroEntry(slot 3, PlayCount, 5)` → `[0x91, 0x03, 0x05, 0x00]`.
  - `LoopForever` → `[0x91, 0xC3, 0x00, 0x00]` (slot | 0x80 | 0x40).
  - `WhileHeld` maps identically to LoopForever (documented approximation).
  - Slot range guard (slot > 19 rejected).
- **Not automated:** the `0x05` write sequence on hardware, playback timing, repeat-mode feel —
  QA. `TelinkMouseService` remains I/O-only, untested by design (same as Easen).

## QA (manual test steps)

1. Connect the TAIPAN mouse **with its USB cable**. Launch the app and open the TAIPAN card.
   **Expected:** a "Macros" section appears in the left menu.
2. Open Macros and record a short macro: press A, wait ~1 second, press B, stop recording.
   Save it as "TestAB". **Expected:** the macro appears in the list with its events.
3. Go to Buttons, click the Forward side-button marker, and assign the "TestAB" macro.
   **Expected:** no error; the macro shows as assigned.
4. Open a text editor and press the physical Forward side button once.
   **Expected:** "a" then (after about a second) "b" are typed.
5. Edit the macro's repeat setting to play 3 times, re-assign, press Forward once.
   **Expected:** "ab" is typed three times.
6. Record a second macro ("TestCD": C then D) and assign it to the Back button.
   **Expected:** Forward still plays TestAB; Back plays TestCD.
7. Reassign Forward to "Volume Up". Press Forward. **Expected:** volume goes up, no letters typed.
8. In the recorder, hold a key so events accumulate. **Expected:** recording stops at 32 events
   with the "limit reached" banner, and the banner names **32** — the same number the "Strings: N"
   counter shows. On a BLAZE both say 62.
9. Regression — on a BLAZE, assign a macro to a side button and play it.
   **Expected:** works exactly as before.
10. Switch the TAIPAN to the dongle and try assigning a macro. **Expected:** the assignment is
    saved in the app but the device does not change until the cable is reconnected (same
    cable-only rule as button remaps — known hardware limitation).

## Review focus

`thread-safety` (macro + button writes under one `_writeLock`), `async/await` (paced writes
stay awaited, no sync-over-async), `null-safety` (`ButtonBinding.Macro` nullable),
`performance hot-path` (none — user-action frequency).

## Performance & quality

No hot paths: a macro write is a rare user action. The macro-table image for the worst realistic
case (a few bound macros) is ≤ 4 000 bytes built once per write; packet pacing adds ~12 ms × N
packets (~2 s for a full 20-slot table, ~200 ms for one slot) — acceptable for an explicit save
action, and writes stay serialized behind the existing `SemaphoreSlim`. No allocations of note
outside the write itself.

## Risks

- **`0xD1` erases the macro sector.** Mitigated by always rewriting every referenced slot in one
  sequence and allocating slots deterministically per write; a failed mid-sequence write leaves
  unreferenced slots garbage but nothing points at them.
- **25-byte chunking for `0x05` is doc-inferred, not hardware-confirmed.** The vendor's worked
  write example writes 3 bytes per packet; the button table's 24-byte chunking with identical
  framing is hardware-confirmed, so 25-byte chunks are very likely fine — but the first QA step
  exercising playback is the real gate. Fallback: drop to 3-byte packets (slower, per the doc).
- **`WhileHeld` semantics are approximated** (toggle instead of hold) — must be stated to QA so
  it isn't filed as a bug, and revisited if the OEM app shows a true hold mode.
- **Delay granularity:** events < 5 ms apart lose their delay (10 ms units); typing-speed
  recordings are unaffected.

## Open questions

1. Is the toggle approximation acceptable for `WhileHeld` on TAIPAN, or should the recorder hide
   that mode for this device? (Plan assumes: acceptable, verified in QA.)
2. Recorder cap by device name vs catalog-driven — plan picks name-based (consistent with the
   hotspot dispatch); promote to catalog config when a third device family arrives.

## Review findings

### Round 1 — Ship: yes

#### Code
- [x] Delay actions were emitted after their event, playing inter-key pauses as held keys
  ("123" → "1222333" via OS autorepeat) — `TaipanMacroEncoder.cs` — Blocker *(found on
  hardware and fixed in-round: delays now precede their action; golden tests updated)*
- [ ] `UploadMacrosAsync` allocates the full 20-slot buffer but transmits only the used
  region — `TelinkMouseService.cs` — Nit (cosmetic; size to `slot × SlotLength` if touched
  again)
- [ ] `WhileHeld` maps to the infinite+interrupt toggle (no hold-to-repeat in the firmware)
  — `TaipanButtonMap.cs` — documented approximation, QA-noted; revisit only if the OEM app
  demonstrates a true hold mode

#### Tests
- [x] Encoder byte layout, mouse bitmask, delay quantization/cap, capacity truncation —
  covered by `TaipanMacroEncoderTests` (golden bytes include the delay-order fix)
- [x] Macro bind entries per repeat mode + slot range guard — covered by
  `TaipanButtonMapTests`
- [ ] Playback timing and repeat modes on hardware — QA steps 4–8 of this plan (cable only)

### Round 2 — Ship: yes

_Branch `fix/fixes-on-30.09.2026` (commits 7f3105d, 1a2d87c), reviewed 2026-09-30. Scope for this
plan: the recorder's "maximum of N steps reached" banner showed 62 while a TAIPAN stopped at 32
strings; fixed by re-notifying `RecordLimitLabel` at the cap._

#### Plan conformance
- Matches the amended file-level entry and QA step 8 (banner and "Strings: N" counter name the same
  number). Limits re-derived from the encoders: TAIPAN `(200/3 − 1)/2 = 32`, BLAZE `(128 − 4)/2 = 62`
  — the mirrored constants are correct; only the label was stale.

#### Code
- none blocking. Accepted limitation, carried from Round 1: `MaxMacroEvents` still picks the limit by
  comparing `Device.Name` to `"PG TAIPAN"`
  (`src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.MacroRecorder.cs:26-29`).
  Residual risk: renaming the catalog `Name` (not `DisplayName`, which is what #141 changed) silently
  moves TAIPAN to 62, and the device then drops everything past 32 on upload. Moving the cap into the
  catalog next to `MacroMaxDelayMs` / `MacroMaxRepetitions` would remove it.

#### Tests
- [x] `DeviceDetailsMacroRecorderTests.Taipan_StopsAt32Steps_AndRefreshesTheLimitMessage` — asserts
  the 32-step stop and the `RecordLimitLabel` notification; verified to fail without the fix. It
  cannot assert the rendered number, because resources are not loaded in tests and `GetString`
  returns the key.

#### Nits
- The recorder labels (`RecordLimitLabel`, `RecordedStepsCountLabel`, `RecordButtonLabel`) are not
  re-raised on a language switch — pre-existing, visible only if the language changes while the
  Create Macro dialog is open.

#### What's done well
- Found the real cause (a computed property nobody notified) rather than touching the limits, and
  refreshed it at the one moment it becomes visible.

#### Verdict
Ship: yes. First: (1) consider moving the step cap into the catalog.
