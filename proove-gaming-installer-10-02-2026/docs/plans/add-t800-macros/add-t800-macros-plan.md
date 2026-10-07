# Add keyboard macros support for T800 (RY5088)

## Goal

Bring the T800 keyboard up to macro parity with Cyrex / Aress / Rampage (Smart Infinite) and
Raster / T-Racer: record a macro in the App, upload the library to the board, bind it to a
physical key, play it with the configured repeat behaviour, and unassign it. The whole
App → gRPC → DeviceHost pipeline is already device-family-generic, and the T800 config entry
in `src/ProoveGaming.DeviceHost/appsettings.json` **already declares `SupportsMacros: true`** —
so the Macros section is visible for T800 today and every upload silently fails
(`DeviceKeyboardMacroServiceFactory` logs "not a macro-supported keyboard" and returns null).
This plan closes that gap with an RY5088 macro encoder and upload service, plus the T800 branch
of the App's per-family macro binding record.

## Scope

**In:**

- RY5088 macro wire format: `0x0B` set-macro / `0x8B` read-macro framing in `Ry5088Protocol`.
- A pure `Ry5088MacroEncoder` that packs one `MacroLibraryEntry` into the ≤256-byte macro blob.
- `T800KeyboardMacroService : IKeyboardMacroService` with read-back verification, and its
  branch in `DeviceKeyboardMacroServiceFactory`.
- Carrying the macro repeat count to the device: on this family it lives **inside the macro
  blob**, not in the key-matrix binding — an additive `repeat_count` field on the
  `MacroLibraryEntry` proto message, threaded App → gRPC → Infrastructure.
- The T800 branch of the App's macro binding record (`[0x09, playType, slot]` — note code1/code2
  are **swapped** relative to Raster / Smart Infinite).
- Extracting the T800 vendor-interface selection (feature-length + `mi_02`) into a shared
  helper: this change would create the **third** identical copy, which is past this repo's
  extraction trigger.
- Knowledge-base translation of the vendor macro-format section (previously untranslated).
- Unit tests mirroring the existing macro test suites.

**Out:**

- Mouse-event pass-through in T800 keyboard macros. The RY5088 format *does* document mouse
  codes (`0xF0–0xF8`), unlike the other two families, but for launch T800 mirrors the existing
  keyboards and drops mouse events (counted and logged, same as `SmartInfiniteMacroEncoder`).
  Revisit once base playback is hardware-verified.
- Macro slots 16–63. The device stores 64 macros; the App library is capped at 16
  (`MacroLibrarySlots`), and T800 manages slots 0–15 only, like the other keyboards.
- `0xF9` XY-motion and `0xFA` jump-macro actions (no App concept maps to them).
- Any change to Raster / T-Racer / Smart Infinite upload behaviour.
- UI changes. The Macros section, recorder, and unassign (X) flows are family-agnostic and
  already render for T800.

## Protocol reference (from the vendor docs)

