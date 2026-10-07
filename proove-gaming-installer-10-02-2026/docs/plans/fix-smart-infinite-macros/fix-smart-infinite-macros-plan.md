# fix-smart-infinite-macros — Make macros actually work on Cyrex / Aress

## Goal

Macros on Cyrex and Aress do not play. The vendor's Keyborder-v3 driver app — whose command
numbering matches our firmware where the V3.1 workbook does not (`0x17`/`0x18` matrix, `0x20`
factory reset, the `0x38`/`0x39` travel area) — reveals **three concrete divergences** between
what our code sends and what this firmware expects: the macro library is written with the wrong
command (`0x2D` instead of `0x1C`) and the wrong shape (2048-byte C8 reserve instead of the
`hong_space_size` buffer from Device Info), and key bindings use the wrong record type
(`0x60`/`0x61`, which this firmware's own KeyType enum defines as **FUNCTION** — its macro types
are **`0x50` MACRO_NO_COUNT / `0x51` MACRO_WITH_COUNT**). This plan aligns the Smart Infinite
macro path with the vendor app's implementation, extracted verbatim from its bundle.

## Scope

**In:**
- Macro library upload for the Smart Infinite family via `0x1C` (write) / `0x1B` (read), buffer
  sized and formatted exactly as the vendor app's `formatHongRect` serializer.
- A Smart Infinite–specific macro encoder (20 slots, region-size header, modifier events encoded
  as hot-key type `0x02` with the modifier bitmask).
- Key-binding records `{0x50, slot, stopMode}` / `{0x51, slot, playCount}` for the Smart Infinite
  family in the App; Raster / T-Racer keep their hardware-verified `0x60`/`0x61`.
- Read-back verification via `0x1B` promoted to authoritative (a discarded write fails the upload).
- Migration of `SmartInfiniteMacroService`'s copied transfer plumbing to `SmartInfiniteTransport`.
- Documentation corrections (CONTEXT.md entries, knowledge-base macro-format doc, resolving the
  open question in `magnetic-axis-travel.md`).

**Out:**
- Raster / T-Racer macro path — hardware-verified, untouched (`RasterMacroEncoder` and
  `RasterKeyboardMacroService` stay as they are).
- Growing the App's macro library capacity to the firmware's 20 slots (App cap stays as today;
  16 ≤ 20 so nothing breaks — capacity bump is a separate feature).
- The `TEXT_OUTPUT` (0x61-on-this-firmware) and advanced-key (`0x80+`) record types.
- T800 macros (different protocol family, separate story).

## Evidence (extracted from Keyborder-v3, `index-CDyQl7nP.js`)

1. **Commands.** `getHongRect` reads the macro area with `gethongMessage = 0x1B` (256 bytes for
   its UI); `setHongRect` writes with `sethongMessage = 0x1C`, sending the **whole formatted
   buffer** chunk-addressed by byte offset — same envelope as every other command. `0x2C`/`0x2D`
   appear nowhere in the app; our previous "0x2D confirmed working" observed only acks, and the
   all-zero read-back in `docs/plans/add-cyrex-aress-parity/macro-investigation-result.md` is
   exactly what a wrong-command write looks like.
2. **Buffer format** (`formatHongRect(settings, size)` with `size = hong_space_size` from Device
   Info byte 5 × 256 — 1024 on Cyrex):
   - byte 0–1: `0xAA 0x55`
   - byte 2–3: **full buffer length** LE (= `hong_space_size`, not the used length)
   - byte 4–5: **slot count** LE (= 20, including empty slots)
   - byte 6–7: reserved 0
   - byte 8 + slot×2: per-slot block offset LE; `0x0000` = empty slot; 20 slots → blocks start at 48
   - block: 2 bytes action count LE, 2 reserved, then 4-byte actions `{delay_L, delay_H, code, attr}`
   - `attr`: bit 7 = press(1)/release(0); low bits = **1** for a normal key (code = HID usage) or
     **2** for a modifier ("hot key": code = the **modifier bitmask** value, e.g. LShift `0x02`)
