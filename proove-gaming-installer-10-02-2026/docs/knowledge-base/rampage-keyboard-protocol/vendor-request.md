# Rampage keyboard — request to the firmware vendor

**Subject:** The device reports **`key count = 0`** in Device Info and silently discards every
key-matrix write. Configuration writes on the same interface persist correctly. Is key remapping
enabled in this firmware build?

---

> [!IMPORTANT]
> **Factory reply received 2026-08-04 (RM1):**
>
> - Commands `0x10` and `0x11` must not be used;
> - only command `0x18` needs to be sent;
> - the total transfer length must be **384 bytes**;
> - `0x10`/`0x11` are not used for the other commands either.
>
> This answers §3.4 question 7 in favour of the docx (full 384 bytes), and removes the session
> bracket entirely. Note that the "7 × 56 = 392" variant listed under *Variants already tried*
> had a bounds-guard defect that silently dropped the 7th packet, so a true 384-byte transfer
> (6 × 56 + 48) had in fact never been sent. The implementation now writes exactly 384 bytes as a
> bare `0x18` transfer — see `RampageProtocol.EnumerateWriteChunks` and
> [`hardware-findings.md`](hardware-findings.md) for the re-test result.

---

## 0. The two measurements that matter

**(a) Device Info (`0x12`) reports zero remappable keys.**

```
AA BB 58 32 80 08 00 00 00 03 00 01 04 04 04 02 ...
                     ^^
```

| Index | Field (per the supplied Device Info table) | Value | Expected |
|-------|-------------------------------------------|-------|----------|
| 0, 1 | validity marker | `AA BB` | ✅ correct |
| 2, 3 | product ID | `0x3258` | ✅ correct |
| 4 | key-matrix size (×3 bytes) | `0x80` = 128 → 384 B | ✅ matches `KEY_MATRIX_TAB_SIZE` |
| 5 | macro space (×256 bytes) | `0x08` = 8 → 2048 B | ✅ matches `MACRO_TAB_SIZE` |
| **6** | **actual key count** | **`0x00` = 0** | ❌ **expected 96** |
| 9 | device type | `3` (USB+BLE+2.4G) | ✅ correct |

Every other field in the block is right, so the block is being read and decoded correctly. The
device simply declares that it has **no** keys available for remapping — while `0x17` returns a
complete, valid 96-key table.

**(b) Writes on the same interface do persist — just not table writes.**

After sending `0x15 Set General Info` with `mode=10, brightness=59, speed=3, R=118, G=32, B=238`,
reading `0x14` returns:

```
00 0A 3B 03 00 00 76 20 EE 07 ...
   ^^ ^^ ^^       ^^ ^^ ^^ ^^
   10 59  3      118 32 238 7
```

Byte for byte what we wrote. This rules out any fault in our transport, checksum, framing, session
handling or interface selection — those are proven correct by a working round trip on the same
handle, in the same session style, with the same checksum.

**So: `0x15` persists. `0x18` (key matrix) and `0x19` (per-key lighting) are acknowledged `0x55`
and discarded. And the device reports `key count = 0`.**

The obvious reading is that the key-matrix and per-key-lighting features are not enabled in this
firmware build. **Please confirm, and tell us what firmware or build flag enables them.**

---

**Device:** PG Rampage — USB VID `0x28E9` (10473), PID `0x3258` (12888), TKL, 96 key slots.
**Protocol:** Smart Infinite ("智慧无限"), report ID `0x06`, 64-byte reports.
**Interface used:** `mi_01&col05`, HID usage `0x20` / usage page `0xFF87`, `outLen = inLen = 64` —
selected exactly as the vendor's own enumeration code does (image1 of the supplied document).

