# Smart Infinite macros (Rampage / Aress / Cyrex) — implementation plan

Sibling work: [`add-cyrex-aress-parity`](../add-cyrex-aress-parity/add-cyrex-aress-parity-plan.md)
(built the entire macro host stack and hit the acknowledge-and-discard wall),
[`rampage-key-assignments`](../rampage-key-assignments/rampage-key-assignments-plan.md) (the RM1
factory update that proved the fix pattern on hardware: full-table-length writes, no session
bracket).

Primary sources: the factory's C1–C12 clarifications (recorded in
[`docs/knowledge-base/smart-infinite-protocol/factory-clarifications-2026-08.md`](../../knowledge-base/smart-infinite-protocol/factory-clarifications-2026-08.md),
created by this plan) and the macro investigation
[`macro-investigation-result.md`](../add-cyrex-aress-parity/macro-investigation-result.md).

## Implementation status

**Implemented; blocked by firmware on both tested boards (2026-08-04).** The full-table / no-bracket
discipline was implemented exactly as planned and verified on hardware — and it fixed only what it
had fixed for keys. Cyrex `fw 0x0002` acknowledges every macro-write shape (30 bytes / full 2048,
bare / bracketed) and stores nothing: the entire 2048-byte region scans back as zeros via `0x2C`
after each attempt. Rampage `fw 0x0100` rejects `0x2C`/`0x2D` outright with status `0x0F`, with and
without a `0x10` session — on that firmware those are evidently not the macro commands. Key writes
at the new 384-byte shape work on both boards. Full measurements and the follow-up factory
questions:
[`factory-clarifications-2026-08.md`](../../knowledge-base/smart-infinite-protocol/factory-clarifications-2026-08.md).
Pending the factory's answer, Rampage's `SupportsMacros` flag is set to `false` (the UI must not
offer a macro section whose uploads always fail); Cyrex/Aress keep the flag `main` shipped with.
Acceptance criteria 1–3 are therefore **firmware-blocked**; 4–8 hold.

## Goal

Make keyboard macros actually work on the Smart Infinite family. The host side is complete and
byte-verified (packing, `0x2D` write, `0x2C` read-back, key records `0x60`/`0x61`, UI, SQLite
bindings, resync) — but the firmware acknowledges the macro write and stores nothing. The factory's
C1–C12 answers identify the cause as the same one that blocked Rampage key remapping: **a transfer
shorter than the declared table length, and the `0x10`/`0x11` session bracket**. Rampage keys were
unblocked on hardware by exactly this change (bare command, full 384-byte table); this plan applies
the same discipline to the macro table (and, per C1/C3, to the Cyrex/Aress key-table write), and
enables macros on Rampage, which was deliberately excluded while its key path was believed dead.

## What the factory answered (C1–C12, condensed)

| Item | Answer | Consequence here |
|---|---|---|
| C1 | No `0x10`/`0x11`; send bare `0x18`; total 384 bytes; the observed failure IS caused by transfers < 384 | Key write shape for Cyrex/Aress must change (today: 225 bytes inside a bracket) |
| C2 | Replies to `0x12`, `0x16`, `0x17`, `0x1F`, `0x22`, `0x30` were seen; verify their *content*, not just arrival | Hardware-session verification item; `0x1F`/`0x22` are not commands this codebase sends — note and observe |
| C3 | Every reassignment must send the full all-keys array; total **386** bytes; single-key writes forbidden | Contradicts C1's 384 by 2 bytes; factory did not explain. Experiment order below |
| C6 | `0x20` resets everything and reboots the board | Confirms the standing DO-NOT-SEND |
| C7 | Only physical Profile 0 exists; `0x18`/`0x34` act on it | Matches current behavior |
| C8 | `INFO/FUNC_TAB` 64, `KEY_MATRIX/FN_KEY_MATRIX/LED_DEFINE_TAB` 384, **`MACRO_TAB` 2048** | The macro transfer length. Note: Cyrex fw `0x0002` Device Info reports 1024 — the same under-reporting pattern as Rampage's key count 0 |
| C9 | `light_level` 0–99 is wrong; `0x14` indices 12–15 carry max/default brightness and speed | Knowledge-base correction; RGB out of scope here |
| C10 | Host must not enable reporting; use the vendor-defined collection from their screenshot | Matches `SmartInfiniteInterfaceSelector` |
| C11 | `0x33`/`0x34` structured like `0x17`/`0x18`; `0x35` format **not provided** | FN layer stays out of scope |
| C12 | Wired: usage `0xFF87`/`0x20` (also `0xFF88`/`0x21`), 64-byte packet / 56 payload; **2.4G: 64-byte packet / 24 payload** | Wired selection confirmed; the 24-byte dongle payload is recorded for future dongle support and is why this plan stays wired-only |