3. **Binding records** (`applyHongMacroMapping`): `{KeyType.MACRO_NO_COUNT = 0x50, slot,
   stopMode 0..3}` or `{KeyType.MACRO_WITH_COUNT = 0x51, slot, playCount 1..255}` written into the
   key matrix. The firmware's KeyType enum: `0x10` basic, `0x20` multimedia, `0x30` system,
   `0x40` mouse, `0x50/0x51` macro, **`0x60` FUNCTION**, `0x61` TEXT_OUTPUT, `0x70` FN combo,
   `0x80+` advanced keys. The V3.1 workbook's enum (0x50 = FUNC, 0x60 = macro) belongs to a
   different firmware line — the third time that sheet disagrees with our boards.
4. Slot number for a binding is derived from the offset-table position (`(a0 − 8) / 2`), i.e. the
   same slot index the encoder writes — confirming slot-indexed binding into the library.

## Options considered

**A. Transcribe the vendor app's path exactly for this family (recommended).** New pure
`SmartInfiniteMacroEncoder` implementing `formatHongRect`; macro service switches to
`0x1B`/`0x1C`; App binds `0x50`/`0x51` per family. *Trade-off:* a second encoder next to
`RasterMacroEncoder` (~120 lines) — but the two formats genuinely differ (header semantics, slot
count, modifier encoding), and parameterizing the hardware-verified Raster encoder would put the
working Raster path at risk to save one small file.

**B. Parameterize `RasterMacroEncoder`** (region size, slot count, modifier mode). Fewer files,
but every parameter threads through the one encoder both families depend on; a mistake breaks
Raster too, and the Raster path has no hardware regression net. Rejected.

**C. Keep `0x2D`/`0x60` and escalate to the factory.** Slower, and the evidence is now strongly
against it: the vendor's own driver — targeting our firmware's command numbering — never uses
those values on this family. Rejected as primary (the factory question can ride QA findings).

## Recommended approach

Option A. Details and decisions:

- **Write shape:** the full `MacroBytes` buffer (Device Info byte 5 × 256), chunk-addressed via
  the standard envelope — mirroring `setHongRect`, which sends the entire formatted buffer. The
  existing `MacroBytes <= 0` refusal (the Rampage-signal guard) stays.
- **Verification becomes authoritative:** after the write, read the region back with `0x1B` and
  compare. Unlike the old debug-only `0x2C` verify, a mismatch now **fails the upload** — with
  the correct read command, "acked but not stored" must not lead to key slots bound into a
  library the board does not hold. (The service returns false; the App then skips the binding
  write, as today.)
- **Binding records:** the App's single `(type, code2)` decision point
  (`DeviceDetailsViewModel.KeyAssignments.cs:690-699`) becomes per-family: Smart Infinite boards
  (Cyrex / Aress / Rampage) get `0x50` + stopMode (`0x00` play-through / `0x01` while-held — the
  same code2 values already used) and `0x51` + repeat count; Raster / T-Racer keep `0x60`/`0x61`.
  The read-side macro-record detection (binding resync against `ReadKeyMatrix`) accepts the
  family's own types.
- **Rampage rides along — and gets its UI enabled:** it shares `SmartInfiniteMacroService` and
  the binding constants, so it moves to `0x1C`/`0x50` with the others. Its catalog entry shipped
  with `SupportsMacros: false` (the old path never worked there); that flips to `true` in this
  change, because the evidence gap has closed: Keyborder registers PID `12888` on the same
  `GamingKeyboard2` protocol with the shared macro UI, Rampage's Device Info reports a 2048-byte
  macro space, and `RampageProtocol.KnownTypes` already lists `0x50`/`0x51` as macro types. Its
  first hardware run is still QA step 10; the authoritative read-back verify fails safely if the
  firmware declines.
- **Plumbing:** the service's private `ReplyFor`/device-info/chunk loops are replaced with
  `SmartInfiniteTransport` calls (the shared helper extracted during `add-keyboard-actuation`),
  eliminating the last copied transfer layer in this family.