This is not a request for general help. Everything in the supplied material has been implemented and
verified against the hardware. One specific piece of information is missing, and it is described
precisely in [§3](#3-what-we-need).

---

## 1. What works — verified against the device

| Capability | Status | Evidence |
|---|---|---|
| Interface selection | Works | Usage `0x20` / page `0xFF87` resolves to `mi_01&col05`; the two other 64/64 vendor interfaces are correctly rejected |
| Session bracket `0x10` / `0x11` | Works | Every command acknowledged |
| `0x12` device info | Works | Returns product ID, key count 96, versions |
| `0x16` read default key table | Works | Returns all 96 slots correctly |
| `0x17` read current key table | Works | Returns all 96 slots correctly |
| Table framing | Works | 6 packets × 56 bytes at offsets `0x0000, 0x0038, 0x0070, 0x00A8, 0x00E0, 0x0118` |
| Checksum | Works | `cmd + addr_lo + addr_hi + len + Σpayload`, 16-bit little-endian at bytes 5–6. Verified byte-for-byte against all six packets of the lighting-read capture and against the key-write capture |
| Reply protocol | Understood | Byte 7 = `0x55` on success. Every reply arrives one command behind the request (the reply to command *N* is read after sending command *N+1*) |
| RGB, battery, DPI | Work | Same interface, same framing |

Correct decoding is confirmed by an independent check: slot 90 always reads `{0x60, 0x01, 0x00}`
(FN), matching `key_matrix_tab` in the supplied `keyboard_user_flash.c`, and all 96 slots decode to
valid key records.

## 2. What does not work

**`0x18 Set Key Info` is acknowledged with status `0x55` on every packet, but the key table is never
changed — not in RAM, not in flash, not after a power cycle.**

Reproduction (changing slot 33 from `Q {0x10,0x00,0x14}` to `W {0x10,0x00,0x1A}`):

```
06 18 00 00 38 CB 05 00  + 56 bytes   -> acknowledged, status 0x55
06 18 38 00 38 87 04 00  + 56 bytes   -> acknowledged, status 0x55
06 18 70 00 38 0A 04 00  + 56 bytes   -> acknowledged, status 0x55
06 18 A8 00 38 B8 04 00  + 56 bytes   -> acknowledged, status 0x55
06 18 E0 00 38 D0 04 00  + 56 bytes   -> acknowledged, status 0x55
06 18 18 01 38 79 01 00  + 56 bytes   -> acknowledged, status 0x55
```

Acknowledgements are matched to requests by the address each reply echoes back, because every reply
arrives one command behind its request. If any packet is ever *not* acknowledged, or acknowledged
with a status other than `0x55`, we will report that specifically.

This is structurally identical to the vendor's own write capture (image5), which begins
`06 18 00 00 38 5c 05 00`. Only the payload differs, because a different key is being assigned.

Result in every case:

- `0x17` read-back — **unchanged** (still `{0x10, 0x00, 0x14}`)
- `0x16` read-back — **unchanged**, and byte-identical to `0x17` in every test
- Physical key — **still emits Q**
- **After unplugging and replugging the keyboard — still emits Q**

That last point is important: the data is not reaching flash either. This differs from an earlier
internal report which claimed a replug applied the mapping; that report came from a build with a
packet-alignment defect in its read path and should be treated as unreliable.

### Variants already tried — all acknowledged, none persisted

1. 6 packets × 56 bytes = 336 bytes (matches image5)
2. 7 packets × 56 bytes = 392 bytes, covering the full 384-byte table reserve
3. 6 packets × 54 bytes, so each packet contains whole 3-byte records
4. A single 3-byte packet at `addr = slot × 3`
5. Each of the above followed by `0x31 Set Profile` with the **same** profile index
6. Read and write inside one `0x10`…`0x11` session, and in separate sessions
7. Inter-packet delays from 3 ms to 500 ms; settle delays up to 500 ms before read-back
8. Both with and without consuming each packet's acknowledgement

`0x20` was **not** tried — internal testing established it is a full factory reset, not a session
reset. Profile round-trips (`0 → 1 → 0`) were not used either; they freeze the firmware until the
keyboard is physically replugged.

## 3. What we need

**In order of preference. Any one of these unblocks the feature.**

### 3.1 The commit command (best)

The opcode and payload the vendor driver sends **after** `0x18 Set Key Info` to make the new key
table take effect. Specifically:

- Command byte, and the `ADDR` and `Length` field values
- Payload contents, if any
- Whether it must be inside the `0x10` / `0x11` session bracket
- Any required delay before or after it
- Whether it must be preceded by a profile or bank selection

### 3.2 A USB packet capture (equally definitive)

A Wireshark/USBPcap capture of the vendor's own configuration tool performing **one** key remap —
for example Esc → A — on `mi_01&col05`:

- Both directions (host→device and device→host)
- With timestamps
- Covering the whole operation, from clicking "Apply" until the physical key emits the new value

A single trace answers every open question at once.

### 3.3 The vendor configuration tool

If the tool itself can be supplied, we will capture the traffic ourselves and no vendor engineering
time is required.

### 3.4 Answers to these specific questions

**The most important one first:**

1. **Device Info byte 6 (actual key count) reads `0x00` on our sample. Should it be 96?** If zero
   means "key remapping not enabled in this build", what firmware version or build option enables
   it, and can it be flashed onto existing units?
2. Why does `0x15 Set General Info` persist while `0x18` (key matrix) and `0x19` (per-key lighting)
   are acknowledged `0x55` and discarded? Are the table writes gated on the key count from (1), or
   on a separate write-enable?
3. Is there a status or error code for a rejected write? Every packet returns `0x55` regardless, so
   a discarded write is currently indistinguishable from an accepted one.
4. Does `0x18` write to a volatile buffer, and if so what transfers it to flash and to the running
   key-scan matrix?
5. Do `0x16` and `0x17` read different tables? In our testing they return byte-identical data in
   every situation, including immediately after a write.
6. Must a profile or memory bank be selected before writing the key table? `PROFILE_NUM = 3` appears
   in the firmware constants, but no profile-selection step is documented for the write path.
7. The supplied document states the keyboard "only stores the data if a full 384 bytes are sent"
   (注意需要发满384Byte键盘才会存储), but the capture in image5 shows only 336 bytes transferred in 6
   packets. Which is correct?

*Minor, unrelated:* Device Info reports `max brightness = 4` (index 12), but the protocol sheet
describes `light_level` as a 0–99 field and our RGB path sends 0–99. Which is correct for this
model?

## 4. What we can offer to speed this up

- Full DeviceHost logs of any test sequence, with every request and reply in hex
- Any specific packet sequence tried on request, with results returned the same day
- Our implementation is complete apart from this one step: reads, catalog, checksum, interface
  selection and UI are all finished and verified

---

*Related material in this repository:* [`README.md`](README.md) (corrected protocol reference),
[`hardware-findings.md`](hardware-findings.md) (results of driving the device).