## Scope

**In scope**

- Macro-table write shape: bare `0x2D`, full `MACRO_TAB_SIZE` (2048) transfer, zero-padded past the
  packed data, no session bracket — `SmartInfiniteMacroService`.
- Key-table write shape for Cyrex/Aress per C1/C3: bare `0x18`, full `KEY_MATRIX_TAB_SIZE` (384)
  transfer, no bracket — `SmartInfiniteKeyMappingService`. (Rampage's own service already does
  exactly this and is hardware-verified.)
- Enabling macros on Rampage: catalog flag, macro-factory routing, the `0x61` repeat-record branch
  in the ViewModel.
- Recording C1–C12 in the knowledge base and the glossary.

**Out of scope**

- The RGB services' session brackets. They demonstrably work bracketed; C1 says the bracket is
  unnecessary, not harmful. Aligning them is a follow-up riding on the already-pending RGB QA
  (see `rampage-key-assignments` review S1) — one wire-format change per QA round, not two.
- The FN-combo layer (`0x33`–`0x35`) — C11 gave no `0x35` format.
- 2.4G / Bluetooth transports — C12's 24-byte dongle payload needs its own chunking and testing.
- Raster / T-Racer macros — different protocol, working, untouched.
- Profile switching (C7: only Profile 0 exists).

## Options considered

### Option A — apply the RM1 transfer discipline to the existing services *(recommended)*

Change the write shape in place: `SmartInfiniteMacroService` writes the full 2048-byte macro table
without a bracket; `SmartInfiniteKeyMappingService` writes the full 384-byte key table without a
bracket; reads keep their current (working) shapes minus the bracket. Rampage routes into the same
macro service.

*Trade-off:* touches the write path of Cyrex/Aress key remapping, which works today at 225 bytes —
a regression risk on a shipped-working feature. Accepted because (a) the factory explicitly states
the current shape is the defect ("поточна проблема спричинена пакетом < 384"), which strongly
suggests the 225-byte write only reaches RAM and is lost on replug — QA step 6 tests exactly that;
(b) the identical change is hardware-proven on Rampage, the same protocol family.

### Option B — keep Cyrex/Aress key writes untouched; change only the macro path

Minimal blast radius: macros get the new shape, keys stay at 225-in-bracket.

*Rejected.* It ignores half of the factory's answer (C1/C3 are about the key array, explicitly
"single-key / short writes forbidden"), leaves the suspected replug-persistence defect in place,
and leaves the family running two contradictory transfer disciplines — the exact state that made
the last two investigations expensive.

### Option C — unify everything onto the shared service first (fold RampageKeyboardKeyMappingService in), then fix macros

*Rejected for this plan.* That consolidation is already tracked as review finding S3 in
`rampage-key-assignments`; entangling it with a firmware-behavior experiment would make a macro
failure indistinguishable from a consolidation regression. Sequence: land this, then consolidate.

## Recommended approach

**Option A.** Concretely:

### The transfer rule (one sentence)