- **Encoder inputs:** the existing `MacroLibraryEntry` model. Modifier events (HID usages
  `0xE0`–`0xE7`) are translated to the bitmask + attr-type-2 form inside the encoder; all other
  keys keep usage + attr-type-1. Delay and press/release semantics are unchanged.

## File-level change list

Infrastructure:
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteProtocol.cs` — edit: add
  `CmdReadMacroInfo = 0x1B` / `CmdWriteMacroInfo = 0x1C`, `TypeMacroStopMode = 0x50`,
  `TypeMacroPlayCount = 0x51`; rewrite the `TypeMacro`/`CmdSetMacroData` remarks to record the
  correction (0x60 is FUNCTION on this firmware; 0x2C/0x2D were never consumed by the vendor app)
  without deleting the history.
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteMacroEncoder.cs` — **new**: pure
  transcription of `formatHongRect` — region-size buffer, 20-slot offset table, block layout,
  modifier hot-key events; exposes discard/truncation counters like `RasterMacroEncoder` does.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs` — edit: encode with the
  new encoder sized to `info.MacroBytes`; write via `0x1C`; authoritative `0x1B` read-back
  verify; delegate transfer plumbing to `SmartInfiniteTransport`.

App:
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` —
  edit: per-family macro binding record (type + code2) and read-side macro-type detection;
  constants renamed to say which family they belong to.

DeviceHost:
- `src/ProoveGaming.DeviceHost/appsettings.json` — edit: Rampage `SupportsMacros` flips to
  `true` (see the Rampage bullet in Recommended approach).

Tests (bug-repro first — these fail on the current code):
- `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` — edit: Cyrex/Aress
  binding assertions move from `0x60`/`0x61` to `0x50`/`0x51`; add a Raster case pinning
  `0x60`/`0x61` so the family split cannot regress either side.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteMacroEncoderTests.cs` — **new**: header
  bytes (region length at 2–3, slot count 20 at 4–5), offset table (empty slot = 0, first block
  at 48, sequential offsets), action encoding (delay LE, press/release bit, normal vs modifier
  attr and code), region-size fit and truncation behaviour, byte-for-byte comparison against a
  hand-built `formatHongRect` reference vector.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteProtocolTests.cs` — edit if it pins the
  old macro constants.

Docs:
- `docs/knowledge-base/smart-infinite-protocol/macro-format-gamingkeyboard2.md` — **new**: the
  extracted macro commands, buffer format and binding records (source + method noted).
- `docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md` — edit: resolve open
  question 1 (the 0x1B/0x1C-vs-0x2C/0x2D discrepancy is now settled in favour of 0x1B/0x1C).
- `docs/CONTEXT.md` — edit: correct **Macro type byte (key record)** (third and final flip, with
  the per-family truth), **Macro upload (Smart Infinite)** (command, buffer, slot count), and add
  **SmartInfiniteMacroEncoder**.

## Acceptance criteria

- [ ] Given a Cyrex with a recorded macro bound to a key → the App uploads the library and binds
  the key with record `{0x50, slot, 0x00}` (once) or `{0x51, slot, N}` (repeat N) — assertable in
  unit tests via the fake HID service.
- [ ] Given the same actions on a Raster → the binding record is still `{0x60, slot, mode}` /
  `{0x61, slot, N}` (regression-pinned by test).
- [ ] The encoder output for a two-macro library matches the hand-derived `formatHongRect`
  reference vector byte for byte, including a modifier event encoded as `attr 0x82/0x02` + bitmask.
- [ ] Given the device read-back after upload differs from what was sent → `UploadMacrosAsync`
  returns false and the App does not write the key binding (existing skip path).
- [ ] Given a board reporting `MacroBytes = 0` → upload is refused before any write (unchanged).
- [ ] Hardware (QA): a macro bound on Cyrex physically plays; survives replug; a macro containing
  Shift/Ctrl plays with the modifier applied; Aress behaves identically; Raster macros still work.
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

Bug fix (`fix-*`) — the reproducing tests come first and fail on current code:

