# Advanced keys and Rapid Snap (RS) — Cyrex / Aress

Extracted 2026-09-09 from **Keyborder-v3** (`resources/app.asar` →
`out/renderer/assets/index-C1SFvu4g.js`, the readable renderer bundle), the client-supplied
vendor driver whose *Advanced Keys* page works against a real PG Cyrex. Companion to
[`magnetic-axis-travel.md`](magnetic-axis-travel.md), which left the `0x3A`/`0x3B` record format
"not extracted yet". Everything below is **as implemented by the vendor app**; the transport and
key-matrix facts it relies on (`0x17`/`0x18`, `0x84` accepted by our table sanity check) are already
hardware-verified on our unit, the record format is not.

## What Rapid Snap is

The vendor's *Advanced Keys* page offers six per-key behaviours, all stored the same way:

| Type byte | Vendor name | UI label | Vendor description |
|---|---|---|---|
| `0x80` | `ADV_DKS` | Dynamic Keystroke (DKS) | Bind 1 to 4 actions based on how far the key is pressed and released. |
| `0x81` | `ADV_MT` | Mod-Tap (MT) | Distinguish tap and hold on one key to trigger different actions. |
| `0x82` | `ADV_TGL` | Toggle (TGL) | Tap to toggle continuous trigger on or off; hold for normal trigger behaviour. |
| `0x83` | `ADV_SOCD` | Instant Release (SOCD) | When two keys are pressed together, the configured key triggers based on priority. |
| **`0x84`** | **`ADV_RS`** | **Rapid Snap (RS)** | **When two keys are pressed together, the deeper-pressed key triggers.** |
| `0x85` | `ADV_END` | Release Trigger (END) | Trigger another key when this key is released. |

Rapid Snap is a **pair** feature: two physical keys (the vendor UI: *Key 1*, *Key 2*, picked on the
on-screen keyboard, then *Confirm*). The firmware resolves both keys held at once in favour of the
one pressed deeper — the magnetic-board flavour of "snap tap". A key can belong to at most one
advanced-key record.

## Storage model

Two device regions are involved, and both are written on every change:

1. **Advanced-key storage area** — read `0x3A`, write `0x3B`. Size in bytes = Device Info byte 8
   × 64 (`advancedKeyStorageAreasSize`; a board without the feature reports 0). Chunked like every
   other table (56-byte payloads, `addr16` = absolute byte offset). The vendor app **always writes
   the whole area**, zero-padded to its full size, then writes the key matrix.
2. **Key matrix** (`0x17` read / `0x18` write, full 384-byte transfer as usual). Every key that
   takes part in an advanced record has its triplet replaced by
   `{TYPE, offset_lo, offset_hi}` — `TYPE` is the advanced type byte (`0x84` for RS) and the
   16-bit little-endian offset points at that record inside the storage area. For a pair feature
   (RS, SOCD) **both** keys' triplets point at the **same** record.

Removing an advanced key = restore both triplets from the **factory table** (`0x16`) and rebuild
the area without the record.

### Area layout

A flat concatenation of variable-length records, no header, no gaps:

```
record := [TYPE, LEN, payload[LEN]]
```

`LEN` is the payload length — the record occupies `2 + LEN` bytes. Exception: for SOCD (`0x83`)
the low nibble of `LEN` is the length and the high nibble carries the priority
(`socdActualLength = LEN & 0x0F`); parse with the nibble mask or a SOCD record misaligns everything
after it.

The vendor rebuilds the area from scratch on every save, in the fixed group order
TGL, MT, SOCD, RS, DKS, END, and re-points every affected matrix triplet at the new offsets
(`mergeKeyMapsToFlat` + `applyAdvancedMapsToKeyRect`). Offsets are therefore not stable across
saves; only the matrix pointers give a record its identity. If the rebuilt area exceeds the
device's area size the app refuses with *"Stored advanced key data exceeds maximum size"* and
writes nothing.

### Rapid Snap record — 8 bytes

```
[0x84, 0x06, slot1, codeType1, code1, slot2, codeType2, code2]
```

| Offset | Field | Meaning |
|---|---|---|
| 0 | TYPE | `0x84` |
| 1 | LEN | `6` |
| 2 | slot1 | key-matrix **slot** (triplet index, `tripletStart / 3`) of Key 1 |
| 3 | codeType1 | `1` = keyboard usage in `code1`; `2` = modifier bitmask in `code1`; `3` = mouse button |
| 4 | code1 | HID usage (codeType 1) or modifier bitmask (codeType 2) of Key 1 |
| 5 | slot2 | slot of Key 2 |
| 6 | codeType2 | as codeType1 |
| 7 | code2 | as code1 |

