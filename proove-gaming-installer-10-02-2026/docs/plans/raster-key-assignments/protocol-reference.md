# Raster Key-Remap Protocol — Reference

Confirmed empirically on **PG Raster** (VID `0x36B0` / `14000`, PID `0x3061` /
`12385`), USB-cable connection.

Extracted from the vendor's compiled Next.js webapp
(`Raster keyboard dist (2).zip` → `_next/static/chunks/app/page-ba6d4e19076b59c9.js`)
and verified by live device probing.

## HID transport

**Interface:** `mi_01` on the Raster USB device.

- Report descriptor: `06-60-FF-09-61-A1-01-09-62-15-00-25-FF-35-00-45-00-65-00-55-00-75-08-95-40-81-02-09-63-91-02-C1-00`
- Usage page: `0xFF60`, usage: `0x61` (vendor-specific, matches the vendor
  webapp's WebHID filter).
- Report size: **64 bytes** output + **64 bytes** input, plus one OS-level
  reportId prefix byte (`0x00`, unnumbered reports).
- HidSharp buffer: **65 bytes** total (byte 0 = OS reportId prefix, bytes 1..64
  = vendor payload).

**Second interface** (`mi_02&col05`, 64/64 with reportId `0x3F`) — the vendor
protocol works there too for battery (cmd `0x30`) but **not for key remap** on
Raster. Historical dead-end noted in [investigation-log.md](investigation-log.md).

## Packet framing

Every request has the same envelope:

| Offset | Value | Meaning |
|-------:|-------|---------|
| 0 | `0x00` | OS reportId prefix (unnumbered) |
| 1 | `0xAA` | Vendor tag (**required** — firmware ignores packets without it) |
| 2 | `cmd` | Command byte (see command table) |
| 3 | `addr_lo` | For addressed commands: memory offset low byte |
| 4 | `addr_hi` | For addressed commands: memory offset high byte |
| 5 | `len` | For addressed commands: payload length (typically `0x03`) |
| 6..8 | `0x00` | Padding / reserved |
| 9..11 | payload | For addressed writes: 3-byte key record (see below) |
| 12..64 | `0x00` | Padding to buffer size |

**Responses** on the same interface use a slightly different layout:

| Offset | Value | Meaning |
|-------:|-------|---------|
| 0 | `0x00` | OS reportId prefix |
| 1 | `0xAA` | Vendor tag echo |
| 2 | `cmd` | Command byte echo |
| 3 | `addr_lo` | Address echo |
| 4 | `addr_hi` | Address echo |
| 5 | `len` | Length echo |
| 6..7 | `0x00` | Padding |
| 8 | `0x55` | **Success marker** (`0x55` = ACK; inverse of the request's `0xAA`) |
| 9 | `type` | Record type byte |
| 10 | `code1` | Modifier bitmask (for keyboard records) |
| 11 | `code2` | HID scancode (for keyboard records) |

## Commands used

| Byte | Name | Notes |
|------|------|-------|
| `0x10` | `START_COMM` | Session start; no address, no payload |
| `0x11` | `STOP_COMM` | Session end |
| `0x14` | `GET_FUNCINFO` | Reads active profile and device state |
| `0x16` | `GET_DEFAULT_0` | Read factory-default record at slot `addr/3` |
| `0x1A` | `GET_USERKEY_0` | Read user override at slot `addr/3` |
| `0x1B` | `SET_USERKEY_0` | Write user override at slot `addr/3` |
| `0x1D`, `0x1F`, `0x21` | `SET_USERKEY_1..3` | Profiles 1..3 (unused) |
| `0x30` | `GET_BATTERY_STATUS` | Reads battery level (used by existing battery service) |
| `0x31` | `RESTORE_FACTORYSETTINGS` | **Do not use programmatically** — factory reset. Manual FN+Space is the sanctioned path |

## Key record format

A single matrix slot holds a 3-byte record: `[type, code1, code2]`.

| type | Meaning | code1 | code2 |
|------|---------|-------|-------|
| `0x0F` | **No user override** — slot is disabled or factory-default active | 0 | 0 |
| `0x10` | HID keyboard record | Modifier bitmask (0 = no modifier) | HID scancode |
| `0x30` | Mouse / wheel (not implemented in this PR) | | |
| `0x50` | Media / consumer (not implemented in this PR) | | |
| `0x60` | Fn function key (not implemented) | | |

**Important:** sending `type=0x0F` disables the physical key entirely (no HID
output). This was the source of one of the diagnostic dead-ends — see
[investigation-log.md § "Type-byte confusion"](investigation-log.md).

**Modifier bitmask** (`code1` when `type=0x10`):

| Bit | Modifier |
|-----|----------|
| 0 | Left Ctrl |
| 1 | Left Shift |
| 2 | Left Alt |
| 3 | Left GUI (Win) |
| 4 | Right Ctrl |
| 5 | Right Shift |
| 6 | Right Alt |
| 7 | Right GUI |

For plain letter/digit keys the modifier is `0x00` and the scancode goes in
`code2`. E.g. remap something to `Q` = `[0x10, 0x00, 0x14]`.

## Matrix indexing

**Do not trust static tables** — the QK75 Pro V2 layout (extracted from
`36b0_304C_0.json` in the vendor webapp) does **not** match Raster's actual
slot indexing.

Instead: at device connect, iterate slots 0..127 with `GET_DEFAULT_0` (`0x16`)
and build a `HID scancode → slot` map by reading the `code2` field of each
factory-default record where `type=0x10` and `code1=0`. Cache the map in memory
for the session (implementation lives in `RasterKeyboardKeyMappingService.ProbeDefaultMatrixAsync`).

Similarly, `GET_USERKEY_0` (`0x1A`) probes the current user overrides so the UI
can display the actual device state (not a stale local cache).

**Byte offset formula:** `offset = 3 * slot` (each slot holds a 3-byte record).

## Full write sequence for one remap

```
[0x00, 0xAA, 0x10, 0, 0, 0, 0, 0, 0, ...]  ← START_COMM
[0x00, 0xAA, 0x1B, addr_lo, addr_hi, 0x03, 0, 0, 0, type, code1, code2, ...]  ← SET_USERKEY_0
[0x00, 0xAA, 0x11, 0, 0, 0, 0, 0, 0, ...]  ← STOP_COMM
```

Firmware ACKs each write with `0x55` at response byte 8 and echoes the record
in bytes 9–11. Writes persist across power cycles until either overwritten or
factory-reset (FN+Space).

## What does NOT work

- **Sending without `0xAA`** at byte 1 — firmware silently ignores.
- **Type `0x0F`** as a "keyboard key" — disables the key instead.
- **The 64/64 `mi_02&col05` interface** for key remap — accepts writes but
  they have no effect (returns only periodic battery push replies).
- **Unplug+replug expecting a firmware reload** — not needed; writes are
  immediate.

See [investigation-log.md](investigation-log.md) for the full diagnostic
journey.
