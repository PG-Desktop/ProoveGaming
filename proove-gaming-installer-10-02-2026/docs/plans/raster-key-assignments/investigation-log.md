# Raster Key Assignments — Investigation Log

**Date:** 2026-07-10 → 2026-07-13
**Branch:** `features/PG-331-raster-keyassignments`
**Related:** [raster-key-assignments-plan.md](raster-key-assignments-plan.md),
[protocol-reference.md](protocol-reference.md)

Chronological log of what was tried and what was learned. Kept for future
reference so we don't repeat dead-ends.

---

## Starting point

- Prior Rampage attempt on `features/PG-331-keyassignments` reached a wall:
  writes committed to storage but never propagated to the scan matrix.
- Vendor materials for Raster provided by the client:
  - `Shortcut Card Proove Gaming Raster 20.05.2026.pdf` — user-facing FN shortcuts.
  - `Software macro definition table (1) (1).xlsx` — macro storage format only.
  - `Raster keyboard dist (2).zip` — compiled Next.js webapp targeting the
    vendor's PID family `0x36B0/0x3002..0x352F`.
  - `dist (5).zip` — related webapp snapshot.
  - `FW (1) (5).zip` — firmware update executable (untested).

## Phase 1 — Reverse-engineering the vendor webapp

The webapp's compiled `page-*.js` chunk revealed:

- Vendor prefix byte `0xAA` (called `REPORT_ID` in the source).
- Command bytes: `0x10` START, `0x11` STOP, `0x1A`/`0x1B` GetUser/SetUser for
  profile 0, `0x1C`..`0x21` for profiles 1..3, `0x16`..`0x19` GetDefault, etc.
- Set-key packet layout: `[0xAA, cmd, addr_lo, addr_hi, len=3, 0, 0, 0, type,
  code1, code2]`.
- Type bytes: `0x10` = HID keyboard, `0x30` = mouse, `0x50` = media.
- **No CRC** and **no separate commit command** — writes are per-report
  atomic (per the vendor code).
- Layout JSONs exist per PID (e.g. `36b0_304C_0.json` for QK75 Pro V2), but
  **no `36b0_3061_0.json`** — our specific Raster PID isn't in the shipped
  webapp. The webapp only supports a range of related PIDs (SMK/QK family)
  which suggests the vendor targets multiple similar keyboards.

Initial (wrong) assumption: use the vendor webapp's format directly with our
Raster.

## Phase 2 — Dead ends

Four packet-format variants were tested, each rebuild + fresh test. All failed
until the last iteration.

| # | Interface | ReportId | `0xAA` prefix | Result |
|---|-----------|----------|---------------|--------|
| 1 | `mi_01` 65/65 | `0x00` | yes | Set-ack echoed our value but firmware left slot at `type=0x0F` |
| 2 | `mi_01` 65/65 | `0x00` | no | Same as (1) — value not persisting |
| 3 | `mi_02&col05` 64/64 | `0x3F` | yes | No distinct response; only periodic battery pushes |
| 4 | `mi_02&col05` 64/64 | `0x3F` | no | Same as (3) |

At this point we drafted the "we've hit a firmware wall" report for the client.