`codeType` / `code` come straight from the key's **factory** record `{0x10, mask, usage}`
(`getKeyCodeAndType`): usage ≠ 0 → `(1, usage)`; usage = 0 → `(2, mask)` — that is how modifier
keys (Shift, Ctrl, …) are carried. The vendor takes the key's *label* code, i.e. its default identity,
not any remap the slot currently holds — a remapped key that joins a pair loses its remap.

The SOCD record has the same shape with the priority in the LEN high nibble; the other four types
have their own payloads (not needed for RS; DKS is variable length: `[0x80, LEN, countL, countH,
6-byte entries…, 4 bytes travel]`).

### Read path (what the vendor parses on connect)

`parseKeyRectToAdvancedDeviceState`: walk the current key matrix; for every slot whose TYPE is
`0x80`–`0x85`, read the offset from the triplet, take `advancedKeys[offset + 1]` as LEN (nibble-masked
for SOCD), slice the record. For RS both `record[2]` and `record[5]` are registered as owners of the
same record, so a pair renders once (`groupKeysByEqualPayload`).

### Write sequence (vendor, per save)

1. Rebuild the flat area from all records (existing foreign records + the edited set), check it
   fits `advancedKeyStorageAreasSize`, zero-pad to the full size.
2. `0x3B` write of the full area, chunked; each chunk's ack matched on cmd + addr.
3. Build the new key matrix: restore factory triplets on every slot that *was* advanced, then write
   `{TYPE, off_lo, off_hi}` on every slot that *is* advanced (both slots of a pair).
4. `0x18` write of the full matrix, then `0x17` read-back.

Both writes happen inside the vendor's per-device batch (its equivalent of our
[[Smart Infinite device gate]]).

### Guard rules in the vendor UI

- A key that already carries an advanced record cannot be picked for a new one
  (*"This key has an advanced key assigned. Choose another key"*), and Key 1 ≠ Key 2.
- The Key Mapping page refuses to remap an advanced-key slot (`ensureRemapTargetNotAdvancedKey`)
  and shows *"Advanced key configured. Edit it on the Advanced Keys page"* on hover.
- "WakeUp" (the FN-layer reserved slot) can never be picked.
- Restoring a Windows-layer default (`restoreWindowsDefaultLayer`) restores all advanced triplets
  from the factory table and zero-fills the whole area.

## What this means for our implementation

- Our `SmartInfiniteProtocol.IsPlausibleKeyTable` already accepts `0x80`–`0x84` (a `0x84` was seen
  on a real Cyrex table, i.e. this unit has held an RS pair before), so an RS-configured matrix
  reads back without being discarded. `0x85` is not in that set yet.
- Device Info byte 8 (`AdvancedKeyAreaBytes = block[8] × 64`) is the support gate — zero means no
  storage area and the feature must report "unsupported", never write.
- The two-region write must be ordered area → matrix and both verified by read-back, like every
  other table write on this family. Preset pushes that rewrite the key matrix must not clobber the
  `0x84` triplets (see the plan `docs/plans/add-cyrex-extended-keys/`).
- A read-back mismatch is **not** a refusal of the request: the request was accepted and the packets
  went out, so it says one of the two regions did not land — the torn board again. Our write lays the
  set down a second time in the same call against a snapshot read after the failure (which repairs the
  board first if it no longer parses), and only a second mismatch is reported as final. Answering on
  the first mismatch leaves the board torn, and the next read then wipes it.

## The other managed modes (extracted 2026-09-09)

All values `uint16` are little-endian; `codeType`/`code` follow the Rapid Snap rules (1 = HID usage,
2 = modifier bitmask; 3 = mouse button is representable but unreachable from the vendor picker).

### Instant Release (SOCD) — `0x83`, 8 bytes (`handleSaveSOCDData`, line 74490)

```
[0x83, LEN, slot1, codeType1, code1, slot2, codeType2, code2]
LEN = (priority & 0x0F) << 4 | 6
```

| Priority | LEN | Vendor label | Behaviour (vendor tooltip) |
|---|---|---|---|
| 0 | `0x06` | Last Input Priority (default) | When both keys pass the actuation point, the last pressed key overrides the first. |
| 1 | `0x16` | Mutual Cancel | When both keys pass the actuation point, neither key triggers. |
| 2 | `0x26` | Absolute Priority (Key 1) — list: "Absolute A Priority" | Key 1 always overrides Key 2. |
| 3 | `0x36` | Absolute Priority (Key 2) — list: "Absolute B Priority" | Key 2 always overrides Key 1. |

