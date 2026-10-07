# BLAZE / X43 — Engineering Notes & Gotchas

Hard-won rules for anyone working on this device. Most were learned the painful way.

## 1. Never write device fields outside the scope of the change

param_0 and param_1 each pack **several unrelated settings into one block**:
- param_0: report rate, DPI stage, LED effect, brightness, speed, colour — all in one
  64-byte write.
- param_1: DPI table (block 0), colours (block 1), key matrix (block 2).

Because of this coupling, a write "for one setting" can silently clobber others.
Two real incidents:
- An LED colour change once wrote a **fabricated default key matrix + DPI table** into
  param_1 "to make the write look valid" — which would have destroyed the user's real
  button bindings on every colour tweak. **Do not invent plausible-looking defaults for
  fields you aren't intentionally changing.**
- A DPI-stage change rewrote param_0's LED-effect byte, turning the LED off (see
  [led.md](led.md)).

**Rules:**
- Write the **narrowest block possible** (single param_1 block; for param_0, preserve
  every byte you're not deliberately changing — clone the cache, change one byte).
- If a correct write genuinely needs adjacent state (read-modify-write), get the **real**
  current values first — never fabricate. On this device you often *can't* read them
  back (see below), which is itself a reason to keep writes narrow.
- Treat a scoped request ("change the LED, nothing else") as a hard boundary.

## 2. You mostly cannot read state back from this firmware

- Feature reads (`0x82`/`0x83` param reads, generic report reads) return **zeros**.
- There is **no 64-byte input endpoint**, so param blocks can't be read; `0x82/0x83`
  only return a 9-byte ACK header.
- `0x80` (firmware version) is the one read that returns real data.
- The **only** live state channel is the `0x05` vendor report on Col04, which carries
  report rate + DPI stage + LED effect — nothing else.

Consequence: keep an accurate **software-side cache** of what you wrote, and sync it
from the `0x05` report where possible (`EasenMouseService.SyncStateFromDevice`). See the
read-back matrix in [README.md](README.md).

## 3. Transport: WriteFile to interrupt-OUT (EP3) only

64-byte data blocks reach the firmware **only** via `WriteFile` to the interrupt-OUT
endpoint. `HidD_SetOutputReport` and `HidD_SetFeature`-for-bulk both return success but
are silently ignored (they use EP0). 9-byte command headers go via `SetFeature` (EP0),
which does work. Don't "simplify" the transport — it's this way for a reason.

## 4. Key-matrix writes are dangerous

A `param_1` write with `status=0x07, times=3` carrying zeroed blocks **bricked the
buttons** by wiping the matrix + DPI table at once. Always write one block at a time,
and keep `X43Mouse.RestoreAll()` (in `ProoveGaming.Device.Testing`) ready. See
[key-matrix.md](key-matrix.md).

## 5. Physical buttons vs. software

- **DPI button** broadcasts a `0x05` report on change (live).
- **Polling button** does **not** broadcast — we poll `0x80` every 2 s to notice it.
- **Debounce / lift-off** have no physical control at all — software-only.

## 6. Wrong protocol = spurious USB reconnects

This device speaks the **old Easen 9-byte SetFeatureReport protocol**. Sending the
睿鼎荣 V3.1 `0xAA`-framed protocol (a different vendor doc) caused spurious USB
reconnects. Don't mix protocol families by PID.