1. `DeviceDetailsKeyboardMacroBindingTests`: assert the Cyrex binding write is
   `{0x50, slot, mode}` — **fails today** (current code sends `0x60`). Same for repeat → `0x51`.
   Then the fix makes them pass. A parallel Raster-shaped test pins `0x60`/`0x61` and must pass
   before AND after.
2. `SmartInfiniteMacroEncoderTests` (new code, exhaustive): header/offset/block/action layout per
   the Evidence section; modifier translation; empty library (header + empty table only, buffer
   still region-sized); library exceeding the region → truncation counters set.
3. Service-level behaviour (upload refusal on `MacroBytes = 0`, verify-mismatch → false) is
   covered to the extent the existing test seams allow; the HID exchange itself is not unit-tested
   (no hardware in CI), matching how the key-mapping service is treated.

**Deliberately not automated:** real playback on boards, the `0x1C` write being accepted by
firmware (QA below), Rampage behaviour.

## QA (manual test steps)

0. (Engineering pre-step, once, before the QA pass.) With the vendor's Keyborder-v3 app installed,
   record a USB capture (Wireshark + USBPcap) of Keyborder writing one macro to the Cyrex. This
   confirms the write command, the transfer size and the header bytes 2–3 semantics directly from
   the wire — cheaper than a QA round-trip if any of them was mis-read from the app's source — and
   the captured bytes become a second reference vector in `SmartInfiniteMacroEncoderTests`.
1. Connect the PG Cyrex by cable and launch the app. Open the keyboard's page → Macros. Record a
   simple macro (e.g. types "abc") and save it.
2. Bind the macro to a key (e.g. F12) on the Key Assignments page. Expected: no error toast.
3. Press the bound key in a text editor. **Expected: the macro plays — "abc" is typed.**
4. Record a second macro containing a modifier combo (e.g. Ctrl+C, Ctrl+V), bind it, use it on
   selected text. Expected: copy/paste actually happens (modifiers work).
5. Bind a macro with repeat count 3. Expected: one press produces the sequence three times.
6. Unplug and replug the keyboard; press the bound keys again. Expected: macros still play
   (stored on the board, not the PC).
7. Rebind the key back to its normal letter. Expected: the key types normally again.
8. Repeat steps 1–3 on the Aress.
9. Repeat steps 1–3 on a Raster or T-Racer. Expected: macros still work there (no regression).
10. Repeat steps 1–3 on the Rampage — its Macros page is newly enabled by this change and this
    run is the first hardware evidence for it. If binding fails with an error toast (the upload's
    read-back verify refusing), record the log and report it: the enable flag then gets reverted
    for Rampage alone.

## Review focus

1. `thread-safety` — the macro service must keep serialising through `SmartInfiniteDeviceGate`
   and hold it across write + verify.
2. `async/await` — unchanged gate/Task.Run pattern preserved through the transport migration.
3. `null-safety` — encoder handling of empty/oversized libraries and absent modifier mappings.
4. `performance hot-path` — none (user-initiated uploads only); flag anything added per-keystroke.

## Performance & quality

No hot paths: uploads run on save/bind/connect, a ~1 KB transfer (19 packets) plus an equal
read-back — well under the existing 2048-byte transfer it replaces. No new allocations outside
the upload call. No SQLite or gRPC contract changes (the existing `SetMacros`/`SetKeyMapping`
RPCs carry everything).

## Risks

- **`0x1C` is extracted, not hardware-verified.** Same class of risk as the actuation work, with
  the same self-limiting design: the authoritative read-back verify means a discarded write
  fails the upload and no binding is written — worst case macros stay broken with a precise log,
  never silently half-working. QA step 3 is the moment of truth.
- **Rampage moves with the family without its own hardware evidence.** If Rampage firmware
  actually wants the old shape, its macros go from "broken one way" to "broken another way" — QA
  step 10 captures the data; a per-model split remains cheap to add. **Rollback cost is accepted
  consciously (PR review m6):** the `SupportsMacros` flag lives in the DeviceHost's
  `appsettings.json`, which ships with the build — reverting Rampage alone means a new build.
  Accepted because releases here are manual and QA step 10 runs before any release cut; a failed
  step 10 reverts the flag in the same pre-release cycle, not in the field. The failure mode is
  also observable now (the Macros-page banner), not silent.