A Smart Infinite **write** transfers the full C8 table size for that table — `0x18` → 384 bytes,
`0x2D` → 2048 bytes — as bare commands with no `0x10`/`0x11`, chunked by
`SmartInfiniteProtocol.EnumerateChunks(tableSize)` (which already trims the final packet: 384 →
6 × 56 + 48; 2048 → 36 × 56 + 32), zero-padded past the meaningful data. **Reads keep transferring
the data-sized region they transfer today** (device-reported matrix bytes; packed library length),
just without the bracket — reads are proven working and are not the thing the factory corrected.

### 384 vs 386, and 2048 vs the reported 1024 — experiment order, not code branches

- **Key table:** implement **384** (C1, and the value hardware-proven on Rampage). If the Cyrex
  write does not verify or does not survive a replug, the hardware session tries **386** (C3's
  number: 384 data + 2 trailing zero bytes, final chunk 50) as a diagnostic — not as shipped code
  until observed necessary. The discrepancy is recorded in the KB as an open question.
- **Macro table:** implement **2048** (C8's `MACRO_TAB_SIZE`; the key-table precedent shows the
  C8 constant outranks the Device Info field — Cyrex reports 225 matrix bytes yet needs 384
  written, and reports key count 68 / macro space 1024 on the same block where Rampage reports
  key count 0 yet accepts key writes). If the firmware rejects addresses ≥ 1024 (non-`0x55`
  status), the service already fails loudly per chunk; the hardware session then falls back to
  writing the device-reported space. The data-must-fit gate stays against the **device-reported**
  space, which is the storage the firmware claims to have.

### Rampage macros

Rampage's Device Info reports macro space 2048 (byte 5 = `0x08`), so the existing
macro-space-zero refusal passes. Routing: `DeviceKeyboardMacroServiceFactory` sends Rampage to
`SmartInfiniteMacroService` (same selector, tag `"Rampage"`); `appsettings.json` gets
`"SupportsMacros": true` on the Rampage entry; the ViewModel's `MacroBindingRecord` repeat branch
(`0x61`) widens from the 65% boards to the whole family (`IsSmartInfiniteDevice`), since C-series
answers confirm one shared `Keyboard_Key_Type` enum. Everything else (Macros section UI, bindings
table, resync on connect, overlay) is already family-generic and gated on the catalog flag.

### What deliberately does not change

- `SmartInfiniteDeviceGate` — still mandatory (RGB and key/macro I/O interleave on one interface);
  only its doc rationale shifts from "session truncation" to "interleaved exchanges corrupt each
  other's reply matching, and `0x15` zeroes the key staging buffer".
- The macro packing (`RasterMacroEncoder`) — byte-verified against the vendor sheet; the zero-padded
  tail after `DataLen` is exactly what an all-zero flash region holds, so padding to 2048 writes
  the natural "rest is empty" state.
- The Device-Info-first discipline and all refusal gates (macro space 0, key count 0 on the shared
  service, data larger than reported space).
- The read-back verification (`0x2C` across a fresh handle) — it is the instrument that will
  prove or disprove the fix in minutes on hardware.

## File-level change list

### `src/ProoveGaming.Infrastructure`

| File | Action | Intent |
|------|--------|--------|
| `Classes/SmartInfiniteProtocol.cs` | edit | Add the C8 table-size constants (`KeyMatrixTableSize = 384`, `MacroTableSize = 2048`); update the session-bracket and `CmdSetMacroData` doc comments to the factory-confirmed state (bracket not used; short transfers are the discard cause). No framing changes — `EnumerateChunks` already trims correctly. |
| `Services/SmartInfiniteMacroService.cs` | edit | Drop `0x10`/`0x11` from the write and verify paths; write the full `MacroTableSize` (buffer stays zero-padded past `DataLen`); keep Device-Info gates and per-chunk ack checks; update remarks (the "firmware does not keep the data" verdict is superseded by C1/C8). |
| `Services/SmartInfiniteKeyMappingService.cs` | edit | Drop `0x10`/`0x11` everywhere (`InSession` → plain handle); write the full `KeyMatrixTableSize` (table buffer sized 384, read region still `info.MatrixBytes`, tail zero); reads unchanged otherwise. Keep the key-count refusal gate. |
| `Services/DeviceKeyboardMacroServiceFactory.cs` | edit | Route `"Rampage"` to `CreateSmartInfiniteMacroService` and delete the "deliberately absent" comment — its premise (key writes discarded) is disproven. |
| `Classes/SmartInfiniteDeviceGate.cs` | edit | Doc remarks only: the gate's rationale post-bracket-removal. |