Decoded from `docs/ry-yc3123-机械轴__2025_07_18.doc` (YC3123 Gaming SPEC — same RY5088 MCU
family, same command set as the T800's magnetic-axis doc; the key-matrix decode in
[`../add-t800-key-assignments/notes.md`](../add-t800-key-assignments/notes.md) already matches
this doc's action table exactly). The raw extraction with the vendor's own examples is in
[`notes.md`](./notes.md); the format goes into the knowledge base as part of this change.

**Upload (`FEA_CMD_SET_MACRO = 0x0B`)** — HID feature reports on the `mi_02` vendor interface,
same 65-byte framing and `0xFF - sum` checksum as every other RY5088 command:

```
0x0B | Macro# (0..63) | Page (0..4) | Len (0..56) | End | 00 | 00 | CS | payload…
```

256 bytes per macro, written as up to 5 pages of 56; `End = 1` on the final page of the macro
("macro end" flag — the analogue of `FlashEnable` elsewhere in this protocol).

**Read-back (`FEA_CMD_GET_MACRO = 0x8B`)**:

```
0x8B | Macro# | Page (0..3) | 00 | 00 | 00 | 00 | CS
```

Replies are 64-byte pages (4 pages for the 256-byte blob), raw with no echoed header — the
same reply shape as `GET_KEYMATRIX`, so the same echo-detection guard applies.

**Macro blob layout** (per the "Macro Table memory information" section):

```
Byte0..1   repeat count, uint16 LE (0..65535)
then 2-byte words: [Code, Attribute]
           Attribute bit7:    1 = make (press), 0 = break (release)
           Attribute bit0-6:  delay after this action, 1..127 ms;
                              0 = the NEXT word is a 16-bit LE delay instead
           Code: 0x04-0xEF keyboard HID usage, 0xF0-0xF8 mouse, 0xF9 XY, 0xFA jump
0x00 0x00  end-of-macro marker
```

**Key-matrix binding record** (from the doc's key-code table, `Macro Key` row):

```
[0x09, Type, Index, 0x00]      Type: 0 = repeat (play repeat-count times, any key stops)
                               1 = always (loop until any key press)
                               2 = release (plays while pressed, stops on release)
                               Index: macro slot 0..63
```

Note the order: **code1 = play type, code2 = slot** — the mirror image of Raster / Smart
Infinite, where code1 is the slot. This is why the App's `MacroBindingRecord` must return the
full triple rather than assuming code1 = slot.

Space check: the recorder caps a macro at 60 events (`MaxEventsPerMacro`) and delays at
10–1000 ms. Worst case is 60 events all needing the extended-delay word: 2 (repeat) + 60×4 +
2 (end) = 244 ≤ 256 — **a recorder-legal macro always fits**, so the encoder can never need to
truncate for space, only for the event cap.

## Options considered

### A. Full parity: dedicated encoder + service, additive proto field for the repeat count (recommended)

Add `uint32 repeat_count = 4` to the `MacroLibraryEntry` proto message and thread it through
(`IUsbHidDeviceService.SetMacrosAsync` tuple → `UsbHidDeviceService` → `DeviceService` →
Infrastructure `MacroLibraryEntry`). The T800 encoder writes it into the blob header; the
Smart Infinite and Raster encoders ignore it (their repeat travels in the binding's code2).

- **Pros:** repeat counts actually work on T800, matching Smart Infinite behaviour; the field is
  additive and wire-compatible (old clients simply send 0 → clamped to 1); it is also where
  Raster's currently-ignored repeat count will eventually want to live if its `0x61` type is
  ever hardware-verified.
- **Cons:** touches the proto (both sides regenerate) and the `MacroLibraryEntry` record's
  signature; slightly wider change than the minimum.

### B. Minimal: no proto change, blob repeat hard-coded to 1

The service always writes repeat = 1; a macro's repeat count is silently ignored on T800
(exactly Raster's current, documented-as-wrong-but-harmless state). While-held still works
because it rides in the binding type.

- **Pros:** smallest possible diff; no proto regeneration.
- **Cons:** T800 config already advertises `MaxRepetitions: 255`, and the UI exposes a repeat
  spinner — shipping a keyboard where it is knowingly dead when a 4-field plumbing change fixes
  it is a poor trade; a second PR would have to re-touch every layer this one already touches.

### C. Upload granularity: only the provided entries vs. rewriting all 16 App slots

Not a separate implementation, but a contract decision inside either option. Writing only
provided entries leaves stale blobs on the device after a macro is deleted (the board keeps its
copy across reboots); rewriting all 16 slots (empty slots get a 4-byte empty blob: repeat = 1,
immediate end marker, one page, `End = 1`) honours the interface contract — "rewrites the whole
macro library in one operation" — for one extra packet per unused slot.

**Decision:** rewrite all 16 slots. 16 single-page clears + up to 16×5 content pages is
bounded at ~96 feature writes worst case, and in practice a library of a few macros costs
~20 writes — comparable to the Smart Infinite table upload.

## Recommended approach

Option A with C's all-16-slots rewrite. Mirror the structure of the existing pair
(`SmartInfiniteMacroEncoder` + `SmartInfiniteMacroService`) but build on the RY5088 plumbing
that already exists for T800 (`Ry5088Protocol`, `Ry5088DeviceGate`, the retry/echo-detect
pattern of `T800KeyboardKeyMappingService`):

1. **Protocol** (`Ry5088Protocol`): `CmdSetMacro = 0x0B`, `CmdGetMacro = 0x8B`, geometry
   constants (`MacroBytes = 256`, `MacroWritePageBytes = 56`, `MacroReadPageBytes = 64`,
   `MacroReadPages = 4`, `MacroSlots = 64`), and builders `BuildSetMacroPage` /
   `BuildGetMacroPage` following the existing builder idiom (checksum over bytes 1..7 at
   byte 8).
2. **Encoder** (`Ry5088MacroEncoder`, new pure static): one entry → one blob. Repeat count
   clamped 1..65535 into bytes 0–1; per event, code byte + attribute (press bit + inline delay
   when 10..127 ms, extended 16-bit delay word when 128..1000 ms); `0x00 0x00` terminator.
   Mouse events dropped and counted, events capped at 60 — same `PackResult`-style discard
   reporting as the other two encoders so the service can log what was left out.
3. **Service** (`T800KeyboardMacroService`): under `Ry5088DeviceGate` + `Task.Run` +
   `WithRetry` (same shape as `T800KeyboardKeyMappingService`), for each slot 0..15 write the
   encoded blob (or the empty blob) across its pages with `End = 1` on the last, settle, then
   verify each **used** slot by reading its 4 pages back with `0x8B` (echo-detect each reply)
   and comparing the first `blobLength` bytes. Any mismatch fails the upload — same
   "key slots must never point into a library the board does not hold" rule the Smart Infinite
   service enforces. Empty-slot clears are written blind (a stale blob nothing points to is
   harmless; verifying 16 × 4 reads on every colour change is not worth it — see the
   `ResyncKeyMacroBindingsAsync` early-out comment for how often this path runs).
4. **Factory** (`DeviceKeyboardMacroServiceFactory`): `name == "T800"` → select the vendor
   interface via the new shared `Ry5088InterfaceSelector` and construct the service.
5. **Interface selection dedupe** (`Ry5088InterfaceSelector`, new): the feature-length-65 +
   prefer-`mi_02` block currently duplicated in `DeviceKeyMappingServiceFactory` and
   `DeviceActuationServiceFactory` moves here; both existing factories switch to it
   (behaviour-identical), and the macro factory uses it. `DeviceRgbServiceFactory`'s variant has
   extra candidate logging and fallback warnings — leave it alone.
6. **Proto** (`deviceservice.proto`): `uint32 repeat_count = 4;` on `MacroLibraryEntry`, comment
   updated to say where each family stores it (binding code2 for Raster/Smart Infinite, blob
   header for T800).
7. **DeviceHost** (`DeviceService.SetMacros`): map `entry.RepeatCount` into the Infrastructure
   entry.
8. **Infrastructure model** (`MacroLibraryEntry` in `RasterMacroEncoder.cs`): gains
   `int RepeatCount = 1` with a default so existing call sites and tests keep compiling; the
   Raster and Smart Infinite encoders ignore it.
9. **App**:
   - `IUsbHidDeviceService.SetMacrosAsync` entry tuple gains `int RepeatCount`;
     `UsbHidDeviceService` copies it into the proto message.
   - `BuildMacroLibraryPayload` passes `macro.RepeatCount`.
   - `MacroBindingRecord` returns the full `(Type, Code1, Code2)` triple and takes the slot:
     T800 → `(0x09, playType, slot)` with playType 0 = play-count, 2 = while-held;
     Raster / Smart Infinite → existing `(type, slot, mode-or-count)` unchanged.
     `RebuildMacroAssignmentTargets` (Bitmask = Code1, ScanCode = Code2) and
     `ResyncKeyMacroBindingsAsync` (which currently hard-codes code1 = slot) both consume the
     triple, so the two write paths cannot drift — the exact failure mode the existing comment
     at `ResyncKeyMacroBindingsAsync` warns about.
   - New constants beside the existing per-family ones: `T800MacroBindingType = 0x09`,
     `T800MacroPlayTypeRepeat = 0`, `T800MacroPlayTypeWhileHeld = 2` (vendor type 1 "always"
     has no UI control, same as the two extra Raster stop modes).
10. **Docs**: translate the "Macro Table memory information" + macro-key-record sections into
    `docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md` (its header currently lists macro
    as untranslated), cross-link from `ry5088-magnetic-axis-protocol.md` §24b/§25, and add
    `Ry5088MacroEncoder` / `T800KeyboardMacroService` one-liners to `docs/CONTEXT.md`.

Why not reuse `SmartInfiniteMacroService` with a strategy seam: the two share nothing below the
interface — different transport (feature reports vs. output/input), different buffer model
(per-slot 256-byte blobs vs. one offset-table region), different verify geometry. The repo's
existing pattern is one service per protocol family, and this follows it.

## File-level change list

| # | File | Intent |
|---|---|---|
| 1 | `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | Add `uint32 repeat_count = 4;` to `MacroLibraryEntry`; update the family comment. |
| 2 | `src/ProoveGaming.Infrastructure/Classes/Ry5088Protocol.cs` | Add `0x0B`/`0x8B` command bytes, macro geometry constants, `BuildSetMacroPage`, `BuildGetMacroPage`. |
| 3 | `src/ProoveGaming.Infrastructure/Classes/Ry5088MacroEncoder.cs` (new) | Pure encoder: `MacroLibraryEntry` → ≤256-byte RY5088 macro blob with discard counting. |
| 4 | `src/ProoveGaming.Infrastructure/Classes/Ry5088InterfaceSelector.cs` (new) | Shared T800 vendor-interface selection (featLen 65, prefer `mi_02`), extracted from the two existing copies. |
| 5 | `src/ProoveGaming.Infrastructure/Services/T800KeyboardMacroService.cs` (new) | `IKeyboardMacroService` over RY5088: gate, per-slot paged write (`End = 1`), empty-slot clears, read-back verify of used slots. |
| 6 | `src/ProoveGaming.Infrastructure/Services/DeviceKeyboardMacroServiceFactory.cs` | T800 branch → `Ry5088InterfaceSelector` + new service. |
| 7 | `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs` | Replace the private T800 selection block with `Ry5088InterfaceSelector` (behaviour-identical). |
| 8 | `src/ProoveGaming.Infrastructure/Services/DeviceActuationServiceFactory.cs` | Same swap as #7. |
| 9 | `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs` | `MacroLibraryEntry` gains `int RepeatCount = 1` (defaulted — no call-site churn). |
| 10 | `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` | Map `repeat_count` from the request into the Infrastructure entry. |
| 11 | `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` | `SetMacrosAsync` entry tuple gains `RepeatCount`; doc comment gains the T800 `0x09` family note. |
| 12 | `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` | Copy `RepeatCount` into the proto entry. |
| 13 | `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | T800 binding constants; `MacroBindingRecord` returns `(Type, Code1, Code2)` taking the slot; `RebuildMacroAssignmentTargets` and `ResyncKeyMacroBindingsAsync` consume the triple; `BuildMacroLibraryPayload` passes `RepeatCount`. |
| 14 | `docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md` | Translate the macro blob format + macro key record sections; drop "macro untranslated" from the header note. |
| 15 | `docs/knowledge-base/ry5088-magnetic-axis-protocol.md` | Cross-link §24b/§25 to the now-translated macro format. |
| 16 | `docs/CONTEXT.md` | One-line entries for `Ry5088MacroEncoder`, `T800KeyboardMacroService`. |
| 17 | `tests/ProoveGaming.Infrastructure.Tests/Ry5088MacroEncoderTests.cs` (new) | Encoder unit tests (see Test strategy). |
| 18 | `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTests.cs` | Extend with `BuildSetMacroPage` / `BuildGetMacroPage` framing + checksum cases. |
| 19 | `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | T800 cases: binding triple shape, resync write bytes, payload carries repeat count. |

## Acceptance criteria

- [ ] Given a connected T800 and a library macro "AB" (press/release A, press/release B, delays
      ≥10 ms), when the user binds it to a physical key on the Macros page, then the App writes
      key record `[0x09, 0x00, slot]` to that key's matrix slot and `SetMacros` returns
      success (DeviceHost log shows the T800 macro service verified the read-back).
- [ ] Given that binding, when the key is pressed once, the keyboard types the recorded
      sequence exactly once.
- [ ] Given a macro with repeat count 3 bound in play-count mode, one key press types the
      sequence exactly 3 times.
- [ ] Given a macro bound with "repeat until released", holding the key replays the sequence
      until release and stops on release (binding record `[0x09, 0x02, slot]`).
- [ ] Given an event with a delay of 500 ms, the encoder emits the extended-delay form (attr
      low bits 0 + 16-bit LE word) and playback observably pauses ~0.5 s between the steps.
- [ ] Given a bound macro, when the user clicks the key's unassign (X), the key returns to its
      factory record on the device and the `key_macro_bindings` row is deleted — same
      behaviour as on Cyrex.
- [ ] Given a macro deleted from the library while bound on the T800,
      `ResyncKeyMacroBindingsAsync` clears the on-device slot (empty blob uploaded) and drops
      the stale binding row.
- [ ] Given the T800 upload path, a mismatch between the written blob and the `0x8B` read-back
      makes `UploadMacrosAsync` return false and the App sets `MacroUploadFailed` (existing UI
      state) — no key binding write follows a failed upload.
- [ ] Given a Cyrex, Aress, Rampage, Raster or T-Racer, macro upload and binding behaviour is
      byte-identical to before this change (existing macro tests all green; the new
      `RepeatCount` field defaults to 1 and is ignored by their encoders).
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

New feature (`add-*`) — the following are mandatory coverage, in the two existing test
projects (xUnit, mirroring the style of `SmartInfiniteMacroEncoderTests` /
`Ry5088ProtocolTests` / `DeviceDetailsKeyboardMacroBindingTests`):

**`tests/ProoveGaming.Infrastructure.Tests`**

- `Ry5088MacroEncoderTests` (new):
  - Golden path: 2-event press/release macro → repeat `01 00`, correct code/attr words
    (press bit set on make, clear on break, inline delay in bits 0–6), `00 00` terminator.
  - Repeat count 3 → blob bytes 0–1 = `03 00`; repeat clamped at 1 (0 input) and 65535.
  - Delay 500 ms → extended form: attr low bits 0, next word `F4 01`.
  - Delay 127 ms → inline; 128 ms → extended (boundary).
  - Mouse event (code ≥ 0xF0) dropped and counted; 61+ events truncated at 60 and counted.
  - Worst-case legal macro (60 events, all extended delays) fits in 256 bytes.
  - Empty entry → 4-byte empty blob (repeat 1, immediate terminator).
- `Ry5088ProtocolTests` (extend):
  - `BuildSetMacroPage`: command/macro#/page/len/end bytes in the right positions, checksum
    over bytes 1..7 stored at byte 8, payload at byte 9, 65-byte buffer.
  - `BuildGetMacroPage`: same framing checks.
- Factory routing: `DeviceKeyboardMacroServiceFactory` returns a T800 service for the T800
  config entry (if the existing factory tests have a seam for the HID provider; otherwise this
  is covered by the App-side and QA layers).

**`tests/ProoveGaming.App.Tests`**

- `DeviceDetailsKeyboardMacroBindingTests` (extend):
  - For a T800 device, the macro assignment target rows carry `TypeByte 0x09`,
    `Bitmask = playType`, `ScanCode = slot` (the swap), for both play-count and while-held
    macros.
  - For Cyrex and Raster devices, the rows are byte-identical to before the refactor
    (regression pin for the `MacroBindingRecord` signature change).
  - `BuildMacroLibraryPayload` emits each macro's `RepeatCount`.
  - Resync writes `SetKeyMappingAsync(slot, 0x09, playType, deviceSlot)` for T800 bindings.

**Deliberately not covered by automated tests:** real HID transfer, firmware persistence of
the `End` flag across replug, actual playback timing, and the `T800KeyboardMacroService`
device-I/O loop (it has no transport seam, matching `T800KeyboardKeyMappingService`; its pure
parts — encoder and protocol builders — carry the test weight, and hardware behaviour is the
QA checklist's job).

## QA (manual test steps)

1. Connect the T800 keyboard by cable. Open the Proove Gaming app and open the T800 device
   page. **Expected:** the Macros section is visible in the left navigation.
2. Open Macros → record a new macro named "test1": press and release A, then B, then C. Save
   it. **Expected:** the macro appears in the macro list without errors.
3. Select the macro "test1", click a key on the on-screen keyboard (e.g. F), and bind the
   macro to it. **Expected:** the key shows the macro badge; no error banner appears.
4. Open any text editor and press the bound key (F) once. **Expected:** "abc" is typed exactly
   once. The original F key no longer types "f".
5. Edit the macro to set repeat count = 3 (re-bind if asked). Press the bound key once.
   **Expected:** "abcabcabc" — the sequence exactly 3 times.
6. Record a second macro with a long pause: press A, wait about a second, press B. Bind it to
   another key and press that key. **Expected:** "a", a visible ~1 s pause, then "b".
7. Set a macro to "repeat until released" mode and bind it. Hold the bound key for ~2 seconds,
   then release. **Expected:** the sequence repeats while held and stops when released.
8. Unplug the T800 and plug it back in. Press the bound key. **Expected:** the macro still
   plays (bindings survive replug).
9. On the bound key, click the X (unassign). Press the key. **Expected:** the key types its
   original character again; the macro badge is gone.
10. Delete "test1" from the macro library while it is still bound to a key. Press that key.
    **Expected:** the key no longer plays the macro (it acts as its original key), and the app
    shows no stale macro badge on it.
11. While doing steps 2–10, watch for app freezes or cursor hitches. **Expected:** the UI stays
    responsive throughout; binding/upload never blocks typing or navigation.
12. Sanity check on a Cyrex (or Aress/Rampage): bind a macro with repeat 2 to a key and play
    it. **Expected:** unchanged behaviour from the previous release.

## Review focus

1. `gRPC contract` — the proto field is additive (`repeat_count = 4`), both sides regenerate
   from the same file, and old-value defaults (0) are clamped, never trusted.
2. `thread-safety` — every T800 HID exchange holds `Ry5088DeviceGate`; the macro service must
   never interleave with the RGB / key-mapping / actuation services on the shared `mi_02`
   interface.
3. `async/await` — device I/O stays on `Task.Run` off the UI thread; cancellation checked
   between packets, never mid-exchange; no sync-over-async in the App layer.
4. `null-safety` — factory returns null for missing interfaces; the App treats a false upload
   as "do not write the binding", matching the Smart Infinite invariant.

## Performance & quality

- **No hot path.** Macro upload runs on library edits, binding changes, and device connect —
  all background, all behind the App's debounce and the `bindings.Count == 0` early-out that
  already protects unrelated staged edits (see the comment in `ResyncKeyMacroBindingsAsync`).
- **Upload budget:** worst case (16 full macros) ≈ 80 write packets + 64 verify reads at
  ~20 ms inter-packet ≈ 3 s, held under the device gate but off the UI thread; typical case
  (1–3 macros) ≈ 1 s. Empty-slot clears are single packets and unverified by design.
- **Allocations:** encoder allocates one 256-byte buffer per used slot per upload; nothing
  per-frame, nothing on the dispatcher thread.
- **Shared state:** none added — the service is constructed per gRPC call like its siblings;
  serialisation is via the existing static `Ry5088DeviceGate`.

## Risks

- **Persistence semantics of `End` are unverified on hardware.** The doc marks `End = 1` as
  "macro end", and this protocol's other commands use an explicit `FlashEnable`. If macros
  turn out to be RAM-only until some other commit, QA step 8 (replug) catches it; fallback is
  investigating whether `0x0B` needs a trailing flash-bracket write.
- **Read-back reply shape is assumed raw-64-bytes-no-header** (same as `GET_KEYMATRIX`). If
  `0x8B` echoes a header instead, verification will fail deterministically on hardware — the
  encoder/tests stay valid, only the service's reply parsing shifts.
- **`Type = 2` (release) loop behaviour** — whether it replays from the start or loops
  continuously while held is not specified; QA step 7 defines acceptable behaviour (repeats
  while held, stops on release).
- **`MacroBindingRecord` signature change touches Raster and Smart Infinite paths.** The
  regression-pin tests in `DeviceDetailsKeyboardMacroBindingTests` exist precisely to hold
  their bytes constant through the refactor.
- **Same-VID/PID second unit routing** (`device_key` not honoured by `SetMacros`) is a known,
  documented limitation for all keyboards; T800 inherits it, no worse.

## Open questions

1. **Mouse events in T800 macros:** the format supports `0xF0–0xF8`; the plan drops them for
   parity with the other keyboards. OK to defer pass-through to a follow-up?
2. **Repeat while-held blob value:** for a while-held binding the blob's repeat count is
   written as recorded (≥1). If hardware shows type 2 honouring the count oddly, is forcing
   repeat = 1 for while-held bindings acceptable?
3. **Verify depth:** used slots are verified byte-for-byte; empty-slot clears are written
   blind. Any objection to the asymmetry?

## Review findings

### Round 1 — Ship: yes

#### Plan conformance

Plan: `docs/plans/add-t800-macros/add-t800-macros-plan.md` (this document).

- Implemented as planned: all 19 file-level change list items — proto `repeat_count = 4`,
  `Ry5088Protocol` macro commands/geometry/builders + `TypeMacro 0x09` constants,
  `Ry5088MacroEncoder`, `Ry5088InterfaceSelector` (both existing factories switched,
  behaviour-identical), `T800KeyboardMacroService` (gate, retry, all-16-slot rewrite, blind
  clears, authoritative read-back verify with echo detection), factory T800 branch,
  `MacroLibraryEntry.RepeatCount = 1` default, host mapping with clamp, App tuple +
  `MacroBindingRecord` triple + both call sites + payload repeat count, knowledge-base macro
  translation + cross-links, `CONTEXT.md` terms, encoder/protocol/binding tests.
- Deviates from plan (all surfaced, none harmful):
  - The encoder drops **every** code outside `0x04..0xEF`, not just mouse codes `0xF0..0xF4`
    (`Ry5088MacroEncoder.cs:143`), and the result field is named `DroppedEvents` rather than
    the plan's `DroppedMouseEvents`. Deliberate hardening: on this family `0x00` in the code
    position **is** the end marker and `0xFA` is jump-macro, so a stray code would corrupt or
    truncate playback rather than mistype. The plan text is now the stale side; this journal
    entry records the deviation as intended.
  - Seven additional test files under `tests/ProoveGaming.App.Tests/` had their
    `IUsbHidDeviceService` fakes' `SetMacrosAsync` tuple updated — mechanical fallout of plan
    item 11 the change list did not enumerate.
  - The Keyborder-v3 vendor app offered for protocol confirmation turned out to bundle only
    the Smart Infinite protocol (key records `0x10…`, macro types `0x50/0x51`) — no RY5088
    confirmation was possible from it; the YC3123 doc remains the sole source, as the plan
    assumed.
- Missing / partial: factory-routing unit test — the plan made it conditional on an existing
  seam for the HID provider; none exists, so it was skipped as allowed.
- Accepted limitation (per plan): `End`-flag persistence across replug, `0x8B` reply shape
  (assumed raw 64 bytes, no header), and play-type-2 while-held loop behaviour are
  hardware-unverified — residual risk is that the first on-hardware run fails verify or loses
  macros on replug; QA steps 7–8 are the detection net, and the service fails closed (a
  failed verify blocks the key binding write).
- Acceptance criteria: all code-verifiable criteria are implemented and covered by passing
  tests (548 Infrastructure + 555 App); the playback/persistence criteria pend QA hardware.

#### Code

- none (no Blockers; both Should-fix items are docs/tests, listed below)

- [x] **S2. Two XML docs now misdescribe the macro-target byte semantics** — closed: both
  docs rewritten to the per-family operand truth (Raster/Smart Infinite slot-in-code1 vs
  T800 play-type-in-code1) and pointed at `docs/CONTEXT.md` → "Macro type byte (key
  record)" as the maintained single source; the interface doc also names each family's
  upload command and slot-cap constant —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/KeyAssignmentOptionViewModel.cs:43-48` and
  `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardMacroService.cs:5-11` — Should-fix
  **Problem / why it matters:** `MacroId`'s doc says macro targets "carry the vendor macro
  type byte (0x60 / 0x61) with the on-device slot in `Bitmask` (code1) and `ScanCode`
  (code2) left at 0". After this change that is wrong on every count for T800 (`TypeByte`
  is `0x09`, `Bitmask` is the **play type**, `ScanCode` is the **slot**) and was already
  stale for Smart Infinite (`0x50/0x51`, code2 = mode/count). `SyncAssignmentTargetSelection`
  matches on exactly these fields, so the next person debugging selection-highlight problems
  will be reading a description of the wrong wire format. Same story for the
  `IKeyboardMacroService` summary ("0x60 = play, 0x61 = repeat while held", "up to
  RasterMacroEncoder.MaxMacros") — the interface every implementer reads names one family's
  constants as if they were universal.
  **Fix:** rewrite both docs to per-family truth or point at `docs/CONTEXT.md` → "Macro type
  byte (key record)", which is already the maintained single source. Doc-only change, no
  behaviour.

#### Tests

- [x] **S1. No T800 case on the resync re-write path** — closed by
  `T800_Resync_RewritesTheBindingWithTheSwappedOperands` (binds slot 1, clears the recorders,
  awaits `ResyncKeyMacroBindingsAsync`, asserts the re-write equals the original
  `(0x09, 0x00, 0x01)` record and that the library re-upload precedes it) — the three new T800 tests all
  exercise the assign path (`ApplyAssignmentTargetAsync` → target bytes). The other write
  path, `ResyncKeyMacroBindingsAsync` (`DeviceDetailsViewModel.KeyAssignments.cs:880-883`,
  `record.Type, record.Code1, record.Code2`), has no T800 coverage: if someone regressed
  that call back to the pre-change `(record.Type, (byte)entry.Slot, record.Code2)`, every
  existing test would stay green — Raster/Smart Infinite resync tests pass because their
  Code1 *is* the slot — while T800 keys silently re-bound to "play type = slot" on every
  device connect. Add one test mirroring `Resync_ClearsASlotWhoseBindingWasRemoved`: bind a
  macro on T800, clear `KeyWrites`, `await vm.ResyncKeyMacroBindingsAsync()`, assert the
  re-write is `(0x09, playType, slot)` — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs`.

#### Nits

- The plan's own encoder description (§Recommended approach step 2 and the `PackResult`
  bullet) still says "mouse events dropped" / `DroppedMouseEvents` — superseded by the
  broader keyboard-range filter recorded above; harmless since this journal notes it.

#### What's done well

- The operand swap — the single most dangerous seam in this change — is pinned from both
  directions: `T800_MacroBinding_PutsThePlayTypeInCode1AndTheSlotInCode2` uses a two-macro
  library so slot 1 lands visibly in code2, and the existing Cyrex/Raster tests held the old
  byte order constant through the `MacroBindingRecord` refactor.
- The encoder rejects the whole non-keyboard code range with a comment explaining *why*
  (`0x00` = end marker, `0xFA` = jump), not just what — exactly the kind of invariant note
  this repo's protocol work depends on.
- Verify-fails-closed carried over from the Smart Infinite service: "answered and disagreed"
  is final and blocks the binding write, so keys can never point into a library the board
  refused.
- The vendor-doc macro section was translated into the knowledge base in the same change,
  with the write/read page-size asymmetry (56 vs 64) called out where it will be needed.

#### Verdict

Ship: yes — no blockers; both open items are a doc refresh and one added test, safe as
follow-ups in the next round. First: S1 (T800 resync test), S2 (doc refresh), then QA
hardware pass per the plan's checklist.

### Round 2 — Ship: yes

#### Plan conformance

Delta since Round 1 is exactly the two Round 1 closures — no scope drift, no other files
touched:

- **S1 closed and verified:** `T800_Resync_RewritesTheBindingWithTheSwappedOperands`
  (`tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs`) binds the
  second library macro so the slot is 1, then asserts the resync re-write equals the
  original `(0x09, 0x00, 0x01)` record. The guarded regression — code1 reverting to the
  slot — would emit `(0x09, 0x01, 0x01)` and fail the `Code1` assertion, so the test
  discriminates rather than merely re-asserting defaults. Upload-before-bind ordering is
  pinned in the same test. App suite green: 556/556.
- **S2 closed and verified:** `KeyAssignmentOptionViewModel.MacroId` and the
  `IKeyboardMacroService` summary now state the per-family operand truth and point at
  `docs/CONTEXT.md` → "Macro type byte (key record)". The added `<see cref>`s
  (`SmartInfiniteMacroEncoder.SlotCount`, `Ry5088MacroEncoder.LibrarySlots`) resolve —
  clean build shows no CS1574, only the pre-existing AVLN/CS0067 warnings. Comment-only:
  Infrastructure suite still 548/548.

#### Code

- none

#### Tests

- none — both write paths (assign + resync) now pin the T800 operand swap; remaining
  verification is hardware-only (accepted in plan, QA checklist steps 7–8).

#### Nits

- none new; the Round 1 nit (plan text says "mouse events dropped" where the encoder
  filters the whole non-keyboard range) stands as recorded, superseded by the journal.

#### What's done well

- Round 1 items were closed exactly as specified — the test targets the precise regression
  scenario from the finding instead of a generic happy-path duplicate, and the doc fix
  routes future readers to the maintained single source rather than duplicating the table.

#### Verdict

Ship: yes — all review rounds clean, no open code or test items. Next: run the plan's QA
(manual test steps) on T800 hardware; steps 7 (while-held) and 8 (replug persistence) are
the two that can still surprise, and the upload path fails closed if they do.