- **Header bytes 2–3 semantics** (buffer length per the app vs "used length" per the sheet):
  we follow the app; if firmware validates against the other meaning the verify read-back
  catches it on the first QA run.
- The CONTEXT.md macro-type entry flips for the third time — the entry now names the per-family
  truth and its source explicitly so a fourth flip would require new hardware evidence, not
  another document.

## Open questions

1. None blocking. (If QA step 10 shows Rampage rejecting the new shape, decide then between a
   per-model service split or reverting Rampage alone to the old constants.)

## Review findings

### Round 1 — external PR review (PR #85, boikoserhii84, approved with findings) — all items closed

Findings live in the PR comment; closures:

- [x] **M1. Read-back verify moved onto the writing handle** — the verify now runs in its **own
  session** (`SmartInfiniteMacroService.UploadOnce`: two `OnHandle` calls with the staging-buffer
  rationale in comments), matching the T800 service's shape. Pinned by
  `SmartInfiniteMacroServiceTests.StagingOnlyFirmware_FailsTheUploadInsteadOfVerifyingItsOwnEcho`,
  whose fake stages writes per-session exactly like the measured `0x18` behaviour.
- [x] **M2. Silent upload failure** — new `MacroUploadFailed` VM flag + localized banner (en/uk)
  on the Macros page (shared `warningBanner` style extracted to `App.axaml`, the Key Assignments
  banner refactored onto it); `UsbHidDeviceService.SetMacrosAsync` now raises `NotifyError` on a
  clean-RPC `Success == false`. Pinned by
  `Cyrex_UploadFailure_SurfacesTheFlagAndSkipsTheBind`. QA step 10 reworded to the banner.
- [x] **m1. Deterministic refusal retried 4×** — `UploadOnce` returns an `UploadOutcome` marker:
  `Rejected` (no macro space / verify mismatch) is final and never retried; only transient
  failures (open/transfer never completed) loop. Pinned by the session counts in
  `SmartInfiniteMacroServiceTests` (2 sessions on refusal, not 8).
- [x] **m2. Full-buffer hex dump at Error level** — mismatch log now prints a 32-byte window
  around `FirstDivergence`; the full dump moved to `LogLevel.Debug`.
- [x] **m3. CONTEXT.md central entry contradicted the new ones** — the **Smart Infinite protocol**
  entry now says macros `0x1B`/`0x1C` and a Device-Info-sized macro region.
- [x] **m4. Format confirmed by transcription, not capture** — QA step 0 added: a one-off USB
  capture of Keyborder writing a macro, whose bytes become a second reference vector in
  `SmartInfiniteMacroEncoderTests`. (The capture itself is hardware work and stays with QA.)
- [x] **m5. Zero tests on `SmartInfiniteMacroService`** — `ISmartInfiniteIo` seam added to
  `SmartInfiniteTransport` (internal test constructors on transport and service), plus
  `SmartInfiniteMacroServiceTests` with a scripted fake firmware: 4 tests covering fresh-session
  verify, staging-only refusal, `MacroBytes = 0`, and transient-open retry.
- [x] **m6. Rampage rollback costs a release** — decided consciously: flag stays enabled, rollback
  = pre-release config revert; recorded in Risks.
- [x] **m7. Dead constants / stale comments** — `KnownTypes` now uses the legacy-type constants
  and its `0x52` margin is explained; `RasterMacroBindingTypeRepeat` kept deliberately with a
  "reserved until 0x61 is verified on Raster" comment.
- [x] **m8. Unreachable cancellation catch** — `UploadMacrosAsync` re-raises cancellation
  (`ThrowIfCancellationRequested` when the retry loop returns null on a fired token), so the
  `catch (OperationCanceledException)` is reachable again and the comment matches behaviour.