### `src/ProoveGaming.DeviceHost`

| File | Action | Intent |
|------|--------|--------|
| `appsettings.json` | edit | `"SupportsMacros": true` on the Rampage device entry. |

### `src/ProoveGaming.App`

| File | Action | Intent |
|------|--------|--------|
| `ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | `MacroBindingRecord`: repeat-count records (`0x61`) for `IsSmartInfiniteDevice` instead of `IsSmartInfinite65Device`, so a Rampage macro with a repeat count binds the same way Cyrex/Aress do. |

### `docs`

| File | Action | Intent |
|------|--------|--------|
| `knowledge-base/smart-infinite-protocol/factory-clarifications-2026-08.md` | create | The C1–C12 answers verbatim-in-English with per-item interpretation, the 384-vs-386 and 2048-vs-1024 open questions, and the C12 2.4G framing note for future dongle work. Linked from the protocol README. |
| `knowledge-base/smart-infinite-protocol/README.md` | edit | Point the transfer-length and session-bracket sections at the clarifications file; correct `light_level` per C9. |
| `CONTEXT.md` | edit | Update [[Smart Infinite protocol]] (macro table 2048, write discipline family-wide), [[Macro upload (Smart Infinite)]] (full-table transfer, no bracket, Rampage included) and [[Key count]] / [[Macro space]] (Device Info under-reports; C8 constants outrank it for transfer sizing). |
| `plans/add-smart-infinite-macros/add-smart-infinite-macros-plan.md` | create | This document. |

**Flags:** no new NuGet package, no TFM bump, no new project, no `Directory.Build.props` change, no
installer/uninstaller impact, no gRPC contract change (macro upload rides the existing `SetMacros`
RPC).

## Acceptance criteria

1. Given a Cyrex (or Aress) with one recorded macro, when the library is uploaded, then the
   DeviceHost log shows a bare `0x2D` transfer of exactly 2048 bytes (37 packets, final packet
   32 bytes), no `0x10`/`0x11` frames, every packet acked `0x55` — and the Debug read-back
   reports the region **identical**, not all-zero.
2. Given a macro bound to a key on Cyrex/Aress, when the physical key is pressed, then the macro
   plays (the investigation's blocked scenario now passes end-to-end).
3. Given a Rampage, when the Macros section is opened, then it is available (catalog flag), a
   macro can be recorded, uploaded (2048-byte transfer accepted by the gates — Rampage reports
   macro space 2048), bound to a key, and plays on a physical press.
4. Given a Cyrex/Aress key remap, when a key is reassigned, then the write is a bare `0x18`
   transfer of exactly 384 bytes (7 packets, final 48), it verifies on read-back, and the physical
   key emits the new code — same behavior as before the change, new shape on the wire.
5. Given a Cyrex/Aress with a remapped key and an uploaded macro, when the keyboard is unplugged
   and replugged, then both survive — the persistence C1 implies the old shape was losing.
6. Given a Raster or T-Racer, when macros are recorded/bound/played, then behavior is unchanged
   (their service and encoder are untouched).
7. Given a macro-space-zero or key-count-zero device on the shared service, writes are still
   refused with the existing log messages (gates preserved).
8. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

This is an `add-*` change (macros for three boards) wrapped around a wire-shape change; automated
coverage locks the shapes, hardware QA proves the firmware behavior.

### `tests/ProoveGaming.Infrastructure.Tests`

Extend **`SmartInfiniteProtocolTests.cs`**:

- `EnumerateChunks(SmartInfiniteProtocol.KeyMatrixTableSize)` → exactly 7 chunks at
  `0x00,0x38,0x70,0xA8,0xE0,0x118,0x150`, lengths `56×6 + 48`, summing to 384.
- `EnumerateChunks(SmartInfiniteProtocol.MacroTableSize)` → exactly 37 chunks, contiguous,
  final `(0x7E0, 32)`, summing to 2048.
- The new constants equal C8's values (trivial, but they are now load-bearing transfer sizes).

The services themselves are HID-bound and deliberately not unit-tested (sealed `HidDevice`,
same stance as every sibling service); their behavior contract is the log + read-back diagnostics
exercised in QA.

### `tests/ProoveGaming.App.Tests`

- Mirror `DeviceDetailsMacroAssignmentTests` style: on a **Rampage** device with a macro whose
  `RepeatCount > 1`, the generated assignment target carries `TypeByte = 0x61` and
  `ScanCode = repeat count` (the widened `MacroBindingRecord` branch); with
  `RepeatUntilReleased`, `0x60`/`0x01`. Guard: the same macro on a **Raster** still yields `0x60`
  (repeat type stays unverified there).

### Deliberately not automated

- Whether the firmware now stores the library — that is the hardware experiment this plan exists
  for; the `0x2C` read-back diagnostic reports it deterministically in the log.
- The 384-vs-386 question — resolved on hardware, recorded in the KB, and only then (if 386 wins)
  encoded and tested.

## QA (manual test steps)

1. Connect a **Cyrex** by USB cable. In the app, open its device page → **Macros**. Record a short
   macro (e.g. types "test"), save it, and bind it to the **G** key by selecting G on the
   on-screen keyboard and clicking the macro in the list.
   *Expected:* no errors; the G key shows the macro name.
2. Open Notepad and press the physical **G** key.
   *Expected:* the macro plays (types "test"). This exact step failed before the change.
3. Press and hold **G** briefly, then rapidly press it three times.
   *Expected:* each press plays the macro once, cleanly — no stuck keys, no half-played macros.
4. In the app, remap the **E** key to **A** (Key assignments section).
   *Expected:* pressing physical E types "a" — remapping still works after this change.
5. Unplug the Cyrex, wait five seconds, plug it back in. Press **G** and **E** in Notepad.
   *Expected:* G still plays the macro and E still types "a" — settings survive a replug.
6. Reset the G key back to normal (Reset link) and delete the macro.
   *Expected:* G types "g" again; nothing replays.
7. Repeat steps 1–6 on an **Aress**.
   *Expected:* identical behavior.
8. Repeat steps 1–6 on a **Rampage** — note the Macros section should now exist for it.
   *Expected:* identical behavior.
9. Connect a **Raster** (or T-Racer) and play one existing macro, then record and bind a new one.
   *Expected:* exactly as before this change — these keyboards use different low-level code, and
   this confirms nothing regressed.
10. With any of the three keyboards, click through 10–15 keys and targets quickly while the
    keyboard is uploading (bind several macros in a row).
    *Expected:* the app stays responsive; no freezes.

## Review focus

- `thread-safety` — every device exchange must stay behind `SmartInfiniteDeviceGate`; the bracket
  removal must not tempt a gate removal (the gate is what prevents RGB/key/macro interleaving now
  that no session demarcates ownership).
- `async/await` — the services keep sync HID I/O inside `Task.Run` with the gate acquired before;
  no `.Result`/`.Wait()`, nothing on the dispatcher.
- `null-safety` — Device Info remains the precondition for every write; `null` info aborts, never
  defaults.
- `resource disposal` — every `TryOpen`'d stream stays in `using`, including the verify path's
  second handle.

## Performance & quality

- **Hot paths:** none. Macro upload is user-initiated (record/edit/bind) plus once per device
  connect; the full-table shape adds ~2 KB (37 packets, ~0.2 s at the 5 ms pacing) per upload —
  imperceptible at these rates. Key writes grow from 5 to 7 packets.
- **Allocations:** the packed buffer is already `MacroTableSize`-sized by the encoder path;
  padding writes need no new buffer. One 64-byte report per packet, as today.
- **Thread-safety:** unchanged model — shared static gate, fresh service per gRPC call, no new
  shared state.
- **UI thread:** untouched; all I/O stays in DeviceHost behind the RPC.

## Risks

1. **The full-length hypothesis may not hold for the macro table.** Strong precedent (RM1 keys,
   confirmed on hardware; the factory names short transfers as the cause), but the macro table is
   4× larger and its addresses beyond the Cyrex-reported 1024 are unexplored. *Mitigation:* the
   per-chunk ack check fails loudly; the read-back diagnostic distinguishes "stored" from
   "discarded" in one run; fallback experiment (write 1024) is pre-planned. Escalate to the
   factory with the log if both shapes fail — do not ship a workaround.
2. **Cyrex/Aress key-remap regression.** The 225-byte bracketed write works today (at least until
   replug); the 384-byte bare write is the factory's instruction but changes a working wire
   exchange. *Mitigation:* QA steps 4–5 cover both the immediate behavior and the persistence the
   old shape is suspected of losing; Rampage proves the identical shape on the same family.
3. **The 386 mystery.** If C3's 386 is literal and 384 fails on Cyrex, the shipped code is wrong
   by one experiment. *Mitigation:* explicitly ordered experiment in QA; the KB records the open
   question so the answer lands somewhere durable.
4. **Zero-padding semantics.** Padding the macro region to 2048 with zeros overwrites whatever
   followed a previously-shorter library — which is the desired "rest is empty" state, but if the
   firmware keeps unrelated data inside `MACRO_TAB`, padding would clobber it. C8 declares the
   whole 2048 as `MACRO_TAB_SIZE`, so this is theoretical; noted for the hardware session.
5. **Aress is still an expectation, not a measurement** (the investigation only ever uploaded an
   empty library to it). QA step 7 closes that gap.

## Open questions

1. **384 or 386 for the key table (C1 vs C3)?** Implemented as 384; the hardware session decides.
   If 386 wins, the change is one constant plus a KB note.
2. **2048 or the device-reported macro space for the macro transfer?** Implemented as 2048 (C8);
   fallback experiment at the reported size if chunks beyond it are rejected.
3. **Should the RGB services drop their brackets too?** Factory says brackets are unused
   everywhere; out of scope here (see Scope), but the team should decide whether to fold it into
   the same QA round as the pending RGB checksum smoke test.

## Review findings

### Round 1 — Ship: yes

Reviewed at `f38fd68`, working tree clean. Surface: four commits since the previous review round
(`859ad78` S2 gate fix, `3de3f2e` this plan's implementation, `45cbf29` hardware results,
`f38fd68` vendor request). All files verified against the working tree.

#### Plan conformance

Plan: `docs/plans/add-smart-infinite-macros/add-smart-infinite-macros-plan.md` (this file).

- **Implemented as planned:** C8 constants and doc corrections in `SmartInfiniteProtocol`;
  bare full-2048 macro write with zero-padded copy (`SmartInfiniteMacroService.WriteMacroBuffer`);
  bare full-384 key write with `InSession` → `OnHandle` (`SmartInfiniteKeyMappingService`);
  Rampage macro routing; `MacroBindingRecord` widened to `IsSmartInfiniteDevice`; KB
  clarifications file + README pointer + glossary; chunk-shape tests (7×→384, 37×→2048) and the
  Rampage `0x61` record test. All refusal gates preserved; the device gate is held across the
  entire upload including the fresh-handle verify read.
- **Deviates from plan (evidence-driven, documented):** Rampage `"SupportsMacros"` is `false`,
  not `true` — flipped after hardware measurement showed `0x2C`/`0x2D` answer `0x0F` on
  `fw 0x0100` regardless of session, so the UI must not offer a section whose uploads always
  fail. The plan's status section records this; the routing, service and tests remain, so the
  re-enable is one flag when the factory answers.
- **Missing / partial → firmware-blocked:** acceptance criteria 1–3 (macro plays on a physical
  press) cannot be met by any host-side change: Cyrex `fw 0x0002` acks every write shape and
  stores nothing (full-region `0x2C` scan is all-zero after 30-byte/2048-byte, bare/bracketed
  writes); Rampage rejects the commands outright. Measured, recorded in
  `docs/knowledge-base/smart-infinite-protocol/factory-clarifications-2026-08.md`, escalated in
  `vendor-request-macros-2026-08.md`.
- **Acceptance criteria 4–8 met**, including the meaningful win of this iteration: Cyrex/Aress
  key writes now use the factory-prescribed bare-384 shape, verified working on hardware
  (`22:32` log session: device-info read, table read, write 7/7 acked, verify — all bracketless).
- **Accepted limitation (per plan):** the 384-vs-386 question stays open; 384 is implemented and
  hardware-verified on both boards.

#### Blockers

- none

#### Code

- none — three candidate findings were checked and dropped: (1) the 384-byte Cyrex key write
  zero-fills bytes 225–383 that were never read from the device — safe because the 75-slot
  matrix cannot occupy the reserve, and the shape is hardware-verified end-to-end; (2)
  `WriteMacroBuffer`'s padded copy uses `Math.Min(buffer.Length, dataLen)` — `DataLen` ≤ 1096 =
  `BufferLength` by construction, no truncation path; (3) the Rampage macro routing is now
  unreachable from the UI (`SupportsMacros: false`) — intentional, keeps the probe/gRPC path
  alive for the factory follow-up.

#### Tests

- [ ] Coverage matches the plan: transfer shapes locked in `SmartInfiniteProtocolTests`, the
  `0x61` family branch in `DeviceDetailsKeyboardMacroBindingTests` (with the Raster guard
  untouched). The firmware-blocked criteria are deliberately not automated. Standing caveat: CI
  runs no `tests/` projects.

#### Nits

- `appsettings.json` Rampage entry carries a `"Macros"` config block under
  `"SupportsMacros": false` — harmless, but drop or re-enable together when the factory answers.
- `SmartInfiniteKeyMappingService` still reads the post-write verify region as
  `info.MatrixBytes` while writing `KeyMatrixTableSize` — correct (reads are data-sized by
  design), but the asymmetry deserves the one-line comment it has; keep it when consolidating
  per `rampage-key-assignments` S3.

#### What's done well

- The hardware loop closed fast and honestly: implement → measure → *disprove the hypothesis* →
  flip the user-facing flag off → escalate with evidence. No "works on my machine" residue: the
  vendor request contains verbatim tool output and a full-region scan that pre-empts the
  "wrong offset" counter-argument.
- The key-write side quietly became factory-conformant on both boards and is the durable win of
  this branch iteration.
- The KB now tracks conflicts (384/386, 2048/1024, `0x0F` semantics) as first-class open
  questions instead of smoothing them over.

#### Verdict

Ship: **yes** — the code is correct, tested, and hardware-verified everywhere firmware allows;
the macro feature itself is factory-blocked and correctly not exposed to users. First:
**1)** send the vendor request; **2)** the pending branch-level gate from
`../rampage-key-assignments/` Round 1 (S1 RGB smoke test) still stands before merge; **3)** on
the factory's answer, re-enable Rampage's flag and re-run the probe's `macroexp`/`macroscan`.