**Turning point:** the client clarified they had been testing in **wired USB
mode** the whole time (not 2.4G dongle as we'd been assuming). That reframed
the problem — the vendor webapp targets exactly the USB path, so our approach
had to be correct in shape. The remaining question was byte-level detail.

## Phase 3 — Diagnostic pass

Three probes added simultaneously to `RasterKeyboardKeyMappingService.SetKeyMappingAsync`:

1. `GET_FUNCINFO (0x14)` — active profile.
2. `GET_DEFAULT_0 (0x16)` for the target slot — verify indexing.
3. `type=0x0F` instead of `0x10` — matching what firmware echoed.

**Result** — one write to slot 44 with the new format:

```
default idx=44: 00-AA-16-84-00-03-00-00-55-10-00-30
                                              ^^^^^^^
                                       type=0x10, code=0x30 (']')
set-ack:        00-AA-1B-84-00-03-00-00-55-0F-00-14
                                              ^^^^^^^
                                       our type=0x0F persisted, code2=0x14 (Q)
get-after:      00-AA-1A-84-00-03-00-00-55-0F-00-14
                                              ^^^^^^^
                                       value STUCK ✓
```

**Two revelations at once:**

- **The remap mechanism works.** `get-after` returned our value; earlier
  "value not persisting" reads had been misparsing responses because we
  weren't accounting for the `0xAA` shift in the response layout.
- **Slot 44 is `]`, not `W`.** The QK75-based catalog is wrong for Raster.
  Our writes had been reaching the correct firmware slots — they just didn't
  correspond to the physical keys we thought.

Client confirmed: pressing physical `]` now emitted... nothing (silent).

## Phase 4 — Type-byte confusion

`type=0x0F` made `]` silent — not the desired remap effect. Re-reading the
`GET_DEFAULT_0` response for slot 44:

```
type=0x10, code1=0x00, code2=0x30
```

So `type=0x10` **is** the correct "keyboard key" marker. Our earlier
misinterpretation: we thought firmware "normalized" `0x10` → `0x0F` in the
set-ack. Actually the `0x0F` we were seeing was at a different byte position
in the response — a "no override" sentinel, not our type echo.

**Fix:** send `type=0x10` (not `0x0F`).

Client confirmed: physical `]` now emitted `Q`. Physical `H` now emitted
whatever was written to slot 54.

## Phase 5 — Correct matrix indexing

The QK75 layout doesn't match Raster's actual matrix. To find the real
`scancode → slot` map without a vendor-supplied JSON for our PID: **runtime
probe**.

`ProbeDefaultMatrixAsync` was added to iterate slots 0..127 sending
`GET_DEFAULT_0`, filtering for keyboard-type records with no modifier, and
building the scancode → slot map. Runs once per device connect (~640 ms).

Result: all 81 keys correctly map to their physical slots. Remapping W → Q
now works as expected.

## Phase 6 — UI ↔ device desync

Client noticed: if the app writes W → Q, then the user closes the app, does a
manual factory reset (FN+Space), and reopens the app — the UI still shows
"Q" because SQLite still has the intent. UI was lying about actual device
state.

**Fix:** device is the truth source. Added `ProbeUserOverridesAsync` (mirror
of the defaults probe using `GET_USERKEY_0`) — reads the current user-override
state directly from firmware. UI display is synced from that map, not from
SQLite.

`RestoreSavedKeyAssignmentsAsync` (which auto-wrote all SQLite entries back to
the device on connect) was removed — it would have silently undone factory
resets.

## Phase 7 — SQLite retirement

With the device as truth source and no auto-restore, SQLite was pure dead
writes. Removed:
- `key_assignments` table.
- `GetKeyAssignments`, `SaveKeyAssignment`, `ClearKeyAssignment` methods.
- All SQLite calls in `ApplyAssignmentTargetAsync`.

State now lives only in firmware + in-memory `_userOverrides` map (refreshed
per device connect).

## Final state

- Physical remap works instantly (no unplug/replug).
- UI accurately reflects device state at all times.
- SQLite is not involved in key-remap state.
- Diagnostic reads (`GET_FUNCINFO`, `GET_DEFAULT`, `GET_USERKEY` before/after)
  are still in `SetKeyMappingAsync` — worth stripping down for a clean prod
  path (`Start → Set → Stop`) as a follow-up.

## Lessons

1. **Empirically test parsing assumptions.** The "value not persisting" reads
   in Phase 2 were parse errors on the response layout, not real firmware
   behavior. Adding a full byte dump earlier would have caught this.
2. **Runtime probe > static tables** for hardware families where the vendor
   ships incomplete JSON schemas.
3. **Device is the truth source.** Local persistence layers that silently
   overwrite device state produce subtle UX bugs (Phase 6). If persistence is
   needed at all, it should be for user-explicit "presets", not automatic
   sync.
4. **Clarify the setup early.** The 2.4G vs USB confusion in Phase 2 cost
   multiple iterations. First question in any hardware protocol debug should
   be "what mode is the device in right now?".