Pair mode: both slots' triplets point at the record. SOCD is the only type whose LEN must be masked
(`& 0x0F`) when walking the area. Codes come from each key's factory record, as for RS.

### Mod-Tap (MT) — `0x81`, 8 bytes (`handleSaveMTData`, line 74522)

```
[0x81, 0x06, time_lo, time_hi, hold_codeType, hold_code, tap_codeType, tap_code]
```

`time` is the hold threshold in **milliseconds** (vendor slider 0..1000, step 1, default 100): a
hold of at least `time` outputs the hold key (bytes 4–5), a release before it outputs the tap key
(bytes 6–7). Example — Caps Lock: hold → Left Ctrl, tap → Esc, 150 ms: `81 06 96 00 02 01 01 29`.
Single-key mode: only the physical key's triplet becomes `{0x81, offset}`; outputs are any keyboard
key or modifier from the vendor's full picker (not a board slot), so they are encoded from the
chosen key's own `{0x10, mask, usage}` identity. The vendor pre-fills Hold with the physical key.

### Toggle (TGL) — `0x82`, 6 bytes (`handleSaveTGLData`, lines 74530–74546)

```
[0x82, 0x04, time_lo, time_hi, codeType, code]
```

`time` in **milliseconds** (slider 0..1000, default 100; it is *not* scaled by `keyStepMarker` even
though the list component receives it): a tap shorter than `time` toggles a latched continuous
trigger of the output key on/off; a hold of at least `time` behaves as a normal momentary key.
Single-key mode, output key from the full picker as for MT. Example — T toggles W at 100 ms:
`82 04 64 00 01 1A`; a modifier output (Left Shift) is `codeType 2`: `82 04 64 00 02 02`. Only the
physical key's triplet becomes `{0x82, offset}`; the output key's own slot is untouched. In our shared
model the toggled output travels in `hold_key` (`tap_key` stays empty) — no new field.

### Vendor read-back bug — do not copy

`handleEchoMTMode`, `handleEchoTGLMode` and the list components decode the 16-bit time as
`hi.toString(16) + lo.toString(16)` **without zero-padding**, so a value whose low byte is `< 0x10`
with a non-zero high byte (e.g. 261 ms = `0x0105`) is displayed wrongly. The write path is correct.
Our decoders use `BinaryPrimitives.ReadUInt16LittleEndian`; a `0x0105` case is pinned in tests.

### Unmanaged by our App

DKS (`0x80`, variable length: `[0x80, LEN, countL, countH, 6-byte entries…, 4 bytes travel]`) and END
(`0x85`, 8 bytes: `[0x85, 6, time_lo, time_hi, press_codeType, press_code, release_codeType,
release_code]`) are preserved byte-for-byte and re-pointed; their slots are reported as blocked. The
same treatment applies to a managed-type record our decoder cannot re-encode — a SOCD priority nibble
past 3, an MT / TGL time past 1000 ms, an MT / TGL output with `codeType 3` (mouse button), an MT /
TGL record pointed at by more than one slot, or an RS / SOCD record whose own `slot1` / `slot2` bytes
are not the pair of slots whose triplets point at it — on both the read and the write path.

The last of those is the pair-mode twin of the MT / TGL slot-count rule. The vendor writes both sides
of a pair record and never lets them disagree, so a disagreement is not a configuration: it is what a
torn write leaves when the area lands and the `0x18` does not, and the previous layout's pointers find
a record of the right TYPE at the old offset. Decoding it would report a pair the board does not have.
Blocking it rather than triggering an [[Advanced-key area reset]] is deliberate: the area still parses,
every other record is still reachable, and the reset destroys the user's DKS / END records, which we
cannot recreate.

### Refusing an ambiguous output code

`getKeyCodeAndType`'s rule (usage wins, else the mask) is how a **board key's factory identity** is
read, and we follow it there. It is not applied to an MT / TGL **output** the caller chose: an output
record carrying both a non-zero mask and a non-zero usage names two keys and the record has room for
one, so the whole entry is refused with a reason instead. Truncating it silently would write a key
nobody asked for, verify it, and leave the caller's stored row permanently unable to match the
read-back — which makes the App's [[Extended-keys self-heal]] rewrite the flash on every page open
without ever converging.
